using UnityEngine;

namespace MimicMocap
{
    /// <summary>Operations on canonical poses shared by the views and retargeter.</summary>
    public static class PoseTransforms
    {
        static readonly (HumanJoint l, HumanJoint r)[] k_Pairs =
        {
            (HumanJoint.LeftEye, HumanJoint.RightEye),
            (HumanJoint.LeftEar, HumanJoint.RightEar),
            (HumanJoint.LeftShoulder, HumanJoint.RightShoulder),
            (HumanJoint.LeftElbow, HumanJoint.RightElbow),
            (HumanJoint.LeftWrist, HumanJoint.RightWrist),
            (HumanJoint.LeftHandTip, HumanJoint.RightHandTip),
            (HumanJoint.LeftThumb, HumanJoint.RightThumb),
            (HumanJoint.LeftIndex, HumanJoint.RightIndex),
            (HumanJoint.LeftPinky, HumanJoint.RightPinky),
            (HumanJoint.LeftHip, HumanJoint.RightHip),
            (HumanJoint.LeftKnee, HumanJoint.RightKnee),
            (HumanJoint.LeftAnkle, HumanJoint.RightAnkle),
            (HumanJoint.LeftHeel, HumanJoint.RightHeel),
            (HumanJoint.LeftToe, HumanJoint.RightToe),
        };

        /// <summary>
        /// Reflect the pose so the subject's left drives the character's right, like a mirror.
        /// Swaps left/right joints and negates X.
        /// </summary>
        public static void Mirror(PoseFrame f)
        {
            foreach (var (l, r) in k_Pairs)
            {
                var li = (int)l;
                var ri = (int)r;
                (f.Joints[li], f.Joints[ri]) = (f.Joints[ri], f.Joints[li]);
                (f.Confidence[li], f.Confidence[ri]) = (f.Confidence[ri], f.Confidence[li]);
                (f.Rotations[li], f.Rotations[ri]) = (f.Rotations[ri], f.Rotations[li]);
                (f.ImageJoints[li], f.ImageJoints[ri]) = (f.ImageJoints[ri], f.ImageJoints[li]);
            }
            for (var i = 0; i < f.Joints.Length; i++)
            {
                var p = f.Joints[i];
                p.x = -p.x;
                f.Joints[i] = p;
                if (f.HasRotations)
                {
                    var q = f.Rotations[i];
                    f.Rotations[i] = new Quaternion(q.x, -q.y, -q.z, q.w);
                }
                var ij = f.ImageJoints[i];
                ij.x = 1f - ij.x;
                f.ImageJoints[i] = ij;
            }

            // Hands: the subject's left hand becomes the character's right hand, reflected.
            var tmp = new HandFrame();
            f.LeftHand.CopyTo(tmp);
            f.RightHand.CopyTo(f.LeftHand);
            tmp.CopyTo(f.RightHand);
            f.LeftHand.MirrorX();
            f.RightHand.MirrorX();
            var box = f.ImageBox;
            f.ImageBox = new Rect(1f - box.xMax, box.y, box.width, box.height);
            var ro = f.RootOffset;
            ro.x = -ro.x;
            f.RootOffset = ro;
        }

        /// <summary>Pelvis, shoulders, knees and ankles have enough confidence to pose a body.</summary>
        public static bool HasBody(PoseFrame f, float floor)
        {
            return f.Tracked
                && f.ConfidenceOf(HumanJoint.LeftHip) >= floor
                && f.ConfidenceOf(HumanJoint.RightHip) >= floor
                && f.ConfidenceOf(HumanJoint.LeftShoulder) >= floor
                && f.ConfidenceOf(HumanJoint.RightShoulder) >= floor;
        }

        /// <summary>Lowest of the two ankles/heels (used to plant the feet on the floor).</summary>
        public static float LowestFootY(PoseFrame f)
        {
            var y = Mathf.Min(f[HumanJoint.LeftAnkle].y, f[HumanJoint.RightAnkle].y);
            y = Mathf.Min(y, f[HumanJoint.LeftHeel].y);
            y = Mathf.Min(y, f[HumanJoint.RightHeel].y);
            return y;
        }

        /// <summary>Average hip-to-ankle length, a stable measure of the subject's size.</summary>
        public static float LegLength(PoseFrame f)
        {
            var l = Vector3.Distance(f[HumanJoint.LeftHip], f[HumanJoint.LeftKnee]) + Vector3.Distance(f[HumanJoint.LeftKnee], f[HumanJoint.LeftAnkle]);
            var r = Vector3.Distance(f[HumanJoint.RightHip], f[HumanJoint.RightKnee]) + Vector3.Distance(f[HumanJoint.RightKnee], f[HumanJoint.RightAnkle]);
            return 0.5f * (l + r);
        }

        public static float TorsoLength(PoseFrame f)
        {
            var hips = 0.5f * (f[HumanJoint.LeftHip] + f[HumanJoint.RightHip]);
            var shoulders = 0.5f * (f[HumanJoint.LeftShoulder] + f[HumanJoint.RightShoulder]);
            return Vector3.Distance(hips, shoulders);
        }
    }
}
