using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>Renders every character slot in the standing pose to Logs/PoseCheck/character_&lt;name&gt;.png for eyeballing.</summary>
    public class CharacterLookTests
    {
        /// <summary>The scene opens the webcam on load; a camera that is busy or absent logs an engine error that is not this test's business.</summary>
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        [UnityTest]
        public IEnumerator EveryCharacter_RendersStanding()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = null;
            director.trackHands = false;
            director.idleAttract = false;
            director.showHud = false;
            director.visual = MocapDirector.VisualMode.Character;
            director.RestartPipeline();
            yield return null;
            if (director.handMenu != null) director.handMenu.visible = false;

            var standing = new PoseFrame();
            StandingPose.Fill(standing);
            var posed = new PoseFrame();
            GesturePose(posed);
            var report = new StringBuilder();
            var cam = Camera.main;
            var camPos = cam.transform.position;
            var camRot = cam.transform.rotation;
            for (var i = 0; i < director.Slots.Length; i++)
            {
                Assert.IsTrue(director.SelectCharacter(i), "select " + i);
                Assert.IsTrue(director.CharacterReady, director.Slots[i].DisplayName + " did not bind");
                var animator = director.CurrentSlot.Animator;
                // The rig is still in its FBX rest pose here (first selection): remember where its feet point.
                var restSoleL = SoleDirection(animator, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes);
                var restSoleR = SoleDirection(animator, HumanBodyBones.RightFoot, HumanBodyBones.RightToes);
                director.ApplyPoseToCharacter(standing);
                yield return null;
                director.ApplyPoseToCharacter(standing);
                // Standing flat-footed must leave the ankles as the rig keeps them. Retargeting ankle->toe used to give rigs
                // whose toe bone sits well below the ankle (MMD: 45 deg) a permanent toe-up bend; the sole-based rule does not.
                var name = director.Slots[i].DisplayName.Replace(' ', '_');
                if (restSoleL.HasValue && restSoleR.HasValue)
                {
                    var bendL = Vector3.Angle(restSoleL.Value, SoleDirection(animator, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes).Value);
                    var bendR = Vector3.Angle(restSoleR.Value, SoleDirection(animator, HumanBodyBones.RightFoot, HumanBodyBones.RightToes).Value);
                    report.AppendLine($"{director.Slots[i].DisplayName}: standing ankle bend L {bendL:0.0} R {bendR:0.0} deg");
                    Assert.Less(bendL, 12f, name + ": left ankle bends when standing flat");
                    Assert.Less(bendR, 12f, name + ": right ankle bends when standing flat");
                }
                // Frame the figure: camera 2.6 m in front, looking at the chest.
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips).position;
                cam.transform.position = new Vector3(hips.x, 1.0f, hips.z + 2.6f);
                cam.transform.LookAt(new Vector3(hips.x, 0.95f, hips.z));
                var shot = TestSupport.RenderCamera(cam, 720, 1080);
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, $"character_{name}.png"), shot.EncodeToPNG());
                Object.Destroy(shot);
                // Same rig in a gesture (one arm up, one elbow bent, head turned): catches rigs whose arm mesh does
                // not follow the mapped bones, which a near-rest standing pose hides.
                director.ApplyPoseToCharacter(posed);
                yield return null;
                director.ApplyPoseToCharacter(posed);
                shot = TestSupport.RenderCamera(cam, 720, 1080);
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, $"character_{name}_gesture.png"), shot.EncodeToPNG());
                Object.Destroy(shot);
                var renderers = animator.GetComponentsInChildren<Renderer>();
                var mats = 0; var transparent = 0; var clipped = 0;
                foreach (var r in renderers) foreach (var m in r.sharedMaterials) { if (m == null) continue; mats++; if (m.renderQueue >= 3000) transparent++; else if (m.IsKeywordEnabled("_ALPHATEST_ON")) clipped++; }
                report.AppendLine($"{director.Slots[i].DisplayName}: renderers {renderers.Length}, materials {mats} (transparent {transparent}, alpha-clipped {clipped}), fingers {director.CharacterHasFingers}");
            }
            cam.transform.SetPositionAndRotation(camPos, camRot);
            Debug.Log("[MimicMocap] Character look report:\n" + report);
#else
            yield break;
#endif
        }

        /// <summary>World direction from the foot bone to the toes bone, or null when the rig has no toes bone.</summary>
        static Vector3? SoleDirection(Animator animator, HumanBodyBones foot, HumanBodyBones toes)
        {
            var f = animator.GetBoneTransform(foot);
            var t = animator.GetBoneTransform(toes);
            if (f == null || t == null) return null;
            var d = t.position - f.position;
            return d.sqrMagnitude > 1e-8f ? d.normalized : (Vector3?)null;
        }

        /// <summary>Standing pose with the right arm raised overhead, the left elbow bent across the chest and the head turned.</summary>
        public static void GesturePose(PoseFrame f)
        {
            StandingPose.Fill(f);
            void Set(HumanJoint j, float x, float y, float z) { f.Joints[(int)j] = new Vector3(x, y, z); f.Confidence[(int)j] = 1f; }
            // Right arm (subject's right = +X) straight up.
            Set(HumanJoint.RightShoulder, 0.19f, 0.48f, 0f);
            Set(HumanJoint.RightElbow, 0.24f, 0.74f, 0.03f);
            Set(HumanJoint.RightWrist, 0.26f, 1.00f, 0.05f);
            Set(HumanJoint.RightHandTip, 0.27f, 1.09f, 0.06f);
            Set(HumanJoint.RightIndex, 0.27f, 1.09f, 0.06f);
            Set(HumanJoint.RightThumb, 0.23f, 1.03f, 0.08f);
            Set(HumanJoint.RightPinky, 0.30f, 1.06f, 0.04f);
            // Left arm bent 90 degrees at the elbow, forearm across the chest.
            Set(HumanJoint.LeftShoulder, -0.19f, 0.48f, 0f);
            Set(HumanJoint.LeftElbow, -0.30f, 0.24f, 0.04f);
            Set(HumanJoint.LeftWrist, -0.06f, 0.30f, 0.18f);
            Set(HumanJoint.LeftHandTip, 0.03f, 0.33f, 0.20f);
            Set(HumanJoint.LeftIndex, 0.03f, 0.33f, 0.20f);
            Set(HumanJoint.LeftThumb, -0.03f, 0.36f, 0.22f);
            Set(HumanJoint.LeftPinky, 0.01f, 0.29f, 0.17f);
            // Head turned toward the subject's left.
            Set(HumanJoint.Nose, -0.05f, 0.63f, 0.10f);
            Set(HumanJoint.LeftEye, -0.07f, 0.665f, 0.07f);
            Set(HumanJoint.RightEye, -0.01f, 0.665f, 0.10f);
            Set(HumanJoint.LeftEar, -0.06f, 0.62f, -0.04f);
            Set(HumanJoint.RightEar, 0.07f, 0.62f, 0.03f);
        }
    }
}
