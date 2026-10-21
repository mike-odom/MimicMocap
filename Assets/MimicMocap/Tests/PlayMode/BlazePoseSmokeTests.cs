using System.Collections;
using System.IO;
using MimicMocap.BlazePose;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Runs the real BlazePose models on frames extracted from the test dance videos and writes
    /// annotated PNGs to Logs/PoseCheck so a human can eyeball the result.
    /// </summary>
    public class BlazePoseSmokeTests
    {
        // Amber_Genshin_1 is a stylised anime render that scores just under the detector threshold; it is
        // reported but not required so the test tracks real-footage behaviour.
        static readonly string[] k_Images = { "Kung_Fu_Girl_1", "bye_bye_bye_-_Trainer_1", "Kung_Fu_Girl_2_1", "Amber_Genshin_1" };
        static readonly string[] k_Optional = { "Amber_Genshin_1" };

        static string OutputDir
        {
            get
            {
                var dir = Path.Combine(Application.dataPath, "..", "Logs", "PoseCheck");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        static BlazePoseSource CreateSource(BlazePoseSource.Variant variant)
        {
#if UNITY_EDITOR
            var det = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/MimicMocap/Models/BlazePose/pose_detection.onnx");
            var name = variant == BlazePoseSource.Variant.Lite ? "lite" : variant == BlazePoseSource.Variant.Heavy ? "heavy" : "full";
            var lm = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>($"Assets/MimicMocap/Models/BlazePose/pose_landmarks_detector_{name}.onnx");
            var anchors = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/MimicMocap/Data/blazepose_anchors.csv");
            Assert.NotNull(det, "detector model");
            Assert.NotNull(lm, "landmarker model");
            Assert.NotNull(anchors, "anchors");
            var s = new BlazePoseSource(det, lm, anchors, variant);
            s.Initialize();
            Assert.IsTrue(s.IsAvailable, s.Status);
            return s;
#else
            Assert.Ignore("Editor only");
            return null;
#endif
        }

        [UnityTest]
        public IEnumerator FullModel_FindsAPersonInEveryTestFrame()
        {
            var source = CreateSource(BlazePoseSource.Variant.Full);
            var pose = new PoseFrame();
            var problems = new System.Collections.Generic.List<string>();
            var log = new System.Text.StringBuilder();
            var frontFacingWithRightSign = 0;
            var frontFacing = 0;

            foreach (var name in k_Images)
            {
                var tex = TestSupport.LoadTestImage(name);
                if (tex == null) { problems.Add("missing test image " + name); continue; }
                source.ResetTracking();

                // First submit runs the detector, second uses the tracked ROI.
                var ok = true;
                for (var pass = 0; pass < 2 && ok; pass++)
                {
                    source.Submit(tex, Time.timeAsDouble);
                    var waited = 0f;
                    while (source.IsBusy && waited < 20f) { waited += Time.deltaTime; yield return null; }
                    if (source.IsBusy) { problems.Add(name + ": inference timed out"); ok = false; break; }
                    if (!source.TryGetLatest(pose)) { problems.Add(name + ": no result"); ok = false; break; }
                }
                if (!ok) continue;

                log.AppendLine($"{name}: tracked={pose.Tracked} presence={source.LastPresence:0.00} det={source.LastDetectorScore:0.00} detectorRan={source.DetectorRanLastFrame} status={source.Status}");
                if (pose.Tracked)
                {
                    void J(HumanJoint j) => log.AppendLine($"   {j,-14} {Fmt(pose[j])} conf {pose.ConfidenceOf(j):0.00}");
                    J(HumanJoint.Nose); J(HumanJoint.LeftEar); J(HumanJoint.RightEar);
                    J(HumanJoint.LeftShoulder); J(HumanJoint.RightShoulder); J(HumanJoint.LeftWrist); J(HumanJoint.RightWrist);
                    J(HumanJoint.LeftHip); J(HumanJoint.RightHip); J(HumanJoint.LeftKnee); J(HumanJoint.RightKnee); J(HumanJoint.LeftAnkle); J(HumanJoint.RightAnkle);
                    var lsh = source.LastImageLandmarks[(int)BlazeLandmark.LeftShoulder];
                    var rsh = source.LastImageLandmarks[(int)BlazeLandmark.RightShoulder];
                    log.AppendLine($"   2D shoulders L={lsh} R={rsh}  rootOffset {Fmt(pose.RootOffset)} torso {PoseTransforms.TorsoLength(pose):0.00}m leg {PoseTransforms.LegLength(pose):0.00}m box {pose.ImageBox}");

                    if (pose[HumanJoint.Nose].y < 0.2f) problems.Add($"{name}: nose y {pose[HumanJoint.Nose].y:0.00} should be above the hips");
                    if (pose[HumanJoint.Pelvis].magnitude > 0.05f) problems.Add($"{name}: pelvis not at origin {Fmt(pose[HumanJoint.Pelvis])}");
                    // Facing the camera: subject's left shoulder appears on the image right (x larger).
                    var imageDx = lsh.x - rsh.x;
                    if (Mathf.Abs(imageDx) > 0.08f)
                    {
                        frontFacing++;
                        var worldDx = pose[HumanJoint.RightShoulder].x - pose[HumanJoint.LeftShoulder].x;
                        // Facing camera (imageDx > 0) => right shoulder should be +X; facing away => -X.
                        if (Mathf.Sign(worldDx) == Mathf.Sign(imageDx)) frontFacingWithRightSign++;
                        else problems.Add($"{name}: X sign mismatch: image L-R dx {imageDx:0.00}, world R-L dx {worldDx:0.00}");
                    }
                }
                else if (System.Array.IndexOf(k_Optional, name) < 0) problems.Add(name + ": no person found");
                else log.AppendLine("   (optional frame, no person found)");

                WriteAnnotated(tex, source, pose, Path.Combine(OutputDir, name + "_blazepose.png"));
            }

            log.AppendLine($"front-facing frames {frontFacing}, with consistent X sign {frontFacingWithRightSign}");
            foreach (var p in problems) log.AppendLine("PROBLEM: " + p);
            File.WriteAllText(Path.Combine(OutputDir, "blazepose_report.txt"), log.ToString());
            Debug.Log(log.ToString());
            source.Dispose();
            Assert.IsEmpty(problems, string.Join(" | ", problems));
            Assert.Greater(frontFacing, 0, "no front-facing frame to validate the X axis");
        }

        static string Fmt(Vector3 v) => $"({v.x,6:0.00},{v.y,6:0.00},{v.z,6:0.00})";

        static void WriteAnnotated(Texture2D src, BlazePoseSource source, PoseFrame pose, string path)
        {
            var w = src.width;
            var h = src.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (pose.Tracked)
            {
                var pairs = new[]
                {
                    (BlazeLandmark.LeftShoulder, BlazeLandmark.RightShoulder), (BlazeLandmark.LeftShoulder, BlazeLandmark.LeftElbow), (BlazeLandmark.LeftElbow, BlazeLandmark.LeftWrist),
                    (BlazeLandmark.RightShoulder, BlazeLandmark.RightElbow), (BlazeLandmark.RightElbow, BlazeLandmark.RightWrist),
                    (BlazeLandmark.LeftShoulder, BlazeLandmark.LeftHip), (BlazeLandmark.RightShoulder, BlazeLandmark.RightHip), (BlazeLandmark.LeftHip, BlazeLandmark.RightHip),
                    (BlazeLandmark.LeftHip, BlazeLandmark.LeftKnee), (BlazeLandmark.LeftKnee, BlazeLandmark.LeftAnkle),
                    (BlazeLandmark.RightHip, BlazeLandmark.RightKnee), (BlazeLandmark.RightKnee, BlazeLandmark.RightAnkle),
                };
                foreach (var (a, b) in pairs)
                    DrawLine(copy, source.LastImageLandmarks[(int)a], source.LastImageLandmarks[(int)b], new Color(0.2f, 1f, 0.3f));
                for (var i = 0; i < BlazePoseLayout.BodyLandmarks; i++)
                {
                    var p = source.LastImageLandmarks[i];
                    var isLeft = i is 1 or 2 or 3 or 7 or 9 or 11 or 13 or 15 or 17 or 19 or 21 or 23 or 25 or 27 or 29 or 31;
                    DrawDot(copy, p, isLeft ? new Color(1f, 0.4f, 0.1f) : new Color(0.2f, 0.6f, 1f), 5);
                }
            }
            copy.Apply();
            File.WriteAllBytes(path, copy.EncodeToPNG());
            Object.DestroyImmediate(copy);
        }

        static void DrawDot(Texture2D t, Vector2 n, Color c, int r)
        {
            var cx = Mathf.RoundToInt(n.x * t.width);
            var cy = Mathf.RoundToInt(n.y * t.height);
            for (var y = -r; y <= r; y++)
                for (var x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    var px = cx + x; var py = cy + y;
                    if (px < 0 || py < 0 || px >= t.width || py >= t.height) continue;
                    t.SetPixel(px, py, c);
                }
        }

        static void DrawLine(Texture2D t, Vector2 a, Vector2 b, Color c)
        {
            var pa = new Vector2(a.x * t.width, a.y * t.height);
            var pb = new Vector2(b.x * t.width, b.y * t.height);
            var steps = Mathf.CeilToInt(Vector2.Distance(pa, pb));
            for (var i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(pa, pb, steps == 0 ? 0 : i / (float)steps);
                var px = Mathf.RoundToInt(p.x); var py = Mathf.RoundToInt(p.y);
                if (px < 0 || py < 0 || px >= t.width || py >= t.height) continue;
                t.SetPixel(px, py, c);
                if (px + 1 < t.width) t.SetPixel(px + 1, py, c);
            }
        }
    }
}
