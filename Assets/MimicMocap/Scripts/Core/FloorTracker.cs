using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Where the floor is under the subject, so a jump can lift the avatar instead of being cancelled by planting
    /// the feet. Sources report the pelvis's vertical position in their own frame (RootOffset.y: absolute camera space
    /// for Maxine, image height in metres for BlazePose, the sender's world for VMC); none of them knows the floor,
    /// and a camera that looks down at the sidewalk sees the floor change with distance. So the floor is simply where
    /// the lowest foot was while the subject was grounded, followed quickly (a few tenths of a second) so walking
    /// toward or away from a tilted camera never reads as a lift. A jump is told apart by its launch: the foot rises
    /// several centimetres within a tenth of a second, which walking never does. While airborne the floor is frozen
    /// and the lift is the foot's height above it; landing (or anything that stays up longer than a jump can) grounds
    /// the tracker again and the floor resumes following.
    /// </summary>
    public sealed class FloorTracker
    {
        /// <summary>Lift below this is still standing (metres).</summary>
        public float DeadBand = 0.03f;
        /// <summary>A foot this close to the floor is on it (metres).</summary>
        public float GroundBand = 0.06f;
        /// <summary>Rise over the last <see cref="LaunchFrames"/> frames that counts as a jump launch (metres).</summary>
        public float LaunchRise = 0.08f;
        public int LaunchFrames = 3;
        /// <summary>Airborne for longer than this is not a jump: the floor follows the feet again (seconds).</summary>
        public float MaxJumpSeconds = 1.2f;
        /// <summary>Time constant for the floor following grounded feet upward (seconds); downward it snaps.</summary>
        public float FollowSeconds = 0.2f;
        public float MinFootConfidence = 0.4f;
        /// <summary>Lift smoothing: fast, so a jump is not squashed, just de-jittered.</summary>
        public float LiftMinCutoff = 4f, LiftBeta = 1f;
        /// <summary>
        /// Fastest a foot can rise or fall (m/s). A jump takes off at about 3 m/s; a frame whose foot moved faster is a glitch
        /// in the 3D estimate and is skipped. On the two-person install footage such one-frame spikes (0.7 m in 33 ms) read as
        /// take-offs and threw the avatar 1.3 m up for a frame, off the top of the screen.
        /// </summary>
        public float MaxFootSpeed = 4f;
        /// <summary>A jump in foot height that persists this many frames is real (someone else, a step onto something) and is taken.</summary>
        public int MaxSpikeFrames = 3;
        /// <summary>
        /// Time constant (s) the shown lift eases toward its target with, up at take-off and down at landing. Airborne is
        /// only recognised once the foot has risen 8 cm, so without the ease the figure popped up by that much in one frame
        /// and snapped down on landing: small dance hops read as a two-frame jerk of 10% of the figure's height.
        /// 0.04 s covers most of a step in two to three frames.
        /// </summary>
        public float EaseSeconds = 0.04f;

        readonly float[] m_History = new float[8];
        int m_Count;
        float m_Floor;
        bool m_Has, m_Airborne;
        float m_AirTime;
        readonly OneEuroFilter m_LiftFilter = new OneEuroFilter(4f, 1f, 1f);
        bool m_HasLastFoot;
        float m_LastFootY;
        double m_EaseStamp;
        bool m_HasEaseStamp;
        double m_LastStamp;
        int m_SpikeFrames;
        /// <summary>Frames skipped as glitches since creation.</summary>
        public int SpikesRejected { get; private set; }

        public bool HasFloor => m_Has;
        public bool Airborne => m_Airborne;
        public float Floor => m_Floor;
        /// <summary>Metres the lowest foot is above the floor, zero when grounded.</summary>
        public float Lift { get; private set; }

        public void Reset()
        {
            m_Count = 0;
            m_Has = false;
            m_Airborne = false;
            m_AirTime = 0f;
            m_Floor = 0f;
            Lift = 0f;
            m_LiftFilter.Reset();
            m_HasLastFoot = false;
            m_SpikeFrames = 0;
            m_HasEaseStamp = false;
        }

        /// <summary>Feed one pose (canonical frame: hip-centred joints plus RootOffset) and get the lift in metres.</summary>
        public float Update(PoseFrame pose, float dt)
        {
            if (!pose.Tracked || pose.ConfidenceOf(HumanJoint.LeftAnkle) < MinFootConfidence || pose.ConfidenceOf(HumanJoint.RightAnkle) < MinFootConfidence)
            {
                // Feet out of view: nothing to measure against; come down and start fresh on the next good frame.
                m_Airborne = false;
                m_Count = 0;
                Lift = 0f;
                m_LiftFilter.Reset();
                m_HasLastFoot = false;
                return Lift;
            }

            var footY = pose.RootOffset.y + PoseTransforms.LowestFootY(pose);
            // Glitch guard, timed by the poses themselves (the caller's dt is the render frame, not the pose interval).
            if (m_HasLastFoot)
            {
                var poseDt = Mathf.Clamp((float)(pose.Timestamp - m_LastStamp), 1f / 60f, 0.2f);
                if (Mathf.Abs(footY - m_LastFootY) > MaxFootSpeed * poseDt + 0.03f && m_SpikeFrames < MaxSpikeFrames)
                {
                    m_SpikeFrames++;
                    SpikesRejected++;
                    return Lift;
                }
            }
            if (m_SpikeFrames >= MaxSpikeFrames)
            {
                // The change stayed: a new baseline (another person, a step onto something), not a take-off. Start over here.
                m_Count = 0;
                m_Airborne = false;
                m_Floor = footY;
                Lift = 0f;
                m_LiftFilter.Reset();
            }
            m_SpikeFrames = 0;
            m_HasLastFoot = true;
            m_LastFootY = footY;
            m_LastStamp = pose.Timestamp;
            if (!m_Has)
            {
                m_Floor = footY;
                m_Has = true;
            }

            var rise = m_Count >= LaunchFrames ? footY - m_History[(m_Count - LaunchFrames) % m_History.Length] : 0f;
            m_History[m_Count % m_History.Length] = footY;
            m_Count++;

            var err = footY - m_Floor;
            if (m_Airborne)
            {
                m_AirTime += dt;
                if (err <= GroundBand || m_AirTime > MaxJumpSeconds) m_Airborne = false;
            }
            else if (err > GroundBand && rise > LaunchRise)
            {
                m_Airborne = true;
                m_AirTime = 0f;
            }

            if (!m_Airborne)
            {
                if (err < 0f) m_Floor = footY; // the floor was too high: snap down
                else if (dt > 0f && FollowSeconds > 0f) m_Floor = Mathf.Lerp(m_Floor, footY, 1f - Mathf.Exp(-dt / FollowSeconds));
                else m_Floor = footY;
                err = footY - m_Floor;
            }

            var raw = m_Airborne ? Mathf.Max(0f, err - DeadBand) : 0f;
            m_LiftFilter.MinCutoff = LiftMinCutoff;
            m_LiftFilter.Beta = LiftBeta;
            var target = m_Airborne ? Mathf.Max(0f, m_LiftFilter.Filter(raw, pose.Timestamp)) : 0f;
            if (!m_Airborne) m_LiftFilter.Reset();
            // Ease the shown lift toward the target, timed by the poses.
            var easeDt = m_HasEaseStamp ? Mathf.Clamp((float)(pose.Timestamp - m_EaseStamp), 0f, 0.2f) : 0f;
            m_EaseStamp = pose.Timestamp;
            m_HasEaseStamp = true;
            if (EaseSeconds <= 0f) Lift = target;
            else Lift += (target - Lift) * (1f - Mathf.Exp(-easeDt / EaseSeconds));
            if (Lift < 0.002f && target <= 0f) Lift = 0f;
            return Lift;
        }
    }
}
