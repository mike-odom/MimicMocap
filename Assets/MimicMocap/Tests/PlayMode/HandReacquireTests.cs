using System.Collections;
using System.IO;
using MimicMocap.BlazeHand;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// A person who walks off camera and comes back further away must get their fingers back. The reference hand
    /// photo stands in for the person: tracked at full size first (hands large), then, after a gap with no frames,
    /// shown again at a fraction of the size. A fresh tracker on the small hand is the baseline.
    /// </summary>
    public class HandReacquireTests
    {
        const int k_Frames = 15;

        /// <summary>Reference-photo joints (normalised, y-up), scaled about the frame centre.</summary>
        static PoseFrame Joints(float scale)
        {
            var pose = new PoseFrame { Tracked = true, HasImageJoints = true, Timestamp = 1 };
            void J(HumanJoint j, float x, float y)
            {
                pose.ImageJoints[(int)j] = new Vector2(0.5f + (x - 0.5f) * scale, 0.5f + (y - 0.5f) * scale);
                pose.Confidence[(int)j] = 1f;
            }
            J(HumanJoint.LeftWrist, 0.459f, 0.283f);
            J(HumanJoint.LeftElbow, 0.380f, 0.003f);
            J(HumanJoint.LeftIndex, 0.519f, 0.410f);
            J(HumanJoint.LeftPinky, 0.558f, 0.327f);
            return pose;
        }

        /// <summary>The photo shrunk by <paramref name="scale"/> on a grey canvas of the original size, centred.</summary>
        static Texture2D Composite(Texture2D src, float scale)
        {
            var w = src.width;
            var h = src.height;
            var sw = Mathf.RoundToInt(w * scale);
            var sh = Mathf.RoundToInt(h * scale);
            var small = TestSupport.ReadableCopy(src, sw, sh);
            var canvas = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var grey = new Color32[w * h];
            for (var i = 0; i < grey.Length; i++) grey[i] = new Color32(110, 110, 110, 255);
            canvas.SetPixels32(grey);
            canvas.SetPixels32((w - sw) / 2, (h - sh) / 2, sw, sh, small.GetPixels32());
            canvas.Apply();
            Object.Destroy(small);
            return canvas;
        }

        static IEnumerator Run(HandTracker hands, Texture tex, PoseFrame pose, HandFrame left, HandFrame right, System.Text.StringBuilder log, string label)
        {
            hands.Submit(tex, pose, Time.timeAsDouble);
            var w = 0f;
            while (hands.InFlight > 0 && w < 20f) { w += Time.deltaTime; yield return null; }
            hands.TryGetLatest(left, right);
            log.AppendLine($"  {label}: tracked={left.Tracked} presence={hands.LastLeftPresence:0.00} reason='{hands.LastLeftReason}' crop={hands.LastLeftCropPixels:0}px maxExtent={hands.LeftMaxExtent:0}px roi={hands.LeftHasRoi}");
        }

        [UnityTest]
        public IEnumerator Hands_ComeBackAfterLeavingAndReturningFurtherAway()
        {
#if UNITY_EDITOR
            var model = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/MimicMocap/Models/BlazeHand/hand_landmarks_detector.onnx");
            Assert.NotNull(model, "hand landmarker model");
            var tex = TestSupport.LoadTestImage("hand_reference");
            Assert.NotNull(tex, "hand_reference image");

            var log = new System.Text.StringBuilder();
            var left = new HandFrame();
            var right = new HandFrame();
            var problems = new System.Collections.Generic.List<string>();

            foreach (var scale in new[] { 0.4f, 0.3f })
            {
                var small = Composite(tex, scale);
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, $"hand_reacquire_{scale:0.0}.png"), small.EncodeToPNG());
                var smallPose = Joints(scale);

                // Baseline: a tracker that has never seen the big hand.
                var fresh = new HandTracker(model);
                fresh.Initialize();
                Assert.IsTrue(fresh.IsAvailable, fresh.Status);
                log.AppendLine($"scale {scale:0.0}: fresh tracker on the small hand");
                var freshTracked = 0;
                for (var i = 0; i < k_Frames; i++)
                {
                    yield return Run(fresh, small, smallPose, left, right, log, $"fresh {i}");
                    if (left.Tracked) freshTracked++;
                }
                fresh.Dispose();

                // The person: hands large for a few frames, then gone (no frames at all), then back at the small size.
                var warm = new HandTracker(model);
                warm.Initialize();
                Assert.IsTrue(warm.IsAvailable, warm.Status);
                log.AppendLine($"scale {scale:0.0}: warm tracker, full size first");
                var bigPose = Joints(1f);
                var bigTracked = 0;
                for (var i = 0; i < 3; i++)
                {
                    yield return Run(warm, tex, bigPose, left, right, log, $"big {i}");
                    if (left.Tracked) bigTracked++;
                }
                log.AppendLine($"scale {scale:0.0}: warm tracker after the gap, small hand");
                var warmTracked = 0;
                var warmTrackedLastFive = 0;
                for (var i = 0; i < k_Frames; i++)
                {
                    yield return Run(warm, small, smallPose, left, right, log, $"back {i}");
                    if (left.Tracked) { warmTracked++; if (i >= k_Frames - 5) warmTrackedLastFive++; }
                }
                warm.Dispose();
                Object.Destroy(small);

                log.AppendLine($"scale {scale:0.0}: big {bigTracked}/3, fresh {freshTracked}/{k_Frames}, warm after return {warmTracked}/{k_Frames} (last five {warmTrackedLastFive}/5)");
                if (bigTracked < 2) problems.Add($"scale {scale:0.0}: the full-size hand was not tracked ({bigTracked}/3)");
                if (freshTracked < k_Frames / 2) problems.Add($"scale {scale:0.0}: baseline too weak, fresh tracker {freshTracked}/{k_Frames}");
                else if (warmTrackedLastFive < 3) problems.Add($"scale {scale:0.0}: hand not reacquired after returning smaller: {warmTracked}/{k_Frames} (last five {warmTrackedLastFive}/5) vs fresh {freshTracked}/{k_Frames}");
            }

            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "hand_reacquire_report.txt"), log.ToString());
            Debug.Log(log.ToString());
            Assert.IsEmpty(problems, string.Join(" | ", problems));
#else
            Assert.Ignore("Editor only");
            yield break;
#endif
        }
    }
}
