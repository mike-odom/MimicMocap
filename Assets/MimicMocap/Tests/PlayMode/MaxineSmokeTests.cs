using System.Collections;
using System.Collections.Generic;
using System.IO;
using MimicMocap.BlazePose;
using MimicMocap.Maxine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Runs the NVIDIA Maxine body pose SDK on the test frames, checks the canonical frame, and compares
    /// limb directions with BlazePose on the same frames to validate the axis conversion.
    /// Skips (Assert.Ignore) if the SDK is not installed.
    /// </summary>
    public class MaxineSmokeTests
    {
        static readonly string[] k_Images = { "Kung_Fu_Girl_1", "bye_bye_bye_-_Trainer_1", "Kung_Fu_Girl_2_1" };

        static readonly (int a, int b)[] k_MaxineLines =
        {
            ((int)MaxineJoint.LeftShoulder, (int)MaxineJoint.RightShoulder),
            ((int)MaxineJoint.LeftShoulder, (int)MaxineJoint.LeftElbow), ((int)MaxineJoint.LeftElbow, (int)MaxineJoint.LeftWrist),
            ((int)MaxineJoint.RightShoulder, (int)MaxineJoint.RightElbow), ((int)MaxineJoint.RightElbow, (int)MaxineJoint.RightWrist),
            ((int)MaxineJoint.LeftShoulder, (int)MaxineJoint.LeftHip), ((int)MaxineJoint.RightShoulder, (int)MaxineJoint.RightHip),
            ((int)MaxineJoint.LeftHip, (int)MaxineJoint.RightHip),
            ((int)MaxineJoint.LeftHip, (int)MaxineJoint.LeftKnee), ((int)MaxineJoint.LeftKnee, (int)MaxineJoint.LeftAnkle),
            ((int)MaxineJoint.RightHip, (int)MaxineJoint.RightKnee), ((int)MaxineJoint.RightKnee, (int)MaxineJoint.RightAnkle),
            ((int)MaxineJoint.Pelvis, (int)MaxineJoint.Torso), ((int)MaxineJoint.Torso, (int)MaxineJoint.Neck), ((int)MaxineJoint.Neck, (int)MaxineJoint.Nose),
        };

        [UnityTest]
        public IEnumerator Maxine_TracksTestFrames_AndAgreesWithBlazePose()
        {
            var mx = new MaxinePoseSource();
            mx.Initialize();
            if (!mx.IsAvailable)
            {
                Assert.Ignore("Maxine SDK unavailable: " + mx.Status);
                yield break;
            }

            var bz = TestSupport.CreateBlaze(BlazePoseSource.Variant.Full);
            var poseM = new PoseFrame();
            var poseB = new PoseFrame();
            var log = new System.Text.StringBuilder();
            var problems = new List<string>();
            var angles = new List<float>();
            log.AppendLine($"Maxine: {mx.Status}");

            foreach (var name in k_Images)
            {
                var tex = TestSupport.LoadTestImage(name);
                Assert.NotNull(tex, name);

                // BlazePose reference.
                bz.ResetTracking();
                for (var pass = 0; pass < 2; pass++)
                {
                    bz.Submit(tex, Time.timeAsDouble);
                    var waited = 0f;
                    while (bz.IsBusy && waited < 20f) { waited += Time.deltaTime; yield return null; }
                    bz.TryGetLatest(poseB);
                }

                // Maxine: several frames so the temporal filter settles; each submit is an async GPU readback.
                var got = false;
                for (var i = 0; i < 6; i++)
                {
                    mx.Submit(tex, Time.timeAsDouble);
                    var waitedFrames = 0;
                    // Drain each frame like the director does: TryGetLatest both polls and returns the result.
                    while (waitedFrames < 120 && !mx.TryGetLatest(poseM) && mx.IsBusy) { waitedFrames++; yield return null; }
                    if (poseM.Timestamp > 0) got = true;
                    yield return null;
                }

                log.AppendLine($"{name}: maxine tracked={poseM.Tracked} score={poseM.Score:0.00} run={mx.LastRunMs:0.0}ms rawPelvis(mm)={TestSupport.Fmt(mx.LastRawPelvis)} rootOffset={TestSupport.Fmt(poseM.RootOffset)} status='{mx.Status}' | blaze tracked={poseB.Tracked}");
                if (!got || !poseM.Tracked) { problems.Add(name + ": Maxine found no person"); continue; }

                void J(HumanJoint j) => log.AppendLine($"   {j,-14} maxine {TestSupport.Fmt(poseM[j])} c{poseM.ConfidenceOf(j):0.00}   blaze {TestSupport.Fmt(poseB[j])}");
                J(HumanJoint.Nose); J(HumanJoint.LeftShoulder); J(HumanJoint.RightShoulder); J(HumanJoint.LeftWrist); J(HumanJoint.RightWrist);
                J(HumanJoint.LeftHip); J(HumanJoint.RightHip); J(HumanJoint.LeftAnkle); J(HumanJoint.RightAnkle);
                log.AppendLine($"   maxine torso {PoseTransforms.TorsoLength(poseM):0.00}m leg {PoseTransforms.LegLength(poseM):0.00}m | blaze torso {PoseTransforms.TorsoLength(poseB):0.00}m leg {PoseTransforms.LegLength(poseB):0.00}m");

                if (poseM[HumanJoint.Nose].y < 0.2f) problems.Add($"{name}: Maxine nose y {poseM[HumanJoint.Nose].y:0.00} not above hips");
                if (poseM[HumanJoint.Pelvis].magnitude > 0.05f) problems.Add($"{name}: Maxine pelvis not at origin");
                var leg = PoseTransforms.LegLength(poseM);
                if (leg < 0.5f || leg > 1.2f) problems.Add($"{name}: Maxine leg length {leg:0.00}m implausible (units?)");

                if (poseB.Tracked)
                {
                    foreach (var (a, b) in TestSupport.CanonicalLimbs)
                    {
                        var dm = (poseM.Joints[b] - poseM.Joints[a]).normalized;
                        var db = (poseB.Joints[b] - poseB.Joints[a]).normalized;
                        var ang = Vector3.Angle(dm, db);
                        angles.Add(ang);
                        log.AppendLine($"   limb {(HumanJoint)a,-13}->{(HumanJoint)b,-13} angle {ang,5:0.0} deg");
                    }
                }

                var copy = TestSupport.ReadableCopy(tex);
                TestSupport.Annotate(copy, mx.LastImageKeypoints, mx.LastKeypointConfidence, k_MaxineLines, new Color(0.2f, 0.9f, 1f), new Color(1f, 0.3f, 0.9f));
                copy.Apply();
                File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, name + "_maxine.png"), copy.EncodeToPNG());
                Object.Destroy(copy);
            }

            angles.Sort();
            var median = angles.Count > 0 ? angles[angles.Count / 2] : 999f;
            log.AppendLine($"limb agreement with BlazePose: median {median:0.0} deg over {angles.Count} limbs, max {(angles.Count > 0 ? angles[angles.Count - 1] : 0):0.0}");
            foreach (var p in problems) log.AppendLine("PROBLEM: " + p);
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, "maxine_report.txt"), log.ToString());
            Debug.Log(log.ToString());
            mx.Dispose();
            bz.Dispose();

            Assert.IsEmpty(problems, string.Join(" | ", problems));
            Assert.Less(median, 30f, "Maxine and BlazePose disagree on limb directions; check MaxinePoseSource.AxisSigns");
        }
    }
}
