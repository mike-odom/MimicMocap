using System;
using System.Collections.Generic;
using MimicMocap.BlazePose;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace MimicMocap.BlazeHand
{
    /// <summary>
    /// MediaPipe hand landmarker (21 points, metric world landmarks) run on rotated square crops placed
    /// around each wrist using the body pose's wrist, index and pinky positions. No palm detector needed.
    /// Runs both hands per submitted frame; results are wrist-relative in the canonical frame.
    ///
    /// A tracked hand is re-cropped from its own landmarks (MediaPipe's tracking mode with Holistic's hysteresis).
    /// A lost hand is searched for with up to three crops at different rotations (knuckle direction, forearm
    /// direction, upright), because the body's guess at the finger direction is the usual reason one hand
    /// re-enters at once while the other never does.
    /// </summary>
    public sealed class HandTracker : IDisposable
    {
        const int k_InputSize = 224;
        const int k_MaxCandidates = 3;

        readonly ModelAsset m_LandmarkerAsset;
        Model m_Model;
        bool m_Disposed;

        /// <summary>Presence needed for a lost hand to be accepted from a body-placed crop.</summary>
        public float PresenceThreshold = 0.35f;
        /// <summary>Crop side as a multiple of the forearm length (elbow to wrist). A hand is roughly 0.7 forearms long.</summary>
        public float ForearmScale = 1.1f;
        /// <summary>Crop side as a multiple of the wrist-to-knuckle distance; the larger of the two estimates wins.</summary>
        public float PalmScale = 3.2f;
        /// <summary>
        /// Crop centre offset from the wrist along the finger direction, as a fraction of the crop side.
        /// MediaPipe's own crops put the wrist near the bottom edge with the hand filling most of the square.
        /// </summary>
        public float CentreShift = 0.35f;
        /// <summary>Crops smaller than this many source pixels are skipped (hand too far away to resolve).</summary>
        public float MinCropPixels = 40f;
        /// <summary>Search crops (lost hand) are this much larger than the body's estimate, so a wrong finger-direction guess still contains the hand.</summary>
        public float SearchScale = 1.3f;
        /// <summary>Search crops sit almost on the wrist (fraction of the crop side along the guessed direction).</summary>
        public float SearchCentreShift = 0.12f;
        /// <summary>Minimum body-joint confidence for the wrist and elbow before a hand is attempted.</summary>
        public float JointConfidenceFloor = 0.4f;
        public float LastLeftCropPixels { get; private set; }
        public float LastRightCropPixels { get; private set; }
        public BackendType Backend = BackendType.GPUCompute;
        /// <summary>When true, the last crop of each hand is kept as a readable 224x224 texture (debug/tests).</summary>
        public bool CaptureCrops;

        readonly HandFrame m_Left = new HandFrame();
        readonly HandFrame m_Right = new HandFrame();
        bool m_HasNewResult;

        public bool IsAvailable { get; private set; }
        /// <summary>No pass can start right now: <see cref="MaxInFlight"/> passes are still waiting for their readback.</summary>
        public bool IsBusy => InFlight >= Mathf.Max(1, MaxInFlight);
        /// <summary>Hand passes dispatched and not yet evaluated.</summary>
        public int InFlight { get { var n = 0; foreach (var p in m_Passes) if (p.InFlight) n++; return n; } }
        public string Status { get; private set; } = "not initialised";
        public float LastMs { get; private set; }
        public Texture2D LastLeftCrop { get; private set; }
        public Texture2D LastRightCrop { get; private set; }
        public float LastLeftPresence { get; private set; }
        public float LastRightPresence { get; private set; }
        /// <summary>Why each hand was or was not tracked on the last frame ("tracked", "no wrist", "presence 0.21", "gate", ...).</summary>
        public string LastLeftReason { get; private set; } = "";
        public string LastRightReason { get; private set; } = "";
        /// <summary>Landmarker runs on the last frame (1 per tracked hand, up to 3 per lost hand).</summary>
        public int LastInferences { get; private set; }
        /// <summary>Completed hand passes since creation; against wall time this is the finger update rate.</summary>
        public int RunCount { get; private set; }
        /// <summary>Diagnostics: each hand's remembered wrist-to-fingertip extent in source pixels (the crop size floor).</summary>
        public float LeftMaxExtent => m_LeftState.MaxExtent;
        public float RightMaxExtent => m_RightState.MaxExtent;
        /// <summary>Diagnostics: whether each hand is being re-cropped from its own previous landmarks.</summary>
        public bool LeftHasRoi => m_LeftState.HasRoi;
        public bool RightHasRoi => m_RightState.HasRoi;
        /// <summary>Diagnostics: each hand's crop "up" (finger direction, texture pixels, y up) from its last detection.</summary>
        public Vector2 LeftUp => new Vector2(m_LeftState.Up.x, m_LeftState.Up.y);
        public Vector2 RightUp => new Vector2(m_RightState.Up.x, m_RightState.Up.y);

        public HandTracker(ModelAsset landmarker)
        {
            m_LandmarkerAsset = landmarker;
        }

        public void Initialize()
        {
            try
            {
                m_Model = ModelLoader.Load(m_LandmarkerAsset);
                var first = AcquirePass();
                if (first == null) throw new InvalidOperationException("could not create a hand pass");
                IsAvailable = true;
                Status = "hands ready";
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = "hand init failed: " + e.Message;
                Debug.LogWarning("[MimicMocap] " + Status);
            }
        }

        /// <summary>Crop affine and metadata captured at submit time, so inference never re-reads the body pose or frame.</summary>
        struct Prepared
        {
            public bool Valid;
            public float2x3 M;
            public float Rot;
            public int TexW, TexH;
            public float Side;
            /// <summary>Crop derived from the hand's own previous landmarks (the keep threshold applies) rather than placed from the body.</summary>
            public bool FromPrevious;
            public bool HasBodyWrist;
            public float2 BodyWristPx;
            public string Label;
            public float2 Centre;
            public float2 Up;
        }

        /// <summary>Where a hand's first-choice crop was placed this pass (texture pixels, y up), for comparing another model on the same crop.</summary>
        public struct CropInfo
        {
            public int Hand;
            public Vector2 Centre;
            public Vector2 Up;
            public float Side;
            public string Label;
            public bool HasBodyWrist;
            public Vector2 BodyWristPx;
            public double Timestamp;
        }

        /// <summary>Raised at submit time for each hand's first-choice crop, with the frame it was sampled from (valid only during the call).</summary>
        public event Action<Texture, CropInfo> CropPrepared;
        /// <summary>Raised when a pass publishes: its timestamp and both hands as the landmarker saw them.</summary>
        public event Action<double, HandFrame, HandFrame> PassPublished;

        readonly PoseFrame m_BodySnapshot = new PoseFrame();

        /// <summary>
        /// Everything one hand pass owns while its GPU work is in flight: its own workers and input crops (one per hand
        /// per candidate, all sampled from the same frame before any await), where the crops were placed, the pending
        /// readbacks and the results. Passes are pipelined: the next frame's crops are dispatched while the previous
        /// pass still waits for its readback, because waiting first held the fingers to about 19 updates a second
        /// against the body's 30 (measured on the install footage). Each pass has its own workers because a worker's
        /// outputs are overwritten by its next run, which with two passes in flight happened before the earlier pass had
        /// read its results back; sharing them put garbage landmarks on the skeleton.
        /// </summary>
        sealed class Pass
        {
            public readonly Worker[,] Workers = new Worker[2, k_MaxCandidates];
            public readonly Tensor<float>[,] Inputs = new Tensor<float>[2, k_MaxCandidates];
            public readonly Prepared[,] Prepared = new Prepared[2, k_MaxCandidates];
            public readonly int[] CandidateCount = new int[2];
            public readonly Awaitable<Tensor<float>>[,] Lm = new Awaitable<Tensor<float>>[2, k_MaxCandidates];
            public readonly Awaitable<Tensor<float>>[,] Flag = new Awaitable<Tensor<float>>[2, k_MaxCandidates];
            public readonly Awaitable<Tensor<float>>[,] World = new Awaitable<Tensor<float>>[2, k_MaxCandidates];
            public readonly HandFrame Left = new HandFrame();
            public readonly HandFrame Right = new HandFrame();
            /// <summary>The body's torso length in source pixels when this pass was submitted (0 when unknown).</summary>
            public float BodyScalePx;
            public string PrepareReasonLeft = "", PrepareReasonRight = "";
            public double Timestamp;
            public int Sequence;
            public bool InFlight;
        }

        /// <summary>Hand passes allowed in flight at once. 2 lets a pass start every frame; 1 is the old wait-then-submit behaviour.</summary>
        public int MaxInFlight = 2;
        readonly List<Pass> m_Passes = new List<Pass>();
        int m_NextSequence;
        int m_PublishedSequence = -1;

        Pass AcquirePass()
        {
            foreach (var p in m_Passes) if (!p.InFlight) return p;
            if (m_Passes.Count >= Mathf.Max(1, MaxInFlight) || m_Model == null) return null;
            var pass = new Pass();
            for (var h = 0; h < 2; h++)
                for (var c = 0; c < k_MaxCandidates; c++)
                {
                    pass.Workers[h, c] = new Worker(m_Model, Backend);
                    pass.Inputs[h, c] = new Tensor<float>(new TensorShape(1, k_InputSize, k_InputSize, 3));
                }
            m_Passes.Add(pass);
            return pass;
        }

        /// <summary>Start tracking both hands for this frame, using the body pose's 2D joints to place the crops.</summary>
        public void Submit(Texture frame, PoseFrame body, double timestamp)
        {
            if (!IsAvailable || IsBusy || m_Disposed || frame == null || body == null || !body.Tracked || !body.HasImageJoints) return;
            var pass = AcquirePass();
            if (pass == null) return;
            // Everything that depends on the body pose or the frame happens now: the caller may mirror or replace
            // the pose next frame, and the webcam texture keeps advancing.
            body.CopyTo(m_BodySnapshot);
            pass.BodyScalePx = TorsoPixels(m_BodySnapshot, frame.width, frame.height);
            pass.CandidateCount[0] = PrepareCandidates(frame, m_BodySnapshot, m_LeftState, HumanJoint.LeftElbow, HumanJoint.LeftWrist, HumanJoint.LeftIndex, HumanJoint.LeftPinky, 0, pass, out pass.PrepareReasonLeft);
            pass.CandidateCount[1] = PrepareCandidates(frame, m_BodySnapshot, m_RightState, HumanJoint.RightElbow, HumanJoint.RightWrist, HumanJoint.RightIndex, HumanJoint.RightPinky, 1, pass, out pass.PrepareReasonRight);
            LastLeftCropPixels = pass.CandidateCount[0] > 0 ? pass.Prepared[0, 0].Side : 0f;
            LastRightCropPixels = pass.CandidateCount[1] > 0 ? pass.Prepared[1, 0].Side : 0f;
            pass.Timestamp = timestamp;
            pass.Sequence = m_NextSequence++;
            pass.InFlight = true;
            if (CropPrepared != null)
            {
                for (var h = 0; h < 2; h++)
                {
                    if (pass.CandidateCount[h] == 0) continue;
                    var p = pass.Prepared[h, 0];
                    CropPrepared(frame, new CropInfo { Hand = h, Centre = new Vector2(p.Centre.x, p.Centre.y), Up = new Vector2(p.Up.x, p.Up.y), Side = p.Side, Label = p.Label, HasBodyWrist = p.HasBodyWrist, BodyWristPx = new Vector2(p.BodyWristPx.x, p.BodyWristPx.y), Timestamp = timestamp });
                }
            }
            RunAsync(pass);
        }

        public bool TryGetLatest(HandFrame left, HandFrame right)
        {
            if (!m_HasNewResult) return false;
            m_Left.CopyTo(left);
            m_Right.CopyTo(right);
            m_HasNewResult = false;
            return true;
        }

        /// <summary>Sample one crop (centre, up direction, side in pixels) into an input tensor.</summary>
        Prepared PrepareOne(Texture frame, float2 centre, float2 up, float side, Tensor<float> input, bool haveWrist, float2 wristPx, string label)
        {
            var texW = frame.width;
            var texH = frame.height;
            var theta = math.atan2(up.y, up.x);
            var rot = 0.5f * Mathf.PI - theta;
            var origin = new float2(0.5f * k_InputSize, 0.5f * k_InputSize);
            var scale = side / k_InputSize;
            var M = BlazeUtils.Mul(
                BlazeUtils.Mul(
                    BlazeUtils.Mul(BlazeUtils.TranslationMatrix(centre), BlazeUtils.ScaleMatrix(new float2(scale, -scale))),
                    BlazeUtils.RotationMatrix(rot)),
                BlazeUtils.TranslationMatrix(-origin));
            BlazeUtils.SampleImageAffine(frame, input, M);
            return new Prepared { Valid = true, M = M, Rot = rot, TexW = texW, TexH = texH, Side = side, HasBodyWrist = haveWrist, BodyWristPx = wristPx, Label = label, Centre = centre, Up = up };
        }

        /// <summary>
        /// Crops to try for one hand this frame. A tracked hand gets exactly its own crop; a lost hand gets up to
        /// three body-placed crops that differ in rotation. Returns how many were prepared.
        /// </summary>
        int PrepareCandidates(Texture frame, PoseFrame body, TrackState state, HumanJoint elbowJ, HumanJoint wristJ, HumanJoint indexJ, HumanJoint pinkyJ, int hand, Pass pass, out string reason)
        {
            var texW = frame.width;
            var texH = frame.height;
            float2 Px(HumanJoint j) => new float2(body.ImageJoints[(int)j].x * texW, body.ImageJoints[(int)j].y * texH);
            var haveWrist = body.ConfidenceOf(wristJ) >= JointConfidenceFloor;
            var wrist = haveWrist ? Px(wristJ) : float2.zero;

            // Tracked: one crop from the hand itself (with the body's wrist pulling it back while it is missing).
            if (state.HasRoi && state.Misses <= MaxMisses)
            {
                BodySize(body, state, texW, texH, elbowJ, wristJ, indexJ, pinkyJ, out var knuckleUpT, out var forearmUpT, out var bodySide);
                var grow = 1f + 0.12f * state.Misses;
                var side = math.max(state.Side, math.max(state.MaxExtent * 1.5f, bodySide * 0.95f)) * grow;
                var centre = state.Centre;
                var up = state.Up;
                if (state.Misses > 0 && haveWrist)
                {
                    // Once the hand has been missed its own finger direction is stale: on the install footage it sat 50 to
                    // 120 degrees off the body's knuckle direction through every miss run (8 degrees on hits), and it was
                    // kept for eight runs before the body was consulted. Take the body's cue on the first miss.
                    if (math.lengthsq(knuckleUpT) > 0f) up = knuckleUpT;
                    else if (math.lengthsq(forearmUpT) > 0f) up = forearmUpT;
                    centre = wrist + up * (side * (CentreShift * 0.6f));
                }
                var tracked = PrepareOne(frame, centre, up, side, pass.Inputs[hand, 0], haveWrist, wrist, "tracked");
                tracked.FromPrevious = true;
                pass.Prepared[hand, 0] = tracked;
                reason = "";
                if (state.Misses > 0 && haveWrist && bodySide > 0f)
                {
                    // A missed hand also gets the body's own wide crop in the same pass (the workers are idle anyway),
                    // instead of waiting out eight misses before the body-placed search begins.
                    var wideSide = math.max(bodySide * SearchScale, MinCropPixels);
                    pass.Prepared[hand, 1] = PrepareOne(frame, wrist + up * (wideSide * SearchCentreShift), up, wideSide, pass.Inputs[hand, 1], true, wrist, "wide body");
                    return 2;
                }
                return 1;
            }

            state.HasRoi = false;
            if (!haveWrist) { reason = "no wrist"; return 0; }

            BodySize(body, state, texW, texH, elbowJ, wristJ, indexJ, pinkyJ, out var knuckleUp, out var forearmUp, out var bodySize);
            // Both hands are the same size: a hand that has never been seen borrows the other hand's remembered extent.
            var other = hand == 0 ? m_RightState : m_LeftState;
            var extent = math.max(state.MaxExtent, other.MaxExtent * 0.9f);
            var size = bodySize;
            if (size <= 0f) size = state.Side;            // no size cue this frame: reuse the last one
            size = math.max(size, extent * 1.5f);
            if (size < MinCropPixels) { reason = $"too small {size:0}px"; return 0; }

            // Candidate directions, most likely first (knuckles, forearm, last known, upright), distinct by 25 degrees.
            var dirCount = 0;
            void Consider(float2 up, string label)
            {
                if (dirCount >= k_MaxCandidates || !math.all(math.isfinite(up)) || math.lengthsq(up) < 1e-6f) return;
                up = math.normalize(up);
                for (var i = 0; i < dirCount; i++)
                    if (math.degrees(math.acos(math.clamp(math.dot(m_CandidateUp[hand, i], up), -1f, 1f))) < 25f) return;
                m_CandidateUp[hand, dirCount] = up;
                m_CandidateLabel[hand, dirCount] = label;
                dirCount++;
            }
            if (math.lengthsq(knuckleUp) > 0f) Consider(knuckleUp, "knuckles");
            if (math.lengthsq(forearmUp) > 0f) Consider(forearmUp, "forearm");
            if (state.Side > 0f && math.lengthsq(state.Up) > 0f) Consider(state.Up, "last");
            Consider(new float2(0f, 1f), "upright");
            if (dirCount == 0) { reason = "no direction"; return 0; }

            // 1. The tight crop shifted along the best guess: the most accurate when the guess is right (small hands
            //    need every pixel). 2. A wider crop centred on the wrist with the same rotation: catches a hand whose
            //    fingers point somewhere else entirely, which a shifted crop misses completely; once found, the tracked
            //    crop tightens again. 3. The tight crop along the next best direction.
            var count = 0;
            var best = m_CandidateUp[hand, 0];
            pass.Prepared[hand, count] = PrepareOne(frame, wrist + best * (size * CentreShift), best, size, pass.Inputs[hand, count], true, wrist, m_CandidateLabel[hand, 0]);
            count++;
            var wide = size * SearchScale;
            pass.Prepared[hand, count] = PrepareOne(frame, wrist + best * (wide * SearchCentreShift), best, wide, pass.Inputs[hand, count], true, wrist, "wide " + m_CandidateLabel[hand, 0]);
            count++;
            if (dirCount > 1)
            {
                var second = m_CandidateUp[hand, 1];
                pass.Prepared[hand, count] = PrepareOne(frame, wrist + second * (size * CentreShift), second, size, pass.Inputs[hand, count], true, wrist, m_CandidateLabel[hand, 1]);
                count++;
            }
            reason = "";
            return count;
        }

        readonly float2[,] m_CandidateUp = new float2[2, k_MaxCandidates];
        readonly string[,] m_CandidateLabel = new string[2, k_MaxCandidates];

        /// <summary>Body-derived finger direction (from the knuckles and from the forearm) and crop size for a hand.</summary>
        void BodySize(PoseFrame body, TrackState state, float texW, float texH, HumanJoint elbowJ, HumanJoint wristJ, HumanJoint indexJ, HumanJoint pinkyJ, out float2 knuckleUp, out float2 forearmUp, out float side)
        {
            float2 Px(HumanJoint j) => new float2(body.ImageJoints[(int)j].x * texW, body.ImageJoints[(int)j].y * texH);
            knuckleUp = float2.zero;
            forearmUp = float2.zero;
            side = 0f;
            if (body.ConfidenceOf(wristJ) < JointConfidenceFloor) return;
            var wrist = Px(wristJ);
            var palmLen = 0f;
            var forearmLen = 0f;
            if (body.ConfidenceOf(indexJ) >= JointConfidenceFloor && body.ConfidenceOf(pinkyJ) >= JointConfidenceFloor)
            {
                var palm = 0.5f * (Px(indexJ) + Px(pinkyJ)) - wrist;
                palmLen = math.length(palm);
                if (palmLen > 2f) knuckleUp = palm / palmLen;
            }
            if (body.ConfidenceOf(elbowJ) >= JointConfidenceFloor)
            {
                var forearm = wrist - Px(elbowJ);
                forearmLen = math.length(forearm);
                if (forearmLen > 2f) forearmUp = forearm / forearmLen;
                // Knuckles too close to the wrist to trust (fingers pointing at the camera): the forearm leads.
                if (palmLen < 0.12f * forearmLen) knuckleUp = float2.zero;
            }
            side = math.max(palmLen * PalmScale, forearmLen * ForearmScale);
        }

        async void RunAsync(Pass pass)
        {
            var t0 = Time.realtimeSinceStartupAsDouble;
            var inferences = 0;
            try
            {
                // Schedule every candidate crop for both hands back to back and queue their readbacks in the same
                // order, so the whole search costs one GPU round trip instead of one per crop.
                for (var hand = 0; hand < 2; hand++)
                {
                    for (var c = 0; c < pass.CandidateCount[hand]; c++)
                    {
                        if (CaptureCrops && c == 0) await CaptureCrop(pass.Inputs[hand, c], hand == 0);
                        if (m_Disposed) return;
                        var worker = pass.Workers[hand, c];
                        worker.Schedule(pass.Inputs[hand, c]);
                        pass.Lm[hand, c] = (worker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();
                        pass.Flag[hand, c] = (worker.PeekOutput("Identity_1") as Tensor<float>).ReadbackAndCloneAsync();
                        pass.World[hand, c] = (worker.PeekOutput("Identity_3") as Tensor<float>).ReadbackAndCloneAsync();
                        inferences++;
                    }
                }
                var leftReason = await EvaluateHand(pass, 0, m_LeftState, pass.Left, true);
                if (m_Disposed) return;
                var rightReason = await EvaluateHand(pass, 1, m_RightState, pass.Right, false);
                if (m_Disposed) return;
                // Passes finish in dispatch order on an in-order GPU; should one ever finish late, it is stale.
                if (pass.Sequence > m_PublishedSequence)
                {
                    m_PublishedSequence = pass.Sequence;
                    pass.Left.CopyTo(m_Left);
                    pass.Right.CopyTo(m_Right);
                    LastLeftReason = leftReason;
                    LastRightReason = rightReason;
                    LastInferences = inferences;
                    m_HasNewResult = true;
                    RunCount++;
                    PassPublished?.Invoke(pass.Timestamp, pass.Left, pass.Right);
                    LastMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
                    Status = $"hands L {Describe(m_Left, LastLeftReason)}/{LastLeftCropPixels:0}px R {Describe(m_Right, LastRightReason)}/{LastRightCropPixels:0}px {LastMs:0.0} ms";
                }
            }
            catch (Exception e)
            {
                if (!m_Disposed)
                {
                    Status = "hand error: " + e.Message;
                    Debug.LogException(e);
                }
            }
            finally
            {
                pass.InFlight = false;
            }
        }

        static string Describe(HandFrame hand, string reason) => hand.Tracked ? hand.Score.ToString("0.00") : (string.IsNullOrEmpty(reason) ? "-" : reason);

        /// <summary>Per-hand tracking memory so the next crop can come from the hand itself, like MediaPipe's tracking mode.</summary>
        sealed class TrackState
        {
            public bool HasRoi;
            public float2 Centre;
            public float2 Up;
            public float Side;
            public int Misses;
            /// <summary>Largest wrist-to-fingertip extent seen recently (px); decays slowly so a foreshortened hand keeps a full-size crop.</summary>
            public float MaxExtent;
            /// <summary>The last MediaPipe detection's wrist-relative 3D landmarks (metres), to lift a 2D fallback detection.</summary>
            public readonly Vector3[] LastWorld = new Vector3[HandFrame.LandmarkCount];
            public bool HasWorld;
            /// <summary>Metres per source pixel of the last MediaPipe detection (wrist to middle knuckle, 3D over 2D).</summary>
            public float MetresPerPixel;
            /// <summary>The body's torso length in pixels on that detection's frame, so the scale can follow the person nearer or further.</summary>
            public float MetresPerPixelBodyPx;
        }

        /// <summary>
        /// SPIKE: a second hand model (RTMPose-m hand5) run on a lost hand's first-choice crop. When every MediaPipe crop
        /// misses, its 2D landmarks are accepted if confident and at the body's wrist, lifted to 3D with the hand's last
        /// MediaPipe scale and depths. On the install footage it found real hands (motion-blurred waves, hands at the
        /// sides) on 43% of the frames MediaPipe missed. Null = off.
        /// </summary>
        public Spike.RtmHandLandmarker Fallback;
        public float FallbackThreshold = 0.35f;
        /// <summary>Hands the fallback supplied since creation.</summary>
        public int FallbackHits { get; private set; }
        /// <summary>Fallback runs started since creation.</summary>
        public int FallbackRuns { get; private set; }

        readonly TrackState m_LeftState = new TrackState();
        readonly TrackState m_RightState = new TrackState();

        /// <summary>Presence needed to keep an already-tracked hand (entry uses PresenceThreshold).</summary>
        public float KeepThreshold = 0.25f;
        /// <summary>Consecutive misses before a hand's own crop is abandoned for the body-derived search.</summary>
        public int MaxMisses = 8;
        /// <summary>Crop side as a multiple of the tracked hand's wrist-to-fingertip extent.</summary>
        public float TrackedHandScale = 2.0f;
        // Crop hysteresis adapted from MediaPipe Holistic's RoiTrackingCalculator (Apache-2.0): keep the previous crop
        // unless it moved by more than a fraction of its size, changed scale, turned, or the hand is no longer inside.
        /// <summary>Re-crop when the wanted centre moved more than this fraction of the crop side (MediaPipe: 0.2).</summary>
        public float RoiTranslationThreshold = 0.2f;
        /// <summary>Re-crop when the wanted size differs by more than this fraction (MediaPipe: 0.4).</summary>
        public float RoiScaleThreshold = 0.4f;
        /// <summary>Re-crop when the finger direction turned more than this in degrees (MediaPipe: 40).</summary>
        public float RoiRotationThreshold = 40f;
        /// <summary>Re-crop when any landmark leaves the crop shrunk by this margin (MediaPipe: -0.1).</summary>
        public float RoiMargin = -0.1f;
        /// <summary>Hands smaller than this many pixels use a lower entry threshold, like XR Animator's far-user mode.</summary>
        public float FarHandPixels = 100f;
        public float FarPresenceThreshold = 0.25f;
        /// <summary>A detected wrist further than this fraction of the crop side from the body's wrist is someone else's hand.</summary>
        public float WristGate = 0.4f;
        /// <summary>
        /// Per-frame decay of the remembered extent while a hand is not found. The extent floors the crop size, and a
        /// stale one (the person walked up close, left, and came back further away) made every crop too big for the
        /// landmarker, which was the only thing that could refresh it: the hand never came back. Fading it on misses
        /// lets the body's own size estimate take over within a dozen frames.
        /// </summary>
        public float MissDecay = 0.93f;

        public void ResetTracking()
        {
            m_LeftState.HasRoi = false;
            m_RightState.HasRoi = false;
            m_LeftState.MaxExtent = 0f;
            m_RightState.MaxExtent = 0f;
            m_LeftState.Side = 0f;
            m_RightState.Side = 0f;
            m_LeftState.HasWorld = false;
            m_RightState.HasWorld = false;
        }

        /// <summary>Collect the already-scheduled candidate runs for a hand, in order, until one is accepted. Returns the reason when none is.</summary>
        async Awaitable<string> EvaluateHand(Pass pass, int hand, TrackState state, HandFrame result, bool isLeft)
        {
            result.Clear();
            result.Timestamp = pass.Timestamp;
            var count = pass.CandidateCount[hand];
            if (count == 0)
            {
                if (isLeft) LastLeftPresence = 0f; else LastRightPresence = 0f;
                state.MaxExtent *= MissDecay;
                return isLeft ? pass.PrepareReasonLeft : pass.PrepareReasonRight;
            }
            var reason = "";
            var accepted = false;
            for (var c = 0; c < count; c++)
            {
                // Every queued readback is awaited (and its clone disposed) even after a hit, so nothing leaks.
                using var landmarks = await pass.Lm[hand, c];   // (1,63): 21 x (x,y,z) in crop pixels
                using var flag = await pass.Flag[hand, c];      // (1,1) presence
                using var world = await pass.World[hand, c];    // (1,63): 21 x (x,y,z) metres, hand-centred
                if (m_Disposed) return "disposed";
                if (accepted) continue;
                var (ok, why) = Evaluate(pass.Prepared[hand, c], landmarks, flag, world, state, result, isLeft);
                if (ok) { accepted = true; reason = "tracked"; state.MetresPerPixelBodyPx = pass.BodyScalePx; }
                else { reason = why; result.Clear(); result.Timestamp = pass.Timestamp; }
            }
            // Every MediaPipe crop missed: run the fallback model on the first-choice crop, which is still on the GPU as this
            // pass's input tensor (MediaPipe's crop, resized in the fallback's graph). One extra round trip, misses only.
            if (!accepted && Fallback != null && Fallback.InputSize == k_InputSize)
            {
                var p = pass.Prepared[hand, 0];
                var pending = Fallback.StartOnTensor(pass.Inputs[hand, 0], p.M, p.TexW, p.TexH, new CropInfo { Hand = hand, Centre = new Vector2(p.Centre.x, p.Centre.y), Up = new Vector2(p.Up.x, p.Up.y), Side = p.Side, Label = p.Label, HasBodyWrist = p.HasBodyWrist, BodyWristPx = new Vector2(p.BodyWristPx.x, p.BodyWristPx.y), Timestamp = pass.Timestamp });
                if (pending != null) FallbackRuns++;
                var rtm = pending != null ? await pending : null;
                if (m_Disposed) return "disposed";
                if (rtm != null && AcceptFallback(rtm, p, state, result, pass.BodyScalePx))
                {
                    accepted = true;
                    reason = "tracked (rtm)";
                    FallbackHits++;
                }
            }
            // Every candidate failed: count one miss for a hand that was being tracked, and let the remembered size fade.
            if (!accepted)
            {
                state.MaxExtent *= MissDecay;
                if (state.HasRoi) state.Misses++;
            }
            return reason;
        }

        /// <summary>Interpret one landmarker run on one crop. Returns whether the hand was accepted and, if not, why.</summary>
        (bool accepted, string reason) Evaluate(Prepared prep, Tensor<float> landmarks, Tensor<float> flag, Tensor<float> world, TrackState state, HandFrame result, bool isLeft)
        {
            var texW = prep.TexW;
            var texH = prep.TexH;
            var M = prep.M;
            var rot = prep.Rot;

            var presence = flag[0];
            if (presence < 0f || presence > 1f) presence = BlazeUtils.Sigmoid(presence);
            if (isLeft) LastLeftPresence = presence; else LastRightPresence = presence;
            var entry = prep.Side < FarHandPixels ? math.min(PresenceThreshold, FarPresenceThreshold) : PresenceThreshold;
            var threshold = prep.FromPrevious ? math.min(KeepThreshold, entry) : entry;
            if (presence < threshold) return (false, $"presence {presence:0.00} ({prep.Label})");

            // Gate: the detected wrist must sit near the body's wrist, or this is someone else's hand in the crop.
            if (prep.HasBodyWrist)
            {
                var detectedWrist = BlazeUtils.Mul(M, new float2(landmarks[0], landmarks[1]));
                if (math.length(detectedWrist - prep.BodyWristPx) > WristGate * prep.Side) return (false, $"gate ({prep.Label})");
            }

            var c = math.cos(rot);
            var s = math.sin(rot);
            var wristPx = float2.zero;
            var middleMcpPx = float2.zero;
            var extent = 0f;
            for (var i = 0; i < HandFrame.LandmarkCount; i++)
            {
                var p = BlazeUtils.Mul(M, new float2(landmarks[3 * i + 0], landmarks[3 * i + 1]));
                result.ImageLandmarks[i] = new Vector2(p.x / texW, p.y / texH);
                if (i == 0) wristPx = p;
                if (i == (int)HandFrame.Landmark.MiddleMcp) middleMcpPx = p;
                extent = math.max(extent, math.length(p - wristPx));

                // World landmarks are in the crop frame (x right, y down, z away): rotate into texture orientation
                // with the y flip (same linear part as M without scale), then into the canonical frame.
                var wx = world[3 * i + 0];
                var wy = world[3 * i + 1];
                var wz = world[3 * i + 2];
                var tx = c * wx - s * wy;
                var ty = -s * wx - c * wy;
                result.Landmarks[i] = new Vector3(-tx, ty, -wz);
            }
            var wristPos = result.Landmarks[0];
            for (var i = 0; i < HandFrame.LandmarkCount; i++)
            {
                result.Landmarks[i] -= wristPos;
                var l = result.Landmarks[i];
                var im = result.ImageLandmarks[i];
                if (!float.IsFinite(l.x) || !float.IsFinite(l.y) || !float.IsFinite(l.z) || !float.IsFinite(im.x) || !float.IsFinite(im.y) || l.sqrMagnitude > 4f)
                    return (false, $"garbage ({prep.Label})");
            }
            result.Tracked = true;
            result.HasImageLandmarks = true;
            result.Score = presence;
            System.Array.Copy(result.Landmarks, state.LastWorld, HandFrame.LandmarkCount);
            var palmPx = math.length(middleMcpPx - wristPx);
            if (palmPx > 2f) { state.MetresPerPixel = result.Landmarks[(int)HandFrame.Landmark.MiddleMcp].magnitude / palmPx; state.HasWorld = true; }
            UpdateRoi(result, state, texW, texH, extent, wristPx, middleMcpPx);
            return (true, "tracked");
        }

        /// <summary>
        /// SPIKE: accept a fallback (2D) detection when MediaPipe missed. It must be confident and at the body's wrist; its
        /// 2D landmarks become the image landmarks, and the 3D ones are the 2D offsets from the wrist at the hand's last
        /// MediaPipe scale (canonical +X is image-left, +Y image-up) with the last MediaPipe depths.
        /// </summary>
        bool AcceptFallback(Spike.RtmHandLandmarker.Result r, Prepared prep, TrackState state, HandFrame result, float bodyScalePx)
        {
            if (r.Score < FallbackThreshold) return false;
            var texW = prep.TexW;
            var texH = prep.TexH;
            float2 Px(int k) => new float2(r.Image[k].x * texW, r.Image[k].y * texH);
            var wristPx = Px(0);
            var middleMcpPx = Px((int)HandFrame.Landmark.MiddleMcp);
            if (prep.HasBodyWrist && math.length(wristPx - prep.BodyWristPx) > WristGate * prep.Side) return false;
            var extent = 0f;
            for (var k = 0; k < HandFrame.LandmarkCount; k++) extent = math.max(extent, math.length(Px(k) - wristPx));
            if (extent < 4f) return false;
            // Metres per pixel: the last MediaPipe scale, else a 9 cm wrist-to-middle-knuckle palm.
            var palmPx = math.max(math.length(middleMcpPx - wristPx), 0.35f * extent);
            // The remembered scale is rescaled by how much bigger or smaller the body is in the image now than on that detection's
            // frame: someone who walked out and came back further away has smaller hands in pixels, so more metres per pixel.
            // Without the rescale a returning person's fallback hands were drawn at their old distance's scale.
            var mpp = state.HasWorld && state.MetresPerPixel > 0f ? state.MetresPerPixel : 0.09f / math.max(1f, palmPx);
            if (state.HasWorld && state.MetresPerPixelBodyPx > 1f && bodyScalePx > 1f)
                mpp *= math.clamp(state.MetresPerPixelBodyPx / bodyScalePx, 0.25f, 4f);
            for (var k = 0; k < HandFrame.LandmarkCount; k++)
            {
                var d = Px(k) - wristPx;
                var z = state.HasWorld ? state.LastWorld[k].z : 0f;
                result.Landmarks[k] = new Vector3(-d.x * mpp, d.y * mpp, z);
                result.ImageLandmarks[k] = r.Image[k];
            }
            result.Tracked = true;
            result.HasImageLandmarks = true;
            result.Score = r.Score;
            UpdateRoi(result, state, texW, texH, extent, wristPx, middleMcpPx);
            return true;
        }

        /// <summary>Distance from the mid-shoulders to the mid-hips in source pixels, or 0 when the body does not show them confidently.</summary>
        float TorsoPixels(PoseFrame body, int texW, int texH)
        {
            foreach (var j in new[] { HumanJoint.LeftShoulder, HumanJoint.RightShoulder, HumanJoint.LeftHip, HumanJoint.RightHip })
                if (body.ConfidenceOf(j) < JointConfidenceFloor) return 0f;
            Vector2 Px(HumanJoint j) => new Vector2(body.ImageJoints[(int)j].x * texW, body.ImageJoints[(int)j].y * texH);
            return Vector2.Distance(0.5f * (Px(HumanJoint.LeftShoulder) + Px(HumanJoint.RightShoulder)), 0.5f * (Px(HumanJoint.LeftHip) + Px(HumanJoint.RightHip)));
        }

        /// <summary>Place the next frame's crop from this frame's detection.</summary>
        void UpdateRoi(HandFrame result, TrackState state, int texW, int texH, float extent, float2 wristPx, float2 middleMcpPx)
        {
            // Next frame's crop comes from this hand. Centre on the landmark centroid (robust when the fingers point at
            // the camera), size from the extent floored by the remembered size, and only follow the finger direction
            // when it is long enough in the image to be trustworthy.
            var centroid = float2.zero;
            for (var i = 0; i < HandFrame.LandmarkCount; i++)
                centroid += new float2(result.ImageLandmarks[i].x * texW, result.ImageLandmarks[i].y * texH);
            centroid /= HandFrame.LandmarkCount;
            state.MaxExtent = math.max(extent, state.MaxExtent * 0.985f);
            var dir = middleMcpPx - wristPx;
            var dirLen = math.length(dir);
            var newUp = state.HasRoi ? state.Up : new float2(0f, 1f);
            if (dirLen > math.max(1f, 0.25f * state.MaxExtent) || state.Side <= 0f || !state.HasRoi)
                newUp = dirLen > 1e-3f ? dir / dirLen : new float2(0f, 1f);
            var newSide = math.max(math.max(extent * TrackedHandScale, state.MaxExtent * 1.5f), MinCropPixels);
            var newCentre = centroid + newUp * (newSide * 0.08f);
            if (state.HasRoi && state.Misses == 0 && state.Side > 0f)
            {
                // The landmarker's output shifts a little with every crop change, so keep the crop where it is until
                // the hand really moved, grew, turned, or is about to leave it (MediaPipe Holistic's rule).
                var translation = math.length(newCentre - state.Centre) / state.Side;
                var scaleChange = math.abs(newSide / state.Side - 1f);
                var turn = math.degrees(math.acos(math.clamp(math.dot(state.Up, newUp), -1f, 1f)));
                var inside = LandmarksInsideCrop(result, state, texW, texH);
                if (translation > RoiTranslationThreshold || scaleChange > RoiScaleThreshold || turn > RoiRotationThreshold || !inside)
                {
                    state.Centre = newCentre;
                    state.Side = newSide;
                    state.Up = newUp;
                }
            }
            else
            {
                state.Up = newUp;
                state.Side = newSide;
                state.Centre = newCentre;
            }
            state.HasRoi = true;
            state.Misses = 0;
        }

        /// <summary>All 21 image landmarks inside the current crop square shrunk by <see cref="RoiMargin"/>.</summary>
        bool LandmarksInsideCrop(HandFrame hand, TrackState state, int texW, int texH)
        {
            var half = 0.5f * state.Side * (1f + RoiMargin);
            var up = state.Up;
            var side = new float2(up.y, -up.x);
            for (var i = 0; i < HandFrame.LandmarkCount; i++)
            {
                var p = new float2(hand.ImageLandmarks[i].x * texW, hand.ImageLandmarks[i].y * texH) - state.Centre;
                if (math.abs(math.dot(p, up)) > half || math.abs(math.dot(p, side)) > half) return false;
            }
            return true;
        }

        async Awaitable CaptureCrop(Tensor<float> input, bool isLeft)
        {
            using var cpu = await input.ReadbackAndCloneAsync();
            if (m_Disposed) return;
            var tex = isLeft ? LastLeftCrop : LastRightCrop;
            if (tex == null)
            {
                tex = new Texture2D(k_InputSize, k_InputSize, TextureFormat.RGBA32, false);
                if (isLeft) LastLeftCrop = tex; else LastRightCrop = tex;
            }
            var pixels = new Color32[k_InputSize * k_InputSize];
            for (var y = 0; y < k_InputSize; y++)
                for (var x = 0; x < k_InputSize; x++)
                {
                    // Tensor rows are top-down; Texture2D rows are bottom-up.
                    var r = cpu[0, y, x, 0];
                    var g = cpu[0, y, x, 1];
                    var b = cpu[0, y, x, 2];
                    pixels[(k_InputSize - 1 - y) * k_InputSize + x] = new Color32((byte)(Mathf.Clamp01(r) * 255), (byte)(Mathf.Clamp01(g) * 255), (byte)(Mathf.Clamp01(b) * 255), 255);
                }
            tex.SetPixels32(pixels);
            tex.Apply();
        }

        public void Dispose()
        {
            m_Disposed = true;
            foreach (var pass in m_Passes)
                for (var h = 0; h < 2; h++)
                    for (var c = 0; c < k_MaxCandidates; c++)
                    {
                        pass.Workers[h, c]?.Dispose();
                        pass.Workers[h, c] = null;
                        pass.Inputs[h, c]?.Dispose();
                        pass.Inputs[h, c] = null;
                    }
            m_Passes.Clear();
            IsAvailable = false;
            if (LastLeftCrop != null) UnityEngine.Object.Destroy(LastLeftCrop);
            if (LastRightCrop != null) UnityEngine.Object.Destroy(LastRightCrop);
        }
    }
}
