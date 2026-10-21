using System.Collections;
using System.IO;
using System.Text;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Two performers in the real scene: Maxine's multi-person tracker on the two-person composite drives two
    /// avatars standing where the people stand, the portals know whose hand pressed them, and asking for two
    /// performers on a single-person source (the VMC receiver) degrades gracefully.
    /// </summary>
    public class TwoPerformerTests
    {
        /// <summary>The scene opens the webcam on load; a camera that is busy or absent logs an engine error that is not this test's business.</summary>
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        [UnityTest]
        public IEnumerator TwoPerformers_DriveTwoAvatarsFromOneFrame()
        {
#if UNITY_EDITOR
            var probe = new MaxinePoseSource();
            probe.Initialize();
            var available = probe.IsAvailable;
            probe.Dispose();
            if (!available) { Assert.Ignore("Maxine SDK unavailable"); yield break; }

            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            Assert.IsTrue(scene.isLoaded, "scene did not load");

            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director, "MocapDirector not in scene");
            var frame = MaxineMultiPersonTests.BuildTwoPersonFrame();
            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = frame;
            director.source = MocapDirector.SourceKind.Maxine;
            director.performers = 2;
            director.trackHands = false;
            director.mirror = true;
            director.showHud = false;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();
            yield return null;

            var report = new StringBuilder();
            report.AppendLine($"source={director.ActiveSource?.Name} status={director.ActiveSource?.Status} performer2Active={director.Performer2Active}");
            Assert.IsTrue(director.ActiveSource is MaxinePoseSource, "Maxine should be the active source");
            Assert.IsTrue(director.Performer2Active, "multi-person mode not active: " + director.ActiveSource?.Status);

            // Let the still frame run through a few times (the engine warms up on the first run).
            var until = Time.realtimeSinceStartup + 20f;
            var both = 0;
            while (Time.realtimeSinceStartup < until && both < 6)
            {
                yield return null;
                if (director.SmoothedPose.Tracked && director.SmoothedPose2.Tracked) both++;
            }
            report.AppendLine($"p1 tracked={director.SmoothedPose.Tracked} score={director.SmoothedPose.Score:0.00} travel={TestSupport.Fmt(director.SmoothedPose.RootOffset)}");
            report.AppendLine($"p2 tracked={director.SmoothedPose2.Tracked} score={director.SmoothedPose2.Score:0.00} travel={TestSupport.Fmt(director.SmoothedPose2.RootOffset)} character2={director.CurrentCharacter2}");
            Assert.GreaterOrEqual(both, 6, "both performers were not tracked together: " + report);

            // Two avatars, apart on stage.
            Assert.IsTrue(director.skeletonView.gameObject.activeInHierarchy, "performer 1 skeleton visible");
            Assert.IsTrue(director.Performer2Root != null && director.Performer2Root.gameObject.activeInHierarchy, "performer 2 root active");
            var p1 = director.skeletonView.GetJointWorldPosition(HumanJoint.Pelvis);
            Vector3 p2;
            if (director.Performer2ShowsCharacter)
            {
                var hips = director.Performer2Animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.IsTrue(director.Performer2Animator.gameObject.activeInHierarchy, "performer 2 character visible");
                p2 = hips.position;
            }
            else
            {
                Assert.IsTrue(director.SkeletonView2.gameObject.activeInHierarchy, "performer 2 skeleton visible");
                p2 = director.SkeletonView2.GetJointWorldPosition(HumanJoint.Pelvis);
            }
            report.AppendLine($"p1 pelvis {TestSupport.Fmt(p1)}  p2 pelvis {TestSupport.Fmt(p2)}");
            Assert.Greater(Mathf.Abs(p1.x - p2.x), 0.6f, "the two avatars should stand apart like the people in the frame");

            // Each performer picks their own look through the portals.
            var menu = director.handMenu;
            menu.visible = true; // hidden by default since the stage went black; the portals are what this test presses
            Assert.NotNull(menu);
            menu.dwellSeconds = 0.3f;
            menu.cooldownSeconds = 0.1f;
            menu.TestHandPerformer = 2;
            menu.TestHandOverride = menu.EntryWorldPosition(0); // Bones for performer 2
            until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until && director.CurrentCharacter2 != -1) yield return null;
            Assert.AreEqual(-1, director.CurrentCharacter2, "performer 2 did not switch to bones");
            Assert.AreEqual(MocapDirector.VisualMode.Skeleton, director.visual, "performer 1 must be untouched by performer 2's press");
            menu.TestHandOverride = menu.EntryWorldPosition(3);
            until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until && director.CurrentCharacter2 != 2) yield return null;
            Assert.AreEqual(2, director.CurrentCharacter2, "performer 2 did not switch to character 2");
            Assert.IsTrue(director.Performer2ShowsCharacter);
            Assert.AreEqual(MocapDirector.VisualMode.Skeleton, director.visual);
            menu.TestHandPerformer = 1;
            menu.TestHandOverride = menu.EntryWorldPosition(2);
            until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until && director.CurrentCharacter != 1) yield return null;
            Assert.AreEqual(1, director.CurrentCharacter, "performer 1 did not switch");
            Assert.AreEqual(2, director.CurrentCharacter2, "performer 2 must keep their choice");
            Assert.AreEqual(3, menu.SelectedEntry2);
            menu.TestHandOverride = null;

            until = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            var shot = TestSupport.RenderCamera(Camera.main, 1280, 720);
            var path = Path.Combine(TestSupport.OutputDir, "two_performers.png");
            File.WriteAllBytes(path, shot.EncodeToPNG());
            Object.Destroy(shot);
            report.AppendLine("saved " + path);

            // Back to one performer: the second avatar goes away and Maxine runs single-person again.
            director.SetPerformers(1);
            yield return null;
            Assert.IsFalse(director.Performer2Active);
            Assert.IsFalse(director.Performer2Root.gameObject.activeSelf);
            // One performer shown; with stableIdentity the tracker keeps running multi-person so the avatar stays with its person.
            Assert.IsTrue(director.ActiveSource is MaxinePoseSource mx && mx.MultiPersonActive == director.stableIdentity, "after SetPerformers(1) Maxine's mode should follow stableIdentity");
            Debug.Log("[MimicMocap] Two performer report:\n" + report);
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator TwoPerformers_OnVmcSourceStaySinglePerson()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = null;
            director.source = MocapDirector.SourceKind.Vmc;
            director.vmcPort = 39700 + new System.Random().Next(0, 200);
            director.performers = 2;
            director.trackHands = false;
            director.showHud = false;
            director.RestartPipeline();
            yield return null;
            Assert.IsTrue(director.ActiveSource is Vmc.VmcPoseSource, "VMC should still be selectable with performers = 2: " + director.ActiveSource?.Name);
            Assert.IsFalse(director.Performer2Active, "a single-person source must not pretend to have a second performer");
            Assert.IsFalse(director.Performer2Root == null || director.Performer2Root.gameObject.activeSelf, "no second avatar on a single-person source");
            for (var i = 0; i < 5; i++) yield return null; // no exceptions while idle
#else
            yield break;
#endif
        }
    }
}
