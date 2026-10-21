using System;
using MimicMocap.BlazeHand;
using MimicMocap.BlazePose;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace MimicMocap.Spike
{
    /// <summary>
    /// SPIKE (throwaway): MMPose RTMPose-m hand5 (Apache-2.0, 21 hand keypoints in 2D, SimCC head) run on the same crop
    /// the MediaPipe hand tracker chose, so the two can be compared frame for frame on the install footage.
    ///
    /// Contract from the model package's pipeline.json: input "input" 1x3x256x256, RGB, (x - mean) / std on a 0..255
    /// scale with the ImageNet mean (123.675, 116.28, 103.53) and std (58.395, 57.12, 57.375); outputs simcc_x and
    /// simcc_y (1x21x512, split ratio 2), decoded as argmax / 2 in crop pixels with confidence min(max_x, max_y). The
    /// normalisation, the layout change and the decode all run inside the graph, so 84 numbers come back per hand.
    /// Keypoint order is COCO-WholeBody's hand order, which is MediaPipe's.
    /// </summary>
    public sealed class RtmHandLandmarker : IDisposable
    {
        const int k_Size = 256;
        const float k_Split = 2f;
        const int k_Slots = 3;

        /// <summary>One finished run: 21 points in normalised texture coordinates (y up) with their confidences.</summary>
        public sealed class Result
        {
            public int Hand;
            public double Timestamp;
            public readonly Vector2[] Image = new Vector2[HandFrame.LandmarkCount];
            public readonly float[] Confidence = new float[HandFrame.LandmarkCount];
            public float Score;
            public float Side;
            public bool HasBodyWrist;
            public Vector2 BodyWristPx;
            public int TexW, TexH;
            public float Ms;
        }

        /// <summary>Crop side relative to the MediaPipe tracker's crop (RTMPose was trained on boxes padded 1.25x, MediaPipe's crops are about 2x the hand).</summary>
        public float Scale = 1f;
        /// <summary>Ignore the crop rotation and sample an upright crop (RTMPose is trained on unrotated boxes).</summary>
        public bool Upright;
        public BackendType Backend = BackendType.GPUCompute;

        public event Action<Result> Completed;
        public bool IsAvailable { get; private set; }
        public string Status { get; private set; } = "not initialised";
        public int Runs { get; private set; }
        public int Skipped { get; private set; }

        Model m_Model;
        readonly Worker[,] m_Workers = new Worker[2, k_Slots];
        readonly Tensor<float>[,] m_Inputs = new Tensor<float>[2, k_Slots];
        readonly bool[,] m_Busy = new bool[2, k_Slots];
        bool m_Disposed;

        /// <summary>Side of the square input this instance is fed. 256 samples its own crop; 224 runs directly on the MediaPipe tracker's crop tensor, resized to 256 in the graph.</summary>
        public int InputSize { get; private set; } = k_Size;

        public void Initialize(ModelAsset asset, int inputSize = k_Size)
        {
            InputSize = inputSize;
            try
            {
                var source = ModelLoader.Load(asset);
                var inShape = source.inputs.Count > 0 ? source.inputs[0].shape.ToString() : "?";
                var outNames = "";
                foreach (var o in source.outputs) outNames += (outNames.Length > 0 ? ", " : "") + o.name;
                var graph = new FunctionalGraph();
                var rgb = graph.AddInput<float>(new TensorShape(1, InputSize, InputSize, 3), "rgb01");
                var mean = Functional.Constant(new TensorShape(1, 1, 1, 3), new[] { 123.675f, 116.28f, 103.53f });
                var invStd = Functional.Constant(new TensorShape(1, 1, 1, 3), new[] { 1f / 58.395f, 1f / 57.12f, 1f / 57.375f });
                var normalised = (rgb * 255f - mean) * invStd;
                var nchw = normalised.Permute(new[] { 0, 3, 1, 2 });
                if (InputSize != k_Size) nchw = Functional.Interpolate(nchw, new[] { k_Size, k_Size }, null, "linear");
                var outputs = Functional.Forward(source, nchw);
                var simccX = outputs[0];
                var simccY = outputs[1];
                m_Model = graph.Compile(Functional.ArgMax(simccX, 2), Functional.ArgMax(simccY, 2), Functional.ReduceMax(simccX, 2), Functional.ReduceMax(simccY, 2));
                for (var h = 0; h < 2; h++)
                    for (var s = 0; s < k_Slots; s++)
                    {
                        m_Workers[h, s] = new Worker(m_Model, Backend);
                        m_Inputs[h, s] = new Tensor<float>(new TensorShape(1, InputSize, InputSize, 3));
                    }
                IsAvailable = true;
                Status = $"RTMPose hand5 ready (input {inShape}, outputs {outNames})";
                Debug.Log("[MimicMocap] " + Status);
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = "RTMPose hand5 init failed: " + e.Message;
                Debug.LogWarning("[MimicMocap] " + Status);
            }
        }

        /// <summary>Sample the crop now (the frame keeps advancing) and run the model on it; <see cref="Completed"/> fires when the readback lands.</summary>
        public void Run(Texture frame, HandTracker.CropInfo crop)
        {
            var pending = Start(frame, crop);
            if (pending != null) Forget(pending);
        }

        static async void Forget(Awaitable<Result> pending) => await pending;

        /// <summary>Sample the crop now and start the model; await the result (null on error). Null when unavailable or every slot is busy.</summary>
        /// <summary>
        /// Run on a crop tensor someone else already sampled (1 x InputSize x InputSize x 3, RGB 0..1), whose crop pixels map
        /// to texture pixels through <paramref name="M"/>. The tensor is only read; null when every slot is busy.
        /// </summary>
        public Awaitable<Result> StartOnTensor(Tensor<float> input, float2x3 M, int texW, int texH, HandTracker.CropInfo crop)
        {
            if (!IsAvailable || m_Disposed || input == null) return null;
            var slot = -1;
            for (var s = 0; s < k_Slots; s++) if (!m_Busy[crop.Hand, s]) { slot = s; break; }
            if (slot < 0) { Skipped++; return null; }
            m_Busy[crop.Hand, slot] = true;
            return Infer(crop, slot, M, texW, texH, crop.Side, input);
        }

        public Awaitable<Result> Start(Texture frame, HandTracker.CropInfo crop)
        {
            if (!IsAvailable || m_Disposed || frame == null || crop.Side <= 1f) return null;
            var slot = -1;
            for (var s = 0; s < k_Slots; s++) if (!m_Busy[crop.Hand, s]) { slot = s; break; }
            if (slot < 0) { Skipped++; return null; }

            var side = crop.Side * Scale;
            var up = Upright ? new float2(0f, 1f) : math.normalizesafe(new float2(crop.Up.x, crop.Up.y), new float2(0f, 1f));
            var theta = math.atan2(up.y, up.x);
            var rot = 0.5f * Mathf.PI - theta;
            var origin = new float2(0.5f * InputSize, 0.5f * InputSize);
            var scale = side / InputSize;
            var M = BlazeUtils.Mul(
                BlazeUtils.Mul(
                    BlazeUtils.Mul(BlazeUtils.TranslationMatrix(new float2(crop.Centre.x, crop.Centre.y)), BlazeUtils.ScaleMatrix(new float2(scale, -scale))),
                    BlazeUtils.RotationMatrix(rot)),
                BlazeUtils.TranslationMatrix(-origin));
            var input = m_Inputs[crop.Hand, slot];
            BlazeUtils.SampleImageAffine(frame, input, M);
            m_Busy[crop.Hand, slot] = true;
            return Infer(crop, slot, M, frame.width, frame.height, side, null);
        }

        async Awaitable<Result> Infer(HandTracker.CropInfo crop, int slot, float2x3 M, int texW, int texH, float side, Tensor<float> external)
        {
            var t0 = Time.realtimeSinceStartupAsDouble;
            try
            {
                var worker = m_Workers[crop.Hand, slot];
                worker.Schedule(external ?? m_Inputs[crop.Hand, slot]);
                var ixAwait = (worker.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
                var iyAwait = (worker.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync();
                var vxAwait = (worker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();
                var vyAwait = (worker.PeekOutput(3) as Tensor<float>).ReadbackAndCloneAsync();
                using var ix = await ixAwait;
                using var iy = await iyAwait;
                using var vx = await vxAwait;
                using var vy = await vyAwait;
                if (m_Disposed) return null;
                var r = new Result { Hand = crop.Hand, Timestamp = crop.Timestamp, Side = side, HasBodyWrist = crop.HasBodyWrist, BodyWristPx = crop.BodyWristPx, TexW = texW, TexH = texH };
                var sum = 0f;
                for (var k = 0; k < HandFrame.LandmarkCount; k++)
                {
                    // Model pixels (0..256) back to this instance's input pixels, then through the crop affine.
                    var toInput = InputSize / (float)k_Size;
                    var p = BlazeUtils.Mul(M, new float2(ix[k] / k_Split * toInput, iy[k] / k_Split * toInput));
                    r.Image[k] = new Vector2(p.x / texW, p.y / texH);
                    r.Confidence[k] = Mathf.Min(vx[k], vy[k]);
                    sum += r.Confidence[k];
                }
                r.Score = sum / HandFrame.LandmarkCount;
                r.Ms = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
                Runs++;
                Completed?.Invoke(r);
                return r;
            }
            catch (Exception e)
            {
                if (!m_Disposed) { Status = "RTMPose hand5 error: " + e.Message; Debug.LogException(e); }
                return null;
            }
            finally
            {
                m_Busy[crop.Hand, slot] = false;
            }
        }

        public void Dispose()
        {
            m_Disposed = true;
            for (var h = 0; h < 2; h++)
                for (var s = 0; s < k_Slots; s++)
                {
                    m_Workers[h, s]?.Dispose();
                    m_Workers[h, s] = null;
                    m_Inputs[h, s]?.Dispose();
                    m_Inputs[h, s] = null;
                }
            IsAvailable = false;
        }
    }
}
