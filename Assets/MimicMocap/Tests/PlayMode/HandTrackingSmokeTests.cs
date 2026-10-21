using System.Collections;
using System.Collections.Generic;
using System.IO;
using MimicMocap.BlazeHand;
using MimicMocap.BlazePose;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Body pose -> wrist crops -> MediaPipe hand landmarker on the test frames. Writes the crops and
    /// annotated frames to Logs/PoseCheck so the crop placement and finger landmarks can be eyeballed.
    /// </summary>
    public class HandTrackingSmokeTests
    {
        static readonly string[] k_Images = { "bye_bye_bye_-_Trainer_1", "Kung_Fu_Girl_1", "Kung_Fu_Girl_2_1" };

        static readonly (int a, int b)[] k_HandLines = HandFrame.Bones;

        [UnityTest]
        public IEnumerator Hands_TrackFromBodyWristCrops()
        {
#if UNITY_EDITOR
            var model = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/MimicMocap/Models/BlazeHand/hand_landmarks_detector.onnx");
            Assert.NotNull(model, "hand landmarker model");
            var hands = new HandTracker(model) { CaptureCrops = true };
            hands.Initialize();
            Assert.IsTrue(hands.IsAvailable, hands.Status);
            var body = TestSupport.CreateBlaze(BlazePoseSource.Variant.Full);

            var pose = new PoseFrame();
            var left = new HandFrame();
            var right = new HandFrame();
            var log = new System.Text.StringBuilder();
            var problems = new List<string>();
            var trackedHands = 0;

            foreach (var name in k_Images)
            {
                var tex = TestSupport.LoadTestImage(name);
                Assert.NotNull(tex, name);

                body.ResetTracking();
                for (var pass = 0; pass < 2; pass++)
                {
                    body.Submit(tex, Time.timeAsDouble);
                    var waited = 0f;
                    while (body.IsBusy && waited < 20f) { waited += Time.deltaTime; yield return null; }
                    body.TryGetLatest(pose);
                }
                if (!pose.Tracked) { problems.Add(name + ": body not tracked"); continue; }

                hands.Submit(tex, pose, Time.timeAsDouble);
                var w = 0f;
                while (hands.InFlight > 0 && w < 20f) { w += Time.deltaTime; yield return null; }
                Assert.IsTrue(hands.TryGetLatest(left, right), name + ": no hand result");

                log.AppendLine($"{name}: L tracked={left.Tracked} presence={hands.LastLeftPresence:0.00} crop={hands.LastLeftCropPixels:0}px | R tracked={right.Tracked} presence={hands.LastRightPresence:0.00} crop={hands.LastRightCropPixels:0}px | {hands.LastMs:0.0} ms");
                foreach (var (hand, label) in new[] { (left, "L"), (right, "R") })
                {
                    if (!hand.Tracked) continue;
                    trackedHands++;
                    var span = 0f;
                    for (var i = 0; i < HandFrame.LandmarkCount; i++) span = Mathf.Max(span, hand.Landmarks[i].magnitude);
                    log.AppendLine($"   {label} hand span from wrist {span * 100:0.0} cm; index tip {TestSupport.Fmt(hand.Landmarks[(int)HandFrame.Landmark.IndexTip])} thumb tip {TestSupport.Fmt(hand.Landmarks[(int)HandFrame.Landmark.ThumbTip])}");
                    if (span < 0.08f || span > 0.35f) problems.Add($"{name} {label}: hand span {span * 100:0.0} cm implausible");
                    if (Vector3.Distance(hand.Landmarks[0], Vector3.zero) > 1e-4f) problems.Add($"{name} {label}: landmarks not wrist-relative");
                }

                // Annotated frame: body wrist/index/pinky in orange, hand landmarks in cyan.
                var copy = TestSupport.ReadableCopy(tex);
                foreach (var j in new[] { HumanJoint.LeftElbow, HumanJoint.LeftWrist, HumanJoint.LeftIndex, HumanJoint.LeftPinky, HumanJoint.RightElbow, HumanJoint.RightWrist, HumanJoint.RightIndex, HumanJoint.RightPinky })
                    TestSupport.DrawDot(copy, pose.ImageJoints[(int)j], new Color(1f, 0.6f, 0.1f), 6);
                foreach (var hand in new[] { left, right })
                {
                    if (!hand.Tracked) continue;
                    TestSupport.Annotate(copy, hand.ImageLandmarks, null, k_HandLines, new Color(0.2f, 1f, 0.9f), new Color(0.2f, 1f, 0.9f), 3);
                }
                copy.Apply();
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, name + "_hands.png"), copy.EncodeToPNG());
                Object.Destroy(copy);

                if (hands.LastLeftCrop != null) File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, name + "_crop_L.png"), hands.LastLeftCrop.EncodeToPNG());
                if (hands.LastRightCrop != null) File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, name + "_crop_R.png"), hands.LastRightCrop.EncodeToPNG());
            }

            log.AppendLine($"hands tracked: {trackedHands} of {k_Images.Length * 2}");
            foreach (var p in problems) log.AppendLine("PROBLEM: " + p);
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "hands_report.txt"), log.ToString());
            Debug.Log(log.ToString());
            hands.Dispose();
            body.Dispose();

            Assert.IsEmpty(problems, string.Join(" | ", problems));
            // Hands in these 1080p dance clips are only 25-45 px across, below what the landmarker resolves reliably,
            // so this is best-effort: at least one hand somewhere proves the body-driven crop path works.
            Assert.GreaterOrEqual(trackedHands, 1, "no hand tracked in any dance frame");
