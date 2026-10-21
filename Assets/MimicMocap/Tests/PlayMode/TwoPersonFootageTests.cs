using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// A two-person take from the install camera (TestVideos/two people leave.mp4, 90 s: both gesturing, each leaving
    /// and returning, one walking up to the lens, then one dancing alone) through the real scene with two performers, set
    /// up as in the live test (mirror on, hands and the hand fallback on). Logs both performers every frame (tracked, held,
    /// tracker slot ids, travel, lift, where each avatar is drawn), flags frames where an avatar jumps or leaves the stage,
    /// and saves matching contact sheets of the camera (performer 1 orange, performer 2 cyan) and the stage.
    /// </summary>
    public class TwoPersonFootageTests
    {
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_Clip = "two people leave.mp4";

        [UnityTest]
        public IEnumerator TwoPeople_AvatarsStayWithTheirPeople()
        {
#if UNITY_EDITOR
            var folder = TestSupport.FindVideoFolder();
            var clipName = System.Environment.GetEnvironmentVariable("MIMIC_TWO_CLIP");
            if (string.IsNullOrEmpty(clipName)) clipName = k_Clip;
            var clip = folder != null ? Path.Combine(folder, clipName) : null;
            if (clip == null || !File.Exists(clip)) { Assert.Ignore(clipName + " not in TestVideos"); yield break; }
            var probe = new MaxinePoseSource();
            probe.Initialize();
            var available = probe.IsAvailable;
            probe.Dispose();
            if (!available) { Assert.Ignore("Maxine SDK unavailable"); yield break; }

            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode("Assets/MimicMocap/Scenes/MimicMocap.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            LogAssert.ignoreFailingMessages = true;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            director.input = MocapDirector.InputKind.VideoFile;
            director.videoPath = clip;
            director.source = MocapDirector.SourceKind.Maxine;
            director.performers = 2;
            director.trackHands = true;
            director.mirror = true;
            director.showHud = false;
            director.idleAttract = false;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();
            yield return null;
            var vid = director.ActiveInput as VideoFileFrameSource;
            Assert.NotNull(vid);
            vid.Loop = false;
            Assert.IsTrue(director.Performer2Active, "two performers need Maxine's multi-person mode: " + director.ActiveSource?.Status);
            var mx = (MaxinePoseSource)director.ActiveSource;
            var cam = Camera.main;

            var csv = new StringBuilder("frame,people,slotId0,slotId1,p1raw,p1shown,p1held,p1score,p1imgX,p1travelX,p1travelY,p1travelZ,p1lift,p1leg,p1drawX,p1drawY,p1drawZ,p2raw,p2shown,p2held,p2score,p2imgX,p2travelX,p2travelY,p2travelZ,p2lift,p2leg,p2drawX,p2drawY,p2drawZ\n");
            var events = new List<string>();
            var bigEvents = 0;   // what was seen live: a jump of half a metre or more, or an avatar off the stage
            var camTiles = new List<Texture2D>();
            var stageTiles = new List<Texture2D>();
            long lastFrame = -1, nextTile = 0;
            Vector3? prev1 = null, prev2 = null;
            var frames = 0; var p1Frames = 0; var p2Frames = 0; var bothFrames = 0; var shownNonFinite = 0;
            var start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 130f)
            {
                yield return null;
                var f = vid.Frame;
                if (f < 0 || f == lastFrame) continue;
                if (lastFrame >= 0 && f < lastFrame) break;
                lastFrame = f;
                frames++;
                var r1 = director.RawPose; var s1 = director.SmoothedPose;
                var r2 = director.RawPose2; var s2 = director.SmoothedPose2;
                if (s1.Tracked) p1Frames++;
                if (s2.Tracked) p2Frames++;
                if (s1.Tracked && s2.Tracked) bothFrames++;
                foreach (var s in new[] { s1, s2 })
                    if (s.Tracked && (!float.IsFinite(s[HumanJoint.Pelvis].x) || !float.IsFinite(s.RootOffset.x) || !float.IsFinite(s.Lift))) shownNonFinite++;
                var d1 = director.skeletonView.GetJointWorldPosition(HumanJoint.Pelvis);
                var d2 = P2Pelvis(director);
                string P(PoseFrame raw, PoseFrame shown, Vector3 drawn) =>
                    $"{(raw.Tracked ? 1 : 0)},{(shown.Tracked ? 1 : 0)},{(shown.Held ? 1 : 0)},{shown.Score:0.00},{raw.ImageJoints[(int)HumanJoint.Pelvis].x:0.000},{shown.RootOffset.x:0.000},{shown.RootOffset.y:0.000},{shown.RootOffset.z:0.000},{shown.Lift:0.000},{PoseTransforms.LegLength(shown):0.000},{drawn.x:0.000},{drawn.y:0.000},{drawn.z:0.000}";
                csv.Append($"{f},{mx.PeopleCount},{mx.SlotId(0)},{mx.SlotId(1)},{P(r1, s1, d1)},{P(r2, s2, d2)}\n");

                // Jumps: a drawn pelvis that moves more than 25 cm between frames, or sits outside the stage area.
                void Check(string who, PoseFrame shown, Vector3 drawn, ref Vector3? prev)
                {
                    if (!shown.Tracked) { prev = null; return; }
                    if (prev.HasValue && Vector3.Distance(drawn, prev.Value) > 0.5f) bigEvents++;
                    if (prev.HasValue && Vector3.Distance(drawn, prev.Value) > 0.25f)
                        events.Add($"frame {f} ({f / 30f:0.0} s): {who} jumped {Vector3.Distance(drawn, prev.Value):0.00} m to ({drawn.x:0.00},{drawn.y:0.00},{drawn.z:0.00}); travel ({shown.RootOffset.x:0.00},{shown.RootOffset.y:0.00},{shown.RootOffset.z:0.00}) lift {shown.Lift:0.00} leg {PoseTransforms.LegLength(shown):0.00} people {mx.PeopleCount} slots {mx.SlotId(0)}/{mx.SlotId(1)}");
                    else if (Mathf.Abs(drawn.x) > 2.2f || drawn.y > 1.6f || Mathf.Abs(drawn.z) > 2.2f)
                    {
                        bigEvents++;
                        if (events.Count == 0 || !events[events.Count - 1].Contains(who + " off stage") || f % 30 == 0)
                            events.Add($"frame {f} ({f / 30f:0.0} s): {who} off stage at ({drawn.x:0.00},{drawn.y:0.00},{drawn.z:0.00}); travel ({shown.RootOffset.x:0.00},{shown.RootOffset.y:0.00},{shown.RootOffset.z:0.00}) lift {shown.Lift:0.00} leg {PoseTransforms.LegLength(shown):0.00} people {mx.PeopleCount} slots {mx.SlotId(0)}/{mx.SlotId(1)}");
                    }
                    prev = drawn;
                }
                Check("performer 1", s1, d1, ref prev1);
                Check("performer 2", s2, d2, ref prev2);

                if (f >= nextTile && director.ActiveInput.Current != null)
                {
                    nextTile = f + 45;
                    var tile = TestSupport.ReadableCopy(director.ActiveInput.Current, 270, 480);
                    // Image joints are mirrored with the pose; flip them back onto the unmirrored camera picture.
                    void Dots(PoseFrame raw, Color c)
                    {
                        if (!raw.Tracked) return;
                        for (var j = 0; j < JointTopology.Count; j++)
                        {
                            if (raw.Confidence[j] < 0.4f) continue;
                            var p = raw.ImageJoints[j];
                            TestSupport.DrawDot(tile, new Vector2(director.mirror ? 1f - p.x : p.x, p.y), c, 3);
                        }
                    }
                    Dots(r1, new Color(1f, 0.6f, 0.1f));
                    Dots(r2, new Color(0.2f, 1f, 0.9f));
                    tile.Apply();
                    camTiles.Add(tile);
                    if (cam != null) stageTiles.Add(TestSupport.RenderCamera(cam, 270, 480));
                }
            }

            var tag = Path.GetFileNameWithoutExtension(clipName).Replace(' ', '_').ToLowerInvariant();
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, tag + "_frames.csv"), csv.ToString());
            if (camTiles.Count > 0) TestSupport.SaveContactSheet(camTiles.ToArray(), 10, 270, 480, Path.Combine(TestSupport.OutputDir, tag + "_camera.png"));
            if (stageTiles.Count > 0) TestSupport.SaveContactSheet(stageTiles.ToArray(), 10, 270, 480, Path.Combine(TestSupport.OutputDir, tag + "_stage.png"));
            foreach (var t in camTiles) Object.Destroy(t);
            foreach (var t in stageTiles) Object.Destroy(t);
            var report = new StringBuilder();
            report.AppendLine($"{clipName}: {frames} frames, performer 1 shown {100f * p1Frames / Mathf.Max(1, frames):0}%, performer 2 shown {100f * p2Frames / Mathf.Max(1, frames):0}%, both {100f * bothFrames / Mathf.Max(1, frames):0}%, body poses {director.InferenceFps:0.0} per second");
            report.AppendLine($"non-finite values: the SDK dropped {mx.NonFiniteFrames} people and {mx.NonFiniteKeypoints} keypoints; shown poses with a non-finite pelvis, travel or lift: {shownNonFinite}");
            report.AppendLine($"steps over 25 cm or off stage: {events.Count}; over 50 cm or off stage (a bounce): {bigEvents}");
            foreach (var e in events) report.AppendLine("  " + e);
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, tag + "_report.txt"), report.ToString());
            Debug.Log("[MimicMocap] Two person footage report:\n" + report);
            Assert.Greater(frames, 1000, "the clip did not play through");
            Assert.AreEqual(0, shownNonFinite, "a non-finite value reached a shown pose:\n" + report);
            Assert.AreEqual(0, bigEvents, "avatars bounced or left the stage:\n" + report);
#else
            yield break;
#endif
        }

        static Vector3 P2Pelvis(MocapDirector director)
        {
            if (director.Performer2ShowsCharacter && director.Performer2Animator != null)
            {
                var hips = director.Performer2Animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) return hips.position;
            }
            return director.SkeletonView2 != null ? director.SkeletonView2.GetJointWorldPosition(HumanJoint.Pelvis) : Vector3.zero;
        }
    }
}
