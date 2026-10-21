using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>The pieces adapted from MediaPipe: scale-normalised One-Euro, visibility low-pass, image-joint smoothing.</summary>
    public class MediaPipeSmoothingTests
    {
        const double k_Dt = 1.0 / 30.0;

        static float Gauss(System.Random r)
        {
            var u1 = 1.0 - r.NextDouble();
            var u2 = r.NextDouble();
            return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
        }

        /// <summary>Mean per-frame step of a still point with noise of <paramref name="sigmaFraction"/> of the object size, raw and filtered, in object sizes.</summary>
        static (float raw, float smoothed) ResidualStep(float objectScale, float sigmaFraction, int seed)
        {
            var f = new ScaledOneEuroFilter2(0.05f, 80f, 1f);
            var r = new System.Random(seed);
            var centre = new Vector2(0.5f, 0.5f);
            Vector2? prev = null, prevRaw = null;
            var sum = 0f; var sumRaw = 0f; var n = 0;
            var t = 0.0;
            for (var i = 0; i < 300; i++, t += k_Dt)
            {
                var noisy = centre + new Vector2(Gauss(r), Gauss(r)) * (sigmaFraction * objectScale);
                var y = f.Filter(noisy, objectScale, t);
                if (i > 30 && prev.HasValue) { sum += Vector2.Distance(y, prev.Value); sumRaw += Vector2.Distance(noisy, prevRaw.Value); n++; }
                prev = y; prevRaw = noisy;
            }
            return (sumRaw / n / objectScale, sum / n / objectScale);
        }

        [Test]
        public void ScaledOneEuro_BehavesTheSameForNearAndFarPeople()
        {
            // The same relative noise on a person filling 80% of the frame and one filling 20% must be
            // reduced by the same factor: that is the whole point of MediaPipe's object-scale normalisation.
            var near = ResidualStep(0.8f, 0.002f, 1);
            var far = ResidualStep(0.2f, 0.002f, 1);
            Assert.AreEqual(near.smoothed, far.smoothed, near.smoothed * 0.05f, $"near {near.smoothed:0.00000} vs far {far.smoothed:0.00000} object sizes per frame");
            Assert.Less(near.smoothed, near.raw * 0.6f, $"image-landmark profile (0.05 / 80) should calm a still pose: raw {near.raw:0.00000} smoothed {near.smoothed:0.00000}");
        }

        [Test]
        public void ScaledOneEuro_FollowsRealMotionWithoutMuchLag()
        {
            var f = new ScaledOneEuroFilter2(0.05f, 80f, 1f);
            var t = 0.0;
            var pos = new Vector2(0.2f, 0.5f);
            Vector2 y = pos;
            for (var i = 0; i < 30; i++, t += k_Dt) y = f.Filter(pos, 0.5f, t);
            // Move at 0.6 object sizes per second (a brisk arm swing) for one second.
            for (var i = 0; i < 30; i++, t += k_Dt) { pos.x += 0.3f * (1f / 30f); y = f.Filter(pos, 0.5f, t); }
            Assert.Less(Mathf.Abs(pos.x - y.x), 0.02f, "lag under 2% of the frame at 0.3 frame-widths per second");
        }

        [Test]
        public void ConfidenceLowPass_RemovesOneFrameBlinks()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var input = new PoseFrame { Tracked = true };
            var output = new PoseFrame();
            void Fill(float c)
            {
                for (var i = 0; i < JointTopology.Count; i++) { input.Joints[i] = new Vector3(i * 0.01f, 0.5f, 0f); input.Confidence[i] = c; }
                input.Timestamp += k_Dt;
            }
            for (var i = 0; i < 10; i++) { Fill(0.9f); s.Smooth(input, output); }
            Fill(0f); // one bad frame
            s.Smooth(input, output);
            Assert.Greater(output.ConfidenceOf(HumanJoint.LeftWrist), PoseSmoother.Settings.Default.confidenceFloor, "a single zero-confidence frame must not drop the joint below the floor");
            for (var i = 0; i < 20; i++) { Fill(0f); s.Smooth(input, output); }
            Assert.Less(output.ConfidenceOf(HumanJoint.LeftWrist), 0.05f, "sustained loss does come through");
        }

        [Test]
        public void SmoothImageJoints_SteadiesTheCropAnchors()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var r = new System.Random(3);
            var frame = new PoseFrame { Tracked = true, HasImageJoints = true };
            var wristTrue = new Vector2(0.62f, 0.55f);
            Vector2? prevRaw = null, prevSmooth = null;
            var rawStep = 0f; var smoothStep = 0f; var n = 0;
            for (var i = 0; i < 200; i++)
            {
                frame.Timestamp += k_Dt;
                for (var j = 0; j < JointTopology.Count; j++)
                {
                    frame.Confidence[j] = 1f;
                    frame.ImageJoints[j] = new Vector2(0.4f + 0.01f * j, 0.3f + 0.005f * j);
                }
                var raw = wristTrue + new Vector2(Gauss(r), Gauss(r)) * 0.0015f; // about 1.5 px of noise at 1080p
                frame.ImageJoints[(int)HumanJoint.LeftWrist] = raw;
                s.SmoothImageJoints(frame);
                var smooth = frame.ImageJoints[(int)HumanJoint.LeftWrist];
                if (i > 30 && prevRaw.HasValue) { rawStep += Vector2.Distance(raw, prevRaw.Value); smoothStep += Vector2.Distance(smooth, prevSmooth.Value); n++; }
                prevRaw = raw; prevSmooth = smooth;
            }
            Assert.Less(smoothStep / n, rawStep / n * 0.7f, $"smoothed step {smoothStep / n:0.00000} vs raw {rawStep / n:0.00000}");
            Assert.Less(Vector2.Distance(frame.ImageJoints[(int)HumanJoint.LeftWrist], wristTrue), 0.01f, "no bias");
        }
    }
}
