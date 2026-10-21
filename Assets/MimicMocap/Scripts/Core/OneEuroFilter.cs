using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// One-Euro filter (Casiez, Roussel, Vogel 2012). Low lag on fast motion, strong smoothing when still.
    /// </summary>
    public sealed class OneEuroFilter
    {
        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        bool m_HasPrev;
        float m_PrevX;
        float m_PrevDx;
        double m_PrevT;

        public OneEuroFilter(float minCutoff = 1f, float beta = 0.02f, float derivativeCutoff = 1f)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DerivativeCutoff = derivativeCutoff;
        }

        public void Reset()
        {
            m_HasPrev = false;
        }

        /// <summary>
        /// Continue from <paramref name="x"/> at time <paramref name="t"/> as if it had been the last output, keeping the
        /// speed estimate. After a pose has been held through a dropout this lets the filter ease from what was shown
        /// instead of jumping to the next raw value.
        /// </summary>
        public void Prime(float x, double t)
        {
            if (!m_HasPrev) m_PrevDx = 0f;
            m_HasPrev = true;
            m_PrevX = x;
            m_PrevT = t;
        }

        public float Filter(float x, double t)
        {
            if (!m_HasPrev)
            {
                m_HasPrev = true;
                m_PrevX = x;
                m_PrevDx = 0f;
                m_PrevT = t;
                return x;
            }

            var dt = (float)(t - m_PrevT);
            if (dt <= 0f) dt = 1f / 60f;
            m_PrevT = t;

            var dx = (x - m_PrevX) / dt;
            var edx = Lerp(m_PrevDx, dx, Alpha(dt, DerivativeCutoff));
            m_PrevDx = edx;

            var cutoff = MinCutoff + Beta * Mathf.Abs(edx);
            var result = Lerp(m_PrevX, x, Alpha(dt, cutoff));
            m_PrevX = result;
            return result;
        }

        static float Alpha(float dt, float cutoff)
        {
            var tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>
    /// Two-axis One-Euro filter with MediaPipe's object-scale normalisation (adapted from
    /// mediapipe/calculators/util/landmarks_smoothing_calculator.cc, Apache-2.0): values are divided by the
    /// object's size before filtering and multiplied back after, so min_cutoff and beta are expressed in
    /// "object sizes per second" and one setting behaves the same for a person 2 m or 4 m from the camera.
    /// </summary>
    public sealed class ScaledOneEuroFilter2
    {
        readonly OneEuroFilter m_X, m_Y;
        /// <summary>Below this object size (same units as the values) smoothing is bypassed, as in MediaPipe.</summary>
        public float MinAllowedObjectScale = 1e-6f;

        public ScaledOneEuroFilter2(float minCutoff, float beta, float derivativeCutoff = 1f)
        {
            m_X = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
            m_Y = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
        }

        public void SetParameters(float minCutoff, float beta, float derivativeCutoff)
        {
            foreach (var f in new[] { m_X, m_Y })
            {
                f.MinCutoff = minCutoff;
                f.Beta = beta;
                f.DerivativeCutoff = derivativeCutoff;
            }
        }

        public void Reset()
        {
            m_X.Reset();
            m_Y.Reset();
        }

        public Vector2 Filter(Vector2 v, float objectScale, double t)
        {
            if (objectScale < MinAllowedObjectScale) return v;
            var s = 1f / objectScale;
            return new Vector2(m_X.Filter(v.x * s, t) * objectScale, m_Y.Filter(v.y * s, t) * objectScale);
        }
    }

    /// <summary>Three One-Euro filters, one per axis.</summary>
    public sealed class OneEuroFilter3
    {
        readonly OneEuroFilter m_X, m_Y, m_Z;

        public OneEuroFilter3(float minCutoff, float beta, float derivativeCutoff)
        {
            m_X = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
            m_Y = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
            m_Z = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
        }

        public void SetParameters(float minCutoff, float beta, float derivativeCutoff)
        {
            foreach (var f in new[] { m_X, m_Y, m_Z })
            {
                f.MinCutoff = minCutoff;
                f.Beta = beta;
                f.DerivativeCutoff = derivativeCutoff;
            }
        }

        public void Reset()
        {
            m_X.Reset();
            m_Y.Reset();
            m_Z.Reset();
        }

        public void Prime(Vector3 v, double t)
        {
            m_X.Prime(v.x, t);
            m_Y.Prime(v.y, t);
            m_Z.Prime(v.z, t);
        }

        public Vector3 Filter(Vector3 v, double t)
        {
            return new Vector3(m_X.Filter(v.x, t), m_Y.Filter(v.y, t), m_Z.Filter(v.z, t));
        }
    }
}
