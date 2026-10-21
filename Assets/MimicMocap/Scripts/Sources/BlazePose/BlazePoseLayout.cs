namespace MimicMocap.BlazePose
{
    /// <summary>MediaPipe BlazePose 33-landmark order, plus the 6 auxiliary landmarks (33..38).</summary>
    public enum BlazeLandmark
    {
        Nose = 0,
        LeftEyeInner = 1, LeftEye = 2, LeftEyeOuter = 3,
        RightEyeInner = 4, RightEye = 5, RightEyeOuter = 6,
        LeftEar = 7, RightEar = 8,
        MouthLeft = 9, MouthRight = 10,
        LeftShoulder = 11, RightShoulder = 12,
        LeftElbow = 13, RightElbow = 14,
        LeftWrist = 15, RightWrist = 16,
        LeftPinky = 17, RightPinky = 18,
        LeftIndex = 19, RightIndex = 20,
        LeftThumb = 21, RightThumb = 22,
        LeftHip = 23, RightHip = 24,
        LeftKnee = 25, RightKnee = 26,
        LeftAnkle = 27, RightAnkle = 28,
        LeftHeel = 29, RightHeel = 30,
        LeftFootIndex = 31, RightFootIndex = 32,
        /// <summary>Auxiliary: body centre (mid hips), used for the next-frame ROI.</summary>
        AuxCenter = 33,
        /// <summary>Auxiliary: point above the centre encoding body size and roll, for the ROI.</summary>
        AuxScale = 34,
        Count = 39
    }

    public static class BlazePoseLayout
    {
        public const int BodyLandmarks = 33;
        public const int TotalLandmarks = 39;

        /// <summary>Canonical joint for each BlazePose body landmark, or -1.</summary>
        public static readonly int[] ToCanonical = Build();

        static int[] Build()
        {
            var m = new int[BodyLandmarks];
            for (var i = 0; i < m.Length; i++) m[i] = -1;
            void Map(BlazeLandmark a, HumanJoint b) => m[(int)a] = (int)b;

            Map(BlazeLandmark.Nose, HumanJoint.Nose);
            Map(BlazeLandmark.LeftEye, HumanJoint.LeftEye);
            Map(BlazeLandmark.RightEye, HumanJoint.RightEye);
            Map(BlazeLandmark.LeftEar, HumanJoint.LeftEar);
            Map(BlazeLandmark.RightEar, HumanJoint.RightEar);
            Map(BlazeLandmark.LeftShoulder, HumanJoint.LeftShoulder);
            Map(BlazeLandmark.RightShoulder, HumanJoint.RightShoulder);
            Map(BlazeLandmark.LeftElbow, HumanJoint.LeftElbow);
            Map(BlazeLandmark.RightElbow, HumanJoint.RightElbow);
            Map(BlazeLandmark.LeftWrist, HumanJoint.LeftWrist);
            Map(BlazeLandmark.RightWrist, HumanJoint.RightWrist);
            Map(BlazeLandmark.LeftIndex, HumanJoint.LeftIndex);
            Map(BlazeLandmark.RightIndex, HumanJoint.RightIndex);
            Map(BlazeLandmark.LeftPinky, HumanJoint.LeftPinky);
            Map(BlazeLandmark.RightPinky, HumanJoint.RightPinky);
            Map(BlazeLandmark.LeftThumb, HumanJoint.LeftThumb);
            Map(BlazeLandmark.RightThumb, HumanJoint.RightThumb);
            Map(BlazeLandmark.LeftHip, HumanJoint.LeftHip);
            Map(BlazeLandmark.RightHip, HumanJoint.RightHip);
            Map(BlazeLandmark.LeftKnee, HumanJoint.LeftKnee);
            Map(BlazeLandmark.RightKnee, HumanJoint.RightKnee);
            Map(BlazeLandmark.LeftAnkle, HumanJoint.LeftAnkle);
            Map(BlazeLandmark.RightAnkle, HumanJoint.RightAnkle);
            Map(BlazeLandmark.LeftHeel, HumanJoint.LeftHeel);
            Map(BlazeLandmark.RightHeel, HumanJoint.RightHeel);
            Map(BlazeLandmark.LeftFootIndex, HumanJoint.LeftToe);
            Map(BlazeLandmark.RightFootIndex, HumanJoint.RightToe);
            return m;
        }
    }
}
