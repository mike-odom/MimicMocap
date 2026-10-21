using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Smooths a stream of <see cref="PoseFrame"/>s with a One-Euro filter per joint, holds low-confidence joints at
    /// their last good value, and carries the whole pose through a brief tracking loss instead of starting over.
    /// </summary>
    public sealed class PoseSmoother
    {
        [System.Serializable]
        public struct Settings
        {
            [Tooltip("Hz. Lower = smoother when still, more lag.")]
            public float minCutoff;
            [Tooltip("Speed coefficient. Higher = less lag on fast motion, more jitter.")]
            public float beta;
            public float derivativeCutoff;
            [Tooltip("Joints below this confidence hold their last smoothed value.")]
            public float confidenceFloor;
            [Tooltip("Low-pass weight for per-joint confidence (MediaPipe's visibility smoothing uses 0.1). Lower = fewer one-frame blinks, slower to accept a joint.")]
            public float confidenceAlpha;
            [Tooltip("Seconds a brief tracking loss keeps the last smoothed pose on screen, carried along its recent velocity, instead of resetting. A longer loss starts fresh on the next person.")]
            public float holdSeconds;
            [Tooltip("Time constant (s) of the velocity carried through a hold: the pose keeps moving the way it was and slows to a stop. 0 freezes it.")]
            public float extrapolateSeconds;
            [Tooltip("Seconds of the smoothed velocity added to the output to take lag off fast motion (Kinect-style prediction). Adds back some jitter; 0 = off.")]
            public float predictSeconds;
            [Tooltip("Fastest the figure may travel (m/s). Nobody walks faster than this, so faster jumps in the source's position (depth glitches, mostly) are spread out instead of shown.")]
            public float maxTravelSpeed;
            [Tooltip("Time constant (s) of the leg length the views scale the figure by. The per-frame value swung 15% on the install footage, pumping the whole figure in size.")]
            public float legLengthSeconds;
            [Tooltip("Fastest the pelvis may rise or sink above the feet (m/s). A deep squat is about 1.5 m/s; faster changes are leg glitches (occluded legs collapsing for a frame), which would drop the whole figure.")]
            public float maxHipSpeed;
            [Tooltip("Fastest a hand joint may move relative to the hips (m/s): a fast wave or punch peaks around 8. A source position further away than that is not trusted, and nothing drawn moves faster.")]
            public float handMaxSpeed;
            [Tooltip("Fastest any other joint may move relative to the hips (m/s).")]
            public float jointMaxSpeed;
            [Tooltip("Seconds an untrusted joint (lost, out of frame, or jumping faster than a person can move) stays exactly where it was before it starts to ease toward the source's guess.")]
            public float jointHoldSeconds;
            [Tooltip("Speed (m/s) an untrusted joint eases toward the source's guess after the hold, e.g. an arm the tracker can no longer see going back to the side.")]
            public float jointRelaxSpeed;
            [Tooltip("Speed (m/s) a held joint catches up once the source is back within human reach (the hand came back into frame), until it is close enough to be trusted again.")]
            public float jointCatchUpSpeed;

            /// <summary>
            /// Joints are in metres. Tuned for a mirror on the install footage (HandFootageTests, sub-frame lag): the earlier
            /// 1 Hz / beta 0.5 left the wrist 83 ms (26 mm) behind the raw pose with 2.8 mm of roughness (raw 9.2). With the
            /// glitch guards now catching what the heavy filter used to absorb (NaN, depth speed limit, foot spikes, steady
            /// scale), 1.5 Hz / beta 1 plus 33 ms of velocity prediction trails by 22 ms (13 mm) at 4.7 mm of roughness.
            /// </summary>
            public static Settings Default => new Settings
            {
                minCutoff = 1.5f,
                beta = 1f,
                derivativeCutoff = 1f,
                confidenceFloor = 0.4f,
                confidenceAlpha = 0.25f,
                holdSeconds = 0.5f,
                extrapolateSeconds = 0.08f,
                predictSeconds = 0.033f,
                maxTravelSpeed = 3f,
                legLengthSeconds = 1f,
                maxHipSpeed = 2.5f,
                handMaxSpeed = 9f,
                jointMaxSpeed = 6f,
                jointHoldSeconds = 0.4f,
                jointRelaxSpeed = 0.5f,
                jointCatchUpSpeed = 4f,
            };
        }

        readonly OneEuroFilter3[] m_Filters = new OneEuroFilter3[JointTopology.Count];
        readonly Vector3[] m_LastGood = new Vector3[JointTopology.Count];
        readonly bool[] m_HasGood = new bool[JointTopology.Count];
        readonly OneEuroFilter3[] m_LeftHandFilters = new OneEuroFilter3[HandFrame.LandmarkCount];
        readonly OneEuroFilter3[] m_RightHandFilters = new OneEuroFilter3[HandFrame.LandmarkCount];
        // Travel moves the whole figure, so it gets a calmer filter than the joints.
        readonly OneEuroFilter3 m_Travel;
        // Per-joint confidence low-pass (MediaPipe VisibilitySmoothingCalculator) so a one-frame dip never blinks a joint.
        readonly float[] m_Confidence = new float[JointTopology.Count];
        readonly bool[] m_HasConfidence = new bool[JointTopology.Count];
        // Image-space joints (normalised) smoothed MediaPipe-style, scaled by the person's size in the frame
        // (pose_landmark_filtering.pbtxt image landmarks: min_cutoff 0.05, beta 80). Steadies hand crops and the HUD.
        readonly ScaledOneEuroFilter2[] m_ImageFilters = new ScaledOneEuroFilter2[JointTopology.Count];
        Settings m_Settings;

        // Brief tracking loss. The source drops the person for a frame or two now and then (the install footage has
        // three or four such gaps a minute between the real walk-outs). Resetting on every one of them threw away the
        // filters and the last-good memory, so the next frame came out raw: occluded joints at their garbage positions
        // and every joint jittering until the filters had settled again. Instead the last smoothed pose is kept, carried
        // on along its velocity with a short time constant (so a wave keeps waving for a few frames, then stops), and
        // when tracking returns the filters continue from what was shown, easing onto the new detection. None of the
        // surveyed VTuber apps extrapolate; they all hold for 200 to 250 ms (XR Animator 250 ms, OpenSeeFace five
        // frames), and MediaPipe simply resets its filters across a gap, which is the snap this avoids.
        readonly PoseFrame m_Last = new PoseFrame();     // last tracked output
        readonly PoseFrame m_Shown = new PoseFrame();    // last output of any kind, tracked or held
        bool m_HasLast;
        bool m_HasVelocity;
        bool m_Holding;
        double m_LastTrackedTime;
        readonly Vector3[] m_Velocity = new Vector3[JointTopology.Count];
        Vector3 m_TravelVelocity;
        // Travel input after the speed limit, and the smoothed leg length.
        Vector3 m_TravelIn;
        double m_TravelInTime;
        bool m_HasTravelIn;
        float m_Leg;
        double m_LegTime;
        float m_Hip;
        double m_HipTime;
        // Per-joint trust. Maxine keeps reporting a confident wrist when the hand leaves the frame or hides behind the head,
        // and puts it at the side: the arm dropped there in one frame. A joint is now trusted only while its source position
        // is within human reach of where it is shown, and not on the picture's edge.
        readonly double[] m_UntrustedSince = new double[JointTopology.Count];
        readonly bool[] m_Untrusted = new bool[JointTopology.Count];
        /// <summary>Joints currently held because the source position was not trusted (diagnostics, HUD).</summary>
        public int UntrustedJoints { get; private set; }
        /// <summary>Frames on which some joint was not trusted, since creation.</summary>
        public int UntrustedFrames { get; private set; }

        /// <summary>Filter the hand landmarks too (off when a HandStabilizer already smooths them in angle space).</summary>
        public bool SmoothHands = true;

        /// <summary>True while the output is a held pose: the source lost the person less than holdSeconds ago.</summary>
        public bool Holding => m_Holding;

        public PoseSmoother(Settings settings)
        {
            m_Settings = settings;
            m_Travel = new OneEuroFilter3(settings.minCutoff * 0.5f, settings.beta * 0.5f, settings.derivativeCutoff);
            for (var i = 0; i < m_Filters.Length; i++)
            {
                m_Filters[i] = new OneEuroFilter3(settings.minCutoff, settings.beta, settings.derivativeCutoff);
                m_ImageFilters[i] = new ScaledOneEuroFilter2(0.05f, 80f, 1f);
            }
            // Fingers move fast and are small; let them through with less lag.
            for (var i = 0; i < HandFrame.LandmarkCount; i++)
            {
                m_LeftHandFilters[i] = new OneEuroFilter3(settings.minCutoff * 1.5f, settings.beta * 2f, settings.derivativeCutoff);
                m_RightHandFilters[i] = new OneEuroFilter3(settings.minCutoff * 1.5f, settings.beta * 2f, settings.derivativeCutoff);
            }
        }

        public void ApplySettings(Settings settings)
        {
            m_Settings = settings;
            m_Travel.SetParameters(settings.minCutoff * 0.5f, settings.beta * 0.5f, settings.derivativeCutoff);
            foreach (var f in m_Filters) f.SetParameters(settings.minCutoff, settings.beta, settings.derivativeCutoff);
            foreach (var f in m_LeftHandFilters) f.SetParameters(settings.minCutoff * 1.5f, settings.beta * 2f, settings.derivativeCutoff);
            foreach (var f in m_RightHandFilters) f.SetParameters(settings.minCutoff * 1.5f, settings.beta * 2f, settings.derivativeCutoff);
        }

        public void Reset()
        {
            for (var i = 0; i < m_Filters.Length; i++)
            {
                m_Filters[i].Reset();
                m_ImageFilters[i].Reset();
                m_HasGood[i] = false;
                m_HasConfidence[i] = false;
            }
            m_Travel.Reset();
            foreach (var f in m_LeftHandFilters) f.Reset();
            foreach (var f in m_RightHandFilters) f.Reset();
            m_HasLast = false;
            m_HasVelocity = false;
            m_Holding = false;
            m_HasTravelIn = false;
            m_Leg = 0f;
            m_Hip = 0f;
            System.Array.Clear(m_Untrusted, 0, m_Untrusted.Length);
            UntrustedJoints = 0;
        }

        void SmoothHand(HandFrame input, HandFrame output, OneEuroFilter3[] filters, double t)
        {
            if (!input.Tracked)
            {
                foreach (var f in filters) f.Reset();
                return;
            }
            for (var i = 0; i < HandFrame.LandmarkCount; i++)
                output.Landmarks[i] = filters[i].Filter(input.Landmarks[i], t);
        }

        /// <summary>
        /// Smooth the normalised image joints in place, scaled by the person's size in the frame. Run this on the
        /// unmirrored frame before hand crops are placed from it.
        /// </summary>
        public void SmoothImageJoints(PoseFrame frame)
        {
            if (!frame.Tracked || !frame.HasImageJoints) return;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var any = false;
            for (var i = 0; i < JointTopology.Count; i++)
            {
                if (frame.Confidence[i] < m_Settings.confidenceFloor) continue;
                var p = frame.ImageJoints[i];
                if (p == Vector2.zero) continue;
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
                any = true;
            }
            if (!any) return;
            var objectScale = Mathf.Max(max.x - min.x, max.y - min.y);
            for (var i = 0; i < JointTopology.Count; i++)
            {
                if (frame.Confidence[i] < m_Settings.confidenceFloor || frame.ImageJoints[i] == Vector2.zero)
                {
                    m_ImageFilters[i].Reset();
                    continue;
                }
                frame.ImageJoints[i] = m_ImageFilters[i].Filter(frame.ImageJoints[i], objectScale, frame.Timestamp);
            }
        }

        /// <summary>Smooth <paramref name="input"/> into <paramref name="output"/> (may be the same instance).</summary>
        public void Smooth(PoseFrame input, PoseFrame output)
        {
            if (!ReferenceEquals(input, output)) input.CopyTo(output);
            if (!input.Tracked)
            {
                var gap = input.Timestamp - m_LastTrackedTime;
                if (m_HasLast && m_Settings.holdSeconds > 0f && gap >= 0.0 && gap <= m_Settings.holdSeconds)
                {
                    Hold(output, input.Timestamp);
                    SmoothBothHands(input, output);
                    return;
                }
                // Gone for longer than a hold: forget this person, so the next one starts from their own pose.
                Reset();
                return;
            }
            output.Held = false;
            if (m_Holding)
            {
                // Back after a hold: the filters continue from what was shown a frame ago, so the pose eases onto the
                // new detection over a few frames instead of snapping to it.
                for (var i = 0; i < JointTopology.Count; i++) m_Filters[i].Prime(m_Shown.Joints[i], m_Shown.Timestamp);
                m_Travel.Prime(m_Shown.RootOffset, m_Shown.Timestamp);
                m_Holding = false;
            }

            // Published confidence is low-passed (MediaPipe's visibility smoothing) so one bad frame cannot blink a
            // limb downstream. Holding, below, still keys off the raw value: a frame the source itself distrusts
            // carries a garbage position and must not be let through, blink or no blink.
            var alpha = Mathf.Clamp01(m_Settings.confidenceAlpha <= 0f ? 1f : m_Settings.confidenceAlpha);
            for (var i = 0; i < JointTopology.Count; i++)
            {
                if (!m_HasConfidence[i]) { m_Confidence[i] = input.Confidence[i]; m_HasConfidence[i] = true; }
                else m_Confidence[i] += alpha * (input.Confidence[i] - m_Confidence[i]);
                output.Confidence[i] = m_Confidence[i];
            }

            for (var i = 0; i < JointTopology.Count; i++)
            {
                var c = input.Confidence[i];
                var finite = IsFinite(input.Joints[i]);
                // A non-finite value would poison the filter for good (every later output NaN until a reset).
                if (!finite) { c = 0f; output.Confidence[i] = Mathf.Min(output.Confidence[i], m_Confidence[i]); }
                // Plausibility: a confident source position that is further from where the joint is shown than a person could
                // move since the last frame, or that sits on the picture's edge (the hand has left the frame and the source is
                // guessing), is not trusted. The joint is then held, so a confident wrong guess cannot snap a limb.
                var plausible = true;
                if (finite && c >= m_Settings.confidenceFloor && m_HasLast && m_HasGood[i])
                {
                    var maxSpeed = IsHandJoint(i) ? m_Settings.handMaxSpeed : m_Settings.jointMaxSpeed;
                    if (maxSpeed > 0f)
                    {
                        var dtJ = Mathf.Clamp((float)(input.Timestamp - m_Last.Timestamp), 1f / 60f, 0.2f);
                        if ((input.Joints[i] - m_Last.Joints[i]).magnitude > maxSpeed * dtJ + 0.05f) plausible = false;
                    }
                    if (input.HasImageJoints && OnEdge(input.ImageJoints[i])) plausible = false;
                }
                // A joint that was held rejoins only once it is within a few centimetres of the source, so trust never comes
                // back with a jump; until then it catches up (source within reach) or holds and relaxes (source out of reach).
                var closeToHeld = m_HasGood[i] && finite && (input.Joints[i] - m_LastGood[i]).magnitude < k_RejoinDistance;
                var trusted = finite && c >= m_Settings.confidenceFloor && plausible && (!m_Untrusted[i] || closeToHeld);
                Vector3 raw;
                if (trusted || (finite && !m_HasGood[i]))
                {
                    raw = input.Joints[i];
                    m_Untrusted[i] = false;
                }
                else if (m_HasGood[i])
                {
                    // Held where it was; after jointHoldSeconds it eases toward the source's guess at jointRelaxSpeed, so an
                    // arm that really went down while out of view follows within a couple of seconds, and one that is still up
                    // has not moved when the hand comes back into frame.
                    if (!m_Untrusted[i]) { m_Untrusted[i] = true; m_UntrustedSince[i] = input.Timestamp; }
                    if (finite && c >= m_Settings.confidenceFloor)
                    {
                        var dtR = Mathf.Clamp((float)(input.Timestamp - m_Last.Timestamp), 0f, 0.2f);
                        if (plausible) m_LastGood[i] = Vector3.MoveTowards(m_LastGood[i], input.Joints[i], m_Settings.jointCatchUpSpeed * dtR);
                        else if (input.Timestamp - m_UntrustedSince[i] > m_Settings.jointHoldSeconds)
                            m_LastGood[i] = Vector3.MoveTowards(m_LastGood[i], input.Joints[i], m_Settings.jointRelaxSpeed * dtR);
                    }
                    raw = m_LastGood[i];
                }
                else
                {
                    raw = Vector3.zero;
                }

                var smoothed = m_Filters[i].Filter(raw, input.Timestamp);
                output.Joints[i] = smoothed;
                if (trusted || (finite && !m_HasGood[i] && c >= m_Settings.confidenceFloor))
                {
                    m_LastGood[i] = smoothed;
                    m_HasGood[i] = true;
                }
            }
            var untrusted = 0;
            for (var i = 0; i < JointTopology.Count; i++) if (m_Untrusted[i]) untrusted++;
            UntrustedJoints = untrusted;
            if (untrusted > 0) UntrustedFrames++;

            var travelIn = IsFinite(input.RootOffset) ? input.RootOffset : (m_HasLast ? m_Last.RootOffset : Vector3.zero);
            if (m_HasTravelIn && m_Settings.maxTravelSpeed > 0f)
            {
                // Speed limit: on the two-person install footage the source's depth jumped 0.2 to 0.4 m between frames
                // while the person stood still; a walking person moves 5 cm a frame.
                var travelDt = Mathf.Clamp((float)(input.Timestamp - m_TravelInTime), 1f / 60f, 0.2f);
                travelIn = m_TravelIn + Vector3.ClampMagnitude(travelIn - m_TravelIn, m_Settings.maxTravelSpeed * travelDt);
            }
            m_TravelIn = travelIn;
            m_TravelInTime = input.Timestamp;
            m_HasTravelIn = true;
            output.RootOffset = m_Travel.Filter(travelIn, input.Timestamp);

            // Leg length for the views' scale, from confident legs only, smoothed over legLengthSeconds.
            var legsSeen = true;
            foreach (var j in k_LegJoints) if (input.Confidence[(int)j] < m_Settings.confidenceFloor || !IsFinite(input.Joints[(int)j])) legsSeen = false;
            if (legsSeen)
            {
                var leg = PoseTransforms.LegLength(output);
                if (m_Leg <= 0f || m_Settings.legLengthSeconds <= 0f) m_Leg = leg;
                else m_Leg += (leg - m_Leg) * (1f - Mathf.Exp(-Mathf.Clamp((float)(input.Timestamp - m_LegTime), 0f, 0.2f) / m_Settings.legLengthSeconds));
                m_LegTime = input.Timestamp;
            }
            output.LegLength = m_Leg;
            // Pelvis height above the feet, speed-limited: on the two-person footage an occluded pair of legs collapsed for a
            // frame (leg length 0.6 to 0.38 m) and, standing the figure on its feet, dropped the whole body 0.7 m.
            var hip = output.Joints[(int)HumanJoint.Pelvis].y - PoseTransforms.LowestFootY(output);
            if (float.IsFinite(hip))
            {
                if (m_Hip <= 0f || m_Settings.maxHipSpeed <= 0f) m_Hip = hip;
                else
                {
                    var step = m_Settings.maxHipSpeed * Mathf.Clamp((float)(input.Timestamp - m_HipTime), 1f / 60f, 0.2f);
                    m_Hip += Mathf.Clamp(hip - m_Hip, -step, step);
                }
                m_HipTime = input.Timestamp;
            }
            output.HipHeight = m_Hip;

            // Joint velocity (lightly low-passed) for carrying the pose through a dropout.
            var dt = (float)(input.Timestamp - m_Last.Timestamp);
            if (m_HasLast && dt > 0f && dt < 0.2f)
            {
                for (var i = 0; i < JointTopology.Count; i++)
                {
                    var v = (output.Joints[i] - m_Last.Joints[i]) / dt;
                    m_Velocity[i] = m_HasVelocity ? Vector3.Lerp(m_Velocity[i], v, 0.5f) : v;
                }
                var tv = (output.RootOffset - m_Last.RootOffset) / dt;
                m_TravelVelocity = m_HasVelocity ? Vector3.Lerp(m_TravelVelocity, tv, 0.5f) : tv;
                m_HasVelocity = true;
            }
            else m_HasVelocity = false;
            output.CopyTo(m_Last);
            // Prediction takes the filter's lag off the joints. Travel is not predicted: it would amplify the depth glitches the
            // speed limit just spread out, and the limbs are what make the figure feel like a mirror.
            if (m_Settings.predictSeconds > 0f && m_HasVelocity)
                for (var i = 0; i < JointTopology.Count; i++) output.Joints[i] += m_Velocity[i] * m_Settings.predictSeconds;
            // Nothing drawn moves faster than a person can: each joint's step from the last shown pose is capped.
            if (m_Shown.Tracked && !m_Shown.Held && m_Shown.Timestamp < input.Timestamp)
            {
                var dtS = Mathf.Clamp((float)(input.Timestamp - m_Shown.Timestamp), 1f / 60f, 0.2f);
                for (var i = 0; i < JointTopology.Count; i++)
                {
                    var maxSpeed = IsHandJoint(i) ? m_Settings.handMaxSpeed : m_Settings.jointMaxSpeed;
                    if (maxSpeed <= 0f) continue;
                    output.Joints[i] = m_Shown.Joints[i] + Vector3.ClampMagnitude(output.Joints[i] - m_Shown.Joints[i], maxSpeed * dtS);
                }
            }
            output.CopyTo(m_Shown);
            m_HasLast = true;
            m_LastTrackedTime = input.Timestamp;

            SmoothBothHands(input, output);
        }

        /// <summary>The last tracked pose, carried on along its velocity with a decaying time constant.</summary>
        void Hold(PoseFrame output, double t)
        {
            var elapsed = (float)(t - m_LastTrackedTime);
            var tau = m_Settings.extrapolateSeconds;
            // Decaying constant velocity: the carried distance is bounded by velocity x tau however long the hold lasts.
            var carry = m_HasVelocity && tau > 0f ? tau * (1f - Mathf.Exp(-elapsed / tau)) : 0f;
            // The tracked frames showed the pose predicted ahead by predictSeconds; the hold continues from there, never back.
            var lead = m_HasVelocity ? Mathf.Max(0f, m_Settings.predictSeconds) : 0f;
            for (var i = 0; i < JointTopology.Count; i++)
            {
                output.Joints[i] = m_Last.Joints[i] + m_Velocity[i] * (carry + lead);
                output.Confidence[i] = m_Last.Confidence[i];
                output.Rotations[i] = m_Last.Rotations[i];
                output.ImageJoints[i] = m_Last.ImageJoints[i];
            }
            output.HasRotations = m_Last.HasRotations;
            output.HasImageJoints = m_Last.HasImageJoints;
            output.RootOffset = m_Last.RootOffset + m_TravelVelocity * carry;
            output.ImageBox = m_Last.ImageBox;
            output.LegLength = m_Last.LegLength;   // the figure's scale must not fall back to the raw per-frame value
            output.HipHeight = m_Last.HipHeight;
            output.Score = m_Last.Score;
            output.Tracked = true;
            output.Held = true;
            output.Timestamp = t;
            m_Holding = true;
            output.CopyTo(m_Shown);
        }

        static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        /// <summary>A held joint is trusted again once the source is this close (metres).</summary>
        const float k_RejoinDistance = 0.06f;

        static bool IsHandJoint(int i)
        {
            switch ((HumanJoint)i)
            {
                case HumanJoint.LeftWrist: case HumanJoint.RightWrist:
                case HumanJoint.LeftIndex: case HumanJoint.RightIndex:
                case HumanJoint.LeftPinky: case HumanJoint.RightPinky:
                case HumanJoint.LeftThumb: case HumanJoint.RightThumb:
                case HumanJoint.LeftHandTip: case HumanJoint.RightHandTip:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A normalised image position on (or past) the picture's edge, where a source is guessing at a joint it cannot see.</summary>
        static bool OnEdge(Vector2 p) => p != Vector2.zero && (p.x <= 0.005f || p.x >= 0.995f || p.y <= 0.005f || p.y >= 0.995f);
        static readonly HumanJoint[] k_LegJoints = { HumanJoint.LeftHip, HumanJoint.RightHip, HumanJoint.LeftKnee, HumanJoint.RightKnee, HumanJoint.LeftAnkle, HumanJoint.RightAnkle };

        void SmoothBothHands(PoseFrame input, PoseFrame output)
        {
            if (!SmoothHands) return;
            var handTime = input.LeftHand.Timestamp > 0 ? input.LeftHand.Timestamp : input.Timestamp;
            SmoothHand(input.LeftHand, output.LeftHand, m_LeftHandFilters, handTime);
            handTime = input.RightHand.Timestamp > 0 ? input.RightHand.Timestamp : input.Timestamp;
            SmoothHand(input.RightHand, output.RightHand, m_RightHandFilters, handTime);
        }
    }
}
