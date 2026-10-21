using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace MimicMocap.Vmc
{
    /// <summary>
    /// Receives VMC protocol (Virtual Motion Capture, OSC over UDP) bone data from an external tracker such as
    /// XR Animator, VSeeFace or TDPT, and converts it into the canonical <see cref="PoseFrame"/> by running forward
    /// kinematics over the Unity humanoid bone hierarchy. The webcam frame passed to <see cref="Submit"/> is ignored:
    /// the external app owns tracking, this source only listens.
    /// </summary>
    public sealed class VmcPoseSource : IPoseSource
    {
        public const int DefaultPort = 39539;
        const int k_BoneCount = (int)HumanBodyBones.LastBone;

        struct BoneSample
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Present;
        }

        /// <summary>Seconds without packets before the pose is reported as lost.</summary>
        public float TimeoutSeconds = 1.0f;

        public int Port { get; }
        public string Name => "VMC receiver";
        public bool IsAvailable { get; private set; }
        public bool IsBusy => false;
        public string Status { get; private set; } = "not started";
        public string SenderAddress { get; private set; } = "";
        public float PacketsPerSecond { get; private set; }
        public int LastBoneCount { get; private set; }
        public bool LastLeftHand { get; private set; }
        public bool LastRightHand { get; private set; }
        public bool SenderHasPositions { get; private set; }
        public bool EverReceived => m_EverReceived;
        public double SecondsSinceLastPacket => m_EverReceived ? (Stopwatch.GetTimestamp() - Interlocked.Read(ref m_LastPacketTicks)) / (double)Stopwatch.Frequency : double.PositiveInfinity;

        readonly object m_Lock = new object();
        readonly BoneSample[] m_Incoming = new BoneSample[k_BoneCount];
        readonly BoneSample[] m_Bones = new BoneSample[k_BoneCount];
        BoneSample m_IncomingRoot = new BoneSample { Rotation = Quaternion.identity };
        BoneSample m_Root = new BoneSample { Rotation = Quaternion.identity };
        bool m_Dirty;
        volatile bool m_EverReceived;
        bool m_LostReported;
        long m_LastPacketTicks;
        int m_PacketCount;
        int m_RateWindowCount;
        long m_RateWindowStart;
        int m_SenderLoaded = -1;

        UdpClient m_Udp;
        Thread m_Thread;
        volatile bool m_Running;
        string m_LastError;

        readonly OscPacket m_Parser = new OscPacket();
        readonly Action<OscPacket.Message> m_OnMessage;
        static readonly Dictionary<uint, int> s_BoneByHash = BuildBoneLookup();
        static readonly int[] s_Parent = BuildParents();
        static readonly Vector3[] s_DefaultOffset = BuildDefaultOffsets();

        // Forward-kinematics scratch.
        readonly Vector3[] m_WorldPos = new Vector3[k_BoneCount];
        readonly Quaternion[] m_WorldRot = new Quaternion[k_BoneCount];
        readonly bool[] m_Resolved = new bool[k_BoneCount];
        Vector3 m_TravelReference;

        public VmcPoseSource(int port = DefaultPort)
        {
            Port = port;
            m_OnMessage = HandleMessage;
        }

        public void Initialize()
        {
            try
            {
                m_Udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
                m_Running = true;
                m_Thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "VMC receiver" };
                m_Thread.Start();
                IsAvailable = true;
                m_RateWindowStart = Stopwatch.GetTimestamp();
                Status = $"waiting for a VMC sender on UDP :{Port} (XR Animator: Settings > VMC protocol > Send to 127.0.0.1:{Port})";
            }
            catch (SocketException e)
            {
                IsAvailable = false;
                Status = $"UDP port {Port} is busy ({e.SocketErrorCode}); close the other VMC receiver or pass -vmcport";
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = "VMC receiver failed: " + e.Message;
            }
        }

        /// <summary>The camera frame is not used; VMC data arrives asynchronously.</summary>
        public void Submit(Texture frame, double timestamp) { }

        /// <summary>Zero the root travel at the subject's current position.</summary>
        public void ResetTravelReference()
        {
            lock (m_Lock)
            {
                m_TravelReference = new Vector3(m_WorldPos[(int)HumanBodyBones.Hips].x, 0f, m_WorldPos[(int)HumanBodyBones.Hips].z);
            }
        }

        /// <summary>Feed a datagram directly (tests and replay). Thread-safe.</summary>
        public void Ingest(byte[] data, int length, string sender = "local")
        {
            lock (m_Lock)
            {
                m_Parser.Parse(data, length, m_OnMessage);
                m_Dirty = true;
            }
            SenderAddress = sender;
            Interlocked.Exchange(ref m_LastPacketTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref m_PacketCount);
            m_EverReceived = true;
        }

        public bool TryGetLatest(PoseFrame frame)
        {
            var got = false;
            lock (m_Lock)
            {
                if (m_Dirty)
                {
                    Array.Copy(m_Incoming, m_Bones, k_BoneCount);
                    m_Root = m_IncomingRoot;
                    m_Dirty = false;
                    got = true;
                }
            }
            UpdateRate();

            if (got)
            {
                m_LostReported = false;
                BuildFrame(frame);
                Status = $"VMC :{Port}  {PacketsPerSecond:0} pkt/s  {LastBoneCount} bones  fingers {(LastLeftHand ? "L" : "-")}{(LastRightHand ? "R" : "-")}  from {SenderAddress}" + (SenderHasPositions ? "" : "  (rotation only, default proportions)");
                return true;
            }

            if (m_EverReceived && !m_LostReported && SecondsSinceLastPacket > TimeoutSeconds)
            {
                m_LostReported = true;
                frame.Clear();
                frame.Timestamp = Time.timeAsDouble;
                Status = $"VMC :{Port}  no packets for {SecondsSinceLastPacket:0.0} s (last sender {SenderAddress})";
                return true;
            }
            if (!string.IsNullOrEmpty(m_LastError)) Status = "VMC receive error: " + m_LastError;
            return false;
        }

        public void Dispose()
        {
            m_Running = false;
            try { m_Udp?.Close(); } catch { }
            m_Udp = null;
            if (m_Thread != null && m_Thread.IsAlive) m_Thread.Join(500);
            m_Thread = null;
            IsAvailable = false;
        }

        // ---------------------------------------------------------------- receive thread

        void ReceiveLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (m_Running)
            {
                try
                {
                    var data = m_Udp.Receive(ref ep);
                    Ingest(data, data.Length, ep.Address.ToString());
                }
                catch (SocketException e)
                {
                    if (!m_Running) break;
                    m_LastError = e.SocketErrorCode.ToString();
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    m_LastError = e.Message;
                }
            }
        }

        void HandleMessage(OscPacket.Message m)
        {
            if (m.AddressIs("/VMC/Ext/Bone/Pos"))
            {
                if (m.ArgCount < 8) return;
                if (!s_BoneByHash.TryGetValue(m.GetStringHash(0), out var bone)) return;
                var p = new Vector3(m.GetFloat(1), m.GetFloat(2), m.GetFloat(3));
                var q = new Quaternion(m.GetFloat(4), m.GetFloat(5), m.GetFloat(6), m.GetFloat(7));
                if (!Finite(p) || !Finite(q)) return;
                if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f) q = Quaternion.identity;
                m_Incoming[bone] = new BoneSample { Position = p, Rotation = q, Present = true };
            }
            else if (m.AddressIs("/VMC/Ext/Root/Pos"))
            {
                if (m.ArgCount < 8) return;
                var p = new Vector3(m.GetFloat(1), m.GetFloat(2), m.GetFloat(3));
                var q = new Quaternion(m.GetFloat(4), m.GetFloat(5), m.GetFloat(6), m.GetFloat(7));
                if (!Finite(p) || !Finite(q)) return;
                if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f) q = Quaternion.identity;
                m_IncomingRoot = new BoneSample { Position = p, Rotation = q, Present = true };
            }
            else if (m.AddressIs("/VMC/Ext/OK"))
            {
                m_SenderLoaded = m.GetInt(0);
            }
        }

        static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        static bool Finite(Quaternion q) => !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w));

        void UpdateRate()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - m_RateWindowStart) / (double)Stopwatch.Frequency;
            if (elapsed >= 1.0)
            {
                var count = Interlocked.Exchange(ref m_PacketCount, 0);
                PacketsPerSecond = (float)(count / elapsed);
                m_RateWindowStart = now;
            }
        }

        // ---------------------------------------------------------------- forward kinematics

        void BuildFrame(PoseFrame frame)
        {
            frame.Clear();
            frame.Timestamp = Time.timeAsDouble;

            var boneCount = 0;
            SenderHasPositions = false;
            for (var i = 0; i < k_BoneCount; i++)
            {
                m_Resolved[i] = false;
                if (!m_Bones[i].Present) continue;
                boneCount++;
                if (i != (int)HumanBodyBones.Hips && m_Bones[i].Position.sqrMagnitude > 1e-8f) SenderHasPositions = true;
            }
            LastBoneCount = boneCount;
            if (!m_Bones[(int)HumanBodyBones.Hips].Present) { LastLeftHand = LastRightHand = false; return; }
            for (var i = 0; i < k_BoneCount; i++) Resolve(i);

            var pelvis = World(HumanBodyBones.Hips);

            Put(frame, HumanJoint.Pelvis, HumanBodyBones.Hips);
            Put(frame, HumanJoint.Spine, HumanBodyBones.Spine);
            Put(frame, HumanJoint.Chest, Has(HumanBodyBones.UpperChest) && Has(HumanBodyBones.Chest) ? HumanBodyBones.UpperChest : HumanBodyBones.Chest);
            Put(frame, HumanJoint.Neck, HumanBodyBones.Neck);
            Put(frame, HumanJoint.LeftShoulder, HumanBodyBones.LeftUpperArm);
            Put(frame, HumanJoint.RightShoulder, HumanBodyBones.RightUpperArm);
            Put(frame, HumanJoint.LeftElbow, HumanBodyBones.LeftLowerArm);
            Put(frame, HumanJoint.RightElbow, HumanBodyBones.RightLowerArm);
            Put(frame, HumanJoint.LeftWrist, HumanBodyBones.LeftHand);
            Put(frame, HumanJoint.RightWrist, HumanBodyBones.RightHand);
            Put(frame, HumanJoint.LeftHip, HumanBodyBones.LeftUpperLeg);
            Put(frame, HumanJoint.RightHip, HumanBodyBones.RightUpperLeg);
            Put(frame, HumanJoint.LeftKnee, HumanBodyBones.LeftLowerLeg);
            Put(frame, HumanJoint.RightKnee, HumanBodyBones.RightLowerLeg);
            Put(frame, HumanJoint.LeftAnkle, HumanBodyBones.LeftFoot);
            Put(frame, HumanJoint.RightAnkle, HumanBodyBones.RightFoot);

            // Head: the Head bone pivots at the skull base. Synthesise ears, eyes and nose from its rotation so the
            // face-based head aim downstream sees the same landmarks BlazePose would give.
            if (Has(HumanBodyBones.Head))
            {
                var hp = World(HumanBodyBones.Head);
                var hr = WorldRot(HumanBodyBones.Head);
                var earMid = hp + hr * new Vector3(0f, 0.045f, 0.005f);
                Set(frame, HumanJoint.Head, earMid, 1f);
                Set(frame, HumanJoint.LeftEar, earMid + hr * new Vector3(-0.075f, 0f, 0f), 1f);
                Set(frame, HumanJoint.RightEar, earMid + hr * new Vector3(0.075f, 0f, 0f), 1f);
                Set(frame, HumanJoint.LeftEye, hp + hr * new Vector3(-0.032f, 0.065f, 0.085f), 1f);
                Set(frame, HumanJoint.RightEye, hp + hr * new Vector3(0.032f, 0.065f, 0.085f), 1f);
                Set(frame, HumanJoint.Nose, hp + hr * new Vector3(0f, 0.03f, 0.105f), 1f);
            }

            // Feet: toes from the Toes bone when present, else extended from the foot; heels behind the ankle.
            Foot(frame, true);
            Foot(frame, false);

            // Hands: knuckles for the coarse wrist fork, then the full 21-landmark hand when the fingers are sent.
            HandJoints(frame, true);
            HandJoints(frame, false);
            LastLeftHand = FillHand(frame.LeftHand, true);
            LastRightHand = FillHand(frame.RightHand, false);

            frame.SynthesiseTorso();

            // Hip-centre the joints; travel goes into RootOffset (canonical axes match Unity's here).
            for (var i = 0; i < JointTopology.Count; i++) frame.Joints[i] -= pelvis;
            for (var i = 0; i < HandFrame.LandmarkCount; i++) { /* hand landmarks are already wrist-relative */ }
            frame.RootOffset = new Vector3(pelvis.x - m_TravelReference.x, pelvis.y, pelvis.z - m_TravelReference.z);
            frame.Tracked = frame.ConfidenceOf(HumanJoint.LeftShoulder) > 0.5f && frame.ConfidenceOf(HumanJoint.RightShoulder) > 0.5f
                            && frame.ConfidenceOf(HumanJoint.LeftHip) > 0.5f && frame.ConfidenceOf(HumanJoint.RightHip) > 0.5f;
            frame.Score = frame.Tracked ? 1f : 0f;
            frame.HasImageJoints = false;
            frame.LeftHand.Timestamp = frame.RightHand.Timestamp = frame.Timestamp;
        }

        void Resolve(int bone)
        {
            if (m_Resolved[bone]) return;
            var parent = s_Parent[bone];
            Vector3 parentPos;
            Quaternion parentRot;
            if (parent < 0)
            {
                parentPos = m_Root.Position;
                parentRot = m_Root.Rotation;
            }
            else
            {
                Resolve(parent);
                parentPos = m_WorldPos[parent];
                parentRot = m_WorldRot[parent];
            }
            var s = m_Bones[bone];
            if (s.Present)
            {
                var local = s.Position;
                if (!SenderHasPositions && bone != (int)HumanBodyBones.Hips) local = s_DefaultOffset[bone];
                m_WorldPos[bone] = parentPos + parentRot * local;
                m_WorldRot[bone] = parentRot * s.Rotation;
            }
            else
            {
                // Optional bone the sender's avatar lacks: children are expressed relative to the nearest present ancestor.
                m_WorldPos[bone] = parentPos;
                m_WorldRot[bone] = parentRot;
            }
            m_Resolved[bone] = true;
        }

        bool Has(HumanBodyBones b) => m_Bones[(int)b].Present;
        Vector3 World(HumanBodyBones b) => m_WorldPos[(int)b];
        Quaternion WorldRot(HumanBodyBones b) => m_WorldRot[(int)b];

        /// <summary>Local offset of a bone as the sender defined it, or our default proportions for rotation-only senders.</summary>
        Vector3 LocalOffset(HumanBodyBones b)
        {
            var s = m_Bones[(int)b];
            if (s.Present && SenderHasPositions && s.Position.sqrMagnitude > 1e-8f) return s.Position;
            return s_DefaultOffset[(int)b];
        }

        void Put(PoseFrame f, HumanJoint j, HumanBodyBones b)
        {
            if (!Has(b)) return;
            Set(f, j, World(b), 1f);
        }

        static void Set(PoseFrame f, HumanJoint j, Vector3 p, float c)
        {
            f.Joints[(int)j] = p;
            f.Confidence[(int)j] = c;
        }

        void Foot(PoseFrame f, bool left)
        {
            var foot = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
            var toes = left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
            if (!Has(foot)) return;
            var fp = World(foot);
            var fr = WorldRot(foot);
            var toe = Has(toes) ? World(toes) : fp + fr * new Vector3(0f, -0.06f, 0.14f);
            Set(f, left ? HumanJoint.LeftToe : HumanJoint.RightToe, toe, 1f);
            Set(f, left ? HumanJoint.LeftHeel : HumanJoint.RightHeel, fp + fr * new Vector3(0f, -0.065f, -0.04f), 0.9f);
        }

        void HandJoints(PoseFrame f, bool left)
        {
            var hand = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            if (!Has(hand)) return;
            var wrist = World(hand);
            var wr = WorldRot(hand);
            var sign = left ? -1f : 1f;
            var index = left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal;
            var little = left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal;
            var thumb = left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate;

            var indexPos = Has(index) ? World(index) : wrist + wr * new Vector3(sign * 0.09f, 0f, 0.025f);
            var littlePos = Has(little) ? World(little) : wrist + wr * new Vector3(sign * 0.08f, 0f, -0.035f);
            var thumbPos = Has(thumb) ? World(thumb) : wrist + wr * new Vector3(sign * 0.06f, -0.01f, 0.05f);
            var c = Has(index) ? 1f : 0.7f;
            Set(f, left ? HumanJoint.LeftIndex : HumanJoint.RightIndex, indexPos, c);
            Set(f, left ? HumanJoint.LeftHandTip : HumanJoint.RightHandTip, indexPos, c);
            Set(f, left ? HumanJoint.LeftPinky : HumanJoint.RightPinky, littlePos, Has(little) ? 1f : 0.7f);
            Set(f, left ? HumanJoint.LeftThumb : HumanJoint.RightThumb, thumbPos, Has(thumb) ? 1f : 0.7f);
        }

        bool FillHand(HandFrame hand, bool left)
        {
            hand.Clear();
            var handBone = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            if (!Has(handBone)) return false;
            var fingers = left
                ? new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal }
                : new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal };
            var present = 0;
            foreach (var p in fingers) if (Has(p)) present++;
            if (present < 4) return false;

            var wrist = World(handBone);
            var L = hand.Landmarks;
            L[0] = Vector3.zero;
            for (var fi = 0; fi < 5; fi++)
            {
                var proximal = (int)fingers[fi];
                var intermediate = proximal + 1;
                var distal = proximal + 2;
                var baseIndex = 1 + fi * 4;
                var wr = WorldRot(handBone);
                var sign = left ? -1f : 1f;
                var fallbackDir = wr * new Vector3(sign, 0f, 0f);

                var pPos = Has((HumanBodyBones)proximal) ? World((HumanBodyBones)proximal) : wrist + wr * s_DefaultOffset[proximal];
                var iPos = Has((HumanBodyBones)intermediate) ? World((HumanBodyBones)intermediate) : pPos + fallbackDir * s_DefaultOffset[intermediate].magnitude;
                var dPos = Has((HumanBodyBones)distal) ? World((HumanBodyBones)distal) : iPos + fallbackDir * s_DefaultOffset[distal].magnitude;
                Vector3 tip;
                if (Has((HumanBodyBones)distal))
                {
                    // The distal phalanx continues the intermediate one; its length is about 0.85x the previous segment.
                    tip = dPos + WorldRot((HumanBodyBones)distal) * LocalOffset((HumanBodyBones)distal) * 0.85f;
                }
                else tip = dPos + (dPos - iPos) * 0.85f;

                L[baseIndex] = pPos - wrist;
                L[baseIndex + 1] = iPos - wrist;
                L[baseIndex + 2] = dPos - wrist;
                L[baseIndex + 3] = tip - wrist;
            }
            hand.Tracked = true;
            hand.Score = present / 5f;
            return true;
        }

        // ---------------------------------------------------------------- static tables

        static Dictionary<uint, int> BuildBoneLookup()
        {
            var d = new Dictionary<uint, int>();
            for (var i = 0; i < k_BoneCount; i++)
                d[OscPacket.Hash(((HumanBodyBones)i).ToString())] = i;
            return d;
        }

        static int[] BuildParents()
        {
            var p = new int[k_BoneCount];
            for (var i = 0; i < k_BoneCount; i++) p[i] = -1;
            void Set(HumanBodyBones child, HumanBodyBones parent) => p[(int)child] = (int)parent;

            Set(HumanBodyBones.Spine, HumanBodyBones.Hips);
            Set(HumanBodyBones.Chest, HumanBodyBones.Spine);
            Set(HumanBodyBones.UpperChest, HumanBodyBones.Chest);
            Set(HumanBodyBones.Neck, HumanBodyBones.UpperChest);
            Set(HumanBodyBones.Head, HumanBodyBones.Neck);
            Set(HumanBodyBones.LeftEye, HumanBodyBones.Head);
            Set(HumanBodyBones.RightEye, HumanBodyBones.Head);
            Set(HumanBodyBones.Jaw, HumanBodyBones.Head);
            Set(HumanBodyBones.LeftShoulder, HumanBodyBones.UpperChest);
            Set(HumanBodyBones.RightShoulder, HumanBodyBones.UpperChest);
            Set(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftShoulder);
            Set(HumanBodyBones.RightUpperArm, HumanBodyBones.RightShoulder);
            Set(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftUpperArm);
            Set(HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm);
            Set(HumanBodyBones.LeftHand, HumanBodyBones.LeftLowerArm);
            Set(HumanBodyBones.RightHand, HumanBodyBones.RightLowerArm);
            Set(HumanBodyBones.LeftUpperLeg, HumanBodyBones.Hips);
            Set(HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips);
            Set(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftUpperLeg);
            Set(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg);
            Set(HumanBodyBones.LeftFoot, HumanBodyBones.LeftLowerLeg);
            Set(HumanBodyBones.RightFoot, HumanBodyBones.RightLowerLeg);
            Set(HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot);
            Set(HumanBodyBones.RightToes, HumanBodyBones.RightFoot);
            foreach (var (hand, first) in new[] { (HumanBodyBones.LeftHand, HumanBodyBones.LeftThumbProximal), (HumanBodyBones.RightHand, HumanBodyBones.RightThumbProximal) })
            {
                for (var f = 0; f < 5; f++)
                {
                    var proximal = (int)first + f * 3;
                    p[proximal] = (int)hand;
                    p[proximal + 1] = proximal;
                    p[proximal + 2] = proximal + 1;
                }
            }
            return p;
        }

        /// <summary>T-pose local offsets (metres) for a ~1.65 m normalised humanoid facing +Z, used when a sender omits positions.</summary>
        static Vector3[] BuildDefaultOffsets()
        {
            var o = new Vector3[k_BoneCount];
            void Set(HumanBodyBones b, float x, float y, float z) => o[(int)b] = new Vector3(x, y, z);
            void Mirror(HumanBodyBones left, HumanBodyBones right, float x, float y, float z) { Set(left, -x, y, z); Set(right, x, y, z); }

            Set(HumanBodyBones.Hips, 0f, 0.92f, 0f);
            Set(HumanBodyBones.Spine, 0f, 0.09f, 0f);
            Set(HumanBodyBones.Chest, 0f, 0.12f, 0f);
            Set(HumanBodyBones.UpperChest, 0f, 0.12f, 0f);
            Set(HumanBodyBones.Neck, 0f, 0.13f, 0f);
            Set(HumanBodyBones.Head, 0f, 0.09f, 0f);
            Set(HumanBodyBones.Jaw, 0f, 0f, 0.05f);
            Mirror(HumanBodyBones.LeftEye, HumanBodyBones.RightEye, 0.03f, 0.06f, 0.09f);
            Mirror(HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, 0.03f, 0.10f, 0f);
            Mirror(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, 0.13f, 0f, 0f);
            Mirror(HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, 0.27f, 0f, 0f);
            Mirror(HumanBodyBones.LeftHand, HumanBodyBones.RightHand, 0.25f, 0f, 0f);
            Mirror(HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal, 0.03f, -0.01f, 0.03f);
            Mirror(HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.RightThumbIntermediate, 0.035f, 0f, 0.02f);
            Mirror(HumanBodyBones.LeftThumbDistal, HumanBodyBones.RightThumbDistal, 0.03f, 0f, 0.012f);
            Mirror(HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal, 0.09f, 0f, 0.025f);
            Mirror(HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightIndexIntermediate, 0.04f, 0f, 0f);
            Mirror(HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal, 0.025f, 0f, 0f);
            Mirror(HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal, 0.095f, 0f, 0.005f);
            Mirror(HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate, 0.045f, 0f, 0f);
            Mirror(HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal, 0.027f, 0f, 0f);
            Mirror(HumanBodyBones.LeftRingProximal, HumanBodyBones.RightRingProximal, 0.09f, 0f, -0.015f);
            Mirror(HumanBodyBones.LeftRingIntermediate, HumanBodyBones.RightRingIntermediate, 0.04f, 0f, 0f);
            Mirror(HumanBodyBones.LeftRingDistal, HumanBodyBones.RightRingDistal, 0.025f, 0f, 0f);
            Mirror(HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal, 0.08f, 0f, -0.035f);
            Mirror(HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.RightLittleIntermediate, 0.032f, 0f, 0f);
            Mirror(HumanBodyBones.LeftLittleDistal, HumanBodyBones.RightLittleDistal, 0.02f, 0f, 0f);
            Mirror(HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, 0.09f, -0.04f, 0f);
            Mirror(HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg, 0f, -0.42f, 0f);
            Mirror(HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, 0f, -0.42f, 0f);
            Mirror(HumanBodyBones.LeftToes, HumanBodyBones.RightToes, 0f, -0.07f, 0.13f);
            return o;
        }
    }
}
