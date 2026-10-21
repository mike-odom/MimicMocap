using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// A canned relaxed standing pose (arms slightly out, facing the camera) in the canonical frame,
    /// hip-centred, metres. Used for menu previews, idle display and tests.
    /// </summary>
    public static class StandingPose
    {
        public static void Fill(PoseFrame f)
        {
            f.Clear();
            void Set(HumanJoint j, float x, float y, float z) { f.Joints[(int)j] = new Vector3(x, y, z); f.Confidence[(int)j] = 1f; }
            void Pair(HumanJoint left, HumanJoint right, float x, float y, float z) { Set(left, -x, y, z); Set(right, x, y, z); }

            Set(HumanJoint.Pelvis, 0f, 0f, 0f);
            Set(HumanJoint.Spine, 0f, 0.16f, 0.01f);
            Set(HumanJoint.Chest, 0f, 0.36f, 0.01f);
            Set(HumanJoint.Neck, 0f, 0.50f, 0f);
            Set(HumanJoint.Head, 0f, 0.62f, 0f);
            Set(HumanJoint.Nose, 0f, 0.63f, 0.11f);
            Pair(HumanJoint.LeftEye, HumanJoint.RightEye, 0.032f, 0.665f, 0.09f);
            Pair(HumanJoint.LeftEar, HumanJoint.RightEar, 0.075f, 0.62f, 0f);
            Pair(HumanJoint.LeftShoulder, HumanJoint.RightShoulder, 0.19f, 0.48f, 0f);
            Pair(HumanJoint.LeftElbow, HumanJoint.RightElbow, 0.40f, 0.29f, 0.02f);
            Pair(HumanJoint.LeftWrist, HumanJoint.RightWrist, 0.56f, 0.09f, 0.06f);
            Pair(HumanJoint.LeftHandTip, HumanJoint.RightHandTip, 0.64f, 0.00f, 0.09f);
            Pair(HumanJoint.LeftIndex, HumanJoint.RightIndex, 0.64f, 0.00f, 0.09f);
            Pair(HumanJoint.LeftThumb, HumanJoint.RightThumb, 0.58f, 0.03f, 0.11f);
            Pair(HumanJoint.LeftPinky, HumanJoint.RightPinky, 0.63f, 0.01f, 0.04f);
            Pair(HumanJoint.LeftHip, HumanJoint.RightHip, 0.10f, -0.02f, 0f);
            Pair(HumanJoint.LeftKnee, HumanJoint.RightKnee, 0.11f, -0.47f, 0.01f);
            Pair(HumanJoint.LeftAnkle, HumanJoint.RightAnkle, 0.12f, -0.90f, -0.02f);
            Pair(HumanJoint.LeftHeel, HumanJoint.RightHeel, 0.12f, -0.95f, -0.06f);
            Pair(HumanJoint.LeftToe, HumanJoint.RightToe, 0.13f, -0.95f, 0.14f);
            f.Tracked = true;
            f.Score = 1f;
            f.Timestamp = 0;
        }
    }
}
