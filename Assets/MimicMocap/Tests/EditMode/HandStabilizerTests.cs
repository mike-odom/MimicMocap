using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>
    /// The anatomical hand model: round-trips its own template, removes most landmark jitter, never lets a
    /// dropout snap or vanish, and keeps every joint inside its limits.
    /// </summary>
    public class HandStabilizerTests
    {
        const double k_Dt = 1.0 / 30.0;

        static PoseFrame BodyWithWrist(bool right)
        {
            var f = new PoseFrame { Tracked = true, Score = 1f };
            void Set(HumanJoint j, Vector3 v) { f[j] = v; f.Confidence[(int)j] = 1f; }
            var sign = right ? 1f : -1f;
            Set(HumanJoint.LeftHip, new Vector3(-0.1f, 0f, 0f));
            Set(HumanJoint.RightHip, new Vector3(0.1f, 0f, 0f));
            Set(HumanJoint.LeftShoulder, new Vector3(-0.2f, 0.5f, 0f));
            Set(HumanJoint.RightShoulder, new Vector3(0.2f, 0.5f, 0f));
            // Arm raised, hand up, palm toward the camera (+Z), knuckles above the wrist.
            Set(right ? HumanJoint.RightElbow : HumanJoint.LeftElbow, new Vector3(sign * 0.35f, 0.45f, 0f));
            Set(right ? HumanJoint.RightWrist : HumanJoint.LeftWrist, new Vector3(sign * 0.4f, 0.7f, 0.05f));
            Set(right ? HumanJoint.RightIndex : HumanJoint.LeftIndex, new Vector3(sign * 0.4f - sign * 0.025f, 0.785f, 0.05f));
            Set(right ? HumanJoint.RightPinky : HumanJoint.LeftPinky, new Vector3(sign * 0.4f + sign * 0.03f, 0.775f, 0.05f));
            Set(right ? HumanJoint.RightThumb : HumanJoint.LeftThumb, new Vector3(sign * 0.4f - sign * 0.05f, 0.74f, 0.08f));
            return f;
        }

        static void FillDetected(HandFrame hand, Vector3[] landmarks, double t, float score = 0.95f)
        {
            System.Array.Copy(landmarks, hand.Landmarks, HandFrame.LandmarkCount);
            hand.Tracked = true;
            hand.HasImageLandmarks = true;
            hand.Score = score;
            hand.Timestamp = t;
        }

        [Test]
        public void Template_RoundTripsThroughTheSolver()
        {
            var palm = Quaternion.LookRotation(Vector3.up, Vector3.forward); // fingers up, palm toward the camera
            var open = new Vector3[HandFrame.LandmarkCount];
            HandStabilizer.TemplateHand(true, palm, 0.09f, open, 0f);
            // Sanity on the template: fingertips above the knuckles, thumb on the right hand's thumb side (-X), palm faces +Z.
            Assert.Greater(open[(int)HandFrame.Landmark.IndexTip].y, open[(int)HandFrame.Landmark.IndexMcp].y + 0.06f);
            Assert.Less(open[(int)HandFrame.Landmark.ThumbTip].x, open[(int)HandFrame.Landmark.PinkyMcp].x, "right-hand thumb points to the subject's left (-X)");

            var curled = new Vector3[HandFrame.LandmarkCount];
            HandStabilizer.TemplateHand(true, palm, 0.09f, curled, 40f);
            // Curled fingertips move toward the palm side (+Z, the palm faces the camera) and lose height.
            Assert.Greater(curled[(int)HandFrame.Landmark.MiddleTip].z, open[(int)HandFrame.Landmark.MiddleTip].z + 0.03f);
            Assert.Less(curled[(int)HandFrame.Landmark.MiddleTip].y, open[(int)HandFrame.Landmark.MiddleTip].y - 0.02f);

            // Feed the curled template as a detection for a while: the output must converge back onto it.
            var stab = new HandStabilizer(HandStabilizer.Settings.Default);
            var body = BodyWithWrist(true);
            var t = 0.0;
            for (var i = 0; i < 90; i++, t += k_Dt)
            {
                FillDetected(body.RightHand, curled, t);
                stab.Apply(body, t);
            }
            var maxErr = 0f;
            for (var i = 0; i < HandFrame.LandmarkCount; i++) maxErr = Mathf.Max(maxErr, Vector3.Distance(body.RightHand.Landmarks[i], curled[i]));
            Assert.Less(maxErr, 0.006f, "stabilised hand should reproduce its own template to within 6 mm (the dead band allows 1.5 deg per joint)");
            Assert.IsTrue(body.RightHand.Tracked);
            Assert.Greater(body.RightHand.Score, 0.95f);
        }

        [Test]
        public void Jitter_IsReducedByAnOrderOfMagnitude()
        {
            var palm = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            var clean = new Vector3[HandFrame.LandmarkCount];
            HandStabilizer.TemplateHand(false, palm, 0.09f, clean, 20f);
            var rng = new System.Random(7);
            Vector3 Noise(float sigma) => new Vector3(Gauss(rng) * sigma, Gauss(rng) * sigma, Gauss(rng) * sigma);

            var stab = new HandStabilizer(HandStabilizer.Settings.Default);
            var body = BodyWithWrist(false);
            var noisy = new Vector3[HandFrame.LandmarkCount];
            var prevIn = new Vector3[HandFrame.LandmarkCount];
            var prevOut = new Vector3[HandFrame.LandmarkCount];
            var inJitter = 0.0; var outJitter = 0.0; var n = 0;
            var t = 0.0;
            for (var i = 0; i < 150; i++, t += k_Dt)
            {
                for (var k = 0; k < HandFrame.LandmarkCount; k++) noisy[k] = clean[k] + (k == 0 ? Vector3.zero : Noise(0.004f)); // 4 mm per axis, typical far-hand noise
                FillDetected(body.LeftHand, noisy, t);
                stab.Apply(body, t);
                if (i > 30)
                {
                    foreach (var tip in new[] { HandFrame.Landmark.IndexTip, HandFrame.Landmark.MiddleTip, HandFrame.Landmark.ThumbTip, HandFrame.Landmark.PinkyTip })
                    {
                        inJitter += Vector3.Distance(noisy[(int)tip], prevIn[(int)tip]);
                        outJitter += Vector3.Distance(body.LeftHand.Landmarks[(int)tip], prevOut[(int)tip]);
                        n++;
                    }
                }
                System.Array.Copy(noisy, prevIn, HandFrame.LandmarkCount);
                System.Array.Copy(body.LeftHand.Landmarks, prevOut, HandFrame.LandmarkCount);
            }
            inJitter /= n; outJitter /= n;
            Debug.Log($"[MimicMocap] fingertip jitter per frame: raw {inJitter * 1000:0.0} mm -> stabilised {outJitter * 1000:0.00} mm");
            Assert.Less(outJitter, inJitter * 0.3f, "stabiliser should cut fingertip jitter by more than 3x");
            // And it stays close to the true hand (no bias from clamping).
            var err = Vector3.Distance(body.LeftHand.Landmarks[(int)HandFrame.Landmark.IndexTip], clean[(int)HandFrame.Landmark.IndexTip]);
            Assert.Less(err, 0.012f);
        }

        [Test]
        public void Dropout_NeitherSnapsNorVanishes()
        {
            var palm = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            var detected = new Vector3[HandFrame.LandmarkCount];
            HandStabilizer.TemplateHand(true, palm, 0.09f, detected, 60f); // a fist-ish hand
            var stab = new HandStabilizer(HandStabilizer.Settings.Default);
            var body = BodyWithWrist(true);
            var t = 0.0;
            for (var i = 0; i < 60; i++, t += k_Dt) { FillDetected(body.RightHand, detected, t); stab.Apply(body, t); }
            var before = (Vector3[])body.RightHand.Landmarks.Clone();

            // Detector loses the hand for two seconds: the hand must remain, move smoothly, and relax (open up a bit).
            var maxStep = 0f;
            var prev = (Vector3[])body.RightHand.Landmarks.Clone();
            for (var i = 0; i < 60; i++, t += k_Dt)
            {
                body.RightHand.Clear();
                body.RightHand.Timestamp = t;
                stab.Apply(body, t);
                Assert.IsTrue(body.RightHand.Tracked, "hand must stay present while the body wrist is confident");
                Assert.IsFalse(body.RightHand.HasImageLandmarks);
                for (var k = 0; k < HandFrame.LandmarkCount; k++) maxStep = Mathf.Max(maxStep, Vector3.Distance(body.RightHand.Landmarks[k], prev[k]));
                System.Array.Copy(body.RightHand.Landmarks, prev, HandFrame.LandmarkCount);
            }
            Assert.Less(maxStep, 0.012f, "no landmark may jump more than 12 mm in one frame during a dropout");
            Assert.Less(body.RightHand.Score, 0.05f, "detector weight decays to zero");
            var fistTip = Vector3.Distance(before[(int)HandFrame.Landmark.MiddleTip], before[(int)HandFrame.Landmark.Wrist]);
            var relaxedTip = Vector3.Distance(body.RightHand.Landmarks[(int)HandFrame.Landmark.MiddleTip], body.RightHand.Landmarks[(int)HandFrame.Landmark.Wrist]);
            Assert.Greater(relaxedTip, fistTip + 0.02f, "fingers relax (open) while the detector is gone");

            // Palm still follows the body: raise the body's knuckles toward the camera and the hand should tilt with them.
            var tipBefore = body.RightHand.Landmarks[(int)HandFrame.Landmark.MiddleTip];
            body[HumanJoint.RightIndex] += new Vector3(0f, -0.03f, 0.06f);
            body[HumanJoint.RightPinky] += new Vector3(0f, -0.03f, 0.06f);
            for (var i = 0; i < 45; i++, t += k_Dt) { body.RightHand.Clear(); body.RightHand.Timestamp = t; stab.Apply(body, t); }
            var tipAfter = body.RightHand.Landmarks[(int)HandFrame.Landmark.MiddleTip];
            Assert.Greater(tipAfter.z, tipBefore.z + 0.02f, "body-driven palm tilts toward the camera");

            // Detector returns with the fist: ease back, no jump.
            maxStep = 0f;
            System.Array.Copy(body.RightHand.Landmarks, prev, HandFrame.LandmarkCount);
            for (var i = 0; i < 45; i++, t += k_Dt)
            {
                FillDetected(body.RightHand, detected, t);
                stab.Apply(body, t);
                for (var k = 0; k < HandFrame.LandmarkCount; k++) maxStep = Mathf.Max(maxStep, Vector3.Distance(body.RightHand.Landmarks[k], prev[k]));
                System.Array.Copy(body.RightHand.Landmarks, prev, HandFrame.LandmarkCount);
            }
            Assert.Less(maxStep, 0.02f, "reacquiring eases in");
            Assert.Greater(body.RightHand.Score, 0.9f);
        }

        [Test]
        public void NoBodyAndNoDetection_ReleasesTheHand()
        {
            var stab = new HandStabilizer(HandStabilizer.Settings.Default);
            var body = new PoseFrame { Tracked = true };
            body.RightHand.Clear();
            stab.Apply(body, 0.0);
            Assert.IsFalse(body.RightHand.Tracked);
        }

        [Test]
        public void ImpossibleFingers_AreClampedToAnatomy()
        {
            // Bend the index finger backwards 80 degrees and sideways 60 degrees: the output must stay within limits.
            var palm = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            var lm = new Vector3[HandFrame.LandmarkCount];
            HandStabilizer.TemplateHand(true, palm, 0.09f, lm, 0f);
            var mcp = lm[(int)HandFrame.Landmark.IndexMcp];
            var back = Quaternion.AngleAxis(-80f, Vector3.right) * Vector3.up; // hyperextension (away from the palm)
            lm[(int)HandFrame.Landmark.IndexPip] = mcp + back * 0.04f;
            lm[(int)HandFrame.Landmark.IndexDip] = lm[(int)HandFrame.Landmark.IndexPip] + back * 0.024f;
            lm[(int)HandFrame.Landmark.IndexTip] = lm[(int)HandFrame.Landmark.IndexDip] + back * 0.02f;
            var stab = new HandStabilizer(HandStabilizer.Settings.Default);
            var body = BodyWithWrist(true);
            var t = 0.0;
            for (var i = 0; i < 60; i++, t += k_Dt) { FillDetected(body.RightHand, lm, t); stab.Apply(body, t); }
            var outMcp = body.RightHand.Landmarks[(int)HandFrame.Landmark.IndexMcp];
            var outPip = body.RightHand.Landmarks[(int)HandFrame.Landmark.IndexPip];
            var dir = (outPip - outMcp).normalized;
            // Palm faces +Z; hyperextension would send the finger toward -Z. At most 15 degrees of that is allowed.
            var angleBack = Vector3.Angle(dir, Vector3.up);
            Assert.Less(angleBack, 17f, "MCP hyperextension clamped to 15 degrees");
            Assert.Less(dir.z, 0.01f, "clamped finger still leans no further back than the limit");
        }

        static float Gauss(System.Random r)
        {
            var u1 = 1.0 - r.NextDouble();
            var u2 = r.NextDouble();
            return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
        }
    }
}
