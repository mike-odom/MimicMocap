using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Guards added after the two-person install footage: depth glitches are speed-limited, the figure's scale (leg length)
    /// is smoothed, and a non-finite value from the source never poisons the filters.
    /// </summary>
    public class SmootherGuardTests
    {
        const double k_Dt = 1.0 / 30.0;

        static PoseFrame Frame(double t, float depth, float legScale = 1f)
        {
            var f = new PoseFrame();
            StandingPose.Fill(f);
            for (var i = 0; i < JointTopology.Count; i++) f.Confidence[i] = 0.9f;
            // Stretch the shins to vary the measured leg length.
            foreach (var (knee, ankle, heel, toe) in new[] { (HumanJoint.LeftKnee, HumanJoint.LeftAnkle, HumanJoint.LeftHeel, HumanJoint.LeftToe), (HumanJoint.RightKnee, HumanJoint.RightAnkle, HumanJoint.RightHeel, HumanJoint.RightToe) })
            {
                var moved = f[knee] + (f[ankle] - f[knee]) * legScale;
                var delta = moved - f[ankle];
                f[ankle] = moved;
                f[heel] += delta;
                f[toe] += delta;
            }
            f.RootOffset = new Vector3(0f, 0f, depth);
            f.Tracked = true;
            f.Timestamp = t;
            return f;
        }

        [Test]
        public void DepthGlitch_IsSpeedLimited()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            for (var i = 0; i < 30; i++) { t += k_Dt; s.Smooth(Frame(t, 0f), o); }
            t += k_Dt;
            s.Smooth(Frame(t, 0.4f), o);   // a 0.4 m depth jump in one frame (12 m/s)
            Assert.Less(o.RootOffset.z, 0.11f, $"one frame may move travel at most 3 m/s x 33 ms: {o.RootOffset.z:0.000}");
            var peak = o.RootOffset.z;
            for (var i = 0; i < 10; i++) { t += k_Dt; s.Smooth(Frame(t, 0f), o); peak = Mathf.Max(peak, o.RootOffset.z); }
            Assert.Less(peak, 0.11f, "the glitch never shows in full");
            Assert.Less(Mathf.Abs(o.RootOffset.z), 0.05f, "and the figure settles back");
            // Real walking (1.2 m/s) passes.
            var z = 0f;
            for (var i = 0; i < 60; i++) { t += k_Dt; z += 1.2f * (float)k_Dt; s.Smooth(Frame(t, z), o); }
            Assert.AreEqual(z, o.RootOffset.z, 0.15f, "walking toward the camera is followed");
        }

        [Test]
        public void LegLength_IsSteadyThroughNoise()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var rng = new System.Random(5);
            var t = 0.0;
            float min = float.MaxValue, max = float.MinValue, rawMin = float.MaxValue, rawMax = float.MinValue;
            for (var i = 0; i < 150; i++)
            {
                t += k_Dt;
                var f = Frame(t, 0f, 1f + (float)(rng.NextDouble() * 0.3 - 0.15));   // shins +-15%, as on the footage
                var raw = PoseTransforms.LegLength(f);
                s.Smooth(f, o);
                if (i < 60) continue;
                min = Mathf.Min(min, o.LegLength); max = Mathf.Max(max, o.LegLength);
                rawMin = Mathf.Min(rawMin, raw); rawMax = Mathf.Max(rawMax, raw);
            }
            Assert.Less((max - min) / max, 0.05f, $"smoothed leg length {min:0.000}..{max:0.000} (raw {rawMin:0.000}..{rawMax:0.000})");
        }

        [Test]
        public void LegGlitch_DoesNotDropTheFigure()
        {
            // Occluded legs collapsing for a frame (shins at 40%) must not sink the pelvis height the figure stands on.
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            for (var i = 0; i < 30; i++) { t += k_Dt; s.Smooth(Frame(t, 0f), o); }
            var standing = o.HipHeight;
            Assert.Greater(standing, 0.5f, "a standing hip height is reported");
            t += k_Dt;
            s.Smooth(Frame(t, 0f, 0.4f), o);
            Assert.Greater(o.HipHeight, standing - 0.09f, $"one glitch frame sinks the hips at most 2.5 m/s x 33 ms: {standing:0.000} to {o.HipHeight:0.000}");
            for (var i = 0; i < 10; i++) { t += k_Dt; s.Smooth(Frame(t, 0f), o); }
            Assert.AreEqual(standing, o.HipHeight, 0.02f, "and it recovers");
            // A real squat (hips 40 cm lower over half a second) is followed.
            for (var i = 1; i <= 15; i++) { t += k_Dt; var f = Frame(t, 0f); for (var j = 0; j < JointTopology.Count; j++) if (f.Joints[j].y > -0.4f) f.Joints[j] += Vector3.down * (0.4f * i / 15f); s.Smooth(f, o); }
            for (var i = 0; i < 10; i++) { t += k_Dt; var f = Frame(t, 0f); for (var j = 0; j < JointTopology.Count; j++) if (f.Joints[j].y > -0.4f) f.Joints[j] += Vector3.down * 0.4f; s.Smooth(f, o); }
            Assert.Less(o.HipHeight, standing - 0.25f, "a squat lowers the figure");
        }

        [Test]
        public void NonFiniteInput_NeverPoisonsTheFilters()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            for (var i = 0; i < 30; i++) { t += k_Dt; s.Smooth(Frame(t, 0.2f), o); }
            t += k_Dt;
            var bad = Frame(t, float.NaN);
            bad[HumanJoint.Pelvis] = new Vector3(float.NaN, 0f, 0f);
            bad[HumanJoint.LeftWrist] = new Vector3(float.PositiveInfinity, 0f, 0f);
            s.Smooth(bad, o);
            for (var i = 0; i < 5; i++) { t += k_Dt; s.Smooth(Frame(t, 0.2f), o); }
            for (var j = 0; j < JointTopology.Count; j++)
                Assert.IsTrue(float.IsFinite(o.Joints[j].x) && float.IsFinite(o.Joints[j].y) && float.IsFinite(o.Joints[j].z), $"{(HumanJoint)j} is finite");
            Assert.IsTrue(float.IsFinite(o.RootOffset.z), "travel is finite");
            Assert.AreEqual(0.2f, o.RootOffset.z, 0.02f);
        }
    }
}
