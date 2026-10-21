using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>
    /// A limb the tracker loses must not snap. Maxine keeps a confident wrist when the hand leaves the frame (or hides behind
    /// the head) and puts it at the side; live, the raised arm dropped there in one frame. Now the joint holds where it was,
    /// eases toward the guess after a moment, rejoins when the hand comes back, and nothing drawn moves faster than a person.
    /// </summary>
    public class JointTrustTests
    {
        const double k_Dt = 1.0 / 30.0;
        static readonly Vector3 k_Raised = new Vector3(-0.25f, 0.85f, 0.05f);   // hand above the head
        static readonly Vector3 k_Side = new Vector3(-0.30f, -0.25f, 0.0f);     // the tracker's guess: arm hanging down

        static PoseFrame Frame(double t, Vector3 wrist, Vector2? wristImage = null)
        {
            var f = new PoseFrame();
            StandingPose.Fill(f);
            for (var i = 0; i < JointTopology.Count; i++) { f.Confidence[i] = 0.9f; f.ImageJoints[i] = new Vector2(0.5f, 0.5f); }
            f.HasImageJoints = true;
            f[HumanJoint.LeftWrist] = wrist;
            if (wristImage.HasValue) f.ImageJoints[(int)HumanJoint.LeftWrist] = wristImage.Value;
            f.Tracked = true;
            f.Timestamp = t;
            return f;
        }

        static void Settle(PoseSmoother s, PoseFrame o, ref double t, Vector3 wrist)
        {
            for (var i = 0; i < 30; i++) { t += k_Dt; s.Smooth(Frame(t, wrist), o); }
        }

        [Test]
        public void LostHand_HoldsThenEasesTowardTheGuess()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            Settle(s, o, ref t, k_Raised);
            var held = o[HumanJoint.LeftWrist];
            Vector3 prev = held;
            var maxStep = 0f;
            var heldFrames = Mathf.FloorToInt(PoseSmoother.Settings.Default.jointHoldSeconds * 30f);
            for (var i = 0; i < heldFrames; i++)
            {
                t += k_Dt;
                s.Smooth(Frame(t, k_Side), o);   // confident, but 1.1 m away in one frame
                Assert.Less(Vector3.Distance(o[HumanJoint.LeftWrist], held), 0.02f, $"frame {i}: the arm stays where it was");
                Assert.Greater(s.UntrustedJoints, 0, "the wrist is flagged as untrusted");
                prev = o[HumanJoint.LeftWrist];
            }
            var arrived = -1;
            for (var i = 0; i < 150; i++)
            {
                t += k_Dt;
                s.Smooth(Frame(t, k_Side), o);
                maxStep = Mathf.Max(maxStep, Vector3.Distance(o[HumanJoint.LeftWrist], prev));
                prev = o[HumanJoint.LeftWrist];
                if (arrived < 0 && Vector3.Distance(o[HumanJoint.LeftWrist], k_Side) < 0.07f) arrived = i;
            }
            Assert.Less(maxStep, PoseSmoother.Settings.Default.jointCatchUpSpeed * (float)k_Dt * 1.2f + 0.005f, $"it never moves faster than the catch-up speed (largest step {maxStep * 1000:0} mm)");
            Assert.Greater(arrived, 40, $"the bulk of the way is the slow ease (arrived after {arrived} frames)");
            Assert.Less(Vector3.Distance(o[HumanJoint.LeftWrist], k_Side), 0.05f, "and it reaches the side within a few seconds");
        }

        [Test]
        public void HandComingBack_RejoinsWithoutMoving()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            Settle(s, o, ref t, k_Raised);
            var held = o[HumanJoint.LeftWrist];
            for (var i = 0; i < 9; i++) { t += k_Dt; s.Smooth(Frame(t, k_Side), o); }   // 0.3 s out of frame
            for (var i = 0; i < 10; i++) { t += k_Dt; s.Smooth(Frame(t, k_Raised), o); }
            Assert.Less(Vector3.Distance(o[HumanJoint.LeftWrist], held), 0.02f, "the arm never left");
            Assert.AreEqual(0, s.UntrustedJoints, "and the wrist is trusted again");
        }

        [Test]
        public void FastWave_IsFollowed()
        {
            // A brisk wave: 25 cm side to side at 2.5 Hz (peak about 2 m/s) never trips the trust test.
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            Settle(s, o, ref t, k_Raised);
            var worst = 0f;
            for (var i = 0; i < 90; i++)
            {
                t += k_Dt;
                var w = k_Raised + Vector3.right * (0.125f * Mathf.Sin((float)t * 2f * Mathf.PI * 2.5f));
                s.Smooth(Frame(t, w), o);
                Assert.AreEqual(0, s.UntrustedJoints, $"frame {i}: a real wave is trusted");
                if (i > 15) worst = Mathf.Max(worst, Vector3.Distance(o[HumanJoint.LeftWrist], w));
            }
            Assert.Less(worst, 0.09f, $"and followed closely (worst {worst * 1000:0} mm)");
        }

        [Test]
        public void HandOnThePictureEdge_IsHeld()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            Settle(s, o, ref t, k_Raised);
            var held = o[HumanJoint.LeftWrist];
            // The tracker still places the wrist plausibly close, but at the top edge of the image: it is guessing.
            for (var i = 0; i < 6; i++) { t += k_Dt; s.Smooth(Frame(t, k_Raised + Vector3.down * 0.03f * i, new Vector2(0.4f, 0.999f)), o); }
            Assert.Greater(s.UntrustedJoints, 0, "a joint on the edge of the picture is not trusted");
            Assert.Less(Vector3.Distance(o[HumanJoint.LeftWrist], held), 0.02f, "and is held");
        }

        [Test]
        public void NothingDrawnMovesFasterThanAPerson()
        {
            var s = new PoseSmoother(PoseSmoother.Settings.Default);
            var o = new PoseFrame();
            var t = 0.0;
            Settle(s, o, ref t, k_Raised);
            var prevElbow = o[HumanJoint.LeftElbow];
            // The whole source pose jumps 1 m (a glitch in the hip-centred frame): every joint is held, none flies.
            var maxStep = 0f;
            for (var i = 0; i < 20; i++)
            {
                t += k_Dt;
                var f = Frame(t, k_Raised + Vector3.right);
                for (var j = 0; j < JointTopology.Count; j++) if ((HumanJoint)j != HumanJoint.LeftWrist) f.Joints[j] += Vector3.right;
                s.Smooth(f, o);
                maxStep = Mathf.Max(maxStep, Vector3.Distance(o[HumanJoint.LeftElbow], prevElbow));
                prevElbow = o[HumanJoint.LeftElbow];
            }
            Assert.LessOrEqual(maxStep, PoseSmoother.Settings.Default.jointMaxSpeed * (float)k_Dt + 1e-3f, $"largest elbow step {maxStep * 1000:0} mm");
        }
    }
}
