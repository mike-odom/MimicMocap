using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Turns noisy, intermittent 21-point hand detections into a hand that is always there and never twitches.
    ///
    /// The trick (the same one VTuber solvers such as Kalidokit use) is to stop treating the 63 landmark
    /// coordinates as the signal. A hand has about 20 degrees of freedom: a palm orientation, a size, and a few
    /// bend angles per finger with hard anatomical limits. Each frame the detection is projected onto that model,
    /// the angles are clamped and smoothed in angle space, and the landmarks handed downstream are rebuilt from
    /// the smoothed model. Noise that does not fit a real hand simply cannot get through.
    ///
    /// When the detector loses the hand (too small, fingers at the camera, out of frame) nothing snaps: the palm
    /// keeps following the body's own wrist/knuckle joints, the fingers relax toward a natural half-curl, and the
    /// hand stays drawn. When detection returns, the model eases back onto it.
    /// </summary>
    public sealed class HandStabilizer
    {
        [System.Serializable]
        public struct Settings
        {
            [Tooltip("One-Euro minimum cutoff (Hz) for finger bend angles. Lower = calmer fingers, more lag.")]
            public float fingerMinCutoff;
            [Tooltip("One-Euro speed coefficient for finger angles. Higher = quicker response on fast gestures.")]
            public float fingerBeta;
            [Tooltip("One-Euro minimum cutoff (Hz) for the palm orientation.")]
            public float palmMinCutoff;
            public float palmBeta;
            [Tooltip("Seconds for the fingers to relax after the detector loses the hand.")]
            public float relaxSeconds;
            [Tooltip("Seconds to ease the palm back onto detections after they return.")]
            public float reacquireSeconds;
            [Tooltip("Detector presence below which a frame counts as lost.")]
            public float presenceFloor;
            [Tooltip("Body joint confidence needed to place a hand from the wrist when the detector has nothing.")]
            public float bodyConfidenceFloor;
            [Tooltip("Finger angle changes smaller than this (degrees) are ignored, so filtered tremor never reaches the fingers.")]
            public float angleDeadBand;
            [Tooltip("Palm rotations smaller than this (degrees) are ignored.")]
            public float palmDeadBand;
            [Tooltip("Seconds a hand stays after both the detector and the body wrist drop out, so confidence flicker never blinks it.")]
            public float holdSeconds;

            public static Settings Default => new Settings
            {
                fingerMinCutoff = 1.2f,
                fingerBeta = 0.003f,   // angles are in degrees, so the speed term is per deg/s: a quick point (~300 deg/s) opens the filter by ~1 Hz
                // The fingertips ride on the palm, so palm lag was most of the fast-motion error on the install footage:
                // 1.4 Hz / 0.03 left the shown index tip 32 mm from the detection on average; 3 Hz / 0.5 gave 26 mm but let
                // synthetic 4 mm noise through at 3.2 mm (the steady-hands promise is a 3x cut); 2 Hz / 0.3 is the balance.
                // Quaternion components move slowly, so beta is large.
                palmMinCutoff = 2.0f,
                palmBeta = 0.3f,
                relaxSeconds = 0.8f,
                reacquireSeconds = 0.3f,
                presenceFloor = 0.3f,
                bodyConfidenceFloor = 0.35f,
                angleDeadBand = 1.5f,
                palmDeadBand = 0.8f,
                holdSeconds = 0.6f,
            };
        }

        /// <summary>Joint angle limits in degrees: (min, max) for MCP flexion, PIP, DIP, and abduction.</summary>
        const float k_McpMin = -15f, k_McpMax = 95f;
        const float k_PipMin = -8f, k_PipMax = 115f;
        const float k_DipMin = -8f, k_DipMax = 90f;
        const float k_SpreadMax = 22f;
        const float k_ThumbCmcMin = -20f, k_ThumbCmcMax = 55f;
        const float k_ThumbMcpMin = -15f, k_ThumbMcpMax = 70f;
        const float k_ThumbIpMin = -15f, k_ThumbIpMax = 85f;
        const float k_ThumbSpreadMin = -25f, k_ThumbSpreadMax = 35f;

        /// <summary>Template hand (right hand, metres, wrist at origin) in palm coordinates: forward, palmar, thumb-side.</summary>
        const float k_TemplatePalm = 0.090f;
        // The middle knuckle defines the forward axis and the index/pinky knuckles the palm plane, so the solver's
        // frame and the template's frame coincide exactly.
        static readonly Vector3[] k_McpOffset =
        {
            new Vector3(0.030f, 0.012f, 0.028f),   // thumb CMC: short, palmar, well to the thumb side
            new Vector3(0.085f, 0.000f, 0.019f),   // index
            new Vector3(0.090f, 0.000f, 0.000f),   // middle
            new Vector3(0.084f, 0.000f, -0.020f),  // ring
            new Vector3(0.075f, 0.000f, -0.039f),  // pinky
        };
        static readonly float[][] k_SegmentLength =
        {
            new[] { 0.044f, 0.032f, 0.026f },      // thumb: metacarpal, proximal, distal
            new[] { 0.040f, 0.024f, 0.020f },
            new[] { 0.044f, 0.027f, 0.021f },
            new[] { 0.040f, 0.025f, 0.020f },
            new[] { 0.032f, 0.019f, 0.018f },
        };
        /// <summary>Rest spread of each finger (degrees toward the thumb side) and the thumb metacarpal's rest direction.</summary>
        static readonly float[] k_RestSpread = { 0f, 5f, 0f, -4f, -10f };
        static readonly float[] k_RelaxedCurl = { 10f, 22f, 25f, 25f, 25f }; // MCP relaxation per finger (thumb = CMC)
        const float k_RelaxedPip = 28f, k_RelaxedDip = 16f;
        static readonly Vector3 k_ThumbRestDir = new Vector3(0.62f, 0.25f, 0.74f);   // forward, palmar, thumb-side (normalised below)
        static readonly Vector3 k_ThumbBendDir = new Vector3(0.10f, 0.72f, -0.68f);  // flexion carries the tip across the palm

        public Settings Config;

        sealed class HandState
        {
            public bool Initialised;
            public float Sign;                      // +1 right hand, -1 left hand
            public Quaternion Palm = Quaternion.identity;
            public Quaternion BodyPalm = Quaternion.identity;
            public bool HasBodyPalm;
            public float Scale = k_TemplatePalm;
            // The person's proportions, learned slowly from detections (template values until a hand is seen): knuckle
            // positions in palm space at template scale, and each segment's length as a multiple of the template's.
            // With the template alone the rebuilt index tip sat 21 to 23 mm from the detection on the install footage
            // even with no filtering, because the detected fingers were 0.7 to 0.8 of the template's length.
            public readonly Vector3[] McpOffset = (Vector3[])k_McpOffset.Clone();
            public readonly float[][] SegmentScale = { new[] { 1f, 1f, 1f }, new[] { 1f, 1f, 1f }, new[] { 1f, 1f, 1f }, new[] { 1f, 1f, 1f }, new[] { 1f, 1f, 1f } };
            public bool ProportionsLearned;
            public float TrackWeight;               // 0 = body-driven, 1 = detector-driven
            public double LastDetectedTime = -100;
            public double LastAnchorTime = -100;    // last time either the detector or the body wrist was available
            public double LastTime;
            public bool HasTime;
            public readonly float[] Angles = new float[5 * 4];     // per finger: spread, j1, j2, j3 (degrees), filtered
            public readonly float[] Shown = new float[5 * 4];      // after the dead band: what gets drawn
            public readonly OneEuroFilter[] AngleFilters = new OneEuroFilter[5 * 4];
            public readonly OneEuroFilter[] PalmFilters = new OneEuroFilter[4];
            public Quaternion FilteredPalm = Quaternion.identity;
            public Quaternion ShownPalm = Quaternion.identity;
        }

        readonly HandState m_Left = new HandState { Sign = -1f };
        readonly HandState m_Right = new HandState { Sign = 1f };
        readonly Vector3[] m_Scratch = new Vector3[HandFrame.LandmarkCount];
        readonly float[] m_Target = new float[5 * 4];

        public HandStabilizer(Settings settings)
        {
            Config = settings;
            foreach (var s in new[] { m_Left, m_Right })
            {
                for (var i = 0; i < s.AngleFilters.Length; i++) s.AngleFilters[i] = new OneEuroFilter(settings.fingerMinCutoff, settings.fingerBeta, 1f);
                for (var i = 0; i < 4; i++) s.PalmFilters[i] = new OneEuroFilter(settings.palmMinCutoff, settings.palmBeta, 1f);
            }
        }

        public void ApplySettings(Settings settings)
        {
            Config = settings;
            foreach (var s in new[] { m_Left, m_Right })
            {
                foreach (var f in s.AngleFilters) { f.MinCutoff = settings.fingerMinCutoff; f.Beta = settings.fingerBeta; }
                foreach (var f in s.PalmFilters) { f.MinCutoff = settings.palmMinCutoff; f.Beta = settings.palmBeta; }
            }
        }

        public void Reset()
        {
            foreach (var s in new[] { m_Left, m_Right })
            {
                s.Initialised = false;
                s.HasBodyPalm = false;
                s.HasTime = false;
                s.TrackWeight = 0f;
                System.Array.Copy(k_McpOffset, s.McpOffset, k_McpOffset.Length);
                foreach (var f in s.SegmentScale) { f[0] = 1f; f[1] = 1f; f[2] = 1f; }
                s.ProportionsLearned = false;
                s.LastDetectedTime = -100;
                s.LastAnchorTime = -100;
                foreach (var f in s.AngleFilters) f.Reset();
                foreach (var f in s.PalmFilters) f.Reset();
            }
        }

        /// <summary>Detector weight of each hand after the last update (0 body-driven .. 1 detector-driven), for the HUD.</summary>
        public float LeftTrackWeight => m_Left.TrackWeight;
        public float RightTrackWeight => m_Right.TrackWeight;

        /// <summary>
        /// Replace both hands of <paramref name="pose"/> (unmirrored, hands wrist-relative) with stabilised hands.
        /// Hands come out Tracked whenever the body wrist is confident, with Score = detector weight.
        /// </summary>
        public void Apply(PoseFrame pose, double time)
        {
            Stabilise(pose, pose.LeftHand, m_Left, HumanJoint.LeftWrist, HumanJoint.LeftElbow, HumanJoint.LeftIndex, HumanJoint.LeftPinky, time);
            Stabilise(pose, pose.RightHand, m_Right, HumanJoint.RightWrist, HumanJoint.RightElbow, HumanJoint.RightIndex, HumanJoint.RightPinky, time);
        }

        void Stabilise(PoseFrame body, HandFrame hand, HandState s, HumanJoint wristJ, HumanJoint elbowJ, HumanJoint indexJ, HumanJoint pinkyJ, double time)
        {
            var detected = hand.Tracked && hand.Score >= Config.presenceFloor && hand.Landmarks[(int)HandFrame.Landmark.MiddleMcp].sqrMagnitude > 1e-6f;
            var bodyWrist = body.Tracked && body.ConfidenceOf(wristJ) >= Config.bodyConfidenceFloor;
            if (detected || bodyWrist) s.LastAnchorTime = time;
            var holding = s.Initialised && time - s.LastAnchorTime <= Config.holdSeconds;
            if (!detected && !bodyWrist && !holding)
            {
                // Nothing to hang a hand on: let it go (the skeleton falls back to its wrist fork).
                hand.Clear();
                hand.Timestamp = time;
                return;
            }

            // Palm orientation the body can supply on its own (knuckles if the source has them, else the forearm).
            UpdateBodyPalm(body, s, wristJ, elbowJ, indexJ, pinkyJ);

            var justInitialised = false;
            if (detected)
            {
                SolveAngles(hand, s, m_Target, out var palm, out var scale);
                s.Palm = palm;
                s.Scale = Mathf.Lerp(s.Scale, scale, s.Initialised ? 0.08f : 1f);
                LearnProportions(hand, s, palm, scale);
                s.LastDetectedTime = time;
                if (!s.Initialised) { System.Array.Copy(m_Target, s.Angles, m_Target.Length); s.Initialised = true; justInitialised = true; }
            }
            else
            {
                // Relax toward a natural half-curl at a rate set by relaxSeconds.
                RelaxedAngles(m_Target);
                if (!s.Initialised)
                {
                    System.Array.Copy(m_Target, s.Angles, m_Target.Length);
                    s.Palm = s.HasBodyPalm ? s.BodyPalm : Quaternion.identity;
                    if (body.ConfidenceOf(indexJ) >= Config.bodyConfidenceFloor)
                        s.Scale = Mathf.Clamp((body[indexJ] - body[wristJ]).magnitude, 0.06f, 0.12f);
                    s.Initialised = true;
                    justInitialised = true;
                }
            }
            if (justInitialised)
            {
                System.Array.Copy(s.Angles, s.Shown, s.Angles.Length);
                s.FilteredPalm = s.ShownPalm = s.HasBodyPalm && !detected ? s.BodyPalm : s.Palm;
            }

            // Detector weight: quick to trust a fresh detection, slow to let go so brief misses never show.
            var dt = s.HasTime ? Mathf.Clamp((float)(time - s.LastTime), 1f / 120f, 0.1f) : 1f / 30f;
            s.LastTime = time;
            s.HasTime = true;
            var targetWeight = detected ? 1f : 0f;
            var rate = detected ? dt / Mathf.Max(0.05f, Config.reacquireSeconds) : dt / Mathf.Max(0.05f, Config.relaxSeconds);
            s.TrackWeight = Mathf.MoveTowards(s.TrackWeight, targetWeight, rate);

            // Angles: ease onto the detected pose as trust builds, or drift toward the relaxed pose (60 deg over relaxSeconds).
            var relaxStep = 60f * dt / Mathf.Max(0.05f, Config.relaxSeconds);
            for (var i = 0; i < s.Angles.Length; i++)
            {
                var target = detected ? Mathf.Lerp(s.Angles[i], m_Target[i], Mathf.Max(0.15f, s.TrackWeight)) : Mathf.MoveTowards(s.Angles[i], m_Target[i], relaxStep);
                s.Angles[i] = s.AngleFilters[i].Filter(target, time);
                // Dead band (backlash): the drawn angle only follows once the filtered angle has moved past the band.
                var d = s.Angles[i] - s.Shown[i];
                if (Mathf.Abs(d) > Config.angleDeadBand) s.Shown[i] = s.Angles[i] - Mathf.Sign(d) * Config.angleDeadBand;
            }

            // Orientation: blend the detector's palm with the body's by trust, filter, then apply the same dead band.
            var palmTarget = s.HasBodyPalm ? Quaternion.Slerp(s.BodyPalm, s.Palm, s.TrackWeight) : s.Palm;
            s.FilteredPalm = FilterQuaternion(s.PalmFilters, s.FilteredPalm, palmTarget, time, true);
            var palmDelta = Quaternion.Angle(s.ShownPalm, s.FilteredPalm);
            if (palmDelta > Config.palmDeadBand) s.ShownPalm = Quaternion.RotateTowards(s.ShownPalm, s.FilteredPalm, palmDelta - Config.palmDeadBand);

            // Rebuild the 21 landmarks from the model.
            Reconstruct(s, hand.Landmarks);
            if (!detected) hand.HasImageLandmarks = false;
            hand.Tracked = true;
            hand.Score = s.TrackWeight;
            hand.Timestamp = time;
        }

        // ---------------------------------------------------------------- body-driven palm

        void UpdateBodyPalm(PoseFrame body, HandState s, HumanJoint wristJ, HumanJoint elbowJ, HumanJoint indexJ, HumanJoint pinkyJ)
        {
            if (!body.Tracked || body.ConfidenceOf(wristJ) < Config.bodyConfidenceFloor) return;
            var w = body[wristJ];
            var haveKnuckles = body.ConfidenceOf(indexJ) >= Config.bodyConfidenceFloor && body.ConfidenceOf(pinkyJ) >= Config.bodyConfidenceFloor
                               && (body[indexJ] - body[pinkyJ]).sqrMagnitude > 1e-6f;
            Vector3 forward;
            Vector3 palmar;
            if (haveKnuckles)
            {
                forward = 0.5f * (body[indexJ] + body[pinkyJ]) - w;
                palmar = s.Sign * Vector3.Cross(body[pinkyJ] - w, body[indexJ] - w);
                if (forward.sqrMagnitude < 1e-6f || palmar.sqrMagnitude < 1e-8f) return;
                s.BodyPalm = Quaternion.LookRotation(forward.normalized, palmar.normalized);
                s.HasBodyPalm = true;
            }
            else if (body.ConfidenceOf(elbowJ) >= Config.bodyConfidenceFloor)
            {
                forward = w - body[elbowJ];
                if (forward.sqrMagnitude < 1e-6f) return;
                // No knuckles: keep the previous palm roll, just re-aim along the forearm.
                var up = s.HasBodyPalm ? s.BodyPalm * Vector3.up : Vector3.forward;
                if (Mathf.Abs(Vector3.Dot(up, forward.normalized)) > 0.98f) up = Vector3.up;
                s.BodyPalm = Quaternion.LookRotation(forward.normalized, up);
                s.HasBodyPalm = true;
            }
        }

        // ---------------------------------------------------------------- solving

        /// <summary>Fit the model to detected landmarks: palm frame, scale, and clamped joint angles (degrees).</summary>
        void SolveAngles(HandFrame hand, HandState s, float[] angles, out Quaternion palm, out float scale)
        {
            var L = hand.Landmarks;
            var w = L[0];
            var forward = L[(int)HandFrame.Landmark.MiddleMcp] - w;
            var palmar = s.Sign * Vector3.Cross(L[(int)HandFrame.Landmark.PinkyMcp] - w, L[(int)HandFrame.Landmark.IndexMcp] - w);
            scale = Mathf.Clamp(forward.magnitude, 0.05f, 0.14f);
            if (forward.sqrMagnitude < 1e-8f || palmar.sqrMagnitude < 1e-10f)
            {
                palm = s.Palm;
                System.Array.Copy(s.Angles, angles, angles.Length);
                return;
            }
            palm = Quaternion.LookRotation(forward.normalized, palmar.normalized);
            var F = palm * Vector3.forward;
            var P = palm * Vector3.up;
            var T = s.Sign * (palm * Vector3.right);

            for (var finger = 0; finger < 5; finger++)
            {
                var b = finger * 4;
                var mcp = 1 + finger * 4;
                var d1 = L[mcp + 1] - L[mcp];
                var d2 = L[mcp + 2] - L[mcp + 1];
                var d3 = L[mcp + 3] - L[mcp + 2];
                if (d1.sqrMagnitude < 1e-10f || d2.sqrMagnitude < 1e-10f || d3.sqrMagnitude < 1e-10f)
                {
                    for (var k = 0; k < 4; k++) angles[b + k] = s.Angles[b + k];
                    continue;
                }
                d1.Normalize(); d2.Normalize(); d3.Normalize();

                FingerFrame(finger, F, P, T, out var restDir, out var bendDir);
                float spreadMin, spreadMax, j1Min, j1Max, j2Min, j2Max, j3Min, j3Max;
                if (finger == 0)
                {
                    spreadMin = k_ThumbSpreadMin; spreadMax = k_ThumbSpreadMax;
                    j1Min = k_ThumbCmcMin; j1Max = k_ThumbCmcMax; j2Min = k_ThumbMcpMin; j2Max = k_ThumbMcpMax; j3Min = k_ThumbIpMin; j3Max = k_ThumbIpMax;
                }
                else
                {
                    spreadMin = -k_SpreadMax; spreadMax = k_SpreadMax;
                    j1Min = k_McpMin; j1Max = k_McpMax; j2Min = k_PipMin; j2Max = k_PipMax; j3Min = k_DipMin; j3Max = k_DipMax;
                }

                // Spread (abduction) is the rotation about the bend direction; flexion is the rotation about the in-plane side axis.
                var side = Vector3.Cross(bendDir, restDir).normalized;
                var d1Plane = d1 - Vector3.Dot(d1, bendDir) * bendDir;
                var spread = d1Plane.sqrMagnitude > 1e-8f ? Mathf.Atan2(Vector3.Dot(d1Plane, side), Vector3.Dot(d1Plane, restDir)) * Mathf.Rad2Deg : 0f;
                spread = Mathf.Clamp(spread, spreadMin, spreadMax);
                var spreadDir = (Mathf.Cos(spread * Mathf.Deg2Rad) * restDir + Mathf.Sin(spread * Mathf.Deg2Rad) * side).normalized;

                // Cumulative bend of each segment relative to the spread rest direction, in the finger's bend plane.
                var a1 = Mathf.Atan2(Vector3.Dot(d1, bendDir), Vector3.Dot(d1, spreadDir)) * Mathf.Rad2Deg;
                var a2 = Mathf.Atan2(Vector3.Dot(d2, bendDir), Vector3.Dot(d2, spreadDir)) * Mathf.Rad2Deg;
                var a3 = Mathf.Atan2(Vector3.Dot(d3, bendDir), Vector3.Dot(d3, spreadDir)) * Mathf.Rad2Deg;
                var j1 = Mathf.Clamp(a1, j1Min, j1Max);
                var j2 = Mathf.Clamp(Mathf.DeltaAngle(a1, a2), j2Min, j2Max);
                var j3 = Mathf.Clamp(Mathf.DeltaAngle(a2, a3), j3Min, j3Max);
                if (finger > 0)
                {
                    // The last joint is the smallest and noisiest; real fingers bend it at about two thirds of the PIP.
                    j3 = Mathf.Lerp(j3, Mathf.Clamp(j2 * 0.67f, j3Min, j3Max), 0.6f);
                    // A curled finger cannot spread: fade abduction out between 30 and 90 degrees of MCP flexion.
                    spread *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30f, 90f, j1));
                }

                angles[b + 0] = spread;
                angles[b + 1] = j1;
                angles[b + 2] = j2;
                angles[b + 3] = j3;
            }
        }

        /// <summary>
        /// Pull the hand's knuckle positions and segment lengths toward this detection (metric world landmarks), slowly
        /// enough that one bad frame cannot warp the hand: the first detection is adopted outright, later ones at 5%.
        /// </summary>
        static void LearnProportions(HandFrame hand, HandState s, Quaternion palm, float scale)
        {
            var L = hand.Landmarks;
            var k = scale / k_TemplatePalm;
            if (k < 1e-4f) return;
            var F = palm * Vector3.forward;
            var P = palm * Vector3.up;
            var T = s.Sign * (palm * Vector3.right);
            var rate = s.ProportionsLearned ? 0.05f : 1f;
            for (var finger = 0; finger < 5; finger++)
            {
                var mcp = 1 + finger * 4;
                var m = L[mcp];
                var local = new Vector3(Vector3.Dot(m, F), Vector3.Dot(m, P), Vector3.Dot(m, T)) / k;
                var t = k_McpOffset[finger];
                // Within half a template knuckle spacing of the template, so a mis-detected knuckle cannot wander off.
                local = new Vector3(Mathf.Clamp(local.x, t.x - 0.02f, t.x + 0.02f), Mathf.Clamp(local.y, t.y - 0.015f, t.y + 0.015f), Mathf.Clamp(local.z, t.z - 0.015f, t.z + 0.015f));
                s.McpOffset[finger] = Vector3.Lerp(s.McpOffset[finger], local, rate);
                for (var seg = 0; seg < 3; seg++)
                {
                    var len = Vector3.Distance(L[mcp + seg + 1], L[mcp + seg]);
                    var ratio = Mathf.Clamp(len / (k_SegmentLength[finger][seg] * k), 0.5f, 1.5f);
                    s.SegmentScale[finger][seg] = Mathf.Lerp(s.SegmentScale[finger][seg], ratio, rate);
                }
            }
            s.ProportionsLearned = true;
        }

        static void RelaxedAngles(float[] angles)
        {
            for (var finger = 0; finger < 5; finger++)
            {
                var b = finger * 4;
                angles[b + 0] = finger == 0 ? 5f : 0f;
                angles[b + 1] = k_RelaxedCurl[finger];
                angles[b + 2] = finger == 0 ? 15f : k_RelaxedPip;
                angles[b + 3] = finger == 0 ? 10f : k_RelaxedDip;
            }
        }

        // ---------------------------------------------------------------- reconstruction

        void Reconstruct(HandState s, Vector3[] outLandmarks)
        {
            var palm = s.ShownPalm;
            var F = palm * Vector3.forward;
            var P = palm * Vector3.up;
            var T = s.Sign * (palm * Vector3.right);
            var k = s.Scale / k_TemplatePalm;

            outLandmarks[0] = Vector3.zero;
            for (var finger = 0; finger < 5; finger++)
            {
                var b = finger * 4;
                var mcp = 1 + finger * 4;
                var basePos = PalmToWorld(s.McpOffset[finger], F, P, T) * k;
                outLandmarks[mcp] = basePos;

                FingerFrame(finger, F, P, T, out var restDir, out var bendDir);
                var side = Vector3.Cross(bendDir, restDir).normalized;
                var spread = s.Shown[b + 0] * Mathf.Deg2Rad;
                var spreadDir = (Mathf.Cos(spread) * restDir + Mathf.Sin(spread) * side).normalized;

                var pos = basePos;
                var cumulative = 0f;
                for (var seg = 0; seg < 3; seg++)
                {
                    cumulative += s.Shown[b + 1 + seg];
                    var a = cumulative * Mathf.Deg2Rad;
                    var dir = Mathf.Cos(a) * spreadDir + Mathf.Sin(a) * bendDir;
                    pos += dir * (k_SegmentLength[finger][seg] * k * s.SegmentScale[finger][seg]);
                    outLandmarks[mcp + 1 + seg] = pos;
                }
            }
        }

        static Vector3 PalmToWorld(Vector3 fpt, Vector3 F, Vector3 P, Vector3 T) => fpt.x * F + fpt.y * P + fpt.z * T;

        /// <summary>
        /// Each finger bends in a plane: <paramref name="restDir"/> is where its proximal segment points when straight,
        /// <paramref name="bendDir"/> (perpendicular) is where flexion carries it. Fingers bend toward the palm; the thumb
        /// swings across it.
        /// </summary>
        static void FingerFrame(int finger, Vector3 F, Vector3 P, Vector3 T, out Vector3 restDir, out Vector3 bendDir)
        {
            if (finger == 0)
            {
                restDir = PalmToWorld(k_ThumbRestDir.normalized, F, P, T);
                var raw = PalmToWorld(k_ThumbBendDir.normalized, F, P, T);
                bendDir = (raw - Vector3.Dot(raw, restDir) * restDir).normalized;
            }
            else
            {
                restDir = RestFingerDir(finger, 0f, F, T);
                bendDir = P;
            }
        }

        /// <summary>Rest direction of a finger's proximal segment in the palm plane, including its natural spread.</summary>
        static Vector3 RestFingerDir(int finger, float extraSpreadDeg, Vector3 F, Vector3 T)
        {
            var a = (k_RestSpread[finger] + extraSpreadDeg) * Mathf.Deg2Rad;
            return (Mathf.Cos(a) * F + Mathf.Sin(a) * T).normalized;
        }

        static Quaternion FilterQuaternion(OneEuroFilter[] filters, Quaternion previous, Quaternion target, double time, bool hasPrevious)
        {
            // Keep the target in the same hemisphere as the running estimate so the filter never takes the long way round.
            if (hasPrevious && Quaternion.Dot(previous, target) < 0f) target = new Quaternion(-target.x, -target.y, -target.z, -target.w);
            var q = new Quaternion(
                filters[0].Filter(target.x, time),
                filters[1].Filter(target.y, time),
                filters[2].Filter(target.z, time),
                filters[3].Filter(target.w, time));
            var mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f) return target;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        readonly HandState m_ProbeLeft = new HandState { Sign = -1f };
        readonly HandState m_ProbeRight = new HandState { Sign = 1f };
        readonly float[] m_ProbeAngles = new float[5 * 4];
        readonly Vector3[] m_ProbeLandmarks = new Vector3[HandFrame.LandmarkCount];

        /// <summary>
        /// Diagnostics: fit the model to one detection and rebuild it with no filtering. Returns the index-tip residual
        /// in metres; <paramref name="meanResidual"/> is the mean over all 21 landmarks and <paramref name="indexLengthRatio"/>
        /// the detected index finger length over the template's at the fitted scale (1 = same proportions).
        /// </summary>
        public float FitResidual(HandFrame detected, bool right, out float meanResidual, out float indexLengthRatio)
        {
            var s = right ? m_ProbeRight : m_ProbeLeft;
            var live = right ? m_Right : m_Left;
            System.Array.Copy(live.McpOffset, s.McpOffset, s.McpOffset.Length);
            for (var f = 0; f < 5; f++) System.Array.Copy(live.SegmentScale[f], s.SegmentScale[f], 3);
            SolveAngles(detected, s, m_ProbeAngles, out var palm, out var scale);
            s.Palm = palm;
            s.ShownPalm = palm;
            s.Scale = scale;
            System.Array.Copy(m_ProbeAngles, s.Shown, m_ProbeAngles.Length);
            System.Array.Copy(m_ProbeAngles, s.Angles, m_ProbeAngles.Length);
            Reconstruct(s, m_ProbeLandmarks);
            var sum = 0f;
            for (var i = 0; i < HandFrame.LandmarkCount; i++) sum += Vector3.Distance(m_ProbeLandmarks[i], detected.Landmarks[i]);
            meanResidual = sum / HandFrame.LandmarkCount;
            var L = detected.Landmarks;
            var detectedIndex = Vector3.Distance(L[5], L[6]) + Vector3.Distance(L[6], L[7]) + Vector3.Distance(L[7], L[8]);
            var templateIndex = (k_SegmentLength[1][0] + k_SegmentLength[1][1] + k_SegmentLength[1][2]) * (scale / k_TemplatePalm);
            indexLengthRatio = templateIndex > 1e-6f ? detectedIndex / templateIndex : 0f;
            return Vector3.Distance(m_ProbeLandmarks[(int)HandFrame.Landmark.IndexTip], L[(int)HandFrame.Landmark.IndexTip]);
        }

        /// <summary>Build a relaxed template hand for tests and previews (right hand when <paramref name="right"/>).</summary>
        public static void TemplateHand(bool right, Quaternion palm, float scale, Vector3[] outLandmarks, float curlDegrees = 0f)
        {
            var s = new HandState { Sign = right ? 1f : -1f, FilteredPalm = palm, ShownPalm = palm, Scale = scale };
            for (var finger = 0; finger < 5; finger++)
            {
                var b = finger * 4;
                s.Shown[b + 1] = curlDegrees;
                s.Shown[b + 2] = curlDegrees * 1.2f;
                s.Shown[b + 3] = curlDegrees * 0.7f;
            }
            new HandStabilizer(Settings.Default).Reconstruct(s, outLandmarks);
        }
    }
}
