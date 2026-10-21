using System;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace MimicMocap.BlazePose
{
    /// <summary>
    /// MediaPipe BlazePose GHUM running on Unity Inference Engine.
    /// Detector (224px) finds the person; landmarker (256px) runs on a rotated square ROI and
    /// returns 33 landmarks + 6 auxiliary. Once tracking, the ROI comes from the previous frame's
    /// auxiliary landmarks and the detector is skipped until the person is lost.
    /// Uses the metric world-landmark output (hip-centred, metres) for 3D.
    /// </summary>
    public sealed class BlazePoseSource : IPoseSource
    {
        public enum Variant { Lite, Full, Heavy }

        const int k_NumAnchors = 2254;
        const int k_DetectorSize = 224;
        const int k_LandmarkerSize = 256;
        const float k_RoiScale = 1.25f;

        readonly ModelAsset m_DetectorAsset;
        readonly ModelAsset m_LandmarkerAsset;
        readonly TextAsset m_AnchorsCsv;
        readonly string m_VariantName;

        public float DetectorThreshold = 0.5f;
        public float PresenceThreshold = 0.5f;
        public BackendType Backend = BackendType.GPUCompute;

        float[,] m_Anchors;
        Worker m_Detector;
        Worker m_Landmarker;
        Tensor<float> m_DetectorInput;
        Tensor<float> m_LandmarkerInput;

        // Tracking state (texture pixel space, y-up)
        bool m_HasRoi;
        float2 m_RoiCenter;
        float2 m_RoiScalePoint;
        // MediaPipe smooths the auxiliary (ROI) landmarks hard (pose_landmark_filtering.pbtxt: min_cutoff 0.01,
        // beta 10, scaled by the ROI size) so the crop barely moves while the person stands still; a crop that
        // creeps makes every landmark creep with it.
        readonly ScaledOneEuroFilter2 m_RoiCenterFilter = new ScaledOneEuroFilter2(0.01f, 10f, 1f);
        readonly ScaledOneEuroFilter2 m_RoiScaleFilter = new ScaledOneEuroFilter2(0.01f, 10f, 1f);

        readonly PoseFrame m_Result = new PoseFrame();
        bool m_HasNewResult;
        bool m_Disposed;

        public BlazePoseSource(ModelAsset detector, ModelAsset landmarker, TextAsset anchorsCsv, Variant variant)
        {
            m_DetectorAsset = detector;
            m_LandmarkerAsset = landmarker;
            m_AnchorsCsv = anchorsCsv;
            m_VariantName = variant.ToString();
        }

        public string Name => "BlazePose " + m_VariantName;
        public bool IsAvailable { get; private set; }
        public bool IsBusy { get; private set; }
        public string Status { get; private set; } = "not initialised";

        /// <summary>Debug: last raw 2D landmarks in normalised texture coords (x right, y up), 39 entries.</summary>
        public readonly Vector2[] LastImageLandmarks = new Vector2[BlazePoseLayout.TotalLandmarks];
        public readonly float[] LastVisibility = new float[BlazePoseLayout.TotalLandmarks];
        public bool DetectorRanLastFrame { get; private set; }
        public float LastDetectorScore { get; private set; }
        public float LastPresence { get; private set; }

        public void Initialize()
        {
            try
            {
                m_Anchors = BlazeUtils.LoadAnchors(m_AnchorsCsv.text, k_NumAnchors);

                var detectorModel = ModelLoader.Load(m_DetectorAsset);
                var graph = new FunctionalGraph();
                var input = graph.AddInput(detectorModel, 0);
                var outputs = Functional.Forward(detectorModel, input);
                var filtered = BlazeUtils.ArgMaxFiltering(outputs[0], outputs[1]);
                detectorModel = graph.Compile(filtered.idx, filtered.score, filtered.box);
                m_Detector = new Worker(detectorModel, Backend);

                var landmarkerModel = ModelLoader.Load(m_LandmarkerAsset);
                m_Landmarker = new Worker(landmarkerModel, Backend);

                m_DetectorInput = new Tensor<float>(new TensorShape(1, k_DetectorSize, k_DetectorSize, 3));
                m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerSize, k_LandmarkerSize, 3));

                IsAvailable = true;
                Status = $"{m_VariantName} on {Backend}";
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = "init failed: " + e.Message;
                Debug.LogException(e);
            }
        }

        public void Submit(Texture frame, double timestamp)
        {
            if (!IsAvailable || IsBusy || m_Disposed || frame == null) return;
            IsBusy = true;
            RunAsync(frame, timestamp);
        }

        async void RunAsync(Texture frame, double timestamp)
        {
            try
            {
                await Process(frame, timestamp);
            }
            catch (Exception e)
            {
                if (!m_Disposed)
                {
                    Status = "error: " + e.Message;
                    Debug.LogException(e);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        public bool TryGetLatest(PoseFrame frame)
        {
            if (!m_HasNewResult) return false;
            m_Result.CopyTo(frame);
            m_HasNewResult = false;
            return true;
        }

        public void ResetTracking()
        {
            m_HasRoi = false;
            m_RoiCenterFilter.Reset();
            m_RoiScaleFilter.Reset();
        }

        async Awaitable Process(Texture texture, double timestamp)
        {
            var texW = texture.width;
            var texH = texture.height;
            DetectorRanLastFrame = false;

            if (!m_HasRoi)
            {
                DetectorRanLastFrame = true;
                if (!await RunDetector(texture, texW, texH))
                {
                    PublishUntracked(timestamp);
                    return;
                }
            }

            // ROI -> landmarker input affine. Crop is centred on m_RoiCenter, "up" points at m_RoiScalePoint.
            var delta = m_RoiScalePoint - m_RoiCenter;
            var radius = k_RoiScale * math.length(delta);
            if (radius < 4f)
            {
                m_HasRoi = false;
                PublishUntracked(timestamp);
                return;
            }
            var theta = math.atan2(delta.y, delta.x);
            var origin = new float2(0.5f * k_LandmarkerSize, 0.5f * k_LandmarkerSize);
            var scale = radius / (0.5f * k_LandmarkerSize);
            var rot = 0.5f * Mathf.PI - theta;
            var M2 = BlazeUtils.Mul(
                BlazeUtils.Mul(
                    BlazeUtils.Mul(BlazeUtils.TranslationMatrix(m_RoiCenter), BlazeUtils.ScaleMatrix(new float2(scale, -scale))),
                    BlazeUtils.RotationMatrix(rot)),
                BlazeUtils.TranslationMatrix(-origin));

            BlazeUtils.SampleImageAffine(texture, m_LandmarkerInput, M2);
            m_Landmarker.Schedule(m_LandmarkerInput);

            var lmAwait = (m_Landmarker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();
            var flagAwait = (m_Landmarker.PeekOutput("Identity_1") as Tensor<float>).ReadbackAndCloneAsync();
            var worldAwait = (m_Landmarker.PeekOutput("Identity_4") as Tensor<float>).ReadbackAndCloneAsync();
            using var landmarks = await lmAwait;   // (1,195): 39 x (x,y,z,vis,presence)
            using var flag = await flagAwait;      // (1,1)
            using var world = await worldAwait;    // (1,117): 39 x (x,y,z) metres, hip-centred

            if (m_Disposed) return;

            var presence = flag[0];
            if (presence < 0f || presence > 1f) presence = BlazeUtils.Sigmoid(presence); // guard if raw logit
            LastPresence = presence;
            if (presence < PresenceThreshold)
            {
                m_HasRoi = false;
                PublishUntracked(timestamp);
                return;
            }

            // 2D landmarks in texture pixel space, then the next-frame ROI from the auxiliary landmarks.
            for (var i = 0; i < BlazePoseLayout.TotalLandmarks; i++)
            {
                var p = BlazeUtils.Mul(M2, new float2(landmarks[5 * i + 0], landmarks[5 * i + 1]));
                LastImageLandmarks[i] = new Vector2(p.x / texW, p.y / texH);
                LastVisibility[i] = BlazeUtils.Sigmoid(landmarks[5 * i + 3]);
            }
            var center = BlazeUtils.Mul(M2, new float2(landmarks[5 * (int)BlazeLandmark.AuxCenter + 0], landmarks[5 * (int)BlazeLandmark.AuxCenter + 1]));
            var scalePt = BlazeUtils.Mul(M2, new float2(landmarks[5 * (int)BlazeLandmark.AuxScale + 0], landmarks[5 * (int)BlazeLandmark.AuxScale + 1]));
            var roiSize = math.max(1f, 2f * k_RoiScale * math.length(scalePt - center));
            var smoothCenter = m_RoiCenterFilter.Filter(new Vector2(center.x, center.y), roiSize, timestamp);
            var smoothScale = m_RoiScaleFilter.Filter(new Vector2(scalePt.x, scalePt.y), roiSize, timestamp);
            m_RoiCenter = new float2(smoothCenter.x, smoothCenter.y);
            m_RoiScalePoint = new float2(smoothScale.x, smoothScale.y);
            m_HasRoi = true;

            // 3D: world landmarks are in the crop frame (x right, y down, z away from camera).
            // Rotate crop-frame xy into texture orientation (y-up) using M2's linear part (without scale),
            // then into the canonical frame: +X = subject's right (= texture -x), +Y up, +Z toward camera.
            var c = math.cos(rot);
            var s = math.sin(rot);
            m_Result.Clear();
            for (var i = 0; i < BlazePoseLayout.BodyLandmarks; i++)
            {
                var canon = BlazePoseLayout.ToCanonical[i];
                if (canon < 0) continue;
                var wx = world[3 * i + 0];
                var wy = world[3 * i + 1];
                var wz = world[3 * i + 2];
                // linear part of M2 with unit scale: [[c, -s],[-s, -c]]  (rotation then y flip)
                var tx = c * wx - s * wy;
                var ty = -s * wx - c * wy;
                m_Result.Joints[canon] = new Vector3(-tx, ty, -wz);
                m_Result.Confidence[canon] = LastVisibility[i] * presence;
                m_Result.ImageJoints[canon] = LastImageLandmarks[i];
            }
            m_Result.HasImageJoints = true;
            // BlazePose has no fingertip: use the index knuckle as the hand tip for the coarse hand fork.
            CopyJoint(HumanJoint.LeftIndex, HumanJoint.LeftHandTip);
            CopyJoint(HumanJoint.RightIndex, HumanJoint.RightHandTip);
            m_Result.SynthesiseTorso();
            m_Result.Tracked = true;
            m_Result.Score = presence;
            m_Result.Timestamp = timestamp;
            m_Result.ImageBox = ImageBoxFromLandmarks();
            m_Result.RootOffset = EstimateRootOffset(texW, texH);
            m_HasNewResult = true;
            Status = $"{m_VariantName} tracking ({presence:0.00})";
        }

        async Awaitable<bool> RunDetector(Texture texture, int texW, int texH)
        {
            var size = Mathf.Max(texW, texH);
            var scale = size / (float)k_DetectorSize;
            // Letterbox: map the 224 square onto the centred max-square of the texture, y flipped (texture is y-up).
            var M = BlazeUtils.Mul(
                BlazeUtils.TranslationMatrix(0.5f * (new float2(texW, texH) + new float2(-size, size))),
                BlazeUtils.ScaleMatrix(new float2(scale, -scale)));
            BlazeUtils.SampleImageAffine(texture, m_DetectorInput, M);
            m_Detector.Schedule(m_DetectorInput);

            var idxAwait = (m_Detector.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
            var scoreAwait = (m_Detector.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();
            var boxAwait = (m_Detector.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();
            using var idxT = await idxAwait;
            using var scoreT = await scoreAwait;
            using var boxT = await boxAwait;
            if (m_Disposed) return false;

            LastDetectorScore = scoreT[0];
            if (scoreT[0] < DetectorThreshold) return false;

            var idx = idxT[0];
            var anchor = k_DetectorSize * new float2(m_Anchors[idx, 0], m_Anchors[idx, 1]);
            // Box layout: cx, cy, w, h, then 4 keypoints (x,y). kp0 = body centre, kp1 = scale/rotation point.
            var kp0 = BlazeUtils.Mul(M, anchor + new float2(boxT[0, 0, 4], boxT[0, 0, 5]));
            var kp1 = BlazeUtils.Mul(M, anchor + new float2(boxT[0, 0, 6], boxT[0, 0, 7]));
            m_RoiCenter = kp0;
            m_RoiScalePoint = kp1;
            m_HasRoi = true;
            // A fresh detection starts the ROI filters over; the first tracked frame seeds them.
            m_RoiCenterFilter.Reset();
            m_RoiScaleFilter.Reset();
            return true;
        }

        /// <summary>
        /// World landmarks are hip-centred, so sideways travel comes from the 2D hip position.
        /// The image's metric width at the subject's depth is inferred from torso size in pixels vs metres.
        /// </summary>
        Vector3 EstimateRootOffset(int texW, int texH)
        {
            var lh = (int)BlazeLandmark.LeftHip;
            var rh = (int)BlazeLandmark.RightHip;
            var ls = (int)BlazeLandmark.LeftShoulder;
            var rs = (int)BlazeLandmark.RightShoulder;
            var hipImg = 0.5f * (LastImageLandmarks[lh] + LastImageLandmarks[rh]);
            var shImg = 0.5f * (LastImageLandmarks[ls] + LastImageLandmarks[rs]);
            var torsoImgH = Mathf.Abs(shImg.y - hipImg.y); // fraction of image height
            var torsoM = PoseTransforms.TorsoLength(m_Result);
            if (torsoImgH < 0.02f || torsoM < 0.05f) return Vector3.zero;
            var imageHeightMetres = torsoM / torsoImgH;
            var imageWidthMetres = imageHeightMetres * texW / texH;
            // Canonical +X is the subject's right, which is image-left. Height is the hips' position in the frame in
            // metres at the subject's depth (image y is up); the floor tracker makes it a lift above the floor.
            var x = (0.5f - hipImg.x) * imageWidthMetres;
            var y = (hipImg.y - 0.5f) * imageHeightMetres;
            return new Vector3(x, y, 0f);
        }

        void CopyJoint(HumanJoint from, HumanJoint to)
        {
            m_Result.Joints[(int)to] = m_Result.Joints[(int)from];
            m_Result.Confidence[(int)to] = m_Result.Confidence[(int)from];
            m_Result.ImageJoints[(int)to] = m_Result.ImageJoints[(int)from];
        }

        Rect ImageBoxFromLandmarks()
        {
            var min = new Vector2(1, 1);
            var max = new Vector2(0, 0);
            for (var i = 0; i < BlazePoseLayout.BodyLandmarks; i++)
            {
                if (LastVisibility[i] < 0.3f) continue;
                var p = LastImageLandmarks[i];
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            return max.x > min.x ? new Rect(min, max - min) : new Rect(0, 0, 0, 0);
        }

        void PublishUntracked(double timestamp)
        {
            m_Result.Clear();
            m_Result.Timestamp = timestamp;
            m_HasNewResult = true;
            Status = $"{m_VariantName} searching (det {LastDetectorScore:0.00})";
        }

        public void Dispose()
        {
            m_Disposed = true;
            m_Detector?.Dispose();
            m_Landmarker?.Dispose();
            m_DetectorInput?.Dispose();
            m_LandmarkerInput?.Dispose();
            m_Detector = null;
            m_Landmarker = null;
            IsAvailable = false;
        }
    }
}
