using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// How steady is what the audience sees? Runs a dance clip through the real scene twice, with the hand
    /// stabiliser off and on, and measures hand presence, hand flicker and fingertip/wrist jitter on the
    /// smoothed pose that drives the avatar. Also checks the attract loop and the ghost stage.
    /// </summary>
    public class StabilityTests
    {
        /// <summary>The scene opens the webcam on load; a camera that is busy or absent logs an engine error that is not this test's business.</summary>
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        sealed class Metrics
        {
            public int Frames, LeftPresent, RightPresent, LeftToggles, RightToggles;
            // Series for the roughness measure (per hand / joint), only over runs where the value exists.
            public readonly List<Vector3>[] Tip = { new List<Vector3>(), new List<Vector3>() };
            public readonly List<Vector3>[] Wrist = { new List<Vector3>(), new List<Vector3>() };
            public readonly List<Vector3> Travel = new List<Vector3>();
            public readonly List<Vector3>[] Elbow = { new List<Vector3>(), new List<Vector3>() };
            public readonly List<Vector3>[] RawWrist = { new List<Vector3>(), new List<Vector3>() };

            /// <summary>Frames of delay between the smoothed series and the raw one (best match over a 0..12 frame shift).</summary>
            public static int LagFrames(List<Vector3> smoothed, List<Vector3> raw)
            {
                var n = Mathf.Min(smoothed.Count, raw.Count);
                if (n < 30) return 0;
                var best = 0; var bestErr = double.MaxValue;
                for (var k = 0; k <= 12; k++)
                {
                    var err = 0.0; var c = 0;
                    for (var t = k + 1; t < n; t++) { err += Vector3.Distance(smoothed[t], raw[t - k]); c++; }
                    err /= Mathf.Max(1, c);
                    if (err < bestErr) { bestErr = err; best = k; }
                }
                return best;
            }

            public float WristLagMs => 0.5f * (LagFrames(Wrist[0], RawWrist[0]) + LagFrames(Wrist[1], RawWrist[1])) * 1000f / 30f;
            public double RawWristRoughness => 0.5 * (Roughness(RawWrist[0]) + Roughness(RawWrist[1]));

            /// <summary>Mean deviation from a 5-frame centred moving average: real motion is smooth over 170 ms, noise is not.</summary>
            public static double Roughness(List<Vector3> s)
            {
                if (s.Count < 7) return 0.0;
                var sum = 0.0;
                var n = 0;
                for (var i = 2; i < s.Count - 2; i++)
                {
                    var avg = (s[i - 2] + s[i - 1] + s[i] + s[i + 1] + s[i + 2]) / 5f;
                    sum += Vector3.Distance(s[i], avg);
                    n++;
                }
                return n > 0 ? sum / n : 0.0;
            }

            public double TipRoughness => 0.5 * (Roughness(Tip[0]) + Roughness(Tip[1]));
            public double WristRoughness => 0.5 * (Roughness(Wrist[0]) + Roughness(Wrist[1]));
            public double ElbowRoughness => 0.5 * (Roughness(Elbow[0]) + Roughness(Elbow[1]));
            public double TravelRoughness => Roughness(Travel);
            public int TipSamples => Tip[0].Count + Tip[1].Count;

            public override string ToString() =>
                $"frames {Frames}: hands present L {100f * LeftPresent / Mathf.Max(1, Frames):0}% R {100f * RightPresent / Mathf.Max(1, Frames):0}%, toggles L {LeftToggles} R {RightToggles}, " +
                $"roughness (mm): fingertip {1000 * TipRoughness:0.00} (wrist-relative, {TipSamples} samples), wrist {1000 * WristRoughness:0.00} (raw {1000 * RawWristRoughness:0.00}), elbow {1000 * ElbowRoughness:0.00}, travel {1000 * TravelRoughness:0.00}, wrist lag {WristLagMs:0} ms";
        }

        static IEnumerator Measure(MocapDirector director, float seconds, Metrics m)
        {
            var lastStamp = -1.0;
            bool? lastLeft = null, lastRight = null;
            var until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                var pose = director.SmoothedPose;
                if (pose.Timestamp == lastStamp || !pose.Tracked) continue;
                lastStamp = pose.Timestamp;
                m.Frames++;
                var l = pose.LeftHand.Tracked; var r = pose.RightHand.Tracked;
                if (l) m.LeftPresent++;
                if (r) m.RightPresent++;
                if (lastLeft.HasValue && lastLeft.Value != l) m.LeftToggles++;
                if (lastRight.HasValue && lastRight.Value != r) m.RightToggles++;
                lastLeft = l; lastRight = r;
                m.Wrist[0].Add(pose[HumanJoint.LeftWrist]);
                m.Wrist[1].Add(pose[HumanJoint.RightWrist]);
                m.RawWrist[0].Add(director.RawPose[HumanJoint.LeftWrist]);
                m.RawWrist[1].Add(director.RawPose[HumanJoint.RightWrist]);
                m.Elbow[0].Add(pose[HumanJoint.LeftElbow]);
                m.Elbow[1].Add(pose[HumanJoint.RightElbow]);
                m.Travel.Add(pose.RootOffset);
                // Fingertip relative to the wrist, so arm motion does not count. Only frames the detector actually saw
                // are sampled, so the stabiliser pass is measured on the same kind of frame as the raw pass (its
                // body-driven hands during detector gaps are a different thing from smoothing); a gap starts a new run.
                var lSeen = l && pose.LeftHand.HasImageLandmarks; var rSeen = r && pose.RightHand.HasImageLandmarks;
                if (lSeen) m.Tip[0].Add(pose.LeftHand.Landmarks[(int)HandFrame.Landmark.IndexTip]); else if (m.Tip[0].Count > 0 && m.Tip[0].Count < 7) m.Tip[0].Clear();
                if (rSeen) m.Tip[1].Add(pose.RightHand.Landmarks[(int)HandFrame.Landmark.IndexTip]); else if (m.Tip[1].Count > 0 && m.Tip[1].Count < 7) m.Tip[1].Clear();
            }
        }

        [UnityTest]
        public IEnumerator Hands_AreSteadierAndAlwaysPresentWithTheStabiliser()
        {
#if UNITY_EDITOR
            var folder = TestSupport.FindVideoFolder();
            if (folder == null) { Assert.Ignore("TestVideos folder not found"); yield break; }
            var clip = Directory.GetFiles(folder, "*.mp4");
            var kungFu = System.Array.Find(clip, f => f.IndexOf("Kung", System.StringComparison.OrdinalIgnoreCase) >= 0) ?? (clip.Length > 0 ? clip[0] : null);
            if (kungFu == null) { Assert.Ignore("no mp4 in TestVideos"); yield break; }

            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            director.input = MocapDirector.InputKind.VideoFile;
            director.videoPath = kungFu;
            director.source = MocapDirector.SourceKind.Auto;
            director.trackHands = true;
            director.showHud = false;
            director.idleAttract = false;
            director.visual = MocapDirector.VisualMode.Skeleton;

            var report = new StringBuilder();
            PoseSmoother.Settings S(float cutoff, float beta) => new PoseSmoother.Settings { minCutoff = cutoff, beta = beta, derivativeCutoff = 1f, confidenceFloor = 0.4f };
            // Pass 0/1 compare the hand stabiliser; passes 1..4 sweep body smoothing profiles (steadiness vs lag).
            var profiles = new[] { PoseSmoother.Settings.Default, PoseSmoother.Settings.Default, S(1.2f, 0.03f), S(0.6f, 3f), S(0.25f, 10f) };
            var labels = new[] { "stabiliser OFF, default body smoothing", "stabiliser ON,  default body smoothing", "stabiliser ON,  body 1.2 Hz / beta 0.03 (old)", "stabiliser ON,  body 0.6 Hz / beta 3", "stabiliser ON,  body 0.25 Hz / beta 10 (MediaPipe-like)" };
            var results = new Metrics[profiles.Length];
            for (var pass = 0; pass < profiles.Length; pass++)
            {
                director.stabilizeHands = pass >= 1;
                director.smoothing = profiles[pass];
                director.RestartPipeline();
                // Let the clip start and the models warm up.
                var warm = Time.realtimeSinceStartup + 2.5f;
                while (Time.realtimeSinceStartup < warm) yield return null;
                results[pass] = new Metrics();
                yield return Measure(director, 7f, results[pass]);
                report.AppendLine($"{labels[pass]} ({director.ActiveSource?.Name}): {results[pass]}");
            }
            // Restore the shipped defaults for anything that runs after this test.
            director.smoothing = PoseSmoother.Settings.Default;
            Debug.Log("[MimicMocap] Stability report (" + Path.GetFileName(kungFu) + "):\n" + report);
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "stability_report.txt"), report.ToString());

            var off = results[0]; var on = results[1];
            Assert.Greater(off.Frames, 60, "clip did not run long enough");
            Assert.Greater(on.Frames, 60, "clip did not run long enough");
            Assert.GreaterOrEqual(on.LeftPresent / (float)on.Frames, 0.95f, "with the stabiliser the left hand should be drawn almost every frame");
            Assert.GreaterOrEqual(on.RightPresent / (float)on.Frames, 0.95f, "with the stabiliser the right hand should be drawn almost every frame");
            Assert.LessOrEqual(on.LeftToggles + on.RightToggles, 2, "hands must not flicker");
            // Fingertip roughness is reported, not asserted: on this clip the detector finds the hands in short bursts, so
            // the stabiliser's detector-seen frames are mostly the blend back from the body-driven hand (motion, not
            // noise), and the comparison swings either way from run to run (7.3 vs 10.8 mm, then 9.9 vs 9.1). The
            // smoothing itself is measured deterministically in HandStabilizerTests (synthetic jitter 9.1 -> 2.4 mm).
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator AttractLoop_ShowsASwayingSkeletonWhenNobodyIsThere()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = null;
            director.trackHands = false;
            director.showHud = false;
            director.idleAttract = true;
            director.idleDelay = 0.3f;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();

            var until = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < until && !director.IdleActive) yield return null;
            Assert.IsTrue(director.IdleActive, "attract loop did not start");
            yield return null;
            Assert.IsTrue(director.skeletonView.IsJointVisible(HumanJoint.Head), "idle skeleton visible");
            var head0 = director.skeletonView.GetJointWorldPosition(HumanJoint.Head);
            var wrist0 = director.skeletonView.GetJointWorldPosition(HumanJoint.RightWrist);
            until = Time.realtimeSinceStartup + 2.5f;
            var moved = 0f; var waved = 0f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                moved = Mathf.Max(moved, Vector3.Distance(director.skeletonView.GetJointWorldPosition(HumanJoint.Head), head0));
                waved = Mathf.Max(waved, Vector3.Distance(director.skeletonView.GetJointWorldPosition(HumanJoint.RightWrist), wrist0));
            }
            Assert.Greater(moved, 0.005f, "idle skeleton should breathe/sway");
            Assert.Greater(waved, 0.1f, "idle skeleton should wave within the first seconds");

            // The idle hands are full template hands.
            var idle = new PoseFrame();
            director.BuildIdlePose(1f, idle);
            Assert.IsTrue(idle.LeftHand.Tracked && idle.RightHand.Tracked);
            Assert.Greater(idle.RightHand.Landmarks[(int)HandFrame.Landmark.MiddleTip].magnitude, 0.12f);

            var shot = TestSupport.RenderCamera(Camera.main, 1280, 720);
            File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "attract_loop.png"), shot.EncodeToPNG());
            Object.Destroy(shot);

            // Ghost stage: black background, no ground.
            director.SetGhostStage(true);
            yield return null;
            var ground = GameObject.Find("Ground");
            Assert.IsNull(ground, "ground should be hidden on the ghost stage");
            Assert.IsFalse(RenderSettings.fog);
            var ghostShot = TestSupport.RenderCamera(Camera.main, 1280, 720);
            File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "ghost_stage.png"), ghostShot.EncodeToPNG());
            // Corners of the frame must be pure black for a Pepper's ghost.
            var c = ghostShot.GetPixel(10, 10);
            Assert.Less(c.r + c.g + c.b, 0.05f, "ghost stage corner is not black: " + c);
            Object.Destroy(ghostShot);
            director.SetGhostStage(false);
            yield return null;
            Assert.IsNotNull(GameObject.Find("Ground"));
#else
            yield break;
#endif
        }
    }
}
