using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Loads the real main scene, drives it from a still test frame through the full pipeline
    /// (BlazePose -> smoother -> skeleton view + humanoid retargeter) and renders the main camera to
    /// PNGs in Logs/PoseCheck so the result can be eyeballed. Also measures how well the rig's limb
    /// directions match the tracked pose.
    /// </summary>
    public class SceneRenderSmokeTests
    {
        /// <summary>The scene opens the webcam on load; a camera that is busy or absent logs an engine error that is not this test's business.</summary>
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";

        static string OutputDir
        {
            get
            {
                var dir = Path.Combine(Application.dataPath, "..", "Logs", "PoseCheck");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        [UnityTest]
        public IEnumerator Scene_PosesSkeletonAndCharacterFromTestFrame()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            Assert.IsTrue(scene.isLoaded, "scene did not load");

            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director, "MocapDirector not in scene");
            var tex = TestSupport.LoadTestImage("bye_bye_bye_-_Trainer_1");
            Assert.NotNull(tex);

            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = tex;
            director.source = MocapDirector.SourceKind.BlazePose;
            director.mirror = true;
            director.showHud = false;
            director.visual = MocapDirector.VisualMode.Both;
            director.RestartPipeline();

            // The driven rig is the active character slot (several rigs live under CharacterRoot now).
            var animator = director.CurrentSlot != null ? director.CurrentSlot.Animator
                : director.characterRoot != null ? director.characterRoot.GetComponentInChildren<Animator>(true) : null;
            var leftHand = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            var handBefore = leftHand != null ? leftHand.position : Vector3.zero;
            var hipsBefore = hips != null ? hips.position : Vector3.zero;

            // Let inference run several times and the smoother settle; also warms up shader variants.
            var deadline = Time.realtimeSinceStartup + 30f;
            var tracked = 0;
            while (Time.realtimeSinceStartup < deadline && tracked < 20)
            {
                yield return null;
                if (director.SmoothedPose.Tracked) tracked++;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine($"source={director.ActiveSource?.Name} status={director.ActiveSource?.Status} trackedFrames={tracked} characterReady={director.CharacterReady}");
            report.AppendLine($"pose score={director.SmoothedPose.Score:0.00} rootOffset={director.SmoothedPose.RootOffset}");
            var maxAngleError = 0f;
            if (animator != null)
            {
                report.AppendLine($"animator isHuman={animator.isHuman} avatar={(animator.avatar != null ? animator.avatar.name : "null")} valid={(animator.avatar != null && animator.avatar.isValid)}");
                if (leftHand != null) report.AppendLine($"left hand before={handBefore} after={leftHand.position}");
                if (hips != null) report.AppendLine($"hips before={hipsBefore} after={hips.position}");
                if (animator.isHuman) maxAngleError = ReportLimbDirections(animator, director.SmoothedPose, report);
            }

            var cam = Camera.main;
            Assert.NotNull(cam, "no main camera");
            yield return null;
            RenderTo(cam, Path.Combine(OutputDir, "scene_both.png"));
            director.SetVisualMode(MocapDirector.VisualMode.Skeleton);
            yield return null;
            yield return null;
            RenderTo(cam, Path.Combine(OutputDir, "scene_skeleton.png"));
            if (director.CharacterReady)
            {
                director.SetVisualMode(MocapDirector.VisualMode.Character);
                yield return null;
                yield return null;
                yield return null;
                RenderTo(cam, Path.Combine(OutputDir, "scene_character.png"));
                // Side view to judge depth/arm placement.
                var camPos = cam.transform.position;
                var camRot = cam.transform.rotation;
                cam.transform.position = new Vector3(3.2f, 1.2f, 0.8f);
                cam.transform.LookAt(new Vector3(0f, 0.9f, 0f));
                RenderTo(cam, Path.Combine(OutputDir, "scene_character_side.png"));
                cam.transform.SetPositionAndRotation(camPos, camRot);

                // Diagnostic overlay: glowing skeleton drawn on top of the character at the same spot.
                director.SetVisualMode(MocapDirector.VisualMode.Both);
                yield return null;
                director.skeletonView.transform.position = director.characterRoot.position;
                yield return null;
                yield return null;
                RenderTo(cam, Path.Combine(OutputDir, "scene_overlay.png"));
            }

            // Second still frame where a hand is large enough for finger tracking, skeleton only.
            var tex2 = TestSupport.LoadTestImage("Kung_Fu_Girl_2_1");
            if (tex2 != null)
            {
                director.stillImage = tex2;
                director.visual = MocapDirector.VisualMode.Skeleton;
                director.RestartPipeline();
                var until = Time.realtimeSinceStartup + 20f;
                var n = 0;
                while (Time.realtimeSinceStartup < until && n < 25) { yield return null; if (director.SmoothedPose.Tracked) n++; }
                report.AppendLine($"kung fu frame: tracked={director.SmoothedPose.Tracked} leftHand={director.SmoothedPose.LeftHand.Tracked} rightHand={director.SmoothedPose.RightHand.Tracked} hands='{director.Hands?.Status}'");
                RenderTo(cam, Path.Combine(OutputDir, "scene_skeleton_kungfu.png"));
                // Close-up on whichever hand tracked (pose is mirrored, so check both).
                var camPos = cam.transform.position;
                var camRot = cam.transform.rotation;
                var handJoint = director.SmoothedPose.LeftHand.Tracked ? HumanJoint.LeftWrist : HumanJoint.RightWrist;
                var wrist = director.skeletonView.GetJointWorldPosition(handJoint);
                cam.transform.position = wrist + new Vector3(0f, 0.15f, 0.9f);
                cam.transform.LookAt(wrist);
                RenderTo(cam, Path.Combine(OutputDir, "scene_skeleton_kungfu_close.png"));
                cam.transform.SetPositionAndRotation(camPos, camRot);
            }

            File.WriteAllText(Path.Combine(OutputDir, "scene_report.txt"), report.ToString());
            Debug.Log(report.ToString());

            Assert.Greater(tracked, 0, "pipeline never produced a tracked pose from the still frame");
            Assert.IsTrue(director.CharacterReady, "character under CharacterRoot is not a valid Humanoid; run MimicMocap > Setup Characters");
            Assert.Greater(Vector3.Distance(handBefore, leftHand.position), 0.05f, "retargeter did not move the character's left hand");
            Assert.Less(maxAngleError, 25f, "a rig limb points more than 25 degrees away from the tracked limb; see Logs/PoseCheck/scene_report.txt");
#else
            Assert.Ignore("Editor only");
            yield break;
#endif
        }

        /// <summary>
        /// Deterministic finger render: a synthetic T-pose body plus the hand landmarks from MediaPipe's
        /// reference photo, pushed straight into the skeleton view and photographed up close.
        /// </summary>
        [UnityTest]
        public IEnumerator Scene_RendersFingersFromReferenceHand()
        {
#if UNITY_EDITOR
            LogAssert.ignoreFailingMessages = true; // a busy or missing webcam logs an engine error on scene load
            var scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = null; // no body source driving the view during this test
            director.trackHands = false;
            director.idleAttract = false;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();
            yield return null;

            // Hand landmarks from the reference photo.
            var model = UnityEditor.AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>("Assets/MimicMocap/Models/BlazeHand/hand_landmarks_detector.onnx");
            var tex = TestSupport.LoadTestImage("hand_reference");
            Assert.NotNull(model); Assert.NotNull(tex);
            var hands = new MimicMocap.BlazeHand.HandTracker(model);
            hands.Initialize();
            var seed = new PoseFrame { Tracked = true, HasImageJoints = true, Timestamp = 1 };
            void J(HumanJoint j, float x, float y) { seed.ImageJoints[(int)j] = new Vector2(x, y); seed.Confidence[(int)j] = 1f; }
            J(HumanJoint.LeftWrist, 0.459f, 0.283f); J(HumanJoint.LeftElbow, 0.380f, 0.003f); J(HumanJoint.LeftIndex, 0.519f, 0.410f); J(HumanJoint.LeftPinky, 0.558f, 0.327f);
            hands.Submit(tex, seed, 1);
            var w = 0f;
            while (hands.InFlight > 0 && w < 20f) { w += Time.deltaTime; yield return null; }
            var left = new HandFrame(); var right = new HandFrame();
            Assert.IsTrue(hands.TryGetLatest(left, right) && left.Tracked, "reference hand did not track");
            hands.Dispose();

            // Synthetic standing body, arms out, in canonical metres (hip-centred).
            var pose = new PoseFrame { Tracked = true, Score = 1f, Timestamp = 2 };
            void P(HumanJoint j, float x, float y, float z) { pose[j] = new Vector3(x, y, z); pose.Confidence[(int)j] = 1f; }
            P(HumanJoint.LeftHip, -0.1f, 0f, 0f); P(HumanJoint.RightHip, 0.1f, 0f, 0f);
            P(HumanJoint.LeftKnee, -0.1f, -0.45f, 0f); P(HumanJoint.RightKnee, 0.1f, -0.45f, 0f);
            P(HumanJoint.LeftAnkle, -0.1f, -0.9f, 0f); P(HumanJoint.RightAnkle, 0.1f, -0.9f, 0f);
            P(HumanJoint.LeftHeel, -0.1f, -0.93f, -0.05f); P(HumanJoint.RightHeel, 0.1f, -0.93f, -0.05f);
            P(HumanJoint.LeftToe, -0.1f, -0.93f, 0.15f); P(HumanJoint.RightToe, 0.1f, -0.93f, 0.15f);
            P(HumanJoint.LeftShoulder, -0.2f, 0.5f, 0f); P(HumanJoint.RightShoulder, 0.2f, 0.5f, 0f);
            P(HumanJoint.LeftElbow, -0.48f, 0.5f, 0f); P(HumanJoint.RightElbow, 0.48f, 0.5f, 0f);
            P(HumanJoint.LeftWrist, -0.74f, 0.5f, 0f); P(HumanJoint.RightWrist, 0.74f, 0.5f, 0f);
            P(HumanJoint.LeftHandTip, -0.9f, 0.5f, 0f); P(HumanJoint.RightHandTip, 0.9f, 0.5f, 0f);
            P(HumanJoint.LeftThumb, -0.82f, 0.46f, 0.05f); P(HumanJoint.RightThumb, 0.82f, 0.46f, 0.05f);
            P(HumanJoint.LeftPinky, -0.86f, 0.5f, -0.04f); P(HumanJoint.RightPinky, 0.86f, 0.5f, -0.04f);
            P(HumanJoint.Nose, 0f, 0.72f, 0.1f); P(HumanJoint.LeftEar, -0.08f, 0.7f, 0f); P(HumanJoint.RightEar, 0.08f, 0.7f, 0f);
            pose.SynthesiseTorso();
            left.CopyTo(pose.LeftHand);
            pose.LeftHand.Tracked = true;

            var view = director.skeletonView;
            view.Apply(pose);
            yield return null;
            view.Apply(pose);
            yield return null;

            var cam = Camera.main;
            RenderTo(cam, Path.Combine(OutputDir, "scene_fingers_tpose.png"));
            var wrist = view.GetJointWorldPosition(HumanJoint.LeftWrist);
            var camPos = cam.transform.position;
            var camRot = cam.transform.rotation;
            cam.transform.position = wrist + new Vector3(0f, 0.12f, 0.7f);
            cam.transform.LookAt(wrist + new Vector3(-0.05f, 0.05f, 0f));
            RenderTo(cam, Path.Combine(OutputDir, "scene_fingers_close.png"));
            cam.transform.SetPositionAndRotation(camPos, camRot);
            Assert.IsTrue(view.IsJointVisible(HumanJoint.LeftWrist));

            // Same pose on the rigged character: check its finger bones follow the landmarks and photograph the hand.
            if (director.CharacterReady && director.CharacterHasFingers)
            {
                director.SetVisualMode(MocapDirector.VisualMode.Character);
                yield return null;
                director.ApplyPoseToCharacter(pose);
                yield return null;
                director.ApplyPoseToCharacter(pose);
                yield return null;

                var animator = director.characterRoot.GetComponentInChildren<Animator>(true);
                var root = animator.transform;
                var report = new System.Text.StringBuilder("character finger directions (rig vs landmarks):\n");
                var worst = 0f;
                var checks = new (string name, HumanBodyBones a, HumanBodyBones b, int la, int lb)[]
                {
                    ("index prox", HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, 5, 6),
                    ("index mid", HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal, 6, 7),
                    ("middle prox", HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, 9, 10),
                    ("ring prox", HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, 13, 14),
                    ("little prox", HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, 17, 18),
                    ("thumb prox", HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, 1, 2),
                    ("thumb mid", HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal, 2, 3),
                    ("hand", HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, 0, 9),
                };
                foreach (var c in checks)
                {
                    var ta = animator.GetBoneTransform(c.a);
                    var tb = animator.GetBoneTransform(c.b);
                    if (ta == null || tb == null) { report.AppendLine($"   {c.name,-12} missing bone"); continue; }
                    var rigDir = root.InverseTransformDirection(tb.position - ta.position).normalized;
                    var lmDir = (pose.LeftHand.Landmarks[c.lb] - pose.LeftHand.Landmarks[c.la]).normalized;
                    var angle = Vector3.Angle(rigDir, lmDir);
                    worst = Mathf.Max(worst, angle);
                    report.AppendLine($"   {c.name,-12} rig {Fmt(rigDir)} landmark {Fmt(lmDir)} angle {angle,5:0.0} deg");
                }
                report.AppendLine($"   worst finger angle error {worst:0.0} deg");
                Debug.Log(report.ToString());
                File.AppendAllText(Path.Combine(OutputDir, "scene_report.txt"), report.ToString());

                var handBone = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                cam.transform.position = handBone.position + new Vector3(0f, 0.1f, 0.55f);
                cam.transform.LookAt(handBone.position + new Vector3(-0.04f, 0.04f, 0f));
                RenderTo(cam, Path.Combine(OutputDir, "scene_character_fingers_close.png"));
                cam.transform.SetPositionAndRotation(camPos, camRot);
                RenderTo(cam, Path.Combine(OutputDir, "scene_character_tpose.png"));
                Assert.Less(worst, 25f, "character finger bones deviate from the hand landmarks; see scene_report.txt");
            }
#else
            Assert.Ignore("Editor only");
            yield break;
#endif
        }

        /// <summary>
        /// Compare each rig limb direction (world space, root-relative) with the tracked pose's limb direction.
        /// The pose is in canonical axes which match the rig root's axes when the root is at identity.
        /// </summary>
        static float ReportLimbDirections(Animator animator, PoseFrame pose, System.Text.StringBuilder report)
        {
            var root = animator.transform;
            var checks = new (string name, HumanBodyBones a, HumanBodyBones b, HumanJoint pa, HumanJoint pb)[]
            {
                ("L upper arm", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanJoint.LeftShoulder, HumanJoint.LeftElbow),
                ("L forearm", HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanJoint.LeftElbow, HumanJoint.LeftWrist),
                ("R upper arm", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanJoint.RightShoulder, HumanJoint.RightElbow),
                ("R forearm", HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, HumanJoint.RightElbow, HumanJoint.RightWrist),
                ("L thigh", HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanJoint.LeftHip, HumanJoint.LeftKnee),
                ("L shin", HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanJoint.LeftKnee, HumanJoint.LeftAnkle),
                ("R thigh", HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanJoint.RightHip, HumanJoint.RightKnee),
                ("R shin", HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanJoint.RightKnee, HumanJoint.RightAnkle),
                ("spine", HumanBodyBones.Hips, HumanBodyBones.Head, HumanJoint.Pelvis, HumanJoint.Head),
                ("shoulders", HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanJoint.LeftShoulder, HumanJoint.RightShoulder),
            };
            var worst = 0f;
            foreach (var c in checks)
            {
                var ta = animator.GetBoneTransform(c.a);
                var tb = animator.GetBoneTransform(c.b);
                if (ta == null || tb == null) { report.AppendLine($"   {c.name,-12} missing bone"); continue; }
                var rigDir = root.InverseTransformDirection(tb.position - ta.position).normalized;
                var poseDir = (pose[c.pb] - pose[c.pa]).normalized;
                var angle = Vector3.Angle(rigDir, poseDir);
                worst = Mathf.Max(worst, angle);
                report.AppendLine($"   {c.name,-12} rig {Fmt(rigDir)} pose {Fmt(poseDir)} angle {angle,5:0.0} deg");
            }
            report.AppendLine($"   worst limb angle error {worst:0.0} deg");
            return worst;
        }

        static string Fmt(Vector3 v) => $"({v.x,5:0.00},{v.y,5:0.00},{v.z,5:0.00})";

        static void RenderTo(Camera cam, string path)
        {
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
            Object.Destroy(rt);
        }
    }
}
