using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Character slots and the hand menu in the real scene: every rig binds, hovering a portal switches
    /// characters, holding still does not re-press, and the result is rendered to Logs/PoseCheck/character_menu.png.
    /// </summary>
    public class CharacterMenuTests
    {
        /// <summary>The scene opens the webcam on load; a camera that is busy or absent logs an engine error that is not this test's business.</summary>
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        [UnityTest]
        public IEnumerator Menu_SwitchesCharactersByHoldingAHandInAPortal()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            Assert.IsTrue(scene.isLoaded, "scene did not load");

            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director, "MocapDirector not in scene");
            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = null; // no body source: the test drives the menu directly
            director.trackHands = false;
            director.idleAttract = false;
            director.showHud = false;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();
            yield return null;

            var report = new StringBuilder();
            Assert.GreaterOrEqual(director.Slots.Length, 2, "need at least two character slots; run MimicMocap > Setup Characters");
            Assert.AreEqual(1, director.Slots.Count(s => s.gameObject.activeSelf), "exactly one slot should be active");
            Assert.AreEqual(director.Slots.Length, director.Slots.Select(s => s.DisplayName).Distinct().Count(), "slot names are unique");

            var menu = director.handMenu;
            menu.visible = true; // hidden by default since the stage went black; the portals are what this test presses
            Assert.NotNull(menu, "hand menu missing");
            menu.logPreviewFraming = true;
            menu.Rebuild();
            yield return null;
            Assert.AreEqual(director.Slots.Length + 1, menu.EntryCount, "one portal per character plus Bones");
            for (var i = 0; i < menu.EntryCount; i++)
            {
                if (menu.EntryPreview(i) == null) continue;
                var copy = TestSupport.ReadableCopy(menu.EntryPreview(i));
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, $"menu_preview_{i}_{menu.EntryLabel(i)}.png"), copy.EncodeToPNG());
                Object.Destroy(copy);
            }
            Assert.AreEqual("Bones", menu.EntryLabel(0));
            report.AppendLine("portals: " + string.Join(", ", Enumerable.Range(0, menu.EntryCount).Select(i => $"{i}:{menu.EntryLabel(i)} @ {TestSupport.Fmt(menu.EntryWorldPosition(i))}")));
            for (var i = 0; i < menu.EntryCount; i++) Assert.NotNull(menu.EntryPreview(i), "portal " + menu.EntryLabel(i) + " has no preview");
            Assert.AreEqual(0, menu.SelectedEntry, "Bones selected while the view shows the skeleton");

            menu.dwellSeconds = 0.3f;
            menu.cooldownSeconds = 0.1f;
            var pressed = new List<int>();
            menu.EntryPressed += pressed.Add;

            // Hold a hand in the second character's portal.
            menu.TestHandOverride = menu.EntryWorldPosition(2);
            var until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until && pressed.Count == 0) yield return null;
            Assert.AreEqual(1, pressed.Count, "portal 2 was not pressed (hovered=" + menu.HoveredEntry + ", dwell=" + menu.DwellFraction + ")");
            Assert.AreEqual(2, pressed[0]);
            Assert.AreEqual(1, director.CurrentCharacter);
            Assert.AreEqual(MocapDirector.VisualMode.Character, director.visual);
            Assert.IsTrue(director.Slots[1].gameObject.activeSelf, "selected slot active");
            Assert.IsFalse(director.Slots[0].gameObject.activeSelf, "previous slot hidden");
            Assert.IsTrue(director.CharacterReady, director.CurrentSlot.DisplayName + " should bind as a Humanoid");
            Assert.AreEqual(2, menu.SelectedEntry);

            // Holding still must not press again.
            until = Time.realtimeSinceStartup + 0.8f;
            while (Time.realtimeSinceStartup < until) yield return null;
            Assert.AreEqual(1, pressed.Count, "holding still re-pressed the portal");

            // Slide over to Bones.
            menu.TestHandOverride = menu.EntryWorldPosition(0);
            until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until && pressed.Count < 2) yield return null;
            Assert.AreEqual(2, pressed.Count, "Bones portal was not pressed");
            Assert.AreEqual(MocapDirector.VisualMode.Skeleton, director.visual);
            Assert.AreEqual(0, menu.SelectedEntry);

            // Leaving the row clears the hover.
            menu.TestHandOverride = new Vector3(0f, 1f, 0f);
            yield return null;
            yield return null;
            Assert.AreEqual(-1, menu.HoveredEntry);

            // Every slot binds a valid rig and drives from a pose.
            var standing = new PoseFrame();
            StandingPose.Fill(standing);
            for (var i = 0; i < director.Slots.Length; i++)
            {
                Assert.IsTrue(director.SelectCharacter(i));
                Assert.IsTrue(director.CharacterReady, director.Slots[i].DisplayName + " is not a valid Humanoid rig");
                Assert.IsTrue(director.ApplyPoseToCharacter(standing));
                var animator = director.CurrentSlot.Animator;
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                report.AppendLine($"{director.Slots[i].DisplayName}: fingers={director.CharacterHasFingers} headScale={director.Slots[i].headScale} headLocalScale={(head != null ? head.localScale.x : 0f):0.00} active={director.Slots.Count(s => s.gameObject.activeSelf)}");
                Assert.AreEqual(1, director.Slots.Count(s => s.gameObject.activeSelf));
                if (head != null && director.Slots[i].headScale < 0.99f) Assert.Less(head.localScale.x, 0.99f, director.Slots[i].DisplayName + " head scale not applied");
            }

            // Keyboard path.
            director.SelectCharacter(0);
            director.NextCharacter(+1);
            Assert.AreEqual(1, director.CurrentCharacter);
            director.NextCharacter(-2);
            Assert.AreEqual(director.Slots.Length - 1, director.CurrentCharacter);
            Assert.IsTrue(director.SelectCharacterByName(director.Slots[0].DisplayName.Substring(0, 3)));
            Assert.AreEqual(0, director.CurrentCharacter);
            Assert.IsTrue(director.SelectCharacterByName("bones"));
            Assert.AreEqual(MocapDirector.VisualMode.Skeleton, director.visual);

            // Picture: first character standing, a hand part-way through pressing the third portal.
            director.SelectCharacter(0);
            director.ApplyPoseToCharacter(standing);
            director.skeletonView.gameObject.SetActive(false);
            menu.dwellSeconds = 5f;
            menu.TestHandOverride = menu.EntryWorldPosition(3);
            until = Time.realtimeSinceStartup + 2.2f;
            while (Time.realtimeSinceStartup < until) yield return null;
            Assert.AreEqual(3, menu.HoveredEntry);
            Assert.Greater(menu.DwellFraction, 0.2f);
            var cam = Camera.main;
            Assert.NotNull(cam, "no main camera");
            var shot = TestSupport.RenderCamera(cam, 1280, 720);
            var path = Path.Combine(TestSupport.OutputDir, "character_menu.png");
            File.WriteAllBytes(path, shot.EncodeToPNG());
            Object.Destroy(shot);
            report.AppendLine("saved " + path);
            Debug.Log("[MimicMocap] Character menu report:\n" + report);
#else
            yield break;
#endif
        }
    }
}
