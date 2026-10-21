using System.Collections.Generic;
using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Drives any Mecanim Humanoid rig from canonical joint positions.
    /// For each bone we build a look-rotation from (bone -> child direction, up hint) both in the
    /// rig's rest pose and in the tracked pose; the bone's world rotation becomes
    /// tracked * inverse(rest) * restBoneRotation. Twist follows the hint, which is the torso's
    /// facing for limbs, so it stays plausible without wrist/ankle roll data.
    /// </summary>
    public sealed class HumanoidRetargeter
    {
        struct BoneRule
        {
            public HumanBodyBones bone;
            public HumanJoint from;
            public HumanJoint to;
            public HintKind hint;
        }

        enum HintKind { PelvisForward, ChestForward, HeadForward, HipsRight, ShouldersRight, PelvisUp }

        sealed class BoneState
        {
            public Transform transform;
            public BoneRule rule;
            public Quaternion restWorldRotation;
            public Quaternion restFrame;
        }

        static readonly BoneRule[] k_Rules =
        {
            new BoneRule { bone = HumanBodyBones.Spine, from = HumanJoint.Pelvis, to = HumanJoint.Chest, hint = HintKind.PelvisForward },
            new BoneRule { bone = HumanBodyBones.Chest, from = HumanJoint.Spine, to = HumanJoint.Neck, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.UpperChest, from = HumanJoint.Chest, to = HumanJoint.Neck, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.Neck, from = HumanJoint.Neck, to = HumanJoint.Head, hint = HintKind.HeadForward },
            new BoneRule { bone = HumanBodyBones.Head, from = HumanJoint.Neck, to = HumanJoint.Head, hint = HintKind.HeadForward },
            new BoneRule { bone = HumanBodyBones.LeftUpperArm, from = HumanJoint.LeftShoulder, to = HumanJoint.LeftElbow, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.LeftLowerArm, from = HumanJoint.LeftElbow, to = HumanJoint.LeftWrist, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.LeftHand, from = HumanJoint.LeftWrist, to = HumanJoint.LeftHandTip, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.RightUpperArm, from = HumanJoint.RightShoulder, to = HumanJoint.RightElbow, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.RightLowerArm, from = HumanJoint.RightElbow, to = HumanJoint.RightWrist, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.RightHand, from = HumanJoint.RightWrist, to = HumanJoint.RightHandTip, hint = HintKind.ChestForward },
            new BoneRule { bone = HumanBodyBones.LeftUpperLeg, from = HumanJoint.LeftHip, to = HumanJoint.LeftKnee, hint = HintKind.PelvisForward },
            new BoneRule { bone = HumanBodyBones.LeftLowerLeg, from = HumanJoint.LeftKnee, to = HumanJoint.LeftAnkle, hint = HintKind.PelvisForward },
            // Feet follow the sole (heel -> toe) rather than ankle -> toe: where a rig keeps its toe bone (MMD puts it
            // 45 deg below the ankle, Mixamo 35, a tracked foot runs 15-25) would otherwise become a permanent ankle bend.
            // A flat sole is the one thing every rig and every standing subject agree on.
            new BoneRule { bone = HumanBodyBones.LeftFoot, from = HumanJoint.LeftHeel, to = HumanJoint.LeftToe, hint = HintKind.PelvisUp },
            new BoneRule { bone = HumanBodyBones.RightUpperLeg, from = HumanJoint.RightHip, to = HumanJoint.RightKnee, hint = HintKind.PelvisForward },
            new BoneRule { bone = HumanBodyBones.RightLowerLeg, from = HumanJoint.RightKnee, to = HumanJoint.RightAnkle, hint = HintKind.PelvisForward },
            new BoneRule { bone = HumanBodyBones.RightFoot, from = HumanJoint.RightHeel, to = HumanJoint.RightToe, hint = HintKind.PelvisUp },
        };

        /// <summary>One finger bone driven by two hand landmarks.</summary>
        sealed class FingerState
        {
            public Transform transform;
            public int from;             // hand landmark index
            public int to;
            public bool isLeft;
            public Quaternion restWorldRotation;
            public Quaternion restFrame;
        }

        // Mecanim finger bones and the MediaPipe landmark pair that spans each one.
        static readonly (HumanBodyBones bone, int from, int to)[] k_LeftFingerBones =
        {
            (HumanBodyBones.LeftThumbProximal, 1, 2), (HumanBodyBones.LeftThumbIntermediate, 2, 3), (HumanBodyBones.LeftThumbDistal, 3, 4),
            (HumanBodyBones.LeftIndexProximal, 5, 6), (HumanBodyBones.LeftIndexIntermediate, 6, 7), (HumanBodyBones.LeftIndexDistal, 7, 8),
            (HumanBodyBones.LeftMiddleProximal, 9, 10), (HumanBodyBones.LeftMiddleIntermediate, 10, 11), (HumanBodyBones.LeftMiddleDistal, 11, 12),
            (HumanBodyBones.LeftRingProximal, 13, 14), (HumanBodyBones.LeftRingIntermediate, 14, 15), (HumanBodyBones.LeftRingDistal, 15, 16),
            (HumanBodyBones.LeftLittleProximal, 17, 18), (HumanBodyBones.LeftLittleIntermediate, 18, 19), (HumanBodyBones.LeftLittleDistal, 19, 20),
        };
        static readonly (HumanBodyBones bone, int from, int to)[] k_RightFingerBones =
        {
            (HumanBodyBones.RightThumbProximal, 1, 2), (HumanBodyBones.RightThumbIntermediate, 2, 3), (HumanBodyBones.RightThumbDistal, 3, 4),
            (HumanBodyBones.RightIndexProximal, 5, 6), (HumanBodyBones.RightIndexIntermediate, 6, 7), (HumanBodyBones.RightIndexDistal, 7, 8),
            (HumanBodyBones.RightMiddleProximal, 9, 10), (HumanBodyBones.RightMiddleIntermediate, 10, 11), (HumanBodyBones.RightMiddleDistal, 11, 12),
            (HumanBodyBones.RightRingProximal, 13, 14), (HumanBodyBones.RightRingIntermediate, 14, 15), (HumanBodyBones.RightRingDistal, 15, 16),
            (HumanBodyBones.RightLittleProximal, 17, 18), (HumanBodyBones.RightLittleIntermediate, 18, 19), (HumanBodyBones.RightLittleDistal, 19, 20),
        };

        readonly List<FingerState> m_Fingers = new List<FingerState>();
        Quaternion m_LeftRestPalm, m_RightRestPalm;
        bool m_HasLeftFingers, m_HasRightFingers;
        /// <summary>True when the rig has finger bones and they were bound.</summary>
        public bool HasFingers => m_HasLeftFingers || m_HasRightFingers;

        readonly Animator m_Animator;
        readonly Transform m_Root;
        readonly Transform m_Hips;
        readonly List<BoneState> m_Bones = new List<BoneState>();
        readonly Vector3[] m_RestJoints = new Vector3[JointTopology.Count];
        readonly PoseFrame m_RestPose = new PoseFrame();
        Quaternion m_RestHipsRotation;
        Quaternion m_RestHipsFrame;
        Vector3 m_RestHipsLocalPosition;
        float m_RigLegLength;
        float m_RestPelvisAboveFeet;

        public float ConfidenceFloor = 0.35f;
        /// <summary>How much of the subject's sideways/depth movement to apply, in metres per metre.</summary>
        public float TravelScale = 1f;
        public bool ApplyTravel = true;
        /// <summary>Rig-space units the hips may wander from the rest position sideways or in depth.</summary>
        public float MaxTravel = 1.5f;
        /// <summary>Subject-space metres of jump (PoseFrame.Lift) that are applied at most.</summary>
        public float MaxLift = 1.2f;
        /// <summary>Uniform scale applied to the Head bone; below 1 tames oversized stylised skulls so limbs stay visible.</summary>
        public float HeadScale = 1f;
        /// <summary>How much of the subject's own head turn (relative to the torso) the skull follows. 1 = full, 0 = head locked to the chest.</summary>
        public float HeadFollow = 0.7f;
        Transform m_Head;
        Transform m_Neck;
        Vector3 m_RestHeadScale = Vector3.one;
        Quaternion m_RestHeadFrame;
        Quaternion m_RestHeadWorld, m_RestNeckWorld;

        public bool IsValid { get; }

        public HumanoidRetargeter(Animator animator)
        {
            m_Animator = animator;
            if (animator == null || !animator.isHuman)
            {
                IsValid = false;
                return;
            }
            m_Root = animator.transform;
            m_Hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (m_Hips == null)
            {
                IsValid = false;
                return;
            }
            m_Head = animator.GetBoneTransform(HumanBodyBones.Head);
            m_Neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            if (m_Head != null)
            {
                m_RestHeadScale = m_Head.localScale;
                m_RestHeadWorld = m_Head.rotation;
            }
            if (m_Neck != null) m_RestNeckWorld = m_Neck.rotation;

            // Rest pose: whatever the rig is in now (T- or A-pose from the FBX). Build the equivalent
            // canonical joint set from the bone positions so both sides use the same frame construction.
            CaptureRestJoints();
            m_RestHipsRotation = m_Hips.rotation;
            m_RestHipsLocalPosition = m_Root.InverseTransformPoint(m_Hips.position);
            m_RestHipsFrame = HipsFrame(m_RestPose);
            // In the rest pose the skull faces the same way as the chest.
            m_RestHeadFrame = TorsoFrame(m_RestPose);
            m_RigLegLength = PoseTransforms.LegLength(m_RestPose);
            var restPelvis = 0.5f * (m_RestPose[HumanJoint.LeftHip] + m_RestPose[HumanJoint.RightHip]);
            m_RestPelvisAboveFeet = Mathf.Max(0.05f, restPelvis.y - PoseTransforms.LowestFootY(m_RestPose));

            foreach (var rule in k_Rules)
            {
                var t = animator.GetBoneTransform(rule.bone);
                if (t == null) continue;
                var state = new BoneState
                {
                    transform = t,
                    rule = rule,
                    restWorldRotation = t.rotation,
                    restFrame = Frame(m_RestPose, rule),
                };
                m_Bones.Add(state);
            }
            IsValid = m_Bones.Count > 6;

            m_HasLeftFingers = BindFingers(true, k_LeftFingerBones, HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftMiddleProximal, out m_LeftRestPalm);
            m_HasRightFingers = BindFingers(false, k_RightFingerBones, HumanBodyBones.RightHand, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightLittleProximal, HumanBodyBones.RightMiddleProximal, out m_RightRestPalm);
        }

        /// <summary>
        /// Capture the rest frame of each finger bone: direction from the bone to the next bone in the chain
        /// (or continuing the previous segment for the distal bone), with the palm normal as the up hint.
        /// The palm normal is built the same way from rig bones and from hand landmarks so twist stays consistent.
        /// </summary>
        bool BindFingers(bool isLeft, (HumanBodyBones bone, int from, int to)[] table, HumanBodyBones handBone, HumanBodyBones indexBone, HumanBodyBones littleBone, HumanBodyBones middleBone, out Quaternion restPalm)
        {
            restPalm = Quaternion.identity;
            var hand = m_Animator.GetBoneTransform(handBone);
            var index = m_Animator.GetBoneTransform(indexBone);
            var little = m_Animator.GetBoneTransform(littleBone);
            var middle = m_Animator.GetBoneTransform(middleBone);
            if (hand == null || index == null || little == null || middle == null) return false;

            Vector3 L(Transform t) => m_Root.InverseTransformPoint(t.position);
            var handP = L(hand);
            var palmNormal = Vector3.Cross(L(index) - handP, L(little) - handP);
            if (palmNormal.sqrMagnitude < 1e-10f) return false;
            palmNormal.Normalize();
            var palmDir = (L(middle) - handP).normalized;
            restPalm = Quaternion.LookRotation(palmDir, palmNormal);

            var bound = 0;
            for (var i = 0; i < table.Length; i++)
            {
                var (boneId, from, to) = table[i];
                var t = m_Animator.GetBoneTransform(boneId);
                if (t == null) continue;
                // Direction to the next bone in this finger's chain; the distal bone continues its parent's direction.
                Vector3 dir;
                var isDistal = (i % 3) == 2;
                if (!isDistal)
                {
                    var next = m_Animator.GetBoneTransform(table[i + 1].bone);
                    if (next == null) continue;
                    dir = L(next) - L(t);
                }
                else
                {
                    var prev = m_Animator.GetBoneTransform(table[i - 1].bone);
                    if (prev == null) continue;
                    dir = L(t) - L(prev);
                }
                if (dir.sqrMagnitude < 1e-10f) continue;
                var hint = palmNormal;
                if (Mathf.Abs(Vector3.Dot(dir.normalized, hint)) > 0.98f) hint = palmDir;
                m_Fingers.Add(new FingerState
                {
                    transform = t,
                    from = from,
                    to = to,
                    isLeft = isLeft,
                    restWorldRotation = t.rotation,
                    restFrame = Quaternion.LookRotation(dir, hint),
                });
                bound++;
            }
            return bound >= 9;
        }

        static Vector3 PalmNormal(HandFrame hand)
        {
            var n = Vector3.Cross(hand.Landmarks[(int)HandFrame.Landmark.IndexMcp], hand.Landmarks[(int)HandFrame.Landmark.PinkyMcp]);
            return n.sqrMagnitude > 1e-10f ? n.normalized : Vector3.up;
        }

        /// <summary>Drive finger bones (and re-aim the hand bone) from tracked hand landmarks.</summary>
        void ApplyHand(HandFrame hand, bool isLeft)
        {
            if (!hand.Tracked) return;
            var has = isLeft ? m_HasLeftFingers : m_HasRightFingers;
            if (!has) return;

            var palmNormal = PalmNormal(hand);
            var palmDir = hand.Landmarks[(int)HandFrame.Landmark.MiddleMcp];
            if (palmDir.sqrMagnitude > 1e-10f)
            {
                var handBone = m_Animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var restPalm = isLeft ? m_LeftRestPalm : m_RightRestPalm;
                var frame = Quaternion.LookRotation(palmDir, palmNormal);
                var delta = frame * Quaternion.Inverse(restPalm);
                // Rest world rotation of the hand bone lives in m_Bones (LeftHand/RightHand rule).
                foreach (var b in m_Bones)
                {
                    if (b.transform != handBone) continue;
                    handBone.rotation = m_Root.rotation * delta * Quaternion.Inverse(m_Root.rotation) * b.restWorldRotation;
                    break;
                }
            }

            foreach (var f in m_Fingers)
            {
                if (f.isLeft != isLeft) continue;
                var dir = hand.Landmarks[f.to] - hand.Landmarks[f.from];
                if (dir.sqrMagnitude < 1e-10f) continue;
                var hint = palmNormal;
                if (Mathf.Abs(Vector3.Dot(dir.normalized, hint)) > 0.98f) hint = palmDir.normalized;
                var frame = Quaternion.LookRotation(dir, hint);
                var delta = frame * Quaternion.Inverse(f.restFrame);
                f.transform.rotation = m_Root.rotation * delta * Quaternion.Inverse(m_Root.rotation) * f.restWorldRotation;
            }
        }

        void CaptureRestJoints()
        {
            m_RestPose.Clear();
            void Set(HumanJoint j, HumanBodyBones b)
            {
                var t = m_Animator.GetBoneTransform(b);
                if (t == null) return;
                m_RestPose.Joints[(int)j] = m_Root.InverseTransformPoint(t.position);
                m_RestPose.Confidence[(int)j] = 1f;
            }
            Set(HumanJoint.Pelvis, HumanBodyBones.Hips);
            Set(HumanJoint.Spine, HumanBodyBones.Spine);
            Set(HumanJoint.Chest, HumanBodyBones.Chest);
            Set(HumanJoint.Neck, HumanBodyBones.Neck);
            Set(HumanJoint.Head, HumanBodyBones.Head);
            Set(HumanJoint.LeftShoulder, HumanBodyBones.LeftUpperArm);
            Set(HumanJoint.RightShoulder, HumanBodyBones.RightUpperArm);
            Set(HumanJoint.LeftElbow, HumanBodyBones.LeftLowerArm);
            Set(HumanJoint.RightElbow, HumanBodyBones.RightLowerArm);
            Set(HumanJoint.LeftWrist, HumanBodyBones.LeftHand);
            Set(HumanJoint.RightWrist, HumanBodyBones.RightHand);
            Set(HumanJoint.LeftHandTip, HumanBodyBones.LeftMiddleProximal);
            Set(HumanJoint.RightHandTip, HumanBodyBones.RightMiddleProximal);
            Set(HumanJoint.LeftHip, HumanBodyBones.LeftUpperLeg);
            Set(HumanJoint.RightHip, HumanBodyBones.RightUpperLeg);
            Set(HumanJoint.LeftKnee, HumanBodyBones.LeftLowerLeg);
            Set(HumanJoint.RightKnee, HumanBodyBones.RightLowerLeg);
            Set(HumanJoint.LeftAnkle, HumanBodyBones.LeftFoot);
            Set(HumanJoint.RightAnkle, HumanBodyBones.RightFoot);
            Set(HumanJoint.LeftToe, HumanBodyBones.LeftToes);
            Set(HumanJoint.RightToe, HumanBodyBones.RightToes);

            // Fill anything the rig lacks (no chest/neck/toes/finger bones) from neighbours.
            var p = m_RestPose;
            if (p.ConfidenceOf(HumanJoint.Chest) == 0) p[HumanJoint.Chest] = Vector3.Lerp(p[HumanJoint.Spine], 0.5f * (p[HumanJoint.LeftShoulder] + p[HumanJoint.RightShoulder]), 0.8f);
            if (p.ConfidenceOf(HumanJoint.Neck) == 0) p[HumanJoint.Neck] = 0.5f * (p[HumanJoint.LeftShoulder] + p[HumanJoint.RightShoulder]);
            if (p.ConfidenceOf(HumanJoint.Head) == 0) p[HumanJoint.Head] = p[HumanJoint.Neck] + Vector3.up * 0.15f;
            if (p.ConfidenceOf(HumanJoint.LeftHandTip) == 0) p[HumanJoint.LeftHandTip] = p[HumanJoint.LeftWrist] + (p[HumanJoint.LeftWrist] - p[HumanJoint.LeftElbow]).normalized * 0.08f;
            if (p.ConfidenceOf(HumanJoint.RightHandTip) == 0) p[HumanJoint.RightHandTip] = p[HumanJoint.RightWrist] + (p[HumanJoint.RightWrist] - p[HumanJoint.RightElbow]).normalized * 0.08f;
            if (p.ConfidenceOf(HumanJoint.LeftToe) == 0) p[HumanJoint.LeftToe] = p[HumanJoint.LeftAnkle] + Vector3.forward * 0.12f;
            if (p.ConfidenceOf(HumanJoint.RightToe) == 0) p[HumanJoint.RightToe] = p[HumanJoint.RightAnkle] + Vector3.forward * 0.12f;
            // Rest soles: a rig stands flat-footed, so the heel sits under the ankle at toe height and the sole runs level.
            RestHeel(p, HumanJoint.LeftAnkle, HumanJoint.LeftToe, HumanJoint.LeftHeel);
            RestHeel(p, HumanJoint.RightAnkle, HumanJoint.RightToe, HumanJoint.RightHeel);
            for (var i = 0; i < JointTopology.Count; i++) p.Confidence[i] = 1f;
            p.Tracked = true;
        }

        static void RestHeel(PoseFrame p, HumanJoint ankle, HumanJoint toe, HumanJoint heel)
        {
            var a = p[ankle];
            var t = p[toe];
            var forward = new Vector3(t.x - a.x, 0f, t.z - a.z);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            p[heel] = new Vector3(a.x, t.y, a.z) - forward * 0.04f;
        }

        // ---- frames ----
        static Vector3 PelvisForward(PoseFrame f)
        {
            var right = f[HumanJoint.RightHip] - f[HumanJoint.LeftHip];
            var up = f[HumanJoint.Chest] - f[HumanJoint.Pelvis];
            var fwd = Vector3.Cross(right, up);
            return fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        }

        static Vector3 ChestForward(PoseFrame f)
        {
            var right = f[HumanJoint.RightShoulder] - f[HumanJoint.LeftShoulder];
            var up = f[HumanJoint.Neck] - f[HumanJoint.Spine];
            var fwd = Vector3.Cross(right, up);
            return fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        }

        static Vector3 HeadForward(PoseFrame f)
        {
            if (f.ConfidenceOf(HumanJoint.LeftEar) > 0.3f && f.ConfidenceOf(HumanJoint.RightEar) > 0.3f && f.ConfidenceOf(HumanJoint.Nose) > 0.3f)
            {
                var earMid = 0.5f * (f[HumanJoint.LeftEar] + f[HumanJoint.RightEar]);
                var fwd = f[HumanJoint.Nose] - earMid;
                if (fwd.sqrMagnitude > 1e-6f) return fwd.normalized;
            }
            return ChestForward(f);
        }

        /// <summary>Right/up/forward frame from two side points and a forward hint (frame handedness matches Unity's).</summary>
        static Quaternion FrameFromRightAndForward(Vector3 right, Vector3 forwardHint)
        {
            if (right.sqrMagnitude < 1e-8f || forwardHint.sqrMagnitude < 1e-8f) return Quaternion.identity;
            var up = Vector3.Cross(forwardHint, right);
            if (up.sqrMagnitude < 1e-8f) return Quaternion.identity;
            up.Normalize();
            var forward = Vector3.Cross(right, up).normalized;
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>Torso orientation from the shoulders and the chest facing.</summary>
        static Quaternion TorsoFrame(PoseFrame f)
        {
            return FrameFromRightAndForward(f[HumanJoint.RightShoulder] - f[HumanJoint.LeftShoulder], ChestForward(f));
        }

        /// <summary>
        /// Head orientation from the face: the ear line gives the side axis, eyes (or nose) give the facing.
        /// Using the eyes rather than the nose avoids a permanent nod, since the nose sits below the ear line.
        /// </summary>
        static bool TryFaceFrame(PoseFrame f, out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (f.ConfidenceOf(HumanJoint.LeftEar) < 0.3f || f.ConfidenceOf(HumanJoint.RightEar) < 0.3f) return false;
            var earMid = 0.5f * (f[HumanJoint.LeftEar] + f[HumanJoint.RightEar]);
            var right = f[HumanJoint.RightEar] - f[HumanJoint.LeftEar];
            Vector3 forward;
            if (f.ConfidenceOf(HumanJoint.LeftEye) > 0.3f && f.ConfidenceOf(HumanJoint.RightEye) > 0.3f)
                forward = 0.5f * (f[HumanJoint.LeftEye] + f[HumanJoint.RightEye]) - earMid;
            else if (f.ConfidenceOf(HumanJoint.Nose) > 0.3f)
                forward = f[HumanJoint.Nose] - earMid;
            else return false;
            if (forward.sqrMagnitude < 1e-6f || right.sqrMagnitude < 1e-6f) return false;
            frame = FrameFromRightAndForward(right, forward);
            return true;
        }

        /// <summary>
        /// Aim the skull: follow the torso fully, then add a damped share of the head's own turn relative to it.
        /// Returns false if the face isn't visible (the generic neck/head rules then apply).
        /// </summary>
        bool ApplyHead(PoseFrame pose)
        {
            if (m_Head == null || !TryFaceFrame(pose, out var face)) return false;
            var torso = TorsoFrame(pose);
            var target = Quaternion.Slerp(torso, face, Mathf.Clamp01(HeadFollow));
            var delta = target * Quaternion.Inverse(m_RestHeadFrame);
            m_Head.rotation = m_Root.rotation * delta * Quaternion.Inverse(m_Root.rotation) * m_RestHeadWorld;
            if (m_Neck != null)
            {
                var neckDelta = Quaternion.Slerp(torso * Quaternion.Inverse(m_RestHeadFrame), delta, 0.5f);
                m_Neck.rotation = m_Root.rotation * neckDelta * Quaternion.Inverse(m_Root.rotation) * m_RestNeckWorld;
            }
            return true;
        }

        static Vector3 Hint(PoseFrame f, HintKind kind)
        {
            switch (kind)
            {
                case HintKind.PelvisForward: return PelvisForward(f);
                case HintKind.ChestForward: return ChestForward(f);
                case HintKind.HeadForward: return HeadForward(f);
                case HintKind.HipsRight: return (f[HumanJoint.RightHip] - f[HumanJoint.LeftHip]).normalized;
                case HintKind.ShouldersRight: return (f[HumanJoint.RightShoulder] - f[HumanJoint.LeftShoulder]).normalized;
                case HintKind.PelvisUp:
                {
                    var up = f[HumanJoint.Chest] - f[HumanJoint.Pelvis];
                    return up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
                }
            }
            return Vector3.forward;
        }

        static Quaternion Frame(PoseFrame f, BoneRule rule)
        {
            var dir = f[rule.to] - f[rule.from];
            if (dir.sqrMagnitude < 1e-8f) return Quaternion.identity;
            var hint = Hint(f, rule.hint);
            // Guard against hint parallel to dir (e.g. forearm pointing straight forward).
            if (Mathf.Abs(Vector3.Dot(dir.normalized, hint)) > 0.98f) hint = Vector3.Cross(dir, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.up : Vector3.right;
            return Quaternion.LookRotation(dir, hint);
        }

        static Quaternion HipsFrame(PoseFrame f)
        {
            var fwd = PelvisForward(f);
            var up = f[HumanJoint.Chest] - f[HumanJoint.Pelvis];
            if (up.sqrMagnitude < 1e-8f) up = Vector3.up;
            return Quaternion.LookRotation(fwd, up);
        }

        /// <summary>Pose the rig from a canonical frame. Positions are relative to the rig root.</summary>
        public void Apply(PoseFrame pose)
        {
            if (!IsValid || !PoseTransforms.HasBody(pose, ConfidenceFloor)) return;

            // Root travel + hip height: scale the subject to the rig using leg length. The pelvis is measured
            // between the upper-leg bones (the Hips bone itself may sit at the floor on some rigs), and the
            // Hips bone is shifted by the change in pelvis height so crouches and jumps carry over.
            var subjectLeg = Mathf.Max(0.2f, pose.LegLength > 0f ? pose.LegLength : PoseTransforms.LegLength(pose));
            var scale = m_RigLegLength / subjectLeg;
            var subjectHipAboveFeet = (pose.HipHeight > 0f ? pose.HipHeight : pose[HumanJoint.Pelvis].y - PoseTransforms.LowestFootY(pose)) * scale;
            var deltaY = subjectHipAboveFeet - m_RestPelvisAboveFeet;
            deltaY = Mathf.Clamp(deltaY, -0.65f * m_RestPelvisAboveFeet, 0.7f * m_RestPelvisAboveFeet);
            var travel = ApplyTravel ? new Vector3(pose.RootOffset.x, 0f, pose.RootOffset.z) * (scale * TravelScale) : Vector3.zero;
            travel.x = Mathf.Clamp(travel.x, -MaxTravel, MaxTravel);
            travel.z = Mathf.Clamp(travel.z, -MaxTravel, MaxTravel);
            // A jump lifts the whole figure: the floor tracker's lift is in subject metres, scaled like the travel.
            var lift = Mathf.Clamp(pose.Lift, 0f, MaxLift) * scale;
            var hipsLocal = new Vector3(m_RestHipsLocalPosition.x + travel.x, m_RestHipsLocalPosition.y + deltaY + lift, m_RestHipsLocalPosition.z + travel.z);
            m_Hips.position = m_Root.TransformPoint(hipsLocal);

            var hipsFrame = HipsFrame(pose);
            m_Hips.rotation = m_Root.rotation * (hipsFrame * Quaternion.Inverse(m_RestHipsFrame)) * Quaternion.Inverse(m_Root.rotation) * m_RestHipsRotation;

            // Parent-first order is guaranteed by k_Rules ordering (spine before limbs etc.).
            // The neck and head are aimed from the face when it is visible; otherwise their generic rules run.
            var headDone = ApplyHead(pose);
            foreach (var b in m_Bones)
            {
                if (headDone && (b.rule.bone == HumanBodyBones.Neck || b.rule.bone == HumanBodyBones.Head)) continue;
                if (pose.ConfidenceOf(b.rule.from) < ConfidenceFloor || pose.ConfidenceOf(b.rule.to) < ConfidenceFloor) continue;
                var frame = Frame(pose, b.rule);
                var delta = frame * Quaternion.Inverse(b.restFrame);
                b.transform.rotation = m_Root.rotation * delta * Quaternion.Inverse(m_Root.rotation) * b.restWorldRotation;
            }
            if (headDone) ApplyHead(pose); // re-apply after the chest moved so the skull sits on the posed neck

            if (m_Head != null) m_Head.localScale = m_RestHeadScale * HeadScale;

            // Fingers last: they are children of the hand bones posed above.
            ApplyHand(pose.LeftHand, true);
            ApplyHand(pose.RightHand, false);
        }

        /// <summary>Return the rig to its captured rest pose.</summary>
        public void ResetToRest()
        {
            if (!IsValid) return;
            m_Hips.position = m_Root.TransformPoint(m_RestHipsLocalPosition);
            m_Hips.rotation = m_RestHipsRotation;
            foreach (var b in m_Bones) b.transform.rotation = b.restWorldRotation;
            foreach (var f in m_Fingers) f.transform.rotation = f.restWorldRotation;
            if (m_Head != null) m_Head.localScale = m_RestHeadScale;
        }
    }
}
