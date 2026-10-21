using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>
    /// A brief tracking loss (a few frames) must not reset the smoother: the pose is carried on along its velocity, the
    /// filters ease back onto the returning detection, and joints that were being held at their last good value stay
    /// there. Before this, one missed frame wiped every filter and the next frame came out raw.
    /// </summary>
    public class PoseHoldTests
    {
        const double k_Dt = 1.0 / 30.0;

        static PoseFrame Frame(double t, float wristX, float wristConfidence = 0.9f)
        {
            var f = new PoseFrame { Tracked = true, Timestamp = t, Score = 0.9f };
            for (var i = 0; i < JointTopology.Count; i++) { f.Joints[i] = new Vector3(0.1f * i, 0.5f, 0f); f.Confidence[i] = 0.9f; }
            f[HumanJoint.LeftWrist] = new Vector3(wristX, 0.6f, 0.1f);
            f.Confidence[(int)HumanJoint.LeftWrist] = wristConfidence;
            f.RootOffset = new Vector3(wristX * 0.5f, 0f, 0f);
            return f;
        }

        static PoseFrame Lost(double t) => new PoseFrame { Tracked = false, Timestamp = t };

        [Test]
        public void BriefLoss_CarriesThePoseOnAndEasesBack()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var output = new PoseFrame();
            const float speed = 0.5f;   // m/s, a brisk arm swing
            var t = 0.0;
            var x = 0f;
            var shownStep = 0f;
            Vector3 prev = default;
            for (var i = 0; i < 45; i++)
            {
                t += k_Dt; x += speed * (float)k_Dt;
                s.Smooth(Frame(t, x), output);
                if (i > 30) shownStep = Vector3.Distance(output[HumanJoint.LeftWrist], prev);
                prev = output[HumanJoint.LeftWrist];
            }
            var lagBefore = x - output[HumanJoint.LeftWrist].x;
            var lastTracked = output[HumanJoint.LeftWrist];
            var lastTravel = output.RootOffset.x;
            Assert.Greater(shownStep, 0.01f, "the smoothed wrist follows the motion");

            // Three missed frames: still a tracked, held pose, moving on but slowing, never jumping.
            for (var i = 0; i < 3; i++)
            {
                t += k_Dt; x += speed * (float)k_Dt;
                s.Smooth(Lost(t), output);
                Assert.IsTrue(output.Tracked, $"held frame {i} is shown as tracked");
                Assert.IsTrue(output.Held, $"held frame {i} is flagged");
                Assert.IsTrue(s.Holding);
                var step = Vector3.Distance(output[HumanJoint.LeftWrist], prev);
                Assert.Greater(output[HumanJoint.LeftWrist].x, prev.x - 1e-4f, "carried along the previous motion, not backwards");
                Assert.LessOrEqual(step, shownStep * 1.1f + 1e-4f, $"a held frame moves no faster than the pose was moving (step {step * 1000:0.0} mm vs {shownStep * 1000:0.0})");
                Assert.AreEqual(0.9f, output.ConfidenceOf(HumanJoint.LeftWrist), 1e-3f, "confidence is kept, so the joint stays drawn");
                Assert.Greater(output.LegLength, 0f, "the smoothed leg length (the figure's scale) is kept too");
                prev = output[HumanJoint.LeftWrist];
            }
            Assert.Greater(output[HumanJoint.LeftWrist].x, lastTracked.x + 0.5f * shownStep, "the hold extrapolates rather than freezing");
            Assert.Less(output[HumanJoint.LeftWrist].x, x + 0.5f * (float)k_Dt, "the extrapolation does not run ahead of the true motion by more than the prediction");
            Assert.Greater(output.RootOffset.x, lastTravel + 0.002f, "travel is carried too");

            // Tracking returns: no snap, and the filter settles back to its usual lag within half a second.
            var maxStep = 0f;
            for (var i = 0; i < 15; i++)
            {
                t += k_Dt; x += speed * (float)k_Dt;
                s.Smooth(Frame(t, x), output);
                Assert.IsTrue(output.Tracked);
                Assert.IsFalse(output.Held, "a tracked frame is not flagged as held");
                Assert.IsFalse(s.Holding);
                maxStep = Mathf.Max(maxStep, Vector3.Distance(output[HumanJoint.LeftWrist], prev));
                prev = output[HumanJoint.LeftWrist];
            }
            Assert.Less(maxStep, shownStep * 2.5f, $"reacquiring after a hold never snaps: max step {maxStep * 1000:0.0} mm vs steady {shownStep * 1000:0.0} mm");
            var lagAfter = x - output[HumanJoint.LeftWrist].x;
            Assert.AreEqual(lagBefore, lagAfter, 0.02f, $"back at the usual lag (before {lagBefore * 1000:0} mm, after {lagAfter * 1000:0} mm)");
        }

        [Test]
        public void LongLoss_ExpiresTheHoldAndStartsFresh()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var output = new PoseFrame();
            var t = 0.0;
            for (var i = 0; i < 30; i++) { t += k_Dt; s.Smooth(Frame(t, 0.2f), output); }
            var heldFrames = 0;
            for (var i = 0; i < 30; i++)   // one second lost
            {
                t += k_Dt;
                s.Smooth(Lost(t), output);
                if (output.Tracked) heldFrames++;
                else Assert.IsFalse(s.Holding);
            }
            Assert.That(heldFrames, Is.EqualTo(Mathf.RoundToInt(PoseSmoother.Settings.Default.holdSeconds * 30f)).Within(1), "held for holdSeconds, then gone");
            Assert.IsFalse(output.Tracked, "a long loss comes through as untracked");
            Assert.IsFalse(output.Held);
            // Whoever appears next starts from their own pose, not a blend with the last person.
            t += k_Dt;
            s.Smooth(Frame(t, 1.5f), output);
            Assert.IsTrue(output.Tracked);
            Assert.IsFalse(output.Held);
            Assert.AreEqual(1.5f, output[HumanJoint.LeftWrist].x, 1e-4f);
        }

        [Test]
        public void BriefLoss_KeepsAnOccludedJointAtItsLastGoodValue()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var output = new PoseFrame();
            var t = 0.0;
            for (var i = 0; i < 30; i++) { t += k_Dt; s.Smooth(Frame(t, 0.3f), output); }
            // The wrist is occluded (confidence 0, garbage position): held at its last good value.
            for (var i = 0; i < 5; i++) { t += k_Dt; s.Smooth(Frame(t, 9f, 0f), output); }
            Assert.AreEqual(0.3f, output[HumanJoint.LeftWrist].x, 0.02f, "an occluded wrist stays where it was last seen");
            // Two missed frames, then the wrist is still occluded: it must not jump to the garbage position.
            for (var i = 0; i < 2; i++) { t += k_Dt; s.Smooth(Lost(t), output); }
            Assert.IsTrue(output.Held);
            Assert.AreEqual(0.3f, output[HumanJoint.LeftWrist].x, 0.02f, "held frames keep the occluded joint too");
            for (var i = 0; i < 5; i++) { t += k_Dt; s.Smooth(Frame(t, 9f, 0f), output); }
            Assert.IsTrue(output.Tracked);
            Assert.AreEqual(0.3f, output[HumanJoint.LeftWrist].x, 0.02f, "the last-good memory survives a brief loss (a reset let the raw garbage through)");
        }

        [Test]
        public void Hold_FreezesWhenExtrapolationIsOff()
        {
            var settings = PoseSmoother.Settings.Default;
            settings.extrapolateSeconds = 0f;
            var s = new PoseSmoother(settings);
            var output = new PoseFrame();
            var t = 0.0; var x = 0f;
            for (var i = 0; i < 30; i++) { t += k_Dt; x += 0.5f * (float)k_Dt; s.Smooth(Frame(t, x), output); }
            var last = output[HumanJoint.LeftWrist];
            t += k_Dt;
            s.Smooth(Lost(t), output);
            Assert.IsTrue(output.Held);
            Assert.AreEqual(last, output[HumanJoint.LeftWrist]);
        }

        [Test]
        public void Hold_IsOffWithHoldSecondsZero_AndNeverStartsWithoutAPose()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var output = new PoseFrame();
            s.Smooth(Lost(0.0), output);
            Assert.IsFalse(output.Tracked);
            Assert.IsFalse(output.Held);

            var settings = PoseSmoother.Settings.Default;
            settings.holdSeconds = 0f;
            var off = new PoseSmoother(settings);
            off.Smooth(Frame(0.0, 0.2f), output);
            off.Smooth(Lost(k_Dt), output);
            Assert.IsFalse(output.Tracked, "holdSeconds 0 is the old behaviour");
        }
    }
}
