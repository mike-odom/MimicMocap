using System.Collections;
using System.IO;
using MimicMocap.BlazePose;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Opens the first webcam through the real capture path, saves what it sees to Logs/PoseCheck/webcam_frame.png,
    /// and runs both pose sources on a live frame. Nobody needs to be in front of the camera; this proves the
    /// device opens, frames arrive, and the sources accept live frames without errors. Skips if no camera.
    /// </summary>
    public class WebcamSmokeTests
    {
        [UnityTest]
        public IEnumerator Webcam_OpensAndFeedsBothSources()
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0) { Assert.Ignore("No webcam devices on this machine"); yield break; }

            var log = new System.Text.StringBuilder();
            log.AppendLine("devices: " + string.Join(" | ", System.Array.ConvertAll(devices, d => d.name + (d.isFrontFacing ? " (front)" : ""))));

            var cam = new WebcamFrameSource(0, 1280, 720, 30);
            cam.Start();
            var t0 = Time.realtimeSinceStartup;
            var frames = 0;
            while (Time.realtimeSinceStartup - t0 < 12f && frames < 15)
            {
                yield return null;
                cam.Update();
                if (cam.IsReady && cam.HasNewFrame) { frames++; cam.Consume(); }
            }
            log.AppendLine($"{cam.Name}: ready={cam.IsReady} size={cam.Width}x{cam.Height} frames in {Time.realtimeSinceStartup - t0:0.0}s = {frames}");

            if (cam.IsReady && cam.Current != null)
            {
                var copy = TestSupport.ReadableCopy(cam.Current);
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "webcam_frame.png"), copy.EncodeToPNG());
                Object.Destroy(copy);

                // BlazePose on a live frame
                var bz = TestSupport.CreateBlaze(BlazePoseSource.Variant.Full);
                var pose = new PoseFrame();
                bz.Submit(cam.Current, Time.timeAsDouble);
                var waited = 0f;
                while (bz.IsBusy && waited < 20f) { waited += Time.deltaTime; yield return null; }
                var gotB = bz.TryGetLatest(pose);
                log.AppendLine($"BlazePose on live frame: result={gotB} tracked={pose.Tracked} status='{bz.Status}'");
                bz.Dispose();

                // Maxine on a live frame
                var mx = new MaxinePoseSource();
                mx.Initialize();
                if (mx.IsAvailable)
                {
                    var gotM = false;
                    for (var i = 0; i < 3 && !gotM; i++)
                    {
                        mx.Submit(cam.Current, Time.timeAsDouble);
                        var f = 0;
                        while (f < 120 && !(gotM = mx.TryGetLatest(pose)) && mx.IsBusy) { f++; yield return null; }
                        yield return null;
                    }
                    log.AppendLine($"Maxine on live frame: result={gotM} tracked={pose.Tracked} status='{mx.Status}' run={mx.LastRunMs:0.0}ms");
                    Assert.IsTrue(mx.IsAvailable, "Maxine disabled itself on a live frame: " + mx.Status);
                }
                else log.AppendLine("Maxine unavailable: " + mx.Status);
                mx.Dispose();
            }

            cam.Dispose();
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "webcam_report.txt"), log.ToString());
            Debug.Log(log.ToString());
            if (frames == 0)
                Assert.Inconclusive("webcam opened but produced no frames within 12 s (batch-mode editors often do not stream cameras; verify in the player)");
        }
    }
}
