using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// One estimated pose. Joint positions use the canonical layout in <see cref="HumanJoint"/>.
    /// Reused between frames to avoid allocation; copy if you need to keep one.
    /// </summary>
    public sealed class PoseFrame
    {
        public readonly Vector3[] Joints = new Vector3[JointTopology.Count];
        public readonly float[] Confidence = new float[JointTopology.Count];

        /// <summary>Optional per-joint local rotations (only some sources provide these).</summary>
        public readonly Quaternion[] Rotations = new Quaternion[JointTopology.Count];
        public bool HasRotations;

        /// <summary>True when a person was found with usable confidence.</summary>
        public bool Tracked;

        /// <summary>True when this is the last pose carried through a brief tracking loss rather than a fresh estimate (<see cref="PoseSmoother"/>).</summary>
        public bool Held;

        /// <summary>Overall pose confidence 0..1.</summary>
        public float Score;

        /// <summary>Time.timeAsDouble when the source frame was captured.</summary>
        public double Timestamp;

        /// <summary>Normalised (0..1) image-space bounding box of the person, for the HUD.</summary>
        public Rect ImageBox;

        /// <summary>
        /// Where the subject's pelvis is relative to the camera's centre line, in metres, canonical axes.
        /// Joints are hip-centred so this carries the sideways/depth travel. Zero if unknown.
        /// </summary>
        public Vector3 RootOffset;

        /// <summary>Metres the subject's lowest foot is above the floor (a jump), from <see cref="FloorTracker"/>. Zero when grounded or unknown.</summary>
        public float Lift;

        /// <summary>The subject's leg length (hip to ankle, metres) smoothed over about a second by <see cref="PoseSmoother"/>, which the views scale the figure by. Zero when unknown.</summary>
        public float LegLength;

        /// <summary>How high the pelvis sits above the lowest foot (metres), speed-limited by <see cref="PoseSmoother"/>; the views stand the figure on the floor by it. Zero when unknown.</summary>
        public float HipHeight;

        /// <summary>Normalised (0..1, y-up) image positions of the joints the source saw, for hand crops and overlays.</summary>
        public readonly Vector2[] ImageJoints = new Vector2[JointTopology.Count];
        public bool HasImageJoints;

        /// <summary>Optional finger detail, relative to each wrist. Filled by the hand tracker.</summary>
        public readonly HandFrame LeftHand = new HandFrame();
        public readonly HandFrame RightHand = new HandFrame();

        public Vector3 this[HumanJoint j]
        {
            get => Joints[(int)j];
            set => Joints[(int)j] = value;
        }

        public float ConfidenceOf(HumanJoint j) => Confidence[(int)j];

        public void CopyTo(PoseFrame other)
        {
            System.Array.Copy(Joints, other.Joints, Joints.Length);
            System.Array.Copy(Confidence, other.Confidence, Confidence.Length);
            System.Array.Copy(Rotations, other.Rotations, Rotations.Length);
            other.HasRotations = HasRotations;
            other.Tracked = Tracked;
            other.Held = Held;
            other.Score = Score;
            other.Timestamp = Timestamp;
            other.ImageBox = ImageBox;
            other.RootOffset = RootOffset;
            other.Lift = Lift;
            other.LegLength = LegLength;
            other.HipHeight = HipHeight;
            System.Array.Copy(ImageJoints, other.ImageJoints, ImageJoints.Length);
            other.HasImageJoints = HasImageJoints;
            LeftHand.CopyTo(other.LeftHand);
            RightHand.CopyTo(other.RightHand);
        }

        public void Clear()
        {
            for (var i = 0; i < Joints.Length; i++)
            {
                Joints[i] = Vector3.zero;
                Confidence[i] = 0f;
                Rotations[i] = Quaternion.identity;
            }
            HasRotations = false;
            Tracked = false;
            Held = false;
            Score = 0f;
            ImageBox = new Rect(0, 0, 0, 0);
            RootOffset = Vector3.zero;
            Lift = 0f;
            LegLength = 0f;
            HipHeight = 0f;
            for (var i = 0; i < ImageJoints.Length; i++) ImageJoints[i] = Vector2.zero;
            HasImageJoints = false;
            LeftHand.Clear();
            RightHand.Clear();
        }

        /// <summary>
        /// Fill in torso joints that a source doesn't provide directly, from hips, shoulders and head.
        /// </summary>
        public void SynthesiseTorso()
        {
            var lHip = this[HumanJoint.LeftHip];
            var rHip = this[HumanJoint.RightHip];
            var lSh = this[HumanJoint.LeftShoulder];
            var rSh = this[HumanJoint.RightShoulder];
            var hipC = ConfidenceMin(HumanJoint.LeftHip, HumanJoint.RightHip);
            var shC = ConfidenceMin(HumanJoint.LeftShoulder, HumanJoint.RightShoulder);

            var pelvis = 0.5f * (lHip + rHip);
            var chestTop = 0.5f * (lSh + rSh);
            SetIfLow(HumanJoint.Pelvis, pelvis, hipC);
            SetIfLow(HumanJoint.Spine, Vector3.Lerp(pelvis, chestTop, 0.4f), Mathf.Min(hipC, shC));
            SetIfLow(HumanJoint.Chest, Vector3.Lerp(pelvis, chestTop, 0.85f), Mathf.Min(hipC, shC));
            SetIfLow(HumanJoint.Neck, chestTop, shC);

            var earC = ConfidenceMin(HumanJoint.LeftEar, HumanJoint.RightEar);
            var head = 0.5f * (this[HumanJoint.LeftEar] + this[HumanJoint.RightEar]);
            if (earC < 0.3f)
            {
                // No ears: put the head above the neck along the spine direction.
                var up = (chestTop - pelvis).normalized;
                head = chestTop + up * 0.18f;
                earC = shC * 0.8f;
            }
            SetIfLow(HumanJoint.Head, head, earC);
        }

        float ConfidenceMin(HumanJoint a, HumanJoint b) => Mathf.Min(ConfidenceOf(a), ConfidenceOf(b));

        void SetIfLow(HumanJoint j, Vector3 v, float c)
        {
            if (Confidence[(int)j] < c)
            {
                Joints[(int)j] = v;
                Confidence[(int)j] = c;
            }
        }
    }
}
