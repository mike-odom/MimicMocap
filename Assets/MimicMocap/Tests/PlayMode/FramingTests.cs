using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>The zoom and move controls: a standing figure fills the asked fraction of the view, and shifts go the way the keys say.</summary>
    public class FramingTests
    {
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        [UnityTest]
        public IEnumerator Framing_FillsTheViewAndShiftsTheFigure()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            var cam = Camera.main;
            Assert.NotNull(cam);
            cam.aspect = 16f / 9f;

            var feet = Vector3.zero;
            var head = new Vector3(0f, 1.75f, 0f);
            float Height(float fill)
            {
                director.SetFraming(fill, Vector2.zero, save: false);
                return cam.WorldToViewportPoint(head).y - cam.WorldToViewportPoint(feet).y;
            }
            Assert.AreEqual(0.78f, Height(0.78f), 0.03f, "a standing figure should fill the asked fraction of the view height");
            Assert.AreEqual(0.6f, Height(0.6f), 0.03f, "zooming out");
            Assert.AreEqual(0.95f, Height(0.95f), 0.03f, "zooming in");
            Assert.Greater(cam.WorldToViewportPoint(feet).y, 0.0f, "feet stay above the bottom edge when zoomed in");
            Assert.Less(cam.WorldToViewportPoint(head).y, 1.0f, "head stays under the top edge when zoomed in");

            director.SetFraming(MocapDirector.k_DefaultFill, Vector2.zero, save: false);
            var centre = cam.WorldToViewportPoint(new Vector3(0f, 0.875f, 0f));
            Assert.AreEqual(0.5f, centre.x, 0.01f, "the figure starts centred");

            director.SetFraming(MocapDirector.k_DefaultFill, new Vector2(0.3f, 0f), save: false);
            Assert.Greater(cam.WorldToViewportPoint(new Vector3(0f, 0.875f, 0f)).x, centre.x + 0.05f, "a right shift moves the figure to screen-right");
            director.SetFraming(MocapDirector.k_DefaultFill, new Vector2(-0.3f, 0f), save: false);
            Assert.Less(cam.WorldToViewportPoint(new Vector3(0f, 0.875f, 0f)).x, centre.x - 0.05f, "a left shift moves the figure to screen-left");
            director.SetFraming(MocapDirector.k_DefaultFill, new Vector2(0f, 0.3f), save: false);
            Assert.Greater(cam.WorldToViewportPoint(new Vector3(0f, 0.875f, 0f)).y, centre.y + 0.05f, "an up shift moves the figure up the screen");
            director.SetFraming(MocapDirector.k_DefaultFill, new Vector2(0f, -0.3f), save: false);
            Assert.Less(cam.WorldToViewportPoint(new Vector3(0f, 0.875f, 0f)).y, centre.y - 0.05f, "a down shift moves the figure down the screen");

            director.SetFraming(MocapDirector.k_DefaultFill, Vector2.zero, save: false);
#else
            yield break;
#endif
        }
    }
}
