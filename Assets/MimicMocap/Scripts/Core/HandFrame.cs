using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// 21 MediaPipe hand landmarks for one hand. <see cref="Landmarks"/> are in the canonical frame,
    /// metres, relative to the wrist (landmark 0), so they can be attached to whichever wrist position the body gives.
    /// </summary>
    public sealed class HandFrame
    {
        public const int LandmarkCount = 21;

        /// <summary>MediaPipe hand landmark order.</summary>
        public enum Landmark
        {
            Wrist = 0,
            ThumbCmc = 1, ThumbMcp = 2, ThumbIp = 3, ThumbTip = 4,
            IndexMcp = 5, IndexPip = 6, IndexDip = 7, IndexTip = 8,
            MiddleMcp = 9, MiddlePip = 10, MiddleDip = 11, MiddleTip = 12,
            RingMcp = 13, RingPip = 14, RingDip = 15, RingTip = 16,
            PinkyMcp = 17, PinkyPip = 18, PinkyDip = 19, PinkyTip = 20,
        }

        /// <summary>Finger and palm bones as (parent, child) landmark pairs.</summary>
        public static readonly (int a, int b)[] Bones =
        {
            (0, 1), (1, 2), (2, 3), (3, 4),
            (0, 5), (5, 6), (6, 7), (7, 8),
            (5, 9), (9, 10), (10, 11), (11, 12),
            (9, 13), (13, 14), (14, 15), (15, 16),
            (13, 17), (17, 18), (18, 19), (19, 20),
            (0, 17),
        };

        /// <summary>Anatomical bones: metacarpals fan out from the wrist, then three phalanges per finger (two plus the thumb's metacarpal).</summary>
        public static readonly (int a, int b)[] SkeletonBones =
        {
            (0, 1), (1, 2), (2, 3), (3, 4),
            (0, 5), (5, 6), (6, 7), (7, 8),
            (0, 9), (9, 10), (10, 11), (11, 12),
            (0, 13), (13, 14), (14, 15), (15, 16),
            (0, 17), (17, 18), (18, 19), (19, 20),
        };

        public readonly Vector3[] Landmarks = new Vector3[LandmarkCount];
        /// <summary>Normalised (0..1, y-up) image positions, for debugging overlays.</summary>
        public readonly Vector2[] ImageLandmarks = new Vector2[LandmarkCount];
        /// <summary>False when the hand was synthesised (body-driven) and <see cref="ImageLandmarks"/> are stale.</summary>
        public bool HasImageLandmarks;
        public bool Tracked;
        public float Score;
        public double Timestamp;

        public void CopyTo(HandFrame other)
        {
            System.Array.Copy(Landmarks, other.Landmarks, LandmarkCount);
            System.Array.Copy(ImageLandmarks, other.ImageLandmarks, LandmarkCount);
            other.HasImageLandmarks = HasImageLandmarks;
            other.Tracked = Tracked;
            other.Score = Score;
            other.Timestamp = Timestamp;
        }

        public void Clear()
        {
            for (var i = 0; i < LandmarkCount; i++)
            {
                Landmarks[i] = Vector3.zero;
                ImageLandmarks[i] = Vector2.zero;
            }
            HasImageLandmarks = false;
            Tracked = false;
            Score = 0f;
        }

        /// <summary>Reflect across X (used when mirroring the whole pose).</summary>
        public void MirrorX()
        {
            for (var i = 0; i < LandmarkCount; i++)
            {
                var p = Landmarks[i];
                p.x = -p.x;
                Landmarks[i] = p;
                var q = ImageLandmarks[i];
                q.x = 1f - q.x;
                ImageLandmarks[i] = q;
            }
        }
    }
}
