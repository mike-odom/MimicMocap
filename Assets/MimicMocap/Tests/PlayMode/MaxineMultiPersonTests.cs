using System.Collections;
using System.IO;
using System.Text;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Can the installed Maxine SDK track two people in one frame? Builds a two-person composite from the
    /// single-person test frames, asks the source for multi-person mode and reports what came back.
    /// Inconclusive (not failed) when the SDK refuses the mode, so the answer is in the log either way.
    /// </summary>
    public class MaxineMultiPersonTests
    {
        /// <summary>Kung Fu Girl on the left, the Trainer on the right, both full height on a 1920x1080 canvas.</summary>
        public static Texture2D BuildTwoPersonFrame()
        {
            var left = TestSupport.LoadTestImage("Kung_Fu_Girl_1");
            var right = TestSupport.LoadTestImage("bye_bye_bye_-_Trainer_1");

            const int w = 1920, h = 1080;
            var canvas = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var fill = new Color32[w * h];
            for (var i = 0; i < fill.Length; i++) fill[i] = new Color32(24, 20, 28, 255);
            canvas.SetPixels32(fill);

            // Portrait clip scaled to full height; square clip scaled to full height then centre-cropped.
            var leftW = Mathf.RoundToInt(h * left.width / (float)left.height);
            var a = TestSupport.ReadableCopy(left, leftW, h);
            canvas.SetPixels(140, 0, leftW, h, a.GetPixels());
            var b = TestSupport.ReadableCopy(right, h, h);
            const int cropW = 760;
            canvas.SetPixels(w - 160 - cropW, 0, cropW, h, b.GetPixels((h - cropW) / 2, 0, cropW, h));
            canvas.Apply();
            Object.Destroy(a);
            Object.Destroy(b);
            return canvas;
        }

        [UnityTest]
        public IEnumerator Maxine_TracksTwoPeopleInOneFrame()
        {
            var mx = new MaxinePoseSource { MaxPeople = 8 };
            mx.Initialize();
            if (!mx.IsAvailable)
            {
                Assert.Ignore("Maxine SDK unavailable: " + mx.Status);
                yield break;
            }

            var log = new StringBuilder();
            log.AppendLine($"init: {mx.Status}  multiPersonActive={mx.MultiPersonActive} slots={mx.SlotCount}");

            var frame = BuildTwoPersonFrame();
            File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "two_people.png"), frame.EncodeToPNG());

            var slots = new PoseFrame[mx.SlotCount];
            for (var i = 0; i < slots.Length; i++) slots[i] = new PoseFrame();
            var runs = 0;
            for (var i = 0; i < 16; i++)
            {
                mx.Submit(frame, Time.timeAsDouble);
                var waited = 0;
                var got = false;
                while (waited < 120 && !got)
                {
                    // Draining slot 0 polls the SDK; the other slots are then read without another run.
                    got = mx.TryGetPerson(0, slots[0]);
                    if (!got && !mx.IsBusy) break;
                    if (!got) { waited++; yield return null; }
                }
                if (got)
                {
                    runs++;
                    for (var s = 1; s < slots.Length; s++) mx.TryGetPerson(s, slots[s]);
                    var ids = "";
                    for (var s = 0; s < slots.Length; s++) if (mx.SlotId(s) >= 0) ids += $" s{s}=id{mx.SlotId(s)}{(slots[s].Tracked ? "" : "(lost)")}";
                    log.AppendLine($"run {runs}: people={mx.PeopleCount} status='{mx.Status}' {mx.LastRunMs:0.0}ms slots:{ids}");
                }
                yield return null;
            }

            var tracked = 0;
            for (var s = 0; s < slots.Length; s++)
            {
                var f = slots[s];
                if (!f.Tracked) continue;
                tracked++;
                log.AppendLine($"  slot {s} id={mx.SlotId(s)} score={f.Score:0.00} box=({f.ImageBox.x:0.00},{f.ImageBox.y:0.00},{f.ImageBox.width:0.00},{f.ImageBox.height:0.00}) rootOffset={TestSupport.Fmt(f.RootOffset)} leg={PoseTransforms.LegLength(f):0.00}m torso={PoseTransforms.TorsoLength(f):0.00}m nose.y={f[HumanJoint.Nose].y:0.00}");
            }
            log.AppendLine($"tracked people: {tracked}");

            // Picture: both skeletons over the composite.
            var copy = TestSupport.ReadableCopy(frame);
            var colors = new[] { new Color(0.2f, 0.9f, 1f), new Color(1f, 0.6f, 0.1f), Color.green, Color.magenta, Color.yellow, Color.cyan, Color.red, Color.white };
            var lines = new (int a, int b)[JointTopology.Bones.Length];
            for (var i = 0; i < lines.Length; i++) lines[i] = ((int)JointTopology.Bones[i].a, (int)JointTopology.Bones[i].b);
            for (var s = 0; s < slots.Length; s++)
            {
                if (!slots[s].Tracked || !slots[s].HasImageJoints) continue;
                TestSupport.Annotate(copy, slots[s].ImageJoints, slots[s].Confidence, lines, colors[s % colors.Length], Color.white, 6);
            }
            copy.Apply();
            File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, "two_people_maxine.png"), copy.EncodeToPNG());
            Object.Destroy(copy);
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "maxine_multi_report.txt"), log.ToString());
            Debug.Log("[MimicMocap] Maxine multi-person report:\n" + log);
            var multi = mx.MultiPersonActive;
            mx.Dispose();
            Object.Destroy(frame);

            Assert.Greater(runs, 0, "Maxine never returned a frame");
            if (!multi) Assert.Inconclusive("The installed SDK refused multi-person mode; see log. Single-person tracking still works.");
            Assert.GreaterOrEqual(tracked, 2, "multi-person mode is on but fewer than two people were tracked in the composite");
            // The two people stand on opposite sides of the frame.
            var xs = new System.Collections.Generic.List<float>();
            for (var s = 0; s < slots.Length; s++) if (slots[s].Tracked) xs.Add(slots[s].ImageBox.center.x);
            xs.Sort();
            Assert.Less(xs[0], 0.5f, "one person should be in the left half");
            Assert.Greater(xs[xs.Count - 1], 0.5f, "one person should be in the right half");
        }

        [UnityTest]
        public IEnumerator Maxine_SinglePersonDefaultIsUnchanged()
        {
            var mx = new MaxinePoseSource();
            mx.Initialize();
            if (!mx.IsAvailable)
            {
                Assert.Ignore("Maxine SDK unavailable: " + mx.Status);
                yield break;
            }
            Assert.IsFalse(mx.MultiPersonActive);
            Assert.AreEqual(1, mx.SlotCount);
            var tex = TestSupport.LoadTestImage("Kung_Fu_Girl_1");
            var pose = new PoseFrame();
            var got = false;
            for (var i = 0; i < 4 && !got; i++)
            {
                mx.Submit(tex, Time.timeAsDouble);
                var waited = 0;
                while (waited < 120 && !mx.TryGetLatest(pose) && mx.IsBusy) { waited++; yield return null; }
                got = pose.Tracked;
                yield return null;
            }
            mx.Dispose();
            Assert.IsTrue(got, "single-person Maxine lost the Kung Fu frame");
            Assert.Greater(PoseTransforms.LegLength(pose), 0.5f);
        }
    }
}
