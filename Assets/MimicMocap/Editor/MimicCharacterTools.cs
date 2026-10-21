using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MimicMocap.Editor
{
    /// <summary>
    /// Imports rigged character FBX files as Mecanim Humanoids and places one under CharacterRoot in the main scene.
    /// Works for the bundled KayKit skeletons and for any Mixamo FBX dropped into Assets/MimicMocap/Characters.
    /// </summary>
    public static class MimicCharacterTools
    {
        const string k_CharDir = "Assets/MimicMocap/Characters";
        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";
        const string k_MaterialDir = "Assets/MimicMocap/Materials";

        /// <summary>Which FBX to place in the scene. Override with -characterFbx &lt;path&gt; on the command line.</summary>
        static string DefaultCharacter => k_CharDir + "/SkeletonAnimated/Skeleton_Animated.fbx";

        [MenuItem("MimicMocap/Setup Characters")]
        public static void SetupCharacters()
        {
            try
            {
                var fbxPaths = AssetDatabase.FindAssets("t:Model", new[] { k_CharDir })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p)
                    .ToArray();
                if (fbxPaths.Length == 0) throw new FileNotFoundException("No FBX files under " + k_CharDir);

                var report = new StringBuilder();
                foreach (var path in fbxPaths)
                {
                    ImportAsHumanoid(path, report);
                }
                Debug.Log("[MimicMocap] Humanoid import report:\n" + report);

                var chosen = CommandLineArg("-characterFbx") ?? DefaultCharacter;
                PlaceAllInScene(fbxPaths, chosen);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        static string CommandLineArg(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static void ImportAsHumanoid(string assetPath, StringBuilder report)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                report.AppendLine($"{assetPath}: not a model");
                return;
            }
            var changed = false;
            if (importer.animationType != ModelImporterAnimationType.Human) { importer.animationType = ModelImporterAnimationType.Human; changed = true; }
            if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel) { importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; changed = true; }
            if (importer.materialImportMode != ModelImporterMaterialImportMode.ImportStandard) { importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; changed = true; }
            if (importer.materialLocation != ModelImporterMaterialLocation.InPrefab) { importer.materialLocation = ModelImporterMaterialLocation.InPrefab; changed = true; }
            if (!importer.importAnimation) { importer.importAnimation = true; changed = true; }
            if (changed)
            {
                importer.SaveAndReimport();
            }
            // FBX files that embed their textures (Mixamo, Blender exports with embed_textures) get them pulled out so
            // URP materials can reference them.
            var texDir = Path.GetDirectoryName(assetPath).Replace('\\', '/') + "/Textures";
            var hasEmbeddedTextures = AssetDatabase.LoadAllAssetsAtPath(assetPath).Any(a => a is Texture2D);
            if (hasEmbeddedTextures)
            {
                if (!AssetDatabase.IsValidFolder(texDir)) AssetDatabase.CreateFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'), "Textures");
                if (importer.ExtractTextures(texDir))
                {
                    AssetDatabase.Refresh();
                    importer.SaveAndReimport();
                    report.AppendLine($"{Path.GetFileName(assetPath)}: extracted embedded textures to {texDir}");
                }
            }

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go != null && (FixMmdMapping(importer, go, report) || FixBipedFingerMapping(importer, go, report)))
            {
                importer.SaveAndReimport();
                go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            }
            var animator = go != null ? go.GetComponent<Animator>() : null;
            var avatar = animator != null ? animator.avatar : null;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                report.AppendLine($"{Path.GetFileName(assetPath)}: HUMANOID MAPPING FAILED (avatar {(avatar == null ? "null" : avatar.isValid ? "valid" : "invalid")})");
                return;
            }

            var mapped = avatar.humanDescription.human;
            var required = new[] { "Hips", "Spine", "Head", "LeftUpperArm", "LeftLowerArm", "LeftHand", "RightUpperArm", "RightLowerArm", "RightHand", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot" };
            var missing = required.Where(r => mapped.All(h => h.humanName != r)).ToArray();
            var bounds = go.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            report.AppendLine($"{Path.GetFileName(assetPath)}: humanoid OK, {mapped.Length} bones mapped, height {bounds.size.y:0.00}, missing [{string.Join(", ", missing)}]");
            foreach (var h in mapped) report.AppendLine($"    {h.humanName,-16} <- {h.boneName}");
        }

        /// <summary>
        /// Replace Standard-shader materials with URP Lit copies saved beside the FBX. With <paramref name="forceCopy"/>
        /// even already-URP materials are copied out of the FBX, so they can be edited (importer-made materials are
        /// read-only sub-assets).
        /// </summary>
        static void ConvertMaterialsToUrp(string fbxPath, Renderer[] renderers, Shader lit, bool forceCopy = false)
        {
            var dir = Path.GetDirectoryName(fbxPath).Replace('\\', '/') + "/Materials";
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(Path.GetDirectoryName(fbxPath).Replace('\\', '/'), "Materials");
            var converted = new System.Collections.Generic.Dictionary<Material, Material>();
            var stem = Path.GetFileNameWithoutExtension(fbxPath);
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null) continue;
                    var alreadyUrp = src.shader != null && src.shader.name.StartsWith("Universal Render Pipeline");
                    if (alreadyUrp && !forceCopy) continue;
                    if (!converted.TryGetValue(src, out var dst))
                    {
                        var path = forceCopy ? $"{dir}/{stem}_{Sanitize(src.name)}_URP.mat" : $"{dir}/{Sanitize(src.name)}_URP.mat";
                        dst = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (dst == null)
                        {
                            dst = new Material(lit);
                            AssetDatabase.CreateAsset(dst, path);
                        }
                        if (alreadyUrp)
                        {
                            dst.CopyPropertiesFromMaterial(src);
                            dst.shader = lit;
                            EditorUtility.SetDirty(dst);
                            converted[src] = dst;
                            mats[i] = dst;
                            continue;
                        }
                        dst.shader = lit;
                        CopyTexture(src, "_MainTex", dst, "_BaseMap");
                        if (src.HasProperty("_Color")) dst.SetColor("_BaseColor", src.GetColor("_Color"));
                        if (CopyTexture(src, "_BumpMap", dst, "_BumpMap")) dst.EnableKeyword("_NORMALMAP");
                        CopyTexture(src, "_MetallicGlossMap", dst, "_MetallicGlossMap");
                        CopyTexture(src, "_OcclusionMap", dst, "_OcclusionMap");
                        if (CopyTexture(src, "_EmissionMap", dst, "_EmissionMap"))
                        {
                            dst.EnableKeyword("_EMISSION");
                            dst.SetColor("_EmissionColor", src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white);
                        }
                        dst.SetFloat("_Smoothness", src.HasProperty("_Glossiness") ? src.GetFloat("_Glossiness") : 0.4f);
                        dst.SetFloat("_Metallic", src.HasProperty("_Metallic") ? src.GetFloat("_Metallic") : 0f);
                        EditorUtility.SetDirty(dst);
                        converted[src] = dst;
                    }
                    mats[i] = dst;
                }
                r.sharedMaterials = mats;
            }
            Debug.Log($"[MimicMocap] Converted {converted.Count} material(s) to URP Lit for {Path.GetFileName(fbxPath)}");
        }

        /// <summary>
        /// MMD conventions that an FBX cannot carry: parts a material morph normally hides ship with alpha 0 (blush,
        /// tears, glasses) and must stay invisible; hair and lashes rely on texture alpha; skirts and sleeves are
        /// drawn two-sided. Applied to the external URP copies of the rig's materials.
        /// </summary>
        /// <summary>Material names (substrings) for parts MMD models show only through a morph: glasses, lenses, blush, tears.</summary>
        static readonly string[] k_MmdHiddenByDefault = { "メガネ", "眼鏡", "镜片", "glass", "照れ", "blush", "涙", "tear" };
        /// <summary>How much of an MMD material's base colour is emitted unlit, so the toon colours show under the dark stage lights.</summary>
        const float k_MmdSelfLight = 0.45f;

        static void TuneMmdMaterials(Renderer[] renderers, StringBuilder report)
        {
            var seen = new HashSet<Material>();
            int hidden = 0, clipped = 0;
            foreach (var r in renderers)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m) || !m.HasProperty("_BaseColor")) continue;
                    if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                    // MMD textures are flat toon colours meant for an unlit-looking render: kill the specular and
                    // reflections that made skin and hair look like wet plastic, and let the base map glow a little so the
                    // colours survive the dark stage lighting instead of reading as untextured grey.
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
                    if (m.HasProperty("_SpecularHighlights")) { m.SetFloat("_SpecularHighlights", 0f); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); }
                    if (m.HasProperty("_EnvironmentReflections")) { m.SetFloat("_EnvironmentReflections", 0f); m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF"); }
                    var color = m.GetColor("_BaseColor");
                    var hideByName = k_MmdHiddenByDefault.Any(k => m.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (color.a < 0.05f || hideByName)
                    {
                        color.a = 0f;
                        m.SetColor("_BaseColor", color);
                        // Hidden-by-default part: make it a transparent material with zero alpha so it draws nothing.
                        m.SetFloat("_Surface", 1f);
                        m.SetFloat("_Blend", 0f);
                        m.SetOverrideTag("RenderType", "Transparent");
                        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        m.SetFloat("_ZWrite", 0f);
                        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                        hidden++;
                    }
                    else
                    {
                        var tex = m.GetTexture("_BaseMap") as Texture2D;
                        if (tex != null && TextureHasTransparentPixels(tex))
                        {
                            m.SetFloat("_AlphaClip", 1f);
                            m.SetFloat("_Cutoff", 0.5f);
                            m.EnableKeyword("_ALPHATEST_ON");
                            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                            clipped++;
                        }
                        if (m.HasProperty("_EmissionColor"))
                        {
                            m.EnableKeyword("_EMISSION");
                            if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", tex);
                            m.SetColor("_EmissionColor", (tex != null ? Color.white : color) * k_MmdSelfLight);
                            // URP's material post-processor keeps _EMISSION only while one of the emissive GI flags is set.
                            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                        }
                    }
                    EditorUtility.SetDirty(m);
                }
            }
            report?.AppendLine($"MMD materials: {seen.Count} two-sided, self-lit, no specular; {hidden} hidden (alpha 0), {clipped} alpha-clipped");
            Debug.Log($"[MimicMocap] MMD materials: {seen.Count} two-sided, self-lit, no specular; {hidden} hidden (alpha 0), {clipped} alpha-clipped");
        }

        /// <summary>True when any pixel of the texture has alpha below 0.5 (sampled through a RenderTexture, so the asset stays non-readable).</summary>
        static bool TextureHasTransparentPixels(Texture2D tex)
        {
            var w = Mathf.Min(256, tex.width);
            var h = Mathf.Min(256, tex.height);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var pixels = copy.GetPixels32();
            UnityEngine.Object.DestroyImmediate(copy);
            var transparent = 0;
            foreach (var p in pixels) if (p.a < 128) transparent++;
            return transparent > pixels.Length / 200; // more than 0.5% of the texels
        }

        /// <summary>
        /// Packs downloaded from asset sites often ship the texture beside the FBX without the FBX referencing it.
        /// If a material has no base map and there is a BaseColor/Albedo/Diffuse texture in the character folder, use it.
        /// </summary>
        static void AssignLooseTextures(string fbxPath, Renderer[] renderers, Shader lit)
        {
            var folder = Path.GetDirectoryName(fbxPath).Replace('\\', '/');
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            var textures = guids.Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<Texture2D>).Where(t => t != null).ToArray();
            if (textures.Length == 0) return;
            Texture2D Pick(params string[] keys) => textures.FirstOrDefault(t => keys.Any(k => t.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
            var baseMap = Pick("BaseColor", "Base_Color", "Albedo", "Diffuse", "Color") ?? (textures.Length == 1 ? textures[0] : null);
            var normal = Pick("Normal", "_nrm", "_n");
            var assigned = 0;
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") == null && baseMap != null)
                    {
                        // Materials embedded in the FBX prefab are read-only; make a URP asset beside the FBX instead.
                        var path = $"{folder}/Materials/{Sanitize(m.name)}_URP.mat";
                        if (!AssetDatabase.IsValidFolder(folder + "/Materials")) AssetDatabase.CreateFolder(folder, "Materials");
                        var dst = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (dst == null) { dst = new Material(lit); AssetDatabase.CreateAsset(dst, path); }
                        dst.shader = lit;
                        dst.SetTexture("_BaseMap", baseMap);
                        dst.SetColor("_BaseColor", Color.white);
                        dst.SetFloat("_Smoothness", 0.35f);
                        if (normal != null) { dst.SetTexture("_BumpMap", normal); dst.EnableKeyword("_NORMALMAP"); }
                        EditorUtility.SetDirty(dst);
                        mats[i] = dst;
                        assigned++;
                    }
                }
                r.sharedMaterials = mats;
            }
            if (assigned > 0) Debug.Log($"[MimicMocap] Assigned loose texture '{baseMap.name}' to {assigned} material slot(s) for {Path.GetFileName(fbxPath)}");
        }

        static bool CopyTexture(Material src, string srcProp, Material dst, string dstProp)
        {
            if (!src.HasProperty(srcProp) || !dst.HasProperty(dstProp)) return false;
            var t = src.GetTexture(srcProp);
            if (t == null) return false;
            dst.SetTexture(dstProp, t);
            dst.SetTextureScale(dstProp, src.GetTextureScale(srcProp));
            dst.SetTextureOffset(dstProp, src.GetTextureOffset(srcProp));
            return true;
        }

        static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        /// <summary>
        /// 3ds Max Biped rigs name fingers Finger0..4 (thumb..little) with 1/2 suffixes for the joints. Unity's
        /// auto-mapper often mislabels or skips one hand of these, so write the finger mapping explicitly for any
        /// bone that exists. Returns true if the human description was changed.
        /// </summary>
        /// <summary>
        /// MMD (PMX) rigs converted through Blender keep their Japanese bone names, which Unity's auto-mapper does
        /// not know. Map the standard MMD skeleton explicitly. Deform "D" bones are preferred for the legs because
        /// the mesh is weighted to them (the plain bones only drive them through MMD constraints that do not survive
        /// the FBX). Fingers use MMD's full-width digits.
        /// </summary>
        static bool FixMmdMapping(ModelImporter importer, GameObject prefab, StringBuilder report)
        {
            var all = prefab.GetComponentsInChildren<Transform>(true).Select(t => t.name).ToHashSet();
            if (!all.Contains("頭") || !all.Contains("上半身")) return false;

            string Pick(params string[] candidates) => candidates.FirstOrDefault(all.Contains);
            var desired = new Dictionary<string, string>();
            void Map(string human, params string[] candidates)
            {
                var bone = Pick(candidates);
                if (bone != null) desired[human] = bone;
            }

            Map("Hips", "腰", "グルーブ", "センター");
            Map("Spine", "上半身");
            Map("Chest", "上半身2", "上半身２");
            Map("UpperChest", "上半身3", "上半身３");
            Map("Neck", "首");
            Map("Head", "頭");
            // Blender's MMD Tools turns MMD's 左/右 prefixes into Blender's .L/.R suffixes on export; raw PMX names
            // keep the prefix. Both spellings are tried.
            Map("LeftEye", "目.L", "左目");
            Map("RightEye", "目.R", "右目");
            foreach (var (side, jp, suffix) in new[] { ("Left", "左", ".L"), ("Right", "右", ".R") })
            {
                string[] Sided(params string[] names) => names.SelectMany(n => new[] { n + suffix, jp + n }).ToArray();
                Map(side + "Shoulder", Sided("肩"));
                Map(side + "UpperArm", Sided("腕"));
                Map(side + "LowerArm", Sided("ひじ"));
                Map(side + "Hand", Sided("手首"));
                Map(side + "UpperLeg", Sided("足D", "足"));
                Map(side + "LowerLeg", Sided("ひざD", "ひざ"));
                Map(side + "Foot", Sided("足首D", "足首"));
                Map(side + "Toes", Sided("足先EX", "つま先"));
                // Thumb: 親指０ is the metacarpal (Unity's proximal), then １ and ２. Other fingers: １ ２ ３.
                Map(side + " Thumb Proximal", Sided("親指０", "親指0"));
                Map(side + " Thumb Intermediate", Sided("親指１", "親指1"));
                Map(side + " Thumb Distal", Sided("親指２", "親指2"));
                foreach (var (finger, jpFinger) in new[] { ("Index", "人指"), ("Middle", "中指"), ("Ring", "薬指"), ("Little", "小指") })
                {
                    Map($"{side} {finger} Proximal", Sided(jpFinger + "１", jpFinger + "1"));
                    Map($"{side} {finger} Intermediate", Sided(jpFinger + "２", jpFinger + "2"));
                    Map($"{side} {finger} Distal", Sided(jpFinger + "３", jpFinger + "3"));
                }
            }

            var desc = importer.humanDescription;
            var current = desc.human.ToDictionary(h => h.humanName, h => h.boneName);
            if (current.Count == desired.Count && desired.All(kv => current.TryGetValue(kv.Key, out var b) && b == kv.Value)) return false;

            desc.human = desired.Select(kv =>
            {
                var hb = new HumanBone { humanName = kv.Key, boneName = kv.Value };
                hb.limit.useDefaultValues = true;
                return hb;
            }).ToArray();
            desc.skeleton = prefab.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
            }).ToArray();
            importer.humanDescription = desc;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            report.AppendLine($"{Path.GetFileName(importer.assetPath)}: MMD rig, mapped {desired.Count} bones explicitly (hips <- {desired["Hips"]}, legs <- {desired.GetValueOrDefault("LeftUpperLeg")})");
            return true;
        }

        static bool FixBipedFingerMapping(ModelImporter importer, GameObject prefab, StringBuilder report)
        {
            var all = prefab.GetComponentsInChildren<Transform>(true).Select(t => t.name).ToHashSet();
            var pattern = new System.Text.RegularExpressions.Regex(@"^(?<prefix>.+?)_(?<side>[LR])_Finger(?<finger>[0-4])(?<joint>[012])?$");
            var fingerBones = all.Where(n => pattern.IsMatch(n)).ToList();
            if (fingerBones.Count == 0) return false;

            var fingerNames = new[] { "Thumb", "Index", "Middle", "Ring", "Little" };
            var jointNames = new[] { "Proximal", "Intermediate", "Distal" };
            var desired = new Dictionary<string, string>(); // humanName -> boneName
            foreach (var bone in fingerBones)
            {
                var m = pattern.Match(bone);
                var side = m.Groups["side"].Value == "L" ? "Left" : "Right";
                var finger = int.Parse(m.Groups["finger"].Value);
                var joint = m.Groups["joint"].Success ? int.Parse(m.Groups["joint"].Value) : 0;
                if (joint > 2) continue;
                desired[$"{side} {fingerNames[finger]} {jointNames[joint]}"] = bone;
            }

            var desc = importer.humanDescription;
            var human = desc.human.ToList();
            var changed = false;
            // Drop any existing finger entries, then add the explicit ones.
            var fingerHumanNames = new HashSet<string>(HumanTrait.BoneName.Where(n => fingerNames.Any(f => n.Contains(" " + f + " "))));
            var before = human.Count;
            human.RemoveAll(h => fingerHumanNames.Contains(h.humanName));
            foreach (var kv in desired)
            {
                var hb = new HumanBone { humanName = kv.Key, boneName = kv.Value };
                hb.limit.useDefaultValues = true;
                human.Add(hb);
            }
            // Detect whether anything actually differs from the importer's current mapping.
            var current = desc.human.Where(h => fingerHumanNames.Contains(h.humanName)).ToDictionary(h => h.humanName, h => h.boneName);
            changed = current.Count != desired.Count || desired.Any(kv => !current.TryGetValue(kv.Key, out var b) || b != kv.Value);
            if (!changed) return false;

            // Keep the skeleton list in sync so the avatar builder accepts the description.
            if (desc.skeleton == null || desc.skeleton.Length == 0)
            {
                desc.skeleton = prefab.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
                }).ToArray();
            }
            desc.human = human.ToArray();
            importer.humanDescription = desc;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            report.AppendLine($"{Path.GetFileName(importer.assetPath)}: rewrote Biped finger mapping ({desired.Count} finger bones, was {before - human.Count + desired.Count} entries)");
            return true;
        }

        /// <summary>Menu order, label and head scale for the rigs we ship; anything else gets a prettified file name.</summary>
        static readonly (string file, string label, float headScale, bool neon)[] k_KnownCharacters =
        {
            ("Skeleton_Animated", "Skeleton", 1f, true),
            ("Skeletonzombie_T_Avelange", "Zombie", 1f, false),
            ("SourMiku_Black", "Miku", 1f, false),
            ("SourMiku_White", "Miku White", 1f, false),
            ("Skeleton_Minion", "Minion", 0.6f, false),
            ("Skeleton_Warrior", "Warrior", 0.6f, false),
            ("Skeleton_Mage", "Mage", 0.6f, false),
            ("Skeleton_Rogue", "Rogue", 0.6f, false),
        };

        /// <summary>
        /// Swap a rig's (already copied) URP materials for the neon x-ray shader the procedural skeleton uses, keeping
        /// the base texture as luminance detail so cracks and sutures still read.
        /// </summary>
        static void ApplyNeon(Renderer[] renderers, StringBuilder report)
        {
            var neon = Shader.Find("MimicMocap/Neon Bones");
            if (neon == null) { Debug.LogWarning("[MimicMocap] Neon Bones shader not found; rig keeps its lit materials"); return; }
            var seen = new HashSet<Material>();
            foreach (var r in renderers)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m) || AssetDatabase.IsSubAsset(m)) continue;
                    var baseMap = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                    m.shader = neon;
                    m.SetTexture("_MainTex", baseMap);
                    m.SetFloat("_Detail", baseMap != null ? 0.7f : 0f);
                    m.SetColor("_BaseColor", new Color(0.08f, 0.32f, 1.1f));
                    m.SetColor("_RimColor", new Color(0.55f, 0.9f, 2.6f));
                    m.SetFloat("_RimPower", 2.0f);
                    m.SetFloat("_Fill", 1.2f);
                    m.SetFloat("_Core", 0.08f);
                    m.SetColor("_EmissionColor", Color.white);
                    EditorUtility.SetDirty(m);
                }
            }
            report?.AppendLine($"neon: {seen.Count} material(s)");
            Debug.Log($"[MimicMocap] Neon x-ray shader on {seen.Count} material(s)");
        }

        /// <summary>
        /// Place every Humanoid FBX under CharacterRoot as a switchable <see cref="CharacterSlot"/>; only
        /// <paramref name="defaultFbx"/> starts active. The director and hand menu pick the slots up at runtime.
        /// </summary>
        static void PlaceAllInScene(string[] fbxPaths, string defaultFbx)
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            var root = GameObject.Find("CharacterRoot");
            if (root == null) throw new InvalidOperationException("CharacterRoot not found; run CreateScene first");
            for (var i = root.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

            int Rank(string p)
            {
                var idx = Array.FindIndex(k_KnownCharacters, k => p.IndexOf(k.file, StringComparison.OrdinalIgnoreCase) >= 0);
                return idx < 0 ? 100 : idx;
            }
            var ordered = fbxPaths.OrderBy(Rank).ThenBy(p => p).ToList();

            var placed = new List<GameObject>();
            GameObject active = null;
            foreach (var fbxPath in ordered)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (prefab == null) { Debug.LogWarning("[MimicMocap] Character FBX not found: " + fbxPath); continue; }
                if (!(AssetImporter.GetAtPath(fbxPath) is ModelImporter importer) || importer.animationType != ModelImporterAnimationType.Human)
                {
                    Debug.LogWarning($"[MimicMocap] {fbxPath} is not a Humanoid rig; skipped");
                    continue;
                }
                var instance = PlaceCharacter(root, fbxPath, prefab, scene);
                instance.SetActive(false);
                placed.Add(instance);
                if (string.Equals(Path.GetFullPath(fbxPath), Path.GetFullPath(defaultFbx), StringComparison.OrdinalIgnoreCase)) active = instance;
            }
            if (placed.Count == 0) throw new InvalidOperationException("No Humanoid characters could be placed under CharacterRoot");
            if (active == null) active = placed[0];
            active.SetActive(true);

            var director = UnityEngine.Object.FindFirstObjectByType<MocapDirector>();
            if (director != null)
            {
                director.characterRoot = root.transform;
                director.visual = MocapDirector.VisualMode.Skeleton;
                if (director.handMenu == null) director.handMenu = director.GetComponent<HandMenu>() ?? director.gameObject.AddComponent<HandMenu>();
                EditorUtility.SetDirty(director);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MimicMocap] Placed {placed.Count} characters under CharacterRoot ({string.Join(", ", placed.Select(p => p.GetComponent<CharacterSlot>().DisplayName))}); {active.name} starts active");
        }

        static string Prettify(string fileName) => System.Text.RegularExpressions.Regex.Replace(fileName.Replace('_', ' '), @"\s+", " ").Trim();

        static GameObject PlaceCharacter(GameObject root, string fbxPath, GameObject prefab, UnityEngine.SceneManagement.Scene scene)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = Path.GetFileNameWithoutExtension(fbxPath);
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity; // faces +Z, toward the camera

            // Scale so the character stands ~1.75 m tall, then drop it so its lowest point sits on the floor:
            // many downloaded rigs have their origin at the pelvis or mid-body rather than between the feet.
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                var s = 1.75f / Mathf.Max(0.01f, b.size.y);
                instance.transform.localScale = Vector3.one * s;
                var scaled = renderers[0].bounds;
                foreach (var r in renderers) scaled.Encapsulate(r.bounds);
                var lift = root.transform.position.y - scaled.min.y;
                instance.transform.localPosition += new Vector3(0f, lift, 0f);
                Debug.Log($"[MimicMocap] {instance.name}: height {b.size.y:0.00} -> scale {s:0.000}, floor offset {lift:0.000}");
            }

            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var isKayKit = fbxPath.IndexOf("KayKit", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isKayKit)
            {
                // KayKit ships one atlas: a single URP Lit with a little bone glow so bloom catches it.
                var matPath = k_MaterialDir + "/CharacterSkeleton.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(lit);
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(Path.GetDirectoryName(fbxPath).Replace('\\', '/') + "/skeleton_texture.png");
                mat.shader = lit;
                if (tex != null) mat.SetTexture("_BaseMap", tex);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Smoothness", 0.3f);
                mat.EnableKeyword("_EMISSION");
                if (tex != null) mat.SetTexture("_EmissionMap", tex);
                mat.SetColor("_EmissionColor", new Color(0.35f, 0.4f, 0.5f));
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                EditorUtility.SetDirty(mat);
                foreach (var r in renderers)
                {
                    var mats = r.sharedMaterials;
                    for (var i = 0; i < mats.Length; i++) mats[i] = mat;
                    r.sharedMaterials = mats;
                }
            }
            else
            {
                // Keep the rig's own textures, but the importer makes Standard-shader materials which render magenta
                // in URP: convert each to URP Lit, copying the common texture slots, and save them beside the FBX.
                var isMmd = instance.GetComponentsInChildren<Transform>(true).Any(t => t.name == "頭");
                ConvertMaterialsToUrp(fbxPath, renderers, lit, forceCopy: isMmd);
                AssignLooseTextures(fbxPath, renderers, lit);
                if (isMmd)
                {
                    TuneMmdMaterials(renderers, null);
                    // No self-shadowing: alpha-clipped hair strands threw hard, flickering shadows across the face and
                    // body as the model moved. The rig still casts onto the floor; toon models are meant to be shadeless.
                    foreach (var r in renderers) r.receiveShadows = false;
                }
            }

            // KayKit characters are chibi with huge skulls that hide raised arms; shrink the head so gestures read.
            var known = Array.Find(k_KnownCharacters, k => fbxPath.IndexOf(k.file, StringComparison.OrdinalIgnoreCase) >= 0);
            if (known.neon) ApplyNeon(renderers, null);
            var slot = instance.AddComponent<CharacterSlot>();
            slot.displayName = known.file != null ? known.label : Prettify(instance.name);
            slot.headScale = known.file != null ? known.headScale : (isKayKit ? 0.6f : 1f);

            Debug.Log($"[MimicMocap] Placed {instance.name} as '{slot.DisplayName}' under CharacterRoot (scale {instance.transform.localScale.x:0.000})");
            return instance;
        }
    }
}
