using System;
using MimicMocap.BlazePose;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace MimicMocap.Spike
{
    /// <summary>
    /// SPIKE (throwaway): MMPose RTMW3D-x (Apache-2.0), 133 COCO-WholeBody keypoints (17 body, 6 feet, 68 face, 21 + 21
    /// hands) with root-relative depth, run top-down on a person box. Contract from rtmlib's RTMPose3d: input 1x3x384x288
    /// (H x W), RGB, ImageNet mean/std on a 0..255 scale; box padded 1.25x and widened to the 3:4 aspect; outputs
    /// simcc_x, simcc_y, simcc_z with split ratio 2; x and y are crop pixels (argmax / 2), z is
    /// (argmax / 2 / 192 - 1) x 2.1744869 metres. Normalisation, layout and decode run inside the graph.
    /// </summary>
    public sealed class RtmBodyLandmarker : IDisposable
    {
        public const int Keypoints = 133;
        const int k_W = 288, k_H = 384;
        const float k_Split = 2f;
        const float k_ZRange = 2.1744869f;
        const int k_Slots = 2;

        public sealed class Result
        {
            public double Timestamp;
            public readonly Vector2[] Image = new Vector2[Keypoints];   // normalised texture coords, y up
            public readonly float[] Z = new float[Keypoints];           // metres, root-relative, away from camera positive
            public readonly float[] Confidence = new float[Keypoints];
            public float BodyScore, HandScore;
            public float Ms;
            public Rect Box;   // normalised, y up
        }

        public BackendType Backend = BackendType.GPUCompute;
        public event Action<Result> Completed;
        public bool IsAvailable { get; private set; }
        public string Status { get; private set; } = "not initialised";
        public int Runs { get; private set; }
        public int Skipped { get; private set; }

        Model m_Model;
        readonly Worker[] m_Workers = new Worker[k_Slots];
        readonly Tensor<float>[] m_Inputs = new Tensor<float>[k_Slots];
        readonly bool[] m_Busy = new bool[k_Slots];
        bool m_Disposed;

        public void Initialize(ModelAsset asset)
        {
            try
            {
                var source = ModelLoader.Load(asset);
                var inShape = source.inputs.Count > 0 ? source.inputs[0].shape.ToString() : "?";
                var outNames = "";
                foreach (var o in source.outputs) outNames += (outNames.Length > 0 ? ", " : "") + o.name;
                var graph = new FunctionalGraph();
                var rgb = graph.AddInput<float>(new TensorShape(1, k_H, k_W, 3), "rgb01");
                var mean = Functional.Constant(new TensorShape(1, 1, 1, 3), new[] { 123.675f, 116.28f, 103.53f });
                var invStd = Functional.Constant(new TensorShape(1, 1, 1, 3), new[] { 1f / 58.395f, 1f / 57.12f, 1f / 57.375f });
                var nchw = ((rgb * 255f - mean) * invStd).Permute(new[] { 0, 3, 1, 2 });
                var o3 = Functional.Forward(source, nchw);
                m_Model = graph.Compile(
                    Functional.ArgMax(o3[0], 2), Functional.ArgMax(o3[1], 2), Functional.ArgMax(o3[2], 2),
                    Functional.ReduceMax(o3[0], 2), Functional.ReduceMax(o3[1], 2));
                for (var s = 0; s < k_Slots; s++)
                {
                    m_Workers[s] = new Worker(m_Model, Backend);
                    m_Inputs[s] = new Tensor<float>(new TensorShape(1, k_H, k_W, 3));
                }
                IsAvailable = true;
                Status = $"RTMW3D ready (input {inShape}, outputs {outNames})";
                Debug.Log("[MimicMocap] " + Status);
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = "RTMW3D init failed: " + e.Message;
                Debug.LogWarning("[MimicMocap] " + Status);
            }
        }

        /// <summary>Run on the person in <paramref name="box"/> (normalised, y up). Returns false when busy.</summary>
        public bool Run(Texture frame, Rect box, double timestamp)
        {
            if (!IsAvailable || m_Disposed || frame == null || box.width <= 0f || box.height <= 0f) return false;
            var slot = -1;
            for (var s = 0; s < k_Slots; s++) if (!m_Busy[s]) { slot = s; break; }
            if (slot < 0) { Skipped++; return false; }
            var texW = frame.width; var texH = frame.height;
            // rtmlib: centre and 1.25x padded size, widened or heightened to the model's 3:4 aspect, no rotation.
            var centre = new float2(box.center.x * texW, box.center.y * texH);
            var w = box.width * texW * 1.25f;
            var h = box.height * texH * 1.25f;
            const float aspect = (float)k_W / k_H;
            if (w > h * aspect) h = w / aspect; else w = h * aspect;
            var s1 = h / k_H;   // texture pixels per crop pixel (same both ways after the aspect fix)
            // Crop pixel (x right, y down) -> texture pixel (y up).
            var M = new float2x3(s1, 0f, centre.x - 0.5f * k_W * s1, 0f, -s1, centre.y + 0.5f * k_H * s1);
            BlazeUtils.SampleImageAffine(frame, m_Inputs[slot], M);
            m_Busy[slot] = true;
            Infer(slot, M, texW, texH, timestamp, box);
            return true;
        }

        async void Infer(int slot, float2x3 M, int texW, int texH, double timestamp, Rect box)
        {
            var t0 = Time.realtimeSinceStartupAsDouble;
            try
            {
                var worker = m_Workers[slot];
                worker.Schedule(m_Inputs[slot]);
                var ixA = (worker.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
                var iyA = (worker.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync();
                var izA = (worker.PeekOutput(2) as Tensor<int>).ReadbackAndCloneAsync();
                var vxA = (worker.PeekOutput(3) as Tensor<float>).ReadbackAndCloneAsync();
                var vyA = (worker.PeekOutput(4) as Tensor<float>).ReadbackAndCloneAsync();
                using var ix = await ixA;
                using var iy = await iyA;
                using var iz = await izA;
                using var vx = await vxA;
                using var vy = await vyA;
                if (m_Disposed) return;
                var r = new Result { Timestamp = timestamp, Box = box };
                float body = 0f, hands = 0f;
                for (var k = 0; k < Keypoints; k++)
                {
                    var p = BlazeUtils.Mul(M, new float2(ix[k] / k_Split, iy[k] / k_Split));
                    r.Image[k] = new Vector2(p.x / texW, p.y / texH);
                    r.Z[k] = (iz[k] / k_Split / (0.5f * k_H) - 1f) * k_ZRange;
                    r.Confidence[k] = 0.5f * (vx[k] + vy[k]);
                    if (k < 17) body += r.Confidence[k];
                    else if (k >= 91) hands += r.Confidence[k];
                }
                r.BodyScore = body / 17f;
                r.HandScore = hands / 42f;
                r.Ms = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
                Runs++;
                Completed?.Invoke(r);
            }
            catch (Exception e)
            {
                if (!m_Disposed) { Status = "RTMW3D error: " + e.Message; Debug.LogException(e); }
            }
            finally
            {
                m_Busy[slot] = false;
            }
        }

        public void Dispose()
        {
            m_Disposed = true;
            for (var s = 0; s < k_Slots; s++)
            {
                m_Workers[s]?.Dispose(); m_Workers[s] = null;
                m_Inputs[s]?.Dispose(); m_Inputs[s] = null;
            }
            IsAvailable = false;
        }
    }
}
