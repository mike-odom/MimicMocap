using System.Collections;
using System.Collections.Generic;
using System.IO;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Plays the test mp4s through the real scene and pipeline for each available pose source,
    /// measures tracking ratio and inference time, and writes contact sheets (video frame above,
    /// rendered skeleton below) to Logs/PoseCheck/video_&lt;source&gt;_&lt;clip&gt;.png.
    /// </summary>
    public class VideoPipelineTests
    {
        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";
        static readonly string[] k_Clips = { "Kung Fu Girl.mp4", "bye bye bye - Trainer.mp4" };
        const float k_SecondsPerClip = 9f;
        const int k_Tiles = 8;

        [UnityTest]
        public IEnumerator Videos_RunThroughPipeline_ForEachSource()
        {
#if UNITY_EDITOR
            var folder = TestSupport.FindVideoFolder();
            if (folder == null) { Assert.Ignore("No TestVideos folder next to the project"); yield break; }

            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            var cam = Camera.main;
            Assert.NotNull(cam);

            var maxineAvailable = false;
            using (var probe = new MaxinePoseSource()) { probe.Initialize(); maxineAvailable = probe.IsAvailable; }
            var sources = new List<MocapDirector.SourceKind> { MocapDirector.SourceKind.BlazePose };
            if (maxineAvailable) sources.Add(MocapDirector.SourceKind.Maxine);

            var report = new System.Text.StringBuilder();
            report.AppendLine($"video folder {folder}; maxine available {maxineAvailable}");
            var problems = new List<string>();

            foreach (var src in sources)
            {
                foreach (var clip in k_Clips)
                {
                    var path = Path.Combine(folder, clip);
                    if (!File.Exists(path)) { report.AppendLine($"missing {path}"); continue; }

                    director.input = MocapDirector.InputKind.VideoFile;
                    director.videoPath = path;
                    director.source = src;
                    director.mirror = true;
                    director.showHud = false;
                    director.visual = MocapDirector.VisualMode.Skeleton;
                    director.RestartPipeline();

                    var t0 = Time.realtimeSinceStartup;
                    while (!(director.ActiveInput?.IsReady ?? false) && Time.realtimeSinceStartup - t0 < 20f) yield return null;
                    if (!(director.ActiveInput?.IsReady ?? false)) { problems.Add($"{clip}: video never became ready ({director.ActiveInput?.Name})"); continue; }

                    var sourceName = director.ActiveSource?.Name ?? "none";
                    var videoTiles = new Texture2D[k_Tiles];
                    var renderTiles = new Texture2D[k_Tiles];
                    var frames = 0;
                    var tracked = 0;
                    var msSum = 0f;
                    var msMax = 0f;
                    var lastCount = 0;
                    var start = Time.realtimeSinceStartup;
                    var nextTile = start + 0.8f;
                    var tile = 0;
                    var lastPoseTimestamp = -1.0;
                    while (Time.realtimeSinceStartup - start < k_SecondsPerClip)
                    {
                        yield return null;
                        // Count each new pose result once.
                        if (director.SmoothedPose.Timestamp != lastPoseTimestamp)
                        {
                            lastPoseTimestamp = director.SmoothedPose.Timestamp;
                            frames++;
                            if (director.SmoothedPose.Tracked) tracked++;
                            msSum += director.LastInferenceMs;
                            msMax = Mathf.Max(msMax, director.LastInferenceMs);
                        }
                        if (tile < k_Tiles && Time.realtimeSinceStartup >= nextTile)
                        {
                            videoTiles[tile] = TestSupport.ReadableCopy(director.ActiveInput.Current, 320, 180);
                            renderTiles[tile] = TestSupport.RenderCamera(cam, 320, 180);
                            tile++;
                            nextTile += (k_SecondsPerClip - 0.8f) / k_Tiles;
                        }
                        lastCount = frames;
                    }

                    var ratio = frames > 0 ? tracked / (float)frames : 0f;
                    var line = $"{sourceName} | {clip}: poses {frames} in {k_SecondsPerClip:0}s ({frames / k_SecondsPerClip:0.0}/s), tracked {ratio:P0}, inference avg {(frames > 0 ? msSum / frames : 0):0.0} ms max {msMax:0.0} ms, status '{director.ActiveSource?.Status}'";
                    report.AppendLine(line);
                    Debug.Log(line);
                    if (frames < 10) problems.Add($"{sourceName}/{clip}: only {frames} poses produced");
                    else if (ratio < 0.5f) problems.Add($"{sourceName}/{clip}: tracked only {ratio:P0}");

                    var tiles = new Texture2D[k_Tiles * 2];
                    for (var i = 0; i < k_Tiles; i++) { tiles[i] = videoTiles[i]; tiles[k_Tiles + i] = renderTiles[i]; }
                    var safe = sourceName.Replace(' ', '_') + "_" + Path.GetFileNameWithoutExtension(clip).Replace(' ', '_');
                    TestSupport.SaveContactSheet(tiles, k_Tiles, 320, 180, Path.Combine(TestSupport.OutputDir, $"video_{safe}.png"));
                    foreach (var t in tiles) if (t != null) Object.Destroy(t);
                }
            }

            foreach (var p in problems) report.AppendLine("PROBLEM: " + p);
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "video_report.txt"), report.ToString());
            Debug.Log(report.ToString());
            Assert.IsEmpty(problems, string.Join(" | ", problems));
#else
            Assert.Ignore("Editor only");
            yield break;
#endif
        }
    }
}
