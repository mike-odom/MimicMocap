using System;

namespace MimicMocap
{
    /// <summary>
    /// Canonical joint layout every pose source maps into. Positions are metres,
    /// origin at the pelvis, +Y up, +Z toward the camera, +X to the subject's left
    /// as seen by the camera (i.e. camera-space X).
    /// </summary>
    public enum HumanJoint
    {
        Pelvis,
        Spine,
        Chest,
        Neck,
        Head,
        Nose,
        LeftEye,
        RightEye,
        LeftEar,
        RightEar,
        LeftShoulder,
        RightShoulder,
        LeftElbow,
        RightElbow,
        LeftWrist,
        RightWrist,
        LeftHandTip,
        RightHandTip,
        LeftThumb,
        RightThumb,
        LeftHip,
        RightHip,
        LeftKnee,
        RightKnee,
        LeftAnkle,
        RightAnkle,
        LeftHeel,
        RightHeel,
        LeftToe,
        RightToe,
        /// <summary>Index-finger knuckle (used with the wrist and pinky to place the hand crop).</summary>
        LeftIndex,
        RightIndex,
        LeftPinky,
        RightPinky,
        Count
    }

    /// <summary>Parent links and the bone list used by the procedural skeleton view.</summary>
    public static class JointTopology
    {
        public const int Count = (int)HumanJoint.Count;

        /// <summary>Parent of each joint, or -1 for the root.</summary>
        public static readonly int[] Parent = BuildParents();

        /// <summary>Bones drawn by the skeleton view, as (parent, child) pairs.</summary>
        public static readonly (HumanJoint a, HumanJoint b)[] Bones =
        {
            (HumanJoint.Pelvis, HumanJoint.Spine),
            (HumanJoint.Spine, HumanJoint.Chest),
            (HumanJoint.Chest, HumanJoint.Neck),
            (HumanJoint.Neck, HumanJoint.Head),
            (HumanJoint.Chest, HumanJoint.LeftShoulder),
            (HumanJoint.Chest, HumanJoint.RightShoulder),
            (HumanJoint.LeftShoulder, HumanJoint.LeftElbow),
            (HumanJoint.LeftElbow, HumanJoint.LeftWrist),
            (HumanJoint.LeftWrist, HumanJoint.LeftHandTip),
            (HumanJoint.LeftWrist, HumanJoint.LeftThumb),
            (HumanJoint.LeftWrist, HumanJoint.LeftPinky),
            (HumanJoint.RightShoulder, HumanJoint.RightElbow),
            (HumanJoint.RightElbow, HumanJoint.RightWrist),
            (HumanJoint.RightWrist, HumanJoint.RightHandTip),
            (HumanJoint.RightWrist, HumanJoint.RightThumb),
            (HumanJoint.RightWrist, HumanJoint.RightPinky),
            (HumanJoint.Pelvis, HumanJoint.LeftHip),
            (HumanJoint.Pelvis, HumanJoint.RightHip),
            (HumanJoint.LeftHip, HumanJoint.LeftKnee),
            (HumanJoint.LeftKnee, HumanJoint.LeftAnkle),
            (HumanJoint.LeftAnkle, HumanJoint.LeftHeel),
            (HumanJoint.LeftAnkle, HumanJoint.LeftToe),
            (HumanJoint.RightHip, HumanJoint.RightKnee),
            (HumanJoint.RightKnee, HumanJoint.RightAnkle),
            (HumanJoint.RightAnkle, HumanJoint.RightHeel),
            (HumanJoint.RightAnkle, HumanJoint.RightToe),
        };

        static int[] BuildParents()
        {
            var p = new int[Count];
            for (var i = 0; i < Count; i++) p[i] = -1;
            void Set(HumanJoint child, HumanJoint parent) => p[(int)child] = (int)parent;

            Set(HumanJoint.Spine, HumanJoint.Pelvis);
            Set(HumanJoint.Chest, HumanJoint.Spine);
            Set(HumanJoint.Neck, HumanJoint.Chest);
            Set(HumanJoint.Head, HumanJoint.Neck);
            Set(HumanJoint.Nose, HumanJoint.Head);
            Set(HumanJoint.LeftEye, HumanJoint.Head);
            Set(HumanJoint.RightEye, HumanJoint.Head);
            Set(HumanJoint.LeftEar, HumanJoint.Head);
            Set(HumanJoint.RightEar, HumanJoint.Head);
            Set(HumanJoint.LeftShoulder, HumanJoint.Chest);
            Set(HumanJoint.RightShoulder, HumanJoint.Chest);
            Set(HumanJoint.LeftElbow, HumanJoint.LeftShoulder);
            Set(HumanJoint.RightElbow, HumanJoint.RightShoulder);
            Set(HumanJoint.LeftWrist, HumanJoint.LeftElbow);
            Set(HumanJoint.RightWrist, HumanJoint.RightElbow);
            Set(HumanJoint.LeftHandTip, HumanJoint.LeftWrist);
            Set(HumanJoint.RightHandTip, HumanJoint.RightWrist);
            Set(HumanJoint.LeftThumb, HumanJoint.LeftWrist);
            Set(HumanJoint.RightThumb, HumanJoint.RightWrist);
            Set(HumanJoint.LeftIndex, HumanJoint.LeftWrist);
            Set(HumanJoint.RightIndex, HumanJoint.RightWrist);
            Set(HumanJoint.LeftPinky, HumanJoint.LeftWrist);
            Set(HumanJoint.RightPinky, HumanJoint.RightWrist);
            Set(HumanJoint.LeftHip, HumanJoint.Pelvis);
            Set(HumanJoint.RightHip, HumanJoint.Pelvis);
            Set(HumanJoint.LeftKnee, HumanJoint.LeftHip);
            Set(HumanJoint.RightKnee, HumanJoint.RightHip);
            Set(HumanJoint.LeftAnkle, HumanJoint.LeftKnee);
            Set(HumanJoint.RightAnkle, HumanJoint.RightKnee);
            Set(HumanJoint.LeftHeel, HumanJoint.LeftAnkle);
            Set(HumanJoint.RightHeel, HumanJoint.RightAnkle);
            Set(HumanJoint.LeftToe, HumanJoint.LeftAnkle);
            Set(HumanJoint.RightToe, HumanJoint.RightAnkle);
            return p;
        }
    }
}
