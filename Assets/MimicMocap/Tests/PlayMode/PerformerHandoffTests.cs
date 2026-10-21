using System.Collections;
using System.Text;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// One performer shown, everyone tracked: the avatar stays with its person while a second person is in frame, passes
    /// to that person once the first has gone, and does not jump back when the first returns. Played out on the two-person
    /// composite by blanking and restoring the left person in the live frame.
    /// </summary>
    public class PerformerHandoffTests
    {
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        [UnityTest]
        public IEnumerator OnePerformer_PassesToTheOtherPersonWhenTheFirstLeaves()
        {
#if UNITY_EDITOR
            var probe = new MaxinePoseSource();
            probe.Initialize();
            var available = probe.IsAvailable;
            probe.Dispose();
            if (!available) { Assert.Ignore("Maxine SDK unavailable"); yield break; }

            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);

            var frame = MaxineMultiPersonTests.BuildTwoPersonFrame();
            // The left person occupies the left 960 columns; keep their pixels so they can come back later.
            var leftPixels = frame.GetPixels(0, 0, 960, frame.height);
            var blank = new Color[960 * frame.height];
            var background = new Color32(24, 20, 28, 255);
            for (var i = 0; i < blank.Length; i++) blank[i] = background;

            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = frame;
            director.source = MocapDirector.SourceKind.Maxine;
            director.performers = 1;
            director.stableIdentity = true;
            director.trackHands = false;
            director.mirror = false;
            director.showHud = false;
            director.idleAttract = false;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();
            yield return null;
            Assert.IsTrue(director.ActiveSource is MaxinePoseSource mx && mx.MultiPersonActive, "Maxine should run its multi-person tracker for stable identity: " + director.ActiveSource?.Status);

            var report = new StringBuilder();
            float PelvisX() => director.RawPose.ImageJoints[(int)HumanJoint.Pelvis].x;

            // 1. Both people present: performer 1 is the leftmost person (slot 0).
            var until = Time.realtimeSinceStartup + 20f;
            var trackedFrames = 0;
            while (Time.realtimeSinceStartup < until && trackedFrames < 10)
            {
                yield return null;
                if (director.RawPose.Tracked) trackedFrames++;
            }
            Assert.GreaterOrEqual(trackedFrames, 10, "performer 1 was not tracked on the two-person frame: " + director.ActiveSource?.Status);
            var x1 = PelvisX();
            report.AppendLine($"both present: performer slot {director.PerformerSlot}, pelvis x {x1:0.00}");
            Assert.Less(x1, 0.5f, "with both present performer 1 should be the left person");
            Assert.AreEqual(0, director.PerformerSlot);

            // 2. The left person leaves: the avatar passes to the right person.
            frame.SetPixels(0, 0, 960, frame.height, blank);
            frame.Apply();
            var t0 = Time.realtimeSinceStartup;
            var switched = false;
            var untrackedFrames = 0;
            until = t0 + 10f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                if (!director.RawPose.Tracked) { untrackedFrames++; continue; }
                if (PelvisX() > 0.5f) { switched = true; break; }
            }
            var switchSeconds = Time.realtimeSinceStartup - t0;
            report.AppendLine($"left person gone: switched={switched} after {switchSeconds:0.00} s ({untrackedFrames} untracked frames), performer slot {director.PerformerSlot}, pelvis x {PelvisX():0.00}");
            Assert.IsTrue(switched, "performer 1 did not pass to the remaining person: " + report);
            Assert.Less(switchSeconds, 4f, "the handoff took too long: " + report);
            Assert.AreNotEqual(0, director.PerformerSlot, "the avatar should now read another slot: " + report);

            // 3. The left person returns: the avatar stays with the person it has.
            frame.SetPixels(0, 0, 960, frame.height, leftPixels);
            frame.Apply();
            until = Time.realtimeSinceStartup + 3f;
            var rightFrames = 0; var leftFrames = 0;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                if (!director.RawPose.Tracked) continue;
                if (PelvisX() > 0.5f) rightFrames++; else leftFrames++;
            }
            report.AppendLine($"left person back: performer 1 on the right {rightFrames} frames, on the left {leftFrames} frames, slot {director.PerformerSlot}");
            Debug.Log("[MimicMocap] Performer handoff report:\n" + report);
            Assert.Greater(rightFrames, 30, "performer 1 should still be tracked on the right person: " + report);
            Assert.LessOrEqual(leftFrames, 3, "performer 1 must not jump back to the returning person: " + report);
            Object.Destroy(frame);
#else
            yield break;
#endif
        }
    }
}
