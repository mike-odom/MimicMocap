namespace MimicMocap.Maxine
{
    /// <summary>
    /// The 34-keypoint order of the Maxine AR SDK legacy Body Pose feature
    /// (docs: Appendix B, "3D Body Pose Keypoint Format").
    /// </summary>
    public enum MaxineJoint
    {
        Pelvis = 0,
        LeftHip = 1,
        RightHip = 2,
        Torso = 3,
        LeftKnee = 4,
        RightKnee = 5,
        Neck = 6,
        LeftAnkle = 7,
        RightAnkle = 8,
        LeftBigToe = 9,
        RightBigToe = 10,
        LeftSmallToe = 11,
        RightSmallToe = 12,
        LeftHeel = 13,
        RightHeel = 14,
        Nose = 15,
        LeftEye = 16,
        RightEye = 17,
        LeftEar = 18,
        RightEar = 19,
        LeftShoulder = 20,
        RightShoulder = 21,
        LeftElbow = 22,
        RightElbow = 23,
        LeftWrist = 24,
        RightWrist = 25,
        LeftPinkyKnuckle = 26,
        RightPinkyKnuckle = 27,
        LeftMiddleTip = 28,
        RightMiddleTip = 29,
        LeftIndexKnuckle = 30,
        RightIndexKnuckle = 31,
        LeftThumbTip = 32,
        RightThumbTip = 33,
        Count = 34
    }

    public static class MaxineLayout
    {
        /// <summary>Canonical joint for each Maxine joint, or -1 if it has no direct equivalent.</summary>
        public static readonly int[] ToCanonical = Build();

        static int[] Build()
        {
            var m = new int[(int)MaxineJoint.Count];
            for (var i = 0; i < m.Length; i++) m[i] = -1;
            void Map(MaxineJoint a, HumanJoint b) => m[(int)a] = (int)b;

            Map(MaxineJoint.Pelvis, HumanJoint.Pelvis);
            Map(MaxineJoint.LeftHip, HumanJoint.LeftHip);
            Map(MaxineJoint.RightHip, HumanJoint.RightHip);
            Map(MaxineJoint.Torso, HumanJoint.Spine);
            Map(MaxineJoint.LeftKnee, HumanJoint.LeftKnee);
            Map(MaxineJoint.RightKnee, HumanJoint.RightKnee);
            Map(MaxineJoint.Neck, HumanJoint.Neck);
            Map(MaxineJoint.LeftAnkle, HumanJoint.LeftAnkle);
            Map(MaxineJoint.RightAnkle, HumanJoint.RightAnkle);
            Map(MaxineJoint.LeftBigToe, HumanJoint.LeftToe);
            Map(MaxineJoint.RightBigToe, HumanJoint.RightToe);
            Map(MaxineJoint.LeftHeel, HumanJoint.LeftHeel);
            Map(MaxineJoint.RightHeel, HumanJoint.RightHeel);
            Map(MaxineJoint.Nose, HumanJoint.Nose);
            Map(MaxineJoint.LeftEye, HumanJoint.LeftEye);
            Map(MaxineJoint.RightEye, HumanJoint.RightEye);
            Map(MaxineJoint.LeftEar, HumanJoint.LeftEar);
            Map(MaxineJoint.RightEar, HumanJoint.RightEar);
            Map(MaxineJoint.LeftShoulder, HumanJoint.LeftShoulder);
            Map(MaxineJoint.RightShoulder, HumanJoint.RightShoulder);
            Map(MaxineJoint.LeftElbow, HumanJoint.LeftElbow);
            Map(MaxineJoint.RightElbow, HumanJoint.RightElbow);
            Map(MaxineJoint.LeftWrist, HumanJoint.LeftWrist);
            Map(MaxineJoint.RightWrist, HumanJoint.RightWrist);
            Map(MaxineJoint.LeftMiddleTip, HumanJoint.LeftHandTip);
            Map(MaxineJoint.RightMiddleTip, HumanJoint.RightHandTip);
            Map(MaxineJoint.LeftThumbTip, HumanJoint.LeftThumb);
            Map(MaxineJoint.RightThumbTip, HumanJoint.RightThumb);
            Map(MaxineJoint.LeftIndexKnuckle, HumanJoint.LeftIndex);
            Map(MaxineJoint.RightIndexKnuckle, HumanJoint.RightIndex);
            Map(MaxineJoint.LeftPinkyKnuckle, HumanJoint.LeftPinky);
            Map(MaxineJoint.RightPinkyKnuckle, HumanJoint.RightPinky);
            return m;
        }
    }
}
