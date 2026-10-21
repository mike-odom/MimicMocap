using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    public class FloorTrackerTests
    {
        const float k_Dt = 1f / 30f;

        /// <summary>A standing pose whose lowest foot sits at <paramref name="footY"/> in the source's vertical frame, at depth z.</summary>
        static PoseFrame Standing(float footY, float z, double t)
        {
            var f = new PoseFrame();
            StandingPose.Fill(f);
            // StandingPose puts the lowest foot (heels) at -0.95 relative to the pelvis; RootOffset carries the rest.
            f.RootOffset = new Vector3(0f, footY - PoseTransforms.LowestFootY(f), z);
            f.Timestamp = t;
            return f;
        }

        static float Feed(FloorTracker tracker, float footY, float z, ref double t)
        {
            t += k_Dt;
            return tracker.Update(Standing(footY, z, t), k_Dt);
        }

        [Test]
        public void Standing_WithNoise_NeverLifts()
        {
            var tracker = new FloorTracker();
            var rng = new System.Random(3);
            double t = 0;
            var worst = 0f;
            for (var i = 0; i < 120; i++)
            {
                var noise = (float)(rng.NextDouble() * 0.03 - 0.015);
                worst = Mathf.Max(worst, Feed(tracker, noise, 0f, ref t));
            }
            Assert.AreEqual(0f, worst, "standing noise of 1.5 cm must not read as a jump");
        }

        [Test]
        public void WalkingAwayFromATiltedCamera_FollowsTheFloor()
        {
            // A camera looking down sees the floor rise with distance: 1 cm per frame here (1 m/s at a 17 deg tilt).
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            var worst = 0f;
            for (var i = 1; i <= 120; i++) worst = Mathf.Max(worst, Feed(tracker, 0.01f * i, 0.033f * i, ref t));
            Assert.AreEqual(0f, worst, "a slowly rising floor must not read as a jump");
            Assert.AreEqual(1.2f, tracker.Floor, 0.1f, "the floor should have followed the feet (it lags by speed x follow time, 6 cm here)");
        }

        [Test]
        public void Jump_LiftsAndLands()
        {
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            // A 0.35 m jump over 14 frames (0.47 s of air time), parabolic.
            var peak = 0f;
            var liftAtLanding = 1f;
            var prev = 0f;
            var maxStep = 0f;
            for (var i = 1; i <= 14; i++)
            {
                var u = i / 14f;
                var y = 0.35f * 4f * u * (1f - u);
                var lift = Feed(tracker, y, 0f, ref t);
                peak = Mathf.Max(peak, lift);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(lift - prev));
                prev = lift;
                if (i == 14) liftAtLanding = lift;
            }
            Assert.Greater(peak, 0.22f, "the lift should reach most of the jump height");
            Assert.Less(liftAtLanding, 0.12f, "landing brings the figure down (eased over a few frames)");
            Assert.AreEqual(0f, tracker.Floor, 0.02f, "the floor must not have followed the jump");
            var after = new float[4];
            for (var i = 0; i < after.Length; i++) { after[i] = Feed(tracker, 0f, 0f, ref t); maxStep = Mathf.Max(maxStep, Mathf.Abs(after[i] - prev)); prev = after[i]; }
            Assert.Less(after[2], 0.02f, "grounded within three frames of landing");
            Assert.Less(maxStep, 0.6f * peak, $"no frame-to-frame step bigger than 60% of the jump (largest {maxStep:0.000} of {peak:0.000})");
            for (var i = 0; i < 10; i++) Assert.AreEqual(0f, Feed(tracker, 0f, 0f, ref t));
        }

        [Test]
        public void SmallHop_EasesInsteadOfPopping()
        {
            // A dance bounce: feet 12 cm off the floor for a quarter of a second. Without the ease the figure popped up by
            // the launch threshold in one frame and snapped down on landing.
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            var prev = 0f; var maxStep = 0f; var peak = 0f;
            for (var i = 1; i <= 8; i++)
            {
                var u = i / 8f;
                var lift = Feed(tracker, 0.12f * 4f * u * (1f - u), 0f, ref t);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(lift - prev)); prev = lift; peak = Mathf.Max(peak, lift);
            }
            for (var i = 0; i < 6; i++) { var lift = Feed(tracker, 0f, 0f, ref t); maxStep = Mathf.Max(maxStep, Mathf.Abs(lift - prev)); prev = lift; }
            Assert.Less(maxStep, 0.05f, $"the largest frame-to-frame step of the shown lift is {maxStep * 100:0.0} cm (peak {peak * 100:0.0} cm)");
            Assert.AreEqual(0f, prev, 1e-3f, "and it comes back to the floor");
        }

        [Test]
        public void StepDownThenUp_SnapsAndFollows()
        {
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            // Step down a kerb: the floor snaps to the lower level.
            for (var i = 0; i < 10; i++) Assert.AreEqual(0f, Feed(tracker, -0.10f, 0f, ref t));
            Assert.AreEqual(-0.10f, tracker.Floor, 0.005f);
            // Step back up over half a second: no launch, so the floor follows.
            var worst = 0f;
            for (var i = 1; i <= 15; i++) worst = Mathf.Max(worst, Feed(tracker, -0.10f + 0.10f * i / 15f, 0f, ref t));
            for (var i = 0; i < 30; i++) worst = Mathf.Max(worst, Feed(tracker, 0f, 0f, ref t));
            Assert.AreEqual(0f, worst, "a slow step up is not a jump");
            Assert.AreEqual(0f, tracker.Floor, 0.02f);
        }

        [Test]
        public void OneFrameGlitch_IsNotAJump()
        {
            // Seen on the two-person install footage: the 3D estimate glitched for one frame, the foot read 0.7 m higher (or
            // lower), and the launch detector took it for a jump, throwing the avatar off the top of the screen.
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            var worst = 0f;
            foreach (var spike in new[] { 0.7f, -0.7f, 0.5f })
            {
                worst = Mathf.Max(worst, Feed(tracker, spike, 0f, ref t));
                for (var i = 0; i < 10; i++) worst = Mathf.Max(worst, Feed(tracker, 0f, 0f, ref t));
            }
            // Two glitch frames in a row too.
            worst = Mathf.Max(worst, Feed(tracker, 0.6f, 0f, ref t));
            worst = Mathf.Max(worst, Feed(tracker, 0.65f, 0f, ref t));
            for (var i = 0; i < 10; i++) worst = Mathf.Max(worst, Feed(tracker, 0f, 0f, ref t));
            Assert.AreEqual(0f, worst, "a foot that moves faster than any jump for a frame or two is a glitch");
            Assert.AreEqual(0f, tracker.Floor, 0.02f, "and must not move the floor");
            Assert.Greater(tracker.SpikesRejected, 0);
        }

        [Test]
        public void LastingStep_IsAcceptedAfterTheGlitchWindow()
        {
            // A sudden change that stays (a different person, standing on a box) is real: taken after a few frames.
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            for (var i = 0; i < 40; i++) Feed(tracker, 0.5f, 0f, ref t);
            Assert.AreEqual(0.5f, tracker.Floor, 0.05f, "the floor follows a change that persists");
            Assert.AreEqual(0f, Feed(tracker, 0.5f, 0f, ref t), "and it is not a jump");
        }

        [Test]
        public void FeetOutOfView_GiveNoLift()
        {
            var tracker = new FloorTracker();
            double t = 0;
            for (var i = 0; i < 30; i++) Feed(tracker, 0f, 0f, ref t);
            var f = Standing(0.4f, 0f, t + k_Dt);
            f.Confidence[(int)HumanJoint.LeftAnkle] = 0.1f;
            Assert.AreEqual(0f, tracker.Update(f, k_Dt));
        }
    }
}
