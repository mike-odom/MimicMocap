using System.IO;
using MimicMocap.BlazePose;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>Shared helpers for the PlayMode smoke tests.</summary>
    public static class TestSupport
    {
        public static string OutputDir
        {
            get
            {
                var dir = Path.Combine(Application.dataPath, "..", "Logs", "PoseCheck");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// A test image from Tests/Resources/TestImages, or Assert.Ignore when this checkout does not have it
        /// (the frames are not redistributable; see docs/TEST-MEDIA.md).
        /// </summary>
        public static Texture2D LoadTestImage(string name)
        {
            var tex = Resources.Load<Texture2D>("TestImages/" + name);
            if (tex == null) Assert.Ignore($"test image '{name}' is not in this checkout; see docs/TEST-MEDIA.md");
            return tex;
        }

        /// <summary>Folder with the test mp4s: TestVideos next to the project or one level up.</summary>
        public static string FindVideoFolder()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            foreach (var c in new[] { Path.Combine(root, "TestVideos"), Path.Combine(Directory.GetParent(root).FullName, "TestVideos") })
                if (Directory.Exists(c)) return c;
            return null;
        }

        public static BlazePoseSource CreateBlaze(BlazePoseSource.Variant variant)
        {
#if UNITY_EDITOR
            var det = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/MimicMocap/Models/BlazePose/pose_detection.onnx");
            var name = variant == BlazePoseSource.Variant.Lite ? "lite" : variant == BlazePoseSource.Variant.Heavy ? "heavy" : "full";
            var lm = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>($"Assets/MimicMocap/Models/BlazePose/pose_landmarks_detector_{name}.onnx");
            var anchors = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/MimicMocap/Data/blazepose_anchors.csv");
            Assert.NotNull(det, "detector model");
            Assert.NotNull(lm, "landmarker model");
            Assert.NotNull(anchors, "anchors");
            var s = new BlazePoseSource(det, lm, anchors, variant);
            s.Initialize();
            Assert.IsTrue(s.IsAvailable, s.Status);
            return s;
#else
            Assert.Ignore("Editor only");
            return null;
#endif
        }

        /// <summary>Readable RGBA copy of any texture (works for non-readable assets, RenderTextures, WebCamTextures).</summary>
        public static Texture2D ReadableCopy(Texture src, int width = 0, int height = 0)
        {
            var w = width > 0 ? width : src.width;
            var h = height > 0 ? height : src.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return copy;
        }

        public static Texture2D RenderCamera(Camera cam, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        /// <summary>Draw normalised (y-up) points and lines onto a readable texture.</summary>
        public static void Annotate(Texture2D t, Vector2[] points, float[] conf, (int a, int b)[] lines, Color lineColor, Color dotColor, int dotRadius = 5)
        {
            if (lines != null)
                foreach (var (a, b) in lines)
                    if (a < points.Length && b < points.Length && (conf == null || (conf[a] > 0.2f && conf[b] > 0.2f)))
                        DrawLine(t, points[a], points[b], lineColor);
            for (var i = 0; i < points.Length; i++)
            {
                if (conf != null && conf[i] < 0.2f) continue;
                DrawDot(t, points[i], dotColor, dotRadius);
            }
        }

        public static void DrawDot(Texture2D t, Vector2 n, Color c, int r)
        {
            var cx = Mathf.RoundToInt(n.x * t.width);
            var cy = Mathf.RoundToInt(n.y * t.height);
            for (var y = -r; y <= r; y++)
                for (var x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    var px = cx + x; var py = cy + y;
                    if (px < 0 || py < 0 || px >= t.width || py >= t.height) continue;
                    t.SetPixel(px, py, c);
                }
        }

        public static void DrawLine(Texture2D t, Vector2 a, Vector2 b, Color c)
        {
            var pa = new Vector2(a.x * t.width, a.y * t.height);
            var pb = new Vector2(b.x * t.width, b.y * t.height);
            var steps = Mathf.CeilToInt(Vector2.Distance(pa, pb));
            for (var i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(pa, pb, steps == 0 ? 0 : i / (float)steps);
                var px = Mathf.RoundToInt(p.x); var py = Mathf.RoundToInt(p.y);
                if (px < 0 || py < 0 || px >= t.width || py >= t.height) continue;
                t.SetPixel(px, py, c);
                if (px + 1 < t.width) t.SetPixel(px + 1, py, c);
            }
        }

        /// <summary>Tile textures into a grid and save as PNG. All tiles are scaled to tileW x tileH.</summary>
        public static void SaveContactSheet(Texture2D[] tiles, int columns, int tileW, int tileH, string path)
        {
            var rows = Mathf.CeilToInt(tiles.Length / (float)columns);
            var sheet = new Texture2D(columns * tileW, rows * tileH, TextureFormat.RGBA32, false);
            var black = new Color32[sheet.width * sheet.height];
            for (var i = 0; i < black.Length; i++) black[i] = new Color32(0, 0, 0, 255);
            sheet.SetPixels32(black);
            for (var i = 0; i < tiles.Length; i++)
            {
                if (tiles[i] == null) continue;
                var scaled = ReadableCopy(tiles[i], tileW, tileH);
                var col = i % columns;
                var row = rows - 1 - i / columns; // first tile top-left
                sheet.SetPixels32(col * tileW, row * tileH, tileW, tileH, scaled.GetPixels32());
                Object.Destroy(scaled);
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.Destroy(sheet);
        }

        public static readonly (int a, int b)[] CanonicalLimbs =
        {
            ((int)HumanJoint.LeftShoulder, (int)HumanJoint.LeftElbow), ((int)HumanJoint.LeftElbow, (int)HumanJoint.LeftWrist),
            ((int)HumanJoint.RightShoulder, (int)HumanJoint.RightElbow), ((int)HumanJoint.RightElbow, (int)HumanJoint.RightWrist),
            ((int)HumanJoint.LeftHip, (int)HumanJoint.LeftKnee), ((int)HumanJoint.LeftKnee, (int)HumanJoint.LeftAnkle),
            ((int)HumanJoint.RightHip, (int)HumanJoint.RightKnee), ((int)HumanJoint.RightKnee, (int)HumanJoint.RightAnkle),
            ((int)HumanJoint.Pelvis, (int)HumanJoint.Neck), ((int)HumanJoint.LeftShoulder, (int)HumanJoint.RightShoulder),
        };

        public static string Fmt(Vector3 v) => $"({v.x,6:0.00},{v.y,6:0.00},{v.z,6:0.00})";
    }
}
