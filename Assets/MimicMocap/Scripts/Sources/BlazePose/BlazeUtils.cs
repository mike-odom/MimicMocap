using System.Globalization;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace MimicMocap.BlazePose
{
    /// <summary>
    /// Affine helpers, detector post-processing graph and the GPU image sampler,
    /// after Unity's BlazeDetectionSample (Apache-2.0).
    /// </summary>
    public static class BlazeUtils
    {
        // ---- 2x3 affine matrices, column-major float2x3: columns are (xAxis, yAxis, translation) ----
        public static float2x3 Mul(float2x3 a, float2x3 b)
        {
            return new float2x3(
                a[0][0] * b[0][0] + a[1][0] * b[0][1],
                a[0][0] * b[1][0] + a[1][0] * b[1][1],
                a[0][0] * b[2][0] + a[1][0] * b[2][1] + a[2][0],
                a[0][1] * b[0][0] + a[1][1] * b[0][1],
                a[0][1] * b[1][0] + a[1][1] * b[1][1],
                a[0][1] * b[2][0] + a[1][1] * b[2][1] + a[2][1]
            );
        }

        public static float2 Mul(float2x3 a, float2 b)
        {
            return new float2(
                a[0][0] * b.x + a[1][0] * b.y + a[2][0],
                a[0][1] * b.x + a[1][1] * b.y + a[2][1]
            );
        }

        /// <summary>Apply only the linear part (no translation), e.g. for directions.</summary>
        public static float2 MulVector(float2x3 a, float2 b)
        {
            return new float2(
                a[0][0] * b.x + a[1][0] * b.y,
                a[0][1] * b.x + a[1][1] * b.y
            );
        }

        public static float2x3 RotationMatrix(float theta)
        {
            var s = math.sin(theta);
            var c = math.cos(theta);
            return new float2x3(c, -s, 0, s, c, 0);
        }

        public static float2x3 TranslationMatrix(float2 delta)
        {
            return new float2x3(1, 0, delta.x, 0, 1, delta.y);
        }

        public static float2x3 ScaleMatrix(float2 scale)
        {
            return new float2x3(scale.x, 0, 0, 0, scale.y, 0);
        }

        // ---- detector post-processing: pick the best anchor on the GPU ----
        static FunctionalTensor ScoreFiltering(FunctionalTensor rawScores, float clampValue)
        {
            return Functional.Sigmoid(Functional.Clamp(rawScores, -clampValue, clampValue));
        }

        public static (FunctionalTensor idx, FunctionalTensor score, FunctionalTensor box) ArgMaxFiltering(FunctionalTensor rawBoxes, FunctionalTensor rawScores)
        {
            var detectionScores = ScoreFiltering(rawScores, 100f);          // (1, 2254, 1)
            var bestScoreIndex = Functional.ArgMax(rawScores, 1).Squeeze(); // scalar
            var selectedBoxes = Functional.IndexSelect(rawBoxes, 1, bestScoreIndex).Unsqueeze(0);          // (1, 1, 12)
            var selectedScores = Functional.IndexSelect(detectionScores, 1, bestScoreIndex).Unsqueeze(0);  // (1, 1, 1)
            return (bestScoreIndex, selectedScores, selectedBoxes);
        }

        // ---- GPU affine sampler: texture -> NHWC float tensor ----
        static ComputeShader s_Shader;
        static int s_Kernel;
        static readonly int s_Optr = Shader.PropertyToID("Optr");
        static readonly int s_XTex = Shader.PropertyToID("X_tex2D");
        static readonly int s_OHeight = Shader.PropertyToID("O_height");
        static readonly int s_OWidth = Shader.PropertyToID("O_width");
        static readonly int s_OChannels = Shader.PropertyToID("O_channels");
        static readonly int s_XHeight = Shader.PropertyToID("X_height");
        static readonly int s_XWidth = Shader.PropertyToID("X_width");
        static readonly int s_Affine = Shader.PropertyToID("affineMatrix");
        static readonly int s_EncodeSRGB = Shader.PropertyToID("EncodeSRGB");

        static void EnsureShader()
        {
            if (s_Shader != null) return;
            s_Shader = Resources.Load<ComputeShader>("ComputeShaders/ImageTransform");
            if (s_Shader == null) throw new System.InvalidOperationException("Missing Resources/ComputeShaders/ImageTransform.compute");
            s_Kernel = s_Shader.FindKernel("ImageSample");
        }

        static int IDivC(int v, int div) => (v + div - 1) / div;

        /// <summary>
        /// Fill <paramref name="dst"/> (shape 1,H,W,3) by sampling <paramref name="src"/> through the
        /// tensor-pixel -> texture-pixel affine <paramref name="M"/>. Texture pixel space is y-up.
        /// </summary>
        public static void SampleImageAffine(Texture src, Tensor<float> dst, float2x3 M)
        {
            EnsureShader();
            var data = ComputeTensorData.Pin(dst, false);
            s_Shader.SetTexture(s_Kernel, s_XTex, src);
            s_Shader.SetBuffer(s_Kernel, s_Optr, data.buffer);
            s_Shader.SetInt(s_OHeight, dst.shape[1]);
            s_Shader.SetInt(s_OWidth, dst.shape[2]);
            s_Shader.SetInt(s_OChannels, dst.shape[3]);
            s_Shader.SetInt(s_XHeight, src.height);
            s_Shader.SetInt(s_XWidth, src.width);
            // The models want ordinary sRGB-encoded pixels. In a Linear colour space project the sampler returns linear
            // values that must be re-encoded; in Gamma (this project) the samples already are encoded, and encoding them
            // twice washed infrared-lit hands out to white (see ImageTransform.compute).
            s_Shader.SetInt(s_EncodeSRGB, QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
            s_Shader.SetMatrix(s_Affine, new Matrix4x4(
                new Vector4(M[0][0], M[0][1]),
                new Vector4(M[1][0], M[1][1]),
                new Vector4(M[2][0], M[2][1]),
                Vector4.zero));
            s_Shader.Dispatch(s_Kernel, IDivC(dst.shape[1], 8), IDivC(dst.shape[2], 8), 1);
        }

        public static float[,] LoadAnchors(string csv, int numAnchors)
        {
            var anchors = new float[numAnchors, 4];
            var lines = csv.Split('\n');
            for (var i = 0; i < numAnchors; i++)
            {
                var v = lines[i].Split(',');
                for (var j = 0; j < 4; j++)
                    anchors[i, j] = float.Parse(v[j], CultureInfo.InvariantCulture);
            }
            return anchors;
        }

        public static float Sigmoid(float x) => 1f / (1f + math.exp(-x));
    }
}
