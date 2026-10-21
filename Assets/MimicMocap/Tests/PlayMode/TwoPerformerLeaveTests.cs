using System.Collections;
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
    /// Two performers, then one person leaves: the remaining person's avatar must stay where they stand. Reported from a
    /// live test: shortly after the other person left, the remaining avatar flew off screen and only came back after its
    /// person left and re-entered. Played out on the two-person composite by blanking one side; the timeline of both
    /// performers (tracked, held, slot ids, travel, drawn pelvis) is written to Logs/PoseCheck.
    /// </summary>
    public class TwoPerformerLeaveTests
    {
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        [UnityTest] public IEnumerator LeftPersonLeaves_RightAvatarStaysPut() => Run(leftLeaves: true);
        [UnityTest] public IEnumerator RightPersonLeaves_LeftAvatarStaysPut() => Run(leftLeaves: false);

        static IEnumerator Run(bool leftLeaves)
        {
#if UNITY_EDITOR
            var probe = new MaxinePoseSource();
            probe.Initialize();
            var available = probe.IsAvailable;
            probe.Dispose();
            if (!available) { Assert.Ignore("Maxine SDK unavailable"); yield break; }

            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode("Assets/MimicMocap/Scenes/MimicMocap.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            var frame = MaxineMultiPersonTests.BuildTwoPersonFrame();
            var side = leftLeaves ? 0 : 960;
            var keep = frame.GetPixels(side, 0, 960, frame.height);
            var keepTex = new Texture2D(960, frame.height, TextureFormat.RGBA32, false);
            keepTex.SetPixels(keep);
            keepTex.Apply();
            var blank = new Color[960 * frame.height];
            for (var i = 0; i < blank.Length; i++) blank[i] = new Color32(24, 20, 28, 255);

            director.input = MocapDirector.InputKind.StillImage;
            director.stillImage = frame;
            director.source = MocapDirector.SourceKind.Maxine;
            director.performers = 2;
            director.trackHands = true;
            director.mirror = false;   // image left is performer 1's side; mirroring does not touch the slot logic
            director.showHud = false;
            director.idleAttract = false;
            director.visual = MocapDirector.VisualMode.Skeleton;
            director.RestartPipeline();
            yield return null;
            Assert.IsTrue(director.Performer2Active, "multi-person mode: " + director.ActiveSource?.Status);
            var mx = (MaxinePoseSource)director.ActiveSource;

            var report = new StringBuilder();
            report.AppendLine($"{(leftLeaves ? "left" : "right")} person leaves (image side; mirror on)");
            report.AppendLine("t(s)  | P1 trk held slotId travel(x,z)        pelvis(x,z)     | P2 trk held slotId travel(x,z)        pelvis(x,z)");
            string Row(float t)
            {
                var a = director.SmoothedPose; var b = director.SmoothedPose2;
                var p1 = director.skeletonView.GetJointWorldPosition(HumanJoint.Pelvis);
                var p2 = P2Pelvis(director);
                return $"{t,5:0.00} | {(a.Tracked ? 1 : 0)}   {(a.Held ? 1 : 0)}    {mx.SlotId(0),3}   ({a.RootOffset.x,6:0.00},{a.RootOffset.z,6:0.00})  ({p1.x,6:0.00},{p1.z,6:0.00}) | {(b.Tracked ? 1 : 0)}   {(b.Held ? 1 : 0)}    {mx.SlotId(1),3}   ({b.RootOffset.x,6:0.00},{b.RootOffset.z,6:0.00})  ({p2.x,6:0.00},{p2.z,6:0.00})";
            }

            // Both present until both are tracked for a while.
            var until = Time.realtimeSinceStartup + 20f;
            var both = 0;
            while (Time.realtimeSinceStartup < until && both < 30)
            {
                yield return null;
                if (director.SmoothedPose.Tracked && director.SmoothedPose2.Tracked) both++;
            }
            Assert.GreaterOrEqual(both, 30, "both people were not tracked: " + mx.Status);
            report.AppendLine(Row(0f));
            // Which performer is the person who stays? Whoever stands on the other side of the image.
            var stayerIsP1 = (director.RawPose.ImageJoints[(int)HumanJoint.Pelvis].x < 0.5f) != leftLeaves;
            var stayer0 = stayerIsP1 ? director.skeletonView.GetJointWorldPosition(HumanJoint.Pelvis) : P2Pelvis(director);
            report.AppendLine($"the person who stays drives {(stayerIsP1 ? "performer 1" : "performer 2")}; its pelvis is at ({stayer0.x:0.00},{stayer0.z:0.00})");

            // The leaving person walks out of frame over 1.5 s (half in frame on the way, as in life), not a cut.
            var t0 = Time.realtimeSinceStartup;
            var next = 0f;
            var maxDrift = 0f;
            var stayerUntracked = 0;
            var frames = 0;
            while (Time.realtimeSinceStartup - t0 < 6f)
            {
                yield return null;
                var t = Time.realtimeSinceStartup - t0;
                var shift = Mathf.Min(960, Mathf.RoundToInt(960f * t / 1.5f));
                frame.SetPixels(side, 0, 960, frame.height, blank);
                if (shift < 960)
                {
                    if (leftLeaves) frame.SetPixels(0, 0, 960 - shift, frame.height, keepTex.GetPixels(shift, 0, 960 - shift, frame.height));
                    else frame.SetPixels(960 + shift, 0, 960 - shift, frame.height, keepTex.GetPixels(0, 0, 960 - shift, frame.height));
                }
                frame.Apply();
                frames++;
                var pose = stayerIsP1 ? director.SmoothedPose : director.SmoothedPose2;
                var now = stayerIsP1 ? director.skeletonView.GetJointWorldPosition(HumanJoint.Pelvis) : P2Pelvis(director);
                if (!pose.Tracked) stayerUntracked++;
                else maxDrift = Mathf.Max(maxDrift, Vector2.Distance(new Vector2(now.x, now.z), new Vector2(stayer0.x, stayer0.z)));
                if (t >= next) { report.AppendLine(Row(t)); next += 0.2f; }
            }
            report.AppendLine($"after the other person left: the stayer's avatar was untracked on {stayerUntracked} of {frames} frames and its pelvis moved at most {maxDrift:0.00} m on stage");
            frame.SetPixels(side, 0, 960, frame.height, keep);
            frame.Apply();
            var file = Path.Combine(TestSupport.OutputDir, $"two_performer_leave_{(leftLeaves ? "left" : "right")}.txt");
            File.WriteAllText(file, report.ToString());
            Debug.Log("[MimicMocap] Two performer leave report:\n" + report);
            Object.Destroy(frame);
            Object.Destroy(keepTex);
            Assert.LessOrEqual(stayerUntracked, frames / 10, "the person who stayed lost their avatar: " + report);
            Assert.Less(maxDrift, 0.3f, "the person who stayed was moved on stage: " + report);
#else
            yield break;
#endif
        }

        /// <summary>Performer 2's drawn pelvis: its character's hips when it shows one, else its skeleton's pelvis.</summary>
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
