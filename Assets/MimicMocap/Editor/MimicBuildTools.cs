using System;
using System.IO;
using System.Linq;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace MimicMocap.Editor
{
    /// <summary>
    /// Command-line friendly project setup: creates the URP assets, the main scene, and the player build.
    /// Run with: Unity.exe -batchmode -projectPath ... -executeMethod MimicMocap.Editor.MimicBuildTools.&lt;Method&gt; -quit
    /// </summary>
    public static class MimicBuildTools
    {
        const string k_Root = "Assets/MimicMocap";
        const string k_SceneDir = k_Root + "/Scenes";
        const string k_ScenePath = k_SceneDir + "/MimicMocap.unity";
        const string k_SettingsDir = k_Root + "/Settings";
        const string k_MaterialDir = k_Root + "/Materials";
        const string k_ModelDir = k_Root + "/Models/BlazePose";
        const string k_UrpPackagePath = "Packages/com.unity.render-pipelines.universal";

        [MenuItem("MimicMocap/Compile Check")]
        public static void CompileCheck()
        {
            Debug.Log("[MimicMocap] Compile check OK");
        }

        [MenuItem("MimicMocap/Create Scene")]
        public static void CreateScene()
        {
            try
            {
                EnsureFolders();
                var rp = EnsureUrp();
                var mats = CreateMaterials();

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                // Atmosphere
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.05f, 0.06f, 0.11f);
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = 0.02f;
                RenderSettings.fogColor = new Color(0.03f, 0.02f, 0.06f);

                // Camera: sits at +Z looking back at the origin; the subject faces the camera.
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                var cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                camGo.transform.position = new Vector3(0f, 1.15f, 3.3f);
                camGo.transform.LookAt(new Vector3(0f, 0.9f, 0f));
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = true;
                camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

                // Post-processing volume: bloom for the glowing bones, vignette for mood.
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, k_SettingsDir + "/MimicVolume.asset");
                var bloom = profile.Add<Bloom>(true);
                bloom.intensity.value = 1.4f;
                bloom.threshold.value = 0.85f;
                bloom.scatter.value = 0.75f;
                bloom.tint.value = new Color(0.75f, 0.9f, 1f);
                var vignette = profile.Add<Vignette>(true);
                vignette.intensity.value = 0.42f;
                vignette.smoothness.value = 0.6f;
                vignette.color.value = new Color(0.02f, 0f, 0.05f);
                var color = profile.Add<ColorAdjustments>(true);
                color.saturation.value = -12f;
                color.contrast.value = 12f;
                color.colorFilter.value = new Color(0.85f, 0.9f, 1f);
                var grain = profile.Add<FilmGrain>(true);
                grain.intensity.value = 0.25f;
                grain.type.value = FilmGrainLookup.Medium1;
                EditorUtility.SetDirty(profile);
                var volGo = new GameObject("Global Volume");
                var vol = volGo.AddComponent<Volume>();
                vol.isGlobal = true;
                vol.sharedProfile = profile;

                // Lights: cold moon key, green ghost rim from behind, warm pumpkin uplight.
                var moon = new GameObject("Moonlight").AddComponent<Light>();
                moon.type = LightType.Directional;
                moon.color = new Color(0.62f, 0.70f, 1f);
                moon.intensity = 1.1f;
                moon.shadows = LightShadows.Soft;
                moon.transform.rotation = Quaternion.Euler(42f, 155f, 0f);

                var rim = new GameObject("Ghost Rim").AddComponent<Light>();
                rim.type = LightType.Point;
                rim.color = new Color(0.35f, 1f, 0.45f);
                rim.intensity = 4f;
                rim.range = 7f;
                rim.transform.position = new Vector3(0.6f, 1.8f, -1.6f);

                var up = new GameObject("Pumpkin Uplight").AddComponent<Light>();
                up.type = LightType.Point;
                up.color = new Color(1f, 0.5f, 0.12f);
                up.intensity = 2.2f;
                up.range = 3.5f;
                up.transform.position = new Vector3(0f, 0.12f, 1.1f);

                // Ground and moon
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.localScale = new Vector3(6f, 1f, 6f);
                ground.GetComponent<Renderer>().sharedMaterial = mats.ground;

                var moonBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                moonBall.name = "Moon";
                UnityEngine.Object.DestroyImmediate(moonBall.GetComponent<Collider>());
                moonBall.transform.position = new Vector3(-6.5f, 7.5f, -21f);
                moonBall.transform.localScale = Vector3.one * 4.2f;
                moonBall.GetComponent<Renderer>().sharedMaterial = mats.moon;
                moonBall.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

                // Skeleton view
                var skelGo = new GameObject("Skeleton");
                var skel = skelGo.AddComponent<ProceduralSkeletonView>();
                skel.boneMaterial = mats.bone;
                skel.jointMaterial = mats.joint;
                skel.skullMaterial = mats.skull;
                skel.socketMaterial = mats.socket;

                // Character root: drop any Humanoid-rigged FBX (Mixamo zombie/skeleton) under here.
                var charRoot = new GameObject("CharacterRoot");
                charRoot.transform.position = Vector3.zero;

                // Director + HUD
                var dirGo = new GameObject("MocapDirector");
                var director = dirGo.AddComponent<MocapDirector>();
                dirGo.AddComponent<MocapHud>();
                director.handMenu = dirGo.AddComponent<HandMenu>();
                director.blazeDetector = AssetDatabase.LoadAssetAtPath<ModelAsset>(k_ModelDir + "/pose_detection.onnx");
                director.blazeLandmarkerLite = AssetDatabase.LoadAssetAtPath<ModelAsset>(k_ModelDir + "/pose_landmarks_detector_lite.onnx");
                director.blazeLandmarkerFull = AssetDatabase.LoadAssetAtPath<ModelAsset>(k_ModelDir + "/pose_landmarks_detector_full.onnx");
                director.blazeLandmarkerHeavy = AssetDatabase.LoadAssetAtPath<ModelAsset>(k_ModelDir + "/pose_landmarks_detector_heavy.onnx");
                director.blazeAnchors = AssetDatabase.LoadAssetAtPath<TextAsset>(k_Root + "/Data/blazepose_anchors.csv");
                director.handLandmarker = AssetDatabase.LoadAssetAtPath<ModelAsset>(k_Root + "/Models/BlazeHand/hand_landmarks_detector.onnx");
                director.trackHands = director.handLandmarker != null;
                // Downloaded, git-ignored; the director runs MediaPipe alone when it is missing.
                director.handFallbackModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(k_Root + "/Models/RTMPose/rtmpose_m_hand5.onnx");
                director.skeletonView = skel;
                director.characterRoot = charRoot.transform;
                director.stillImage = AssetDatabase.LoadAssetAtPath<Texture2D>(k_Root + "/Tests/Resources/TestImages/Kung_Fu_Girl_1.png");
                if (director.blazeDetector == null || director.blazeLandmarkerFull == null || director.blazeAnchors == null)
                    throw new InvalidOperationException("BlazePose model assets not found under " + k_ModelDir);

                EditorSceneManager.SaveScene(scene, k_ScenePath);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(k_ScenePath, true) };
                AssetDatabase.SaveAssets();
                Debug.Log("[MimicMocap] Scene created at " + k_ScenePath + " with pipeline " + AssetDatabase.GetAssetPath(rp));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        [MenuItem("MimicMocap/Build Windows Player")]
        public static void BuildPlayer()
        {
            try
            {
                if (!File.Exists(k_ScenePath)) CreateScene();
                var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Build");
                Directory.CreateDirectory(outDir);
                var exe = Path.Combine(outDir, "MimicMocap.exe");

                PlayerSettings.productName = "Mimic Mocap";
                PlayerSettings.companyName = "MimicMocap";
                PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
                PlayerSettings.resizableWindow = true;
                PlayerSettings.runInBackground = true;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { k_ScenePath },
                    locationPathName = exe,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };
                var report = BuildPipeline.BuildPlayer(options);
                Debug.Log($"[MimicMocap] Build {report.summary.result}: {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors -> {exe}");
                if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        static void EnsureFolders()
        {
            foreach (var dir in new[] { k_SceneDir, k_SettingsDir, k_MaterialDir })
            {
                if (!AssetDatabase.IsValidFolder(dir))
                    AssetDatabase.CreateFolder(Path.GetDirectoryName(dir).Replace('\\', '/'), Path.GetFileName(dir));
            }
        }

        static UniversalRenderPipelineAsset EnsureUrp()
        {
            var rendererPath = k_SettingsDir + "/MimicRenderer.asset";
            var rpPath = k_SettingsDir + "/MimicURP.asset";

            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, rendererPath);
                ResourceReloader.ReloadAllNullIn(rendererData, k_UrpPackagePath);
                EditorUtility.SetDirty(rendererData);
            }

            var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(rpPath);
            if (rp == null)
            {
                rp = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(rp, rpPath);
            }
            rp.supportsHDR = true;
            rp.msaaSampleCount = 4;
            rp.shadowDistance = 25f;
            rp.supportsCameraDepthTexture = true;
            rp.colorGradingMode = ColorGradingMode.HighDynamicRange;
            EditorUtility.SetDirty(rp);

            GraphicsSettings.defaultRenderPipeline = rp;
            var levels = QualitySettings.names.Length;
            var current = QualitySettings.GetQualityLevel();
            for (var i = 0; i < levels; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = rp;
            }
            QualitySettings.SetQualityLevel(current, false);
            AssetDatabase.SaveAssets();
            return rp;
        }

        struct Materials
        {
            public Material bone, joint, skull, socket, ground, moon;
        }

        static Materials CreateMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (lit == null || unlit == null) throw new InvalidOperationException("URP shaders not found; is the URP package installed?");

            Material Emissive(string name, Color baseColor, Color emission)
            {
                var path = $"{k_MaterialDir}/{name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(lit);
                    AssetDatabase.CreateAsset(m, path);
                }
                m.shader = lit;
                m.SetColor("_BaseColor", baseColor);
                m.SetFloat("_Smoothness", 0.35f);
                m.SetFloat("_Metallic", 0f);
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                EditorUtility.SetDirty(m);
                return m;
            }

            var neon = Shader.Find("MimicMocap/Neon Bones");
            if (neon == null) throw new InvalidOperationException("MimicMocap/Neon Bones shader not found (Assets/MimicMocap/Shaders)");

            Material Neon(string name, Color fill, Color rim, float rimPower)
            {
                var path = $"{k_MaterialDir}/{name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(neon);
                    AssetDatabase.CreateAsset(m, path);
                }
                m.shader = neon;
                m.SetColor("_BaseColor", fill);
                m.SetColor("_RimColor", rim);
                m.SetFloat("_RimPower", rimPower);
                m.SetFloat("_Fill", 1f);
                m.SetFloat("_Core", 0.08f);
                m.SetFloat("_Detail", 0f);
                m.SetColor("_EmissionColor", Color.white);
                EditorUtility.SetDirty(m);
                return m;
            }

            var mats = new Materials
            {
                // Neon: near-black cores, deep blue glow growing toward the edges, hot cyan-blue rims; bloom halos the edges.
                bone = Neon("SkeletonBone", new Color(0.08f, 0.32f, 1.1f), new Color(0.55f, 0.9f, 2.6f), 2.0f),
                joint = Neon("SkeletonJoint", new Color(0.1f, 0.38f, 1.2f), new Color(0.6f, 0.95f, 2.8f), 1.8f),
                skull = Neon("SkeletonSkull", new Color(0.09f, 0.35f, 1.15f), new Color(0.6f, 0.95f, 2.7f), 2.0f),
            };

            // Eye sockets: plain black, drawn opaque so the additive skull cannot glow through them: dark holes.
            var socketPath = k_MaterialDir + "/SkeletonSocket.mat";
            mats.socket = AssetDatabase.LoadAssetAtPath<Material>(socketPath);
            if (mats.socket == null)
            {
                mats.socket = new Material(unlit);
                AssetDatabase.CreateAsset(mats.socket, socketPath);
            }
            mats.socket.shader = unlit;
            mats.socket.SetColor("_BaseColor", Color.black);
            EditorUtility.SetDirty(mats.socket);

            var groundPath = k_MaterialDir + "/Ground.mat";
            mats.ground = AssetDatabase.LoadAssetAtPath<Material>(groundPath);
            if (mats.ground == null)
            {
                mats.ground = new Material(lit);
                AssetDatabase.CreateAsset(mats.ground, groundPath);
            }
            mats.ground.shader = lit;
            mats.ground.SetColor("_BaseColor", new Color(0.035f, 0.035f, 0.05f));
            mats.ground.SetFloat("_Smoothness", 0.6f);
            mats.ground.SetFloat("_Metallic", 0.1f);
            EditorUtility.SetDirty(mats.ground);

            var moonPath = k_MaterialDir + "/Moon.mat";
            mats.moon = AssetDatabase.LoadAssetAtPath<Material>(moonPath);
            if (mats.moon == null)
            {
                mats.moon = new Material(unlit);
                AssetDatabase.CreateAsset(mats.moon, moonPath);
            }
            mats.moon.shader = unlit;
            mats.moon.SetColor("_BaseColor", new Color(2.6f, 2.5f, 2.1f));
            EditorUtility.SetDirty(mats.moon);

            AssetDatabase.SaveAssets();
            return mats;
        }
    }
}
