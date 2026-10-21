using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>The real scene wires the second hand model (RTMPose) in as the fallback for lost hands, and K's toggle works.</summary>
    public class HandFallbackTests
    {
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        [UnityTest]
        public IEnumerator Scene_AttachesTheHandFallbackAndTogglesIt()
        {
#if UNITY_EDITOR
            if (!System.IO.File.Exists("Assets/MimicMocap/Models/RTMPose/rtmpose_m_hand5.onnx")) { Assert.Ignore("RTMPose hand model not downloaded"); yield break; }
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode("Assets/MimicMocap/Scenes/MimicMocap.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            Assert.NotNull(director.handFallbackModel, "the scene should reference the RTMPose hand model");
            Assert.IsTrue(director.handFallback, "the fallback is on by default");
            director.input = MocapDirector.InputKind.StillImage;
            director.trackHands = true;
            director.showHud = false;
            director.idleAttract = false;
            director.RestartPipeline();
            yield return null;
            Assert.IsTrue(director.HandFallbackActive, "the hand tracker should have the fallback attached");
            director.SetHandFallback(false);
            Assert.IsFalse(director.HandFallbackActive, "K turns it off");
            director.SetHandFallback(true);
            Assert.IsTrue(director.HandFallbackActive, "and back on");
#else
            yield break;
#endif
        }
    }
}
