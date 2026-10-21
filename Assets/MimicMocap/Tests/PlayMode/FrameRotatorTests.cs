using NUnit.Framework;
using UnityEngine;

namespace MimicMocap.Tests
{
    /// <summary>The GPU frame rotator: sizes swap on quarter turns and corners land where a rotated picture puts them.</summary>
    public class FrameRotatorTests
    {
        static readonly Color32 TL = new Color32(255, 0, 0, 255);   // top-left of the source
        static readonly Color32 TR = new Color32(0, 255, 0, 255);
        static readonly Color32 BL = new Color32(0, 0, 255, 255);
        static readonly Color32 BR = new Color32(255, 255, 0, 255);

        static Texture2D Source(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[w * h];
            for (var i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 255);
            px[(h - 1) * w + 0] = TL;        // Unity rows are bottom-up: top-left is the last row's first pixel
            px[(h - 1) * w + (w - 1)] = TR;
            px[0] = BL;
            px[w - 1] = BR;
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Color32 Pixel(Texture2D t, int x, int y) => t.GetPixels32()[y * t.width + x];

        static void AssertColor(Color32 expected, Color32 actual, string where)
        {
            Assert.AreEqual(expected.r, actual.r, 2, where + " r");
            Assert.AreEqual(expected.g, actual.g, 2, where + " g");
            Assert.AreEqual(expected.b, actual.b, 2, where + " b");
        }

        [Test]
        public void QuarterTurnsSwapSizeAndMoveCorners()
        {
            var src = Source(6, 4);
            var rot = new FrameRotator();

            // 90 CCW: the source's top-left corner becomes the bottom-left, top-right becomes top-left.
            rot.Rotation = 90;
            var r90 = TestSupport.ReadableCopy(rot.Apply(src));
            Assert.AreEqual(4, r90.width); Assert.AreEqual(6, r90.height);
            AssertColor(TL, Pixel(r90, 0, 0), "90: TL -> bottom-left");
            AssertColor(TR, Pixel(r90, 0, 5), "90: TR -> top-left");
            AssertColor(BR, Pixel(r90, 3, 5), "90: BR -> top-right");
            AssertColor(BL, Pixel(r90, 3, 0), "90: BL -> bottom-right");

            // 270 CCW (90 CW): top-left becomes top-right.
            rot.Rotation = 270;
            var r270 = TestSupport.ReadableCopy(rot.Apply(src));
            Assert.AreEqual(4, r270.width); Assert.AreEqual(6, r270.height);
            AssertColor(TL, Pixel(r270, 3, 5), "270: TL -> top-right");
            AssertColor(BL, Pixel(r270, 0, 5), "270: BL -> top-left");
            AssertColor(TR, Pixel(r270, 3, 0), "270: TR -> bottom-right");

            // 180: corners swap diagonally, size unchanged.
            rot.Rotation = 180;
            var r180 = TestSupport.ReadableCopy(rot.Apply(src));
            Assert.AreEqual(6, r180.width); Assert.AreEqual(4, r180.height);
            AssertColor(TL, Pixel(r180, 5, 0), "180: TL -> bottom-right");
            AssertColor(BR, Pixel(r180, 0, 3), "180: BR -> top-left");

            // Vertical flip alone: top-left becomes bottom-left; and 0 degrees without a flip is a pass-through.
            rot.Rotation = 0;
            rot.FlipVertical = true;
            var flipped = TestSupport.ReadableCopy(rot.Apply(src));
            AssertColor(TL, Pixel(flipped, 0, 0), "flip: TL -> bottom-left");
            AssertColor(BL, Pixel(flipped, 0, 3), "flip: BL -> top-left");
            rot.FlipVertical = false;
            Assert.AreSame(src, rot.Apply(src), "no rotation and no flip returns the source itself");

            Assert.AreEqual(90, FrameRotator.Normalise(450));
            Assert.AreEqual(270, FrameRotator.Normalise(-90));
            Assert.AreEqual(0, FrameRotator.Normalise(45));

            rot.Dispose();
            Object.Destroy(src);
        }

        [Test]
        public void ChoosesTheExternalCameraByDefault()
        {
            var names = new[] { "Integrated Webcam", "Elgato Facecam", "OBS Virtual Camera" };
            Assert.AreEqual(1, MocapDirector.ChooseWebcam(names, 0, "", true));
            Assert.AreEqual(0, MocapDirector.ChooseWebcam(names, 0, "", false));
            Assert.AreEqual(2, MocapDirector.ChooseWebcam(names, 0, "obs", true));
            Assert.AreEqual(1, MocapDirector.ChooseWebcam(names, 0, "nothing-like-this", true), "unknown name falls back to the external preference");
            Assert.AreEqual(0, MocapDirector.ChooseWebcam(new[] { "Integrated Camera" }, 5, "", true), "only an integrated camera: use it");
        }

        [Test]
        public void ChoosesThePreferredCameraFirst()
        {
            // The install laptop's device list as Unity reports it (the Elgato name really contains U+0196, not an L).
            var names = new[] { "EƖgato Virtual Camera", "Integrated Webcam", "Arducam B0590 1080P Day/Night" };
            Assert.AreEqual(2, MocapDirector.ChooseWebcam(names, 0, "", true, "arducam"), "the preferred camera beats the index and the external heuristic");
            Assert.AreEqual(2, MocapDirector.ChooseWebcam(names, 0, "", false, "arducam"), "preferred wins without the external preference too");
            Assert.AreEqual(0, MocapDirector.ChooseWebcam(names, 0, "virtual", true, "arducam"), "-camera still overrides the preference");
            Assert.AreEqual(2, MocapDirector.ChooseWebcam(names, 0, "", true, "logitech, arducam"), "the list is tried in order");
            Assert.AreEqual(1, MocapDirector.ChooseWebcam(new[] { "EƖgato Virtual Camera", "Integrated Webcam" }, 0, "", true, "arducam"), "no preferred camera: the integrated one beats a virtual camera");
            Assert.AreEqual(0, MocapDirector.ChooseWebcam(new[] { "OBS Virtual Camera" }, 3, "", true, "arducam"), "only a virtual camera: use it");
        }

        [Test]
        public void VirtualCamerasAreNotExternalCameras()
        {
            Assert.IsTrue(MocapDirector.IsVirtualCamera("EƖgato Virtual Camera"));
            Assert.IsTrue(MocapDirector.IsVirtualCamera("OBS Virtual Camera"));
            Assert.IsTrue(MocapDirector.IsVirtualCamera("NVIDIA Broadcast"));
            Assert.IsFalse(MocapDirector.IsVirtualCamera("Arducam B0590 1080P Day/Night"));
            Assert.IsFalse(MocapDirector.IsVirtualCamera("Elgato Facecam"));
            var names = new[] { "OBS Virtual Camera", "Integrated Webcam", "Elgato Facecam" };
            Assert.AreEqual(2, MocapDirector.ChooseWebcam(names, 0, "", true), "a physical external camera beats both the virtual and the integrated one");
        }
    }
}