#else
            Assert.Ignore("Editor only");
            yield break;
#endif
        }

        /// <summary>
        /// Controlled case: MediaPipe's reference hand photo with the wrist, elbow and knuckles given by hand,
        /// so crop placement, the model and the coordinate conversion are checked without body-pose noise.
        /// </summary>
        [UnityTest]
        public IEnumerator Hands_TrackReferenceHandFromGivenJoints()
        {
#if UNITY_EDITOR
            var model = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/MimicMocap/Models/BlazeHand/hand_landmarks_detector.onnx");
            Assert.NotNull(model, "hand landmarker model");
            var tex = TestSupport.LoadTestImage("hand_reference");
            Assert.NotNull(tex, "hand_reference image");

            var hands = new HandTracker(model) { CaptureCrops = true };
            hands.Initialize();
            Assert.IsTrue(hands.IsAvailable, hands.Status);

            // Normalised, y-up positions read off the 2303x1107 photo.
            var pose = new PoseFrame { Tracked = true, HasImageJoints = true, Timestamp = 1 };
            void J(HumanJoint j, float x, float y) { pose.ImageJoints[(int)j] = new Vector2(x, y); pose.Confidence[(int)j] = 1f; }
            J(HumanJoint.LeftWrist, 0.459f, 0.283f);
            J(HumanJoint.LeftElbow, 0.380f, 0.003f);
            J(HumanJoint.LeftIndex, 0.519f, 0.410f);
            J(HumanJoint.LeftPinky, 0.558f, 0.327f);

            hands.Submit(tex, pose, Time.timeAsDouble);
            var w = 0f;
            while (hands.InFlight > 0 && w < 20f) { w += Time.deltaTime; yield return null; }
            var left = new HandFrame();
            var right = new HandFrame();
            Assert.IsTrue(hands.TryGetLatest(left, right), "no result");

            var log = $"reference hand: tracked={left.Tracked} presence={hands.LastLeftPresence:0.00} crop={hands.LastLeftCropPixels:0}px {hands.LastMs:0.0} ms\n";
            var span = 0f;
            for (var i = 0; i < HandFrame.LandmarkCount; i++) span = Mathf.Max(span, left.Landmarks[i].magnitude);
            var indexTip = left.ImageLandmarks[(int)HandFrame.Landmark.IndexTip];
            var thumbTip = left.ImageLandmarks[(int)HandFrame.Landmark.ThumbTip];
            log += $"   span {span * 100:0.0} cm; 2D index tip {indexTip} (expect ~0.57,0.55); 2D thumb tip {thumbTip} (expect ~0.48,0.59)\n";
            log += $"   3D index tip {TestSupport.Fmt(left.Landmarks[(int)HandFrame.Landmark.IndexTip])} thumb tip {TestSupport.Fmt(left.Landmarks[(int)HandFrame.Landmark.ThumbTip])} pinky tip {TestSupport.Fmt(left.Landmarks[(int)HandFrame.Landmark.PinkyTip])}\n";
            Debug.Log(log);
            File.AppendAllText(Path.Combine(TestSupport.OutputDir, "hands_report.txt"), log);

            var copy = TestSupport.ReadableCopy(tex);
            foreach (var j in new[] { HumanJoint.LeftElbow, HumanJoint.LeftWrist, HumanJoint.LeftIndex, HumanJoint.LeftPinky })
                TestSupport.DrawDot(copy, pose.ImageJoints[(int)j], new Color(1f, 0.6f, 0.1f), 10);
            if (left.Tracked) TestSupport.Annotate(copy, left.ImageLandmarks, null, HandFrame.Bones, new Color(0.2f, 1f, 0.9f), new Color(1f, 0.2f, 0.6f), 8);
            copy.Apply();
            File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "hand_reference_hands.png"), copy.EncodeToPNG());
            Object.Destroy(copy);
            if (hands.LastLeftCrop != null) File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "hand_reference_crop_L.png"), hands.LastLeftCrop.EncodeToPNG());
            hands.Dispose();

            Assert.IsTrue(left.Tracked, "reference hand not detected (presence " + hands.LastLeftPresence.ToString("0.00") + ")");
            Assert.That(span, Is.InRange(0.12f, 0.30f), "hand span in metres implausible");
            Assert.That(Vector2.Distance(indexTip, new Vector2(0.57f, 0.55f)), Is.LessThan(0.05f), "2D index tip landed away from the photo's fingertip");
            // Fingers point up in the photo: tips should be above the wrist in the canonical frame (+Y up).
            Assert.Greater(left.Landmarks[(int)HandFrame.Landmark.MiddleTip].y, 0.05f, "middle fingertip should be above the wrist");
#else
            Assert.Ignore("Editor only");
            yield break;
#endif
        }
    }
}
