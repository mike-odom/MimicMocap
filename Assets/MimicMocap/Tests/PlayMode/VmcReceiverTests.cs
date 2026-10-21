using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using MimicMocap.Vmc;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>The VMC receiver: OSC decoding, forward kinematics into the canonical frame, and a UDP round trip.</summary>
    public class VmcReceiverTests
    {
        static byte[] Bone(HumanBodyBones b, Vector3 p, Quaternion q) =>
            OscWriter.Message("/VMC/Ext/Bone/Pos", b.ToString(), p.x, p.y, p.z, q.x, q.y, q.z, q.w);

        /// <summary>
        /// A normalised T-pose avatar facing +Z (left side at -X), hips offset from the root to exercise travel,
        /// optionally with the left elbow bent and the left hand's fingers included.
        /// </summary>
        static byte[] BuildBundle(bool upperChest = true, bool leftFingers = true, Quaternion? leftLowerArm = null, bool positions = true)
        {
            var msgs = new List<byte[]> { OscWriter.Message("/VMC/Ext/Root/Pos", "root", 0f, 0f, 0f, 0f, 0f, 0f, 1f) };
            void B(HumanBodyBones b, float x, float y, float z, Quaternion? q = null)
            {
                var p = positions || b == HumanBodyBones.Hips ? new Vector3(x, y, z) : Vector3.zero;
                msgs.Add(Bone(b, p, q ?? Quaternion.identity));
            }
            B(HumanBodyBones.Hips, 0.1f, 0.9f, 0.2f);
            B(HumanBodyBones.Spine, 0f, 0.1f, 0f);
            B(HumanBodyBones.Chest, 0f, 0.15f, 0f);
            if (upperChest) B(HumanBodyBones.UpperChest, 0f, 0.1f, 0f);
            B(HumanBodyBones.Neck, 0f, 0.1f, 0f);
            B(HumanBodyBones.Head, 0f, 0.1f, 0f);
            B(HumanBodyBones.LeftShoulder, -0.05f, 0.05f, 0f);
            B(HumanBodyBones.RightShoulder, 0.05f, 0.05f, 0f);
            B(HumanBodyBones.LeftUpperArm, -0.1f, 0f, 0f);
            B(HumanBodyBones.RightUpperArm, 0.1f, 0f, 0f);
            B(HumanBodyBones.LeftLowerArm, -0.28f, 0f, 0f, leftLowerArm);
            B(HumanBodyBones.RightLowerArm, 0.28f, 0f, 0f);
            B(HumanBodyBones.LeftHand, -0.25f, 0f, 0f);
            B(HumanBodyBones.RightHand, 0.25f, 0f, 0f);
            B(HumanBodyBones.LeftUpperLeg, -0.09f, -0.05f, 0f);
            B(HumanBodyBones.RightUpperLeg, 0.09f, -0.05f, 0f);
            B(HumanBodyBones.LeftLowerLeg, 0f, -0.42f, 0f);
            B(HumanBodyBones.RightLowerLeg, 0f, -0.42f, 0f);
            B(HumanBodyBones.LeftFoot, 0f, -0.42f, 0f);
            B(HumanBodyBones.RightFoot, 0f, -0.42f, 0f);
            B(HumanBodyBones.LeftToes, 0f, -0.07f, 0.13f);
            B(HumanBodyBones.RightToes, 0f, -0.07f, 0.13f);
            if (leftFingers)
            {
                var proximals = new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal };
                var zs = new[] { 0.03f, 0.025f, 0.005f, -0.015f, -0.035f };
                for (var f = 0; f < 5; f++)
                {
                    B(proximals[f], f == 0 ? -0.03f : -0.09f, 0f, zs[f]);
                    B(proximals[f] + 1, -0.04f, 0f, 0f);
                    B(proximals[f] + 2, -0.025f, 0f, 0f);
                }
            }
            return OscWriter.Bundle(msgs.ToArray());
        }

        static PoseFrame Decode(byte[] bundle, VmcPoseSource src = null)
        {
            src ??= new VmcPoseSource(0);
            src.Ingest(bundle, bundle.Length);
            var f = new PoseFrame();
            Assert.IsTrue(src.TryGetLatest(f), "receiver produced a frame");
            return f;
        }

        [Test]
        public void Osc_DecodesBundledMessages()
        {
            var bundle = OscWriter.Bundle(
                OscWriter.Message("/VMC/Ext/OK", 1, 3, 0),
                OscWriter.Message("/VMC/Ext/Bone/Pos", "LeftHand", -0.25f, 0f, 0f, 0f, 0f, 0f, 1f),
                OscWriter.Message("/VMC/Ext/T", 12.5f));
            var parser = new OscPacket();
            var seen = new List<string>();
            var handX = float.NaN;
            var ok = -1;
            var t = float.NaN;
            parser.Parse(bundle, bundle.Length, m =>
            {
                seen.Add(m.Address);
                if (m.AddressIs("/VMC/Ext/Bone/Pos")) { Assert.AreEqual("LeftHand", m.GetString(0)); handX = m.GetFloat(1); Assert.AreEqual(1f, m.GetFloat(7), 1e-6f); }
                if (m.AddressIs("/VMC/Ext/OK")) ok = m.GetInt(0);
                if (m.AddressIs("/VMC/Ext/T")) t = m.GetFloat(0);
            });
            Assert.AreEqual(3, parser.MessageCount);
            CollectionAssert.AreEqual(new[] { "/VMC/Ext/OK", "/VMC/Ext/Bone/Pos", "/VMC/Ext/T" }, seen);
            Assert.AreEqual(-0.25f, handX, 1e-6f);
            Assert.AreEqual(1, ok);
            Assert.AreEqual(12.5f, t, 1e-6f);
            Assert.AreEqual(OscPacket.Hash("LeftHand"), OscPacket.Hash(bundle, System.Array.IndexOf(bundle, (byte)'L'), "LeftHand".Length));
        }

        [Test]
        public void Vmc_ForwardKinematicsPlacesJoints()
        {
            var f = Decode(BuildBundle(leftLowerArm: Quaternion.Euler(0f, 90f, 0f)));
            Assert.IsTrue(f.Tracked);
            Assert.AreEqual(1f, f.Score);

            // Hip-centred with travel carried separately.
            Assert.AreEqual(0f, f[HumanJoint.Pelvis].magnitude, 1e-4f);
            Assert.AreEqual(0.1f, f.RootOffset.x, 1e-4f);
            Assert.AreEqual(0.2f, f.RootOffset.z, 1e-4f);

            // Subject's left is -X (Unity humanoid facing +Z), matching the canonical frame the other sources use.
            // Shoulder (0.05) + upper arm (0.10) offsets from the spine line.
            Assert.AreEqual(-0.15f, f[HumanJoint.LeftShoulder].x, 1e-4f);
            Assert.AreEqual(0.15f, f[HumanJoint.RightShoulder].x, 1e-4f);
            Assert.AreEqual(f[HumanJoint.LeftShoulder].y, f[HumanJoint.RightShoulder].y, 1e-4f);

            // Right arm straight along +X; left elbow bent 90 deg so the hand points at the camera (+Z).
            var rightForearm = f[HumanJoint.RightWrist] - f[HumanJoint.RightElbow];
            Assert.AreEqual(0.25f, rightForearm.x, 1e-4f);
            var leftForearm = f[HumanJoint.LeftWrist] - f[HumanJoint.LeftElbow];
            Assert.AreEqual(0f, leftForearm.x, 1e-4f);
            Assert.AreEqual(0.25f, leftForearm.z, 1e-4f);

            // Torso stacks upward; head above neck; ears symmetric about the head.
            Assert.Greater(f[HumanJoint.Spine].y, f[HumanJoint.Pelvis].y);
            Assert.Greater(f[HumanJoint.Chest].y, f[HumanJoint.Spine].y);
            Assert.Greater(f[HumanJoint.Neck].y, f[HumanJoint.Chest].y);
            Assert.Greater(f[HumanJoint.Head].y, f[HumanJoint.Neck].y);
            Assert.Less(f[HumanJoint.LeftEar].x, f[HumanJoint.RightEar].x);
            Assert.Greater(f[HumanJoint.Nose].z, f[HumanJoint.Head].z);
            Assert.Greater(f.ConfidenceOf(HumanJoint.LeftEye), 0.5f);

            // Legs: knees below hips, ankles below knees, toes ahead of the ankles, heels behind.
            Assert.Less(f[HumanJoint.LeftKnee].y, f[HumanJoint.LeftHip].y);
            Assert.Less(f[HumanJoint.LeftAnkle].y, f[HumanJoint.LeftKnee].y);
            Assert.Greater(f[HumanJoint.LeftToe].z, f[HumanJoint.LeftAnkle].z);
            Assert.Less(f[HumanJoint.RightHeel].z, f[HumanJoint.RightAnkle].z);
            Assert.Greater(PoseTransforms.LegLength(f), 0.7f);

            // Fingers arrived for the left hand only; the bent elbow carries into the finger landmarks.
            Assert.IsTrue(f.LeftHand.Tracked);
            Assert.IsFalse(f.RightHand.Tracked);
            Assert.AreEqual(0f, f.LeftHand.Landmarks[0].magnitude, 1e-5f);
            var mcp = f.LeftHand.Landmarks[(int)HandFrame.Landmark.IndexMcp];
            var tip = f.LeftHand.Landmarks[(int)HandFrame.Landmark.IndexTip];
            Assert.Greater(tip.z, mcp.z + 0.05f, "index finger continues along the forearm direction (+Z)");
            Assert.AreEqual(mcp.x, tip.x, 1e-3f, "the finger keeps its sideways offset from the wrist (0.025 after the 90 deg turn)");
            Assert.AreEqual(0.025f, mcp.x, 1e-3f);
            Assert.Greater(f.LeftHand.Landmarks[(int)HandFrame.Landmark.ThumbTip].magnitude, 0.05f);
            // Coarse knuckle joints follow the fingers.
            Assert.AreEqual(f[HumanJoint.LeftWrist].z + mcp.z, f[HumanJoint.LeftIndex].z, 1e-4f);
            Assert.Greater(f.ConfidenceOf(HumanJoint.RightIndex), 0.5f, "missing right fingers fall back to an estimated knuckle");
        }

        [Test]
        public void Vmc_MissingUpperChestStillChains()
        {
            var with = Decode(BuildBundle(upperChest: true));
            var without = Decode(BuildBundle(upperChest: false));
            Assert.IsTrue(without.Tracked);
            Assert.Greater(without[HumanJoint.Neck].y, without[HumanJoint.Chest].y);
            Assert.Greater(without[HumanJoint.Head].y, without[HumanJoint.Neck].y);
            // The avatar without an UpperChest bone is exactly that segment shorter.
            Assert.AreEqual(with[HumanJoint.Neck].y - 0.1f, without[HumanJoint.Neck].y, 1e-4f);
            Assert.AreEqual(with[HumanJoint.LeftShoulder].y - 0.1f, without[HumanJoint.LeftShoulder].y, 1e-4f);
        }

        [Test]
        public void Vmc_RotationOnlySenderUsesDefaultProportions()
        {
            var src = new VmcPoseSource(0);
            var f = Decode(BuildBundle(positions: false), src);
            Assert.IsFalse(src.SenderHasPositions);
            Assert.IsTrue(f.Tracked);
            Assert.Greater(PoseTransforms.LegLength(f), 0.7f);
            Assert.Greater(Vector3.Distance(f[HumanJoint.LeftShoulder], f[HumanJoint.RightShoulder]), 0.25f);
            Assert.Greater(Vector3.Distance(f[HumanJoint.LeftWrist], f[HumanJoint.LeftShoulder]), 0.45f);
            Assert.IsTrue(f.LeftHand.Tracked);
            Assert.Greater(f.LeftHand.Landmarks[(int)HandFrame.Landmark.MiddleTip].magnitude, 0.15f);
        }

        [Test]
        public void Vmc_MirrorSwapsHandsLikeOtherSources()
        {
            // Bend only the left elbow so the two sides differ.
            var f = Decode(BuildBundle(leftLowerArm: Quaternion.Euler(0f, 90f, 0f)));
            var leftTip = f.LeftHand.Landmarks[(int)HandFrame.Landmark.IndexTip];
            var leftWrist = f[HumanJoint.LeftWrist];
            PoseTransforms.Mirror(f);
            Assert.IsTrue(f.RightHand.Tracked);
            Assert.IsFalse(f.LeftHand.Tracked);
            Assert.AreEqual(-leftTip.x, f.RightHand.Landmarks[(int)HandFrame.Landmark.IndexTip].x, 1e-5f);
            Assert.AreEqual(leftTip.z, f.RightHand.Landmarks[(int)HandFrame.Landmark.IndexTip].z, 1e-5f);
            // The bent arm is now the right one, reflected across X.
            Assert.AreEqual(-leftWrist.x, f[HumanJoint.RightWrist].x, 1e-5f);
            Assert.AreEqual(leftWrist.z, f[HumanJoint.RightWrist].z, 1e-5f);
            Assert.AreEqual(0.25f, (f[HumanJoint.RightWrist] - f[HumanJoint.RightElbow]).z, 1e-4f);
            Assert.AreEqual(0.25f, (f[HumanJoint.LeftWrist] - f[HumanJoint.LeftElbow]).magnitude, 1e-4f);
            Assert.AreEqual(-0.1f, f.RootOffset.x, 1e-4f);
        }

        [Test]
        public void Vmc_LosingTheSenderReportsUntracked()
        {
            var src = new VmcPoseSource(0) { TimeoutSeconds = 0f };
            var f = Decode(BuildBundle(), src);
            Assert.IsTrue(f.Tracked);
            System.Threading.Thread.Sleep(20);
            Assert.IsTrue(src.TryGetLatest(f), "one lost frame is reported after the timeout");
            Assert.IsFalse(f.Tracked);
            Assert.IsFalse(src.TryGetLatest(f), "then nothing until packets resume");
        }

        [UnityTest]
        public IEnumerator Vmc_ReceivesOverUdp()
        {
            var port = 39600 + new System.Random().Next(0, 300);
            var src = new VmcPoseSource(port) { TimeoutSeconds = 0.4f };
            src.Initialize();
            Assert.IsTrue(src.IsAvailable, src.Status);
            var bundle = BuildBundle(leftLowerArm: Quaternion.Euler(0f, 90f, 0f));
            var frame = new PoseFrame();
            try
            {
                using var sender = new UdpClient();
                var target = new IPEndPoint(IPAddress.Loopback, port);
                var got = false;
                var until = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < until && !got)
                {
                    sender.Send(bundle, bundle.Length, target);
                    yield return null;
                    got = src.TryGetLatest(frame) && frame.Tracked;
                }
                Assert.IsTrue(got, "no VMC frame received over loopback UDP: " + src.Status);
                Assert.IsTrue(frame.LeftHand.Tracked);
                Assert.AreEqual(0.25f, (frame[HumanJoint.LeftWrist] - frame[HumanJoint.LeftElbow]).z, 1e-4f);
                Assert.AreEqual("127.0.0.1", src.SenderAddress);
                Debug.Log("[MimicMocap] VMC loopback: " + src.Status);

                // Silence → one untracked frame.
                var lost = false;
                until = Time.realtimeSinceStartup + 2f;
                while (Time.realtimeSinceStartup < until && !lost)
                {
                    yield return null;
                    if (src.TryGetLatest(frame) && !frame.Tracked) lost = true;
                }
                Assert.IsTrue(lost, "receiver never reported the sender as lost");
            }
            finally
            {
                src.Dispose();
            }
        }
    }
}
