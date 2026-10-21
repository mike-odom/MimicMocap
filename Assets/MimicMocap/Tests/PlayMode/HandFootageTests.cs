using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MimicMocap.Tests
{
    /// <summary>
    /// Footage from the install camera (TestVideos/TestHands.mp4: hands raised, turned, waved, a walk out
    /// of frame and back, further away, more hand motion) through the real scene. Writes a per-frame CSV, a reasons
    /// histogram, a per-second detection timeline, an annotated contact sheet and the landmarker's own crops for
    /// frames it rejected, so hand tracking can be judged and tuned on the real picture rather than on dance clips.
    /// </summary>
    public class HandFootageTests
    {
        [SetUp]
        public void IgnoreEnvironmentErrors() => LogAssert.ignoreFailingMessages = true;

        const string k_ScenePath = "Assets/MimicMocap/Scenes/MimicMocap.unity";
        const string k_Clip = "TestHands.mp4";
        /// <summary>Save the landmarker's rejected crops. Off for measurements: the extra readback slows the hand cadence.</summary>
        const bool k_CaptureCrops = false;

        [UnityTest]
        public IEnumerator TestHands_HandDetectionReport()
        {
#if UNITY_EDITOR
            var folder = TestSupport.FindVideoFolder();
            if (folder == null) { Assert.Ignore("TestVideos folder not found"); yield break; }
            // MIMIC_TEST_CLIP picks another clip in TestVideos (experiments: a brightened copy, a night take).
            var clipName = System.Environment.GetEnvironmentVariable("MIMIC_TEST_CLIP");
            if (string.IsNullOrEmpty(clipName)) clipName = k_Clip;
            var clip = Path.Combine(folder, clipName);
            if (!File.Exists(clip)) { Assert.Ignore(clipName + " not in TestVideos"); yield break; }
            var tag = Path.GetFileNameWithoutExtension(clipName).ToLowerInvariant();
            // MIMIC_RUN_TAG keeps an experiment's outputs apart from the default run's.
            var runTag = System.Environment.GetEnvironmentVariable("MIMIC_RUN_TAG");
            if (!string.IsNullOrEmpty(runTag)) tag += "_" + runTag;

            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(k_ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;
            var director = Object.FindFirstObjectByType<MocapDirector>();
            Assert.NotNull(director);
            director.input = MocapDirector.InputKind.VideoFile;
            director.videoPath = clip;
            director.source = MocapDirector.SourceKind.Auto;
            // MIMIC_SOURCE = blaze|maxine and MIMIC_PEOPLE = 1|2 compare body sources on the same clip.
            var sourceName = System.Environment.GetEnvironmentVariable("MIMIC_SOURCE");
            if (sourceName == "blaze") director.source = MocapDirector.SourceKind.BlazePose;
            else if (sourceName == "maxine") director.source = MocapDirector.SourceKind.Maxine;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("MIMIC_PEOPLE"), out var people) && people >= 1 && people <= 2) director.performers = people;
            director.trackHands = true;
            director.stabilizeHands = true;
            // The director's own fallback stays off here: MIMIC_SPIKE_RTM picks the hand model setup explicitly.
            director.handFallback = false;
            director.showHud = false;
            director.idleAttract = false;
            director.mirror = false;   // keep the tracker's left/right and the pose's left/right the same side
            director.visual = MocapDirector.VisualMode.Skeleton;
            // MIMIC_FINGER_PROFILE = "minCutoffHz,beta,deadBandDeg[,palmCutoffHz,palmBeta]" tries other filter settings.
            var profile = System.Environment.GetEnvironmentVariable("MIMIC_FINGER_PROFILE");
            if (!string.IsNullOrEmpty(profile))
            {
                var parts = profile.Split(',');
                var st = director.handStabilizer;
                if (parts.Length > 0 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cut)) st.fingerMinCutoff = cut;
                if (parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var beta)) st.fingerBeta = beta;
                if (parts.Length > 2 && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var band)) st.angleDeadBand = band;
                if (parts.Length > 3 && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pcut)) st.palmMinCutoff = pcut;
                if (parts.Length > 4 && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pbeta)) st.palmBeta = pbeta;
                director.handStabilizer = st;
            }
            // MIMIC_BODY_PROFILE = "minCutoffHz,beta[,holdSeconds,extrapolateSeconds,predictSeconds]" tries other body filter settings.
            var bodyProfile = System.Environment.GetEnvironmentVariable("MIMIC_BODY_PROFILE");
            if (!string.IsNullOrEmpty(bodyProfile))
            {
                var parts = bodyProfile.Split(',');
                var sm = director.smoothing;
                if (parts.Length > 0 && TryF(parts[0], out var v0)) sm.minCutoff = v0;
                if (parts.Length > 1 && TryF(parts[1], out var v1)) sm.beta = v1;
                if (parts.Length > 2 && TryF(parts[2], out var v2)) sm.holdSeconds = v2;
                if (parts.Length > 3 && TryF(parts[3], out var v3)) sm.extrapolateSeconds = v3;
                if (parts.Length > 4 && TryF(parts[4], out var v4)) sm.predictSeconds = v4;
                director.smoothing = sm;
            }
            // MIMIC_BODY_WIDTH downscales the frame the body source sees (hand crops still use the full frame).
            if (!int.TryParse(System.Environment.GetEnvironmentVariable("MIMIC_BODY_WIDTH"), out var bodyWidth)) bodyWidth = 0;
            if (bodyWidth > 0) director.bodyProcessingWidth = bodyWidth;
            director.RestartPipeline();
            yield return null;
            var vid = director.ActiveInput as VideoFileFrameSource;
            Assert.NotNull(vid, "video source");
            vid.Loop = false;
            var hands = director.Hands;
            Assert.NotNull(hands, "hand tracker");
            hands.CaptureCrops = k_CaptureCrops || System.Environment.GetEnvironmentVariable("MIMIC_CAPTURE_CROPS") == "1";
            // MIMIC_HANDS_INFLIGHT overrides the number of pipelined hand passes (cadence experiments).
            if (int.TryParse(System.Environment.GetEnvironmentVariable("MIMIC_HANDS_INFLIGHT"), out var inFlight) && inFlight > 0) hands.MaxInFlight = inFlight;
            var handThresholds = System.Environment.GetEnvironmentVariable("MIMIC_HAND_THRESHOLDS");
            if (!string.IsNullOrEmpty(handThresholds))
            {
                var parts = handThresholds.Split(',');
                if (parts.Length > 0 && TryF(parts[0], out var keep)) hands.KeepThreshold = keep;
                if (parts.Length > 1 && TryF(parts[1], out var entry)) hands.PresenceThreshold = entry;
                if (parts.Length > 2 && TryF(parts[2], out var far)) hands.FarPresenceThreshold = far;
            }
            // SPIKE: MIMIC_SPIKE_RTM=1 runs RTMPose-m hand5 on every first-choice crop next to MediaPipe and compares
            // them on the same frames (MIMIC_SPIKE_RTM_SCALE resizes its crop, MIMIC_SPIKE_RTM_UPRIGHT=1 unrotates it).
            var spike = new RtmSpike();
            if (System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_RTM") == "1")
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>("Assets/MimicMocap/Models/RTMPose/rtmpose_m_hand5.onnx");
                if (asset == null) { Assert.Ignore("RTMPose hand model not downloaded (tools/get_models.ps1)"); yield break; }
                spike.Start(asset, hands);
            }
            // SPIKE: MIMIC_SPIKE_RTM=2 makes RTMPose the fallback for hands MediaPipe has lost (MIMIC_SPIKE_RTM_THR its threshold).
            Spike.RtmHandLandmarker fallback = null;
            if (System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_RTM") == "2")
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>("Assets/MimicMocap/Models/RTMPose/rtmpose_m_hand5.onnx");
                if (asset == null) { Assert.Ignore("RTMPose hand model not downloaded (tools/get_models.ps1)"); yield break; }
                fallback = new Spike.RtmHandLandmarker();
                fallback.Initialize(asset, 224);
                Assert.IsTrue(fallback.IsAvailable, fallback.Status);
                hands.Fallback = fallback;
                if (TryF(System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_RTM_THR"), out var fthr)) hands.FallbackThreshold = fthr;
            }
            // SPIKE: MIMIC_SPIKE_BODY=1 runs RTMW3D-x on Maxine's person box every frame it can and compares raw steadiness.
            var bodySpike = new BodySpike();
            if (System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_BODY") == "1")
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>("Assets/MimicMocap/Spike/Models/rtmw3d_x.onnx");
                if (asset == null) { Assert.Ignore("RTMW3D model not in Assets/MimicMocap/Spike/Models"); yield break; }
                bodySpike.Start(asset);
            }
            var runsAtStart = hands.RunCount;

            var csv = new StringBuilder("frame,clipTime,bodyTracked,score,confLWrist,confRWrist,confLIndex,confRIndex,Lreason,Lpresence,Lcrop,LmaxExtent,Rreason,Rpresence,Rcrop,RmaxExtent,LstabWeight,RstabWeight,LupVsForearm,LupVsKnuckles,RupVsForearm,RupVsKnuckles,maxineBoxes,maxineMeanConf,LtipErrMm,RtipErrMm,LtipSpeedMm,RtipSpeedMm,LfitTipMm,RfitTipMm,LfitMeanMm,RfitMeanMm,LindexLenRatio,RindexLenRatio\n");
            var reasons = new Dictionary<string, int>();
            var perSecondL = new Dictionary<int, (int seen, int total)>();
            var perSecondR = new Dictionary<int, (int seen, int total)>();
            var bodyPerSecond = new Dictionary<int, (int seen, int total)>();
            var tiles = new List<Texture2D>();
            var rejectedCrops = 0;
            long lastFrame = -1;
            var frames = 0; var bodyTracked = 0; var wristL = 0; var wristR = 0; var seenL = 0; var seenR = 0;
            var presenceSumL = 0f; var presenceSumR = 0f;
            var rawWrist = new[] { new List<Vector3>(), new List<Vector3>() };
            var shownWrist = new[] { new List<Vector3>(), new List<Vector3>() };
            var p2Frames = 0; var p2Tracked = 0; var p2HandsL = 0; var p2HandsR = 0;
            var rawTip = new[] { new List<Vector3>(), new List<Vector3>() };
            var shownTip = new[] { new List<Vector3>(), new List<Vector3>() };
            var lastRawStamp = new[] { -1.0, -1.0 };
            // Brief body dropouts: how many, whether the shown pose was held through them, and how far the smoothed
            // wrist jumps when tracking returns (the reset that used to happen made it jump to the raw position).
            var briefGaps = 0; var briefGapFrames = 0; var heldFrames = 0; var gapRun = 0; var sinceGap = 1000;
            var maxStepAfterGap = 0f; var stepSum = 0.0; var stepCount = 0; var bodyMs = 0.0; var bodyMsCount = 0;
            var prevShown = new Vector3[2]; var haveShown = false; var shownConsecutive = false;
            long nextTile = 0;
            var start = Time.realtimeSinceStartup;
            // The default clip is 51.5 s (others up to 90 s); stop when the player reaches the end.
            while (Time.realtimeSinceStartup - start < 110f)
            {
                yield return null;
                var f = vid.Frame;
                if (f < 0 || f == lastFrame) continue;
                if (lastFrame >= 0 && f < lastFrame) break; // looped or restarted
                lastFrame = f;
                var raw = director.RawPose;
                var tracked = raw.Tracked;
                frames++;
                if (tracked) bodyTracked++;
                var shown = director.SmoothedPose;
                if (!tracked) { gapRun++; if (shown.Held) heldFrames++; }
                else if (gapRun > 0) { if (gapRun <= 15) { briefGaps++; briefGapFrames += gapRun; sinceGap = 0; } gapRun = 0; }
                if (shown.Tracked)
                {
                    var shownL = shown[HumanJoint.LeftWrist]; var shownR = shown[HumanJoint.RightWrist];
                    if (haveShown)
                    {
                        var step = Mathf.Max(Vector3.Distance(shownL, prevShown[0]), Vector3.Distance(shownR, prevShown[1]));
                        if (sinceGap <= 10) maxStepAfterGap = Mathf.Max(maxStepAfterGap, step);
                        else if (shownConsecutive) { stepSum += step; stepCount++; }
                    }
                    prevShown[0] = shownL; prevShown[1] = shownR; haveShown = true; shownConsecutive = true;
                }
                else shownConsecutive = false;
                sinceGap++;
                if (tracked) { bodyMs += director.LastInferenceMs; bodyMsCount++; }
                spike.Drain(false);
                if (bodySpike.Active) bodySpike.Frame(director, raw, tracked, f);
                var cLW = raw.Confidence[(int)HumanJoint.LeftWrist];
                var cRW = raw.Confidence[(int)HumanJoint.RightWrist];
                var cLI = raw.Confidence[(int)HumanJoint.LeftIndex];
                var cRI = raw.Confidence[(int)HumanJoint.RightIndex];
                var lReason = hands.LastLeftReason ?? "";
                var rReason = hands.LastRightReason ?? "";
                var lSeen = lReason.StartsWith("tracked");
                var rSeen = rReason.StartsWith("tracked");
                var second = (int)(f / 30);   // VideoPlayer.time read 0 in batch mode; the clip is 30 fps
                var bs = bodyPerSecond.TryGetValue(second, out var vb) ? vb : (0, 0);
                bodyPerSecond[second] = (bs.Item1 + (tracked ? 1 : 0), bs.Item2 + 1);
                if (tracked)
                {
                    if (cLW >= hands.JointConfidenceFloor) wristL++;
                    if (cRW >= hands.JointConfidenceFloor) wristR++;
                    if (lSeen) { seenL++; presenceSumL += hands.LastLeftPresence; }
                    if (rSeen) { seenR++; presenceSumR += hands.LastRightPresence; }
                    Count(reasons, "L " + Bucket(lReason));
                    Count(reasons, "R " + Bucket(rReason));
                    var sl = perSecondL.TryGetValue(second, out var vl) ? vl : (0, 0);
                    perSecondL[second] = (sl.Item1 + (lSeen ? 1 : 0), sl.Item2 + 1);
                    var sr = perSecondR.TryGetValue(second, out var vr) ? vr : (0, 0);
                    perSecondR[second] = (sr.Item1 + (rSeen ? 1 : 0), sr.Item2 + 1);
                }
                // Fluidity series: the raw detector's index tip whenever a new detection arrived, and the stabilised tip
                // the avatar shows on the same frame (both wrist-relative), so lag and jitter can be compared.
                if (tracked)
                {
                    // Body steadiness: wrist position raw and smoothed (metres), for comparing tracker modes.
                    rawWrist[0].Add(raw[HumanJoint.LeftWrist]); rawWrist[1].Add(raw[HumanJoint.RightWrist]);
                    shownWrist[0].Add(director.SmoothedPose[HumanJoint.LeftWrist]); shownWrist[1].Add(director.SmoothedPose[HumanJoint.RightWrist]);
                }
                if (director.Performer2Active)
                {
                    p2Frames++;
                    if (director.SmoothedPose2.Tracked) { p2Tracked++; if (director.SmoothedPose2.LeftHand.HasImageLandmarks) p2HandsL++; if (director.SmoothedPose2.RightHand.HasImageLandmarks) p2HandsR++; }
                }
                var tipErr = new[] { -1f, -1f };
                var tipSpeed = new[] { -1f, -1f };
                var fitTip = new[] { -1f, -1f };
                var fitMean = new[] { -1f, -1f };
                var lenRatio = new[] { -1f, -1f };
                if (tracked)
                {
                    var smoothed = director.SmoothedPose;
                    var rawHands = new[] { raw.LeftHand, raw.RightHand };
                    var shownHands = new[] { smoothed.LeftHand, smoothed.RightHand };
                    for (var h = 0; h < 2; h++)
                    {
                        var rh = rawHands[h];
                        if (rh.Tracked && rh.HasImageLandmarks && rh.Timestamp != lastRawStamp[h] && shownHands[h].Tracked)
                        {
                            lastRawStamp[h] = rh.Timestamp;
                            var rt = rh.Landmarks[(int)HandFrame.Landmark.IndexTip];
                            if (rawTip[h].Count > 0) tipSpeed[h] = 1000f * Vector3.Distance(rt, rawTip[h][rawTip[h].Count - 1]);
                            rawTip[h].Add(rt);
                            shownTip[h].Add(shownHands[h].Landmarks[(int)HandFrame.Landmark.IndexTip]);
                            tipErr[h] = 1000f * Vector3.Distance(rt, shownHands[h].Landmarks[(int)HandFrame.Landmark.IndexTip]);
                            if (director.HandStabilizerP1 != null)
                            {
                                fitTip[h] = 1000f * director.HandStabilizerP1.FitResidual(rh, h == 1, out var mean, out var ratio);
                                fitMean[h] = 1000f * mean;
                                lenRatio[h] = ratio;
                            }
                        }
                    }
                }
                var stab = director.HandStabilizerP1;
                var lw = stab != null ? stab.LeftTrackWeight : 0f;
                var rw = stab != null ? stab.RightTrackWeight : 0f;
                // Angle between the crop's finger direction and the body's own cues (forearm elbow->wrist, knuckles wrist->mid(index,pinky)), in image pixels.
                var texW = director.ActiveInput.Width; var texH = director.ActiveInput.Height;
                float Angle(Vector2 up, HumanJoint fromJ, HumanJoint toJ, HumanJoint toJ2)
                {
                    if (raw.Confidence[(int)fromJ] < hands.JointConfidenceFloor || raw.Confidence[(int)toJ] < hands.JointConfidenceFloor || raw.Confidence[(int)toJ2] < hands.JointConfidenceFloor) return -1f;
                    var a = raw.ImageJoints[(int)fromJ]; var b = 0.5f * (raw.ImageJoints[(int)toJ] + raw.ImageJoints[(int)toJ2]);
                    var d = new Vector2((b.x - a.x) * texW, (b.y - a.y) * texH);
                    if (d.sqrMagnitude < 1f || up.sqrMagnitude < 1e-6f) return -1f;
                    return Vector2.Angle(up, d);
                }
                var lUpF = Angle(hands.LeftUp, HumanJoint.LeftElbow, HumanJoint.LeftWrist, HumanJoint.LeftWrist);
                var lUpK = Angle(hands.LeftUp, HumanJoint.LeftWrist, HumanJoint.LeftIndex, HumanJoint.LeftPinky);
                var rUpF = Angle(hands.RightUp, HumanJoint.RightElbow, HumanJoint.RightWrist, HumanJoint.RightWrist);
                var rUpK = Angle(hands.RightUp, HumanJoint.RightWrist, HumanJoint.RightIndex, HumanJoint.RightPinky);
                csv.Append($"{f},{vid.ClipTime:0.000},{(tracked ? 1 : 0)},{raw.Score:0.00},{cLW:0.00},{cRW:0.00},{cLI:0.00},{cRI:0.00},{Quote(lReason)},{hands.LastLeftPresence:0.00},{hands.LastLeftCropPixels:0},{hands.LeftMaxExtent:0},{Quote(rReason)},{hands.LastRightPresence:0.00},{hands.LastRightCropPixels:0},{hands.RightMaxExtent:0},{lw:0.00},{rw:0.00},{lUpF:0},{lUpK:0},{rUpF:0},{rUpK:0},{(director.ActiveSource is Maxine.MaxinePoseSource mx ? mx.LastBoxCount : -1)},{(director.ActiveSource is Maxine.MaxinePoseSource mx2 ? mx2.LastMeanConfidence : -1f):0.00},{tipErr[0]:0},{tipErr[1]:0},{tipSpeed[0]:0},{tipSpeed[1]:0},{fitTip[0]:0},{fitTip[1]:0},{fitMean[0]:0},{fitMean[1]:0},{lenRatio[0]:0.00},{lenRatio[1]:0.00}\n");

                // A tile every 1.5 s: the frame with the body wrists and knuckles (orange) and any detected hand landmarks (cyan).
                if (f >= nextTile && director.ActiveInput.Current != null)
                {
                    nextTile = f + 45;
                    var tile = TestSupport.ReadableCopy(director.ActiveInput.Current, 270, 480);
                    if (tracked)
                    {
                        foreach (var j in new[] { HumanJoint.LeftWrist, HumanJoint.RightWrist, HumanJoint.LeftIndex, HumanJoint.RightIndex, HumanJoint.LeftPinky, HumanJoint.RightPinky })
                            if (raw.Confidence[(int)j] >= hands.JointConfidenceFloor) TestSupport.DrawDot(tile, raw.ImageJoints[(int)j], new Color(1f, 0.6f, 0.1f), 3);
                        foreach (var hand in new[] { raw.LeftHand, raw.RightHand })
                            if (hand.Tracked && hand.HasImageLandmarks) TestSupport.Annotate(tile, hand.ImageLandmarks, null, HandFrame.Bones, new Color(0.2f, 1f, 0.9f), new Color(0.2f, 1f, 0.9f), 2);
                        foreach (var r in spike.LatestConfident)
                            if (r != null) TestSupport.Annotate(tile, r.Image, null, HandFrame.Bones, new Color(1f, 0.2f, 0.9f), new Color(1f, 0.2f, 0.9f), 1);
                    }
                    tile.Apply();
                    tiles.Add(tile);
                }
                // The landmarker's own view of a crop it rejected while the body wrist was confident: what did it see?
                if (tracked && hands.CaptureCrops && rejectedCrops < 24 && f % 15 == 0)
                {
                    if (!lSeen && cLW >= hands.JointConfidenceFloor && hands.LastLeftCrop != null && lReason.StartsWith("presence"))
                    {
                        File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, $"{tag}_reject_L_{f:0000}_{Safe(lReason)}.png"), hands.LastLeftCrop.EncodeToPNG());
                        rejectedCrops++;
                    }
                    if (!rSeen && cRW >= hands.JointConfidenceFloor && hands.LastRightCrop != null && rReason.StartsWith("presence"))
                    {
                        File.WriteAllBytes(Path.Combine(TestSupport.OutputDir, $"{tag}_reject_R_{f:0000}_{Safe(rReason)}.png"), hands.LastRightCrop.EncodeToPNG());
                        rejectedCrops++;
                    }
                }
            }

            File.WriteAllText(Path.Combine(TestSupport.OutputDir, tag + "_frames.csv"), csv.ToString());
            if (tiles.Count > 0) TestSupport.SaveContactSheet(tiles.ToArray(), 8, 270, 480, Path.Combine(TestSupport.OutputDir, tag + "_sheet.png"));
            foreach (var t in tiles) Object.Destroy(t);

            var elapsed = Time.realtimeSinceStartup - start;
            var report = new StringBuilder();
            report.AppendLine($"finger filters: cutoff {director.handStabilizer.fingerMinCutoff} Hz, beta {director.handStabilizer.fingerBeta}, dead band {director.handStabilizer.angleDeadBand} deg; palm cutoff {director.handStabilizer.palmMinCutoff} Hz, beta {director.handStabilizer.palmBeta}; landmarker presence keep {hands.KeepThreshold} entry {hands.PresenceThreshold} far {hands.FarPresenceThreshold}");
            report.AppendLine($"{clipName} through {director.ActiveSource?.Name}: {frames} frames in {elapsed:0.0} s, body tracked {Pct(bodyTracked, frames)}, body poses {director.InferenceFps:0.0} per second, {hands.MaxInFlight} passes in flight, hand passes {(hands.RunCount - runsAtStart) / (float)Mathf.Max(1, bodyTracked):0.00} per tracked frame ({frames / elapsed:0.0} video frames per second)");
            report.AppendLine($"wrist confident (of tracked frames): L {Pct(wristL, bodyTracked)} R {Pct(wristR, bodyTracked)}");
            report.AppendLine($"hand detected (of tracked frames): L {Pct(seenL, bodyTracked)} (mean presence {presenceSumL / Mathf.Max(1, seenL):0.00}) R {Pct(seenR, bodyTracked)} (mean presence {presenceSumR / Mathf.Max(1, seenR):0.00})");
            report.AppendLine("reasons:");
            foreach (var kv in Sorted(reasons)) report.AppendLine($"  {kv.Key}: {kv.Value}");
            report.AppendLine($"fingertip (wrist-relative, frames with a fresh detection): raw jitter {1000 * 0.5 * (Roughness(rawTip[0]) + Roughness(rawTip[1])):0.0} mm, shown jitter {1000 * 0.5 * (Roughness(shownTip[0]) + Roughness(shownTip[1])):0.0} mm, shown lag {0.5f * (LagFrames(shownTip[0], rawTip[0]) + LagFrames(shownTip[1], rawTip[1])) * 1000f / 30f:0} ms, shown-vs-raw distance {1000 * 0.5 * (MeanDistance(shownTip[0], rawTip[0]) + MeanDistance(shownTip[1], rawTip[1])):0.0} mm ({rawTip[0].Count + rawTip[1].Count} samples)");
            report.AppendLine($"body wrist roughness (mm, 5-frame moving-average deviation): raw {1000 * 0.5 * (Roughness(rawWrist[0]) + Roughness(rawWrist[1])):0.0}, smoothed {1000 * 0.5 * (Roughness(shownWrist[0]) + Roughness(shownWrist[1])):0.0}, smoothed lag {0.5f * (LagFrames(shownWrist[0], rawWrist[0]) + LagFrames(shownWrist[1], rawWrist[1])) * 1000f / 30f:0} ms, shown-vs-raw distance {1000 * 0.5 * (MeanDistance(shownWrist[0], rawWrist[0]) + MeanDistance(shownWrist[1], rawWrist[1])):0.0} mm");
            var bodyWidthUsed = director.ActiveInput != null ? (director.bodyProcessingWidth > 0 ? Mathf.Min(director.bodyProcessingWidth, director.ActiveInput.Width) : director.ActiveInput.Width) : 0;
            report.AppendLine($"body filter: {director.smoothing.minCutoff} Hz, beta {director.smoothing.beta}, hold {director.smoothing.holdSeconds} s, carry {director.smoothing.extrapolateSeconds} s, predict {director.smoothing.predictSeconds} s; body frame width {bodyWidthUsed} px, body source {bodyMs / Mathf.Max(1, bodyMsCount):0.0} ms per pose");
            if (director.SmootherP1 != null) report.AppendLine($"joints held as untrusted (lost, on the picture's edge, or faster than a person): on {director.SmootherP1.UntrustedFrames} frames");
            report.AppendLine($"brief body dropouts (1-15 frames): {briefGaps} totalling {briefGapFrames} frames; shown pose held through {heldFrames} untracked frames; max smoothed wrist step within 10 frames of a dropout {1000f * maxStepAfterGap:0} mm (typical step {1000.0 * stepSum / Mathf.Max(1, stepCount):0.0} mm)");
            spike.Drain(true);
            if (spike.Active) report.Append(spike.Report(tag));
            if (bodySpike.Active) report.Append(bodySpike.Report(tag, frames, elapsed));
            bodySpike.Dispose();
            spike.Dispose();
            if (fallback != null)
            {
                report.AppendLine($"SPIKE RTMPose fallback for lost hands (threshold {hands.FallbackThreshold}): {hands.FallbackRuns} runs, {hands.FallbackHits} hands supplied, {fallback.Skipped} skipped (slots busy)");
                hands.Fallback = null;
                fallback.Dispose();
            }
            if (p2Frames > 0) report.AppendLine($"performer 2: active {p2Frames} frames, tracked {Pct(p2Tracked, p2Frames)}, hands detected L {Pct(p2HandsL, p2Tracked)} R {Pct(p2HandsR, p2Tracked)}");
            report.AppendLine("per second, fraction of frames with the hand detected (0-9, '-' = body not tracked):");
            report.AppendLine("  body " + BodyTimeline(rows: bodyPerSecond, lastSecond: (int)(lastFrame / 30)));
            report.AppendLine("  L    " + Timeline(perSecondL, (int)(lastFrame / 30)));
            report.AppendLine("  R    " + Timeline(perSecondR, (int)(lastFrame / 30)));
            File.WriteAllText(Path.Combine(TestSupport.OutputDir, tag + "_report.txt"), report.ToString());
            Debug.Log("[MimicMocap] TestHands report:\n" + report);

            Assert.Greater(frames, 600, "the clip did not play through");
            Assert.Greater(bodyTracked, frames / 2, "the body was not tracked for most of the clip");
            // Regression floors on the default clip and settings. Measured 86% left and 81% right (run-to-run noise
            // about 3 points); it was 64% / 63% before the miss-frame fixes and the pipelined passes.
            if (clipName == k_Clip && string.IsNullOrEmpty(profile) && string.IsNullOrEmpty(sourceName) && string.IsNullOrEmpty(bodyProfile) && bodyWidth == 0 && string.IsNullOrEmpty(handThresholds) && !spike.Active && fallback == null && !bodySpike.Active)
            {
                Assert.GreaterOrEqual(seenL / (float)bodyTracked, 0.78f, "left hand detection rate fell");
                Assert.GreaterOrEqual(seenR / (float)bodyTracked, 0.72f, "right hand detection rate fell");
                Assert.GreaterOrEqual((hands.RunCount - runsAtStart) / (float)bodyTracked, 0.9f, "hand passes should run on nearly every tracked frame");
            }
#else
            yield break;
#endif
        }

        static string Pct(int a, int b) => $"{100f * a / Mathf.Max(1, b):0}%";

        /// <summary>
        /// SPIKE: RTMW3D-x on Maxine's person box. Both raw outputs are compared for steadiness on the same clip (image-space
        /// wrist roughness in pixels, wrist depth relative to the hips in millimetres), with cost and a contact sheet.
        /// </summary>
        sealed class BodySpike : System.IDisposable
        {
            Spike.RtmBodyLandmarker m_Body;
            readonly List<Spike.RtmBodyLandmarker.Result> m_Results = new List<Spike.RtmBodyLandmarker.Result>();
            readonly List<Vector3>[] m_RtmWrist = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Vector3>[] m_MxWrist = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Vector3>[] m_RtmDepth = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Vector3>[] m_MxDepth = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Texture2D> m_Tiles = new List<Texture2D>();
            double m_Ms, m_BodyScore, m_HandScore; int m_N, m_Submitted;
            long m_NextTile;
            public bool Active => m_Body != null;

            static readonly (int, int)[] k_Links =
            {
                (5, 7), (7, 9), (6, 8), (8, 10), (11, 13), (13, 15), (12, 14), (14, 16), (5, 6), (11, 12), (5, 11), (6, 12),
                (0, 1), (0, 2), (1, 3), (2, 4), (15, 17), (15, 19), (16, 20), (16, 22),
            };

            public void Start(Unity.InferenceEngine.ModelAsset asset)
            {
                m_Body = new Spike.RtmBodyLandmarker();
                m_Body.Initialize(asset);
                Assert.IsTrue(m_Body.IsAvailable, m_Body.Status);
                m_Body.Completed += r => m_Results.Add(r);
            }

            public void Frame(MocapDirector director, PoseFrame raw, bool tracked, long frame)
            {
                foreach (var r in m_Results)
                {
                    m_N++; m_Ms += r.Ms; m_BodyScore += r.BodyScore; m_HandScore += r.HandScore;
                    var W = director.ActiveInput.Width; var H = director.ActiveInput.Height;
                    for (var h = 0; h < 2; h++)
                    {
                        var w = r.Image[9 + h];
                        m_RtmWrist[h].Add(new Vector3(w.x * W, w.y * H, 0f));
                        var hips = 0.5f * (r.Z[11] + r.Z[12]);
                        m_RtmDepth[h].Add(new Vector3(0f, 0f, 1000f * (r.Z[9 + h] - hips)));
                    }
                    if (frame >= m_NextTile && director.ActiveInput.Current != null)
                    {
                        m_NextTile = frame + 45;
                        var tile = TestSupport.ReadableCopy(director.ActiveInput.Current, 270, 480);
                        TestSupport.Annotate(tile, r.Image, r.Confidence, k_Links, new Color(1f, 0.6f, 0.1f), new Color(1f, 0.6f, 0.1f), 3);
                        foreach (var start in new[] { 91, 112 })
                        {
                            var pts = new Vector2[HandFrame.LandmarkCount];
                            var conf = new float[HandFrame.LandmarkCount];
                            System.Array.Copy(r.Image, start, pts, 0, pts.Length);
                            System.Array.Copy(r.Confidence, start, conf, 0, conf.Length);
                            TestSupport.Annotate(tile, pts, conf, HandFrame.Bones, new Color(1f, 0.2f, 0.9f), new Color(1f, 0.2f, 0.9f), 1);
                        }
                        tile.Apply();
                        m_Tiles.Add(tile);
                    }
                }
                m_Results.Clear();
                if (!tracked) return;
                // Maxine's raw 2D wrists (the pose's image joints are smoothed in place) and raw 3D wrist depth from the hips.
                if (director.ActiveSource is Maxine.MaxinePoseSource mx)
                {
                    var W = director.ActiveInput.Width; var H = director.ActiveInput.Height;
                    var l = mx.LastImageKeypoints[(int)Maxine.MaxineJoint.LeftWrist];
                    var rr = mx.LastImageKeypoints[(int)Maxine.MaxineJoint.RightWrist];
                    m_MxWrist[0].Add(new Vector3(l.x * W, l.y * H, 0f));
                    m_MxWrist[1].Add(new Vector3(rr.x * W, rr.y * H, 0f));
                }
                // Canonical +Z is toward the camera; RTMW3D's z grows away from it.
                m_MxDepth[0].Add(new Vector3(0f, 0f, -1000f * raw[HumanJoint.LeftWrist].z));
                m_MxDepth[1].Add(new Vector3(0f, 0f, -1000f * raw[HumanJoint.RightWrist].z));
                if (raw.ImageBox.width > 0f && m_Body.Run(director.ActiveInput.Current, raw.ImageBox, frame)) m_Submitted++;
            }

            public string Report(string tag, int frames, float elapsed)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"SPIKE RTMW3D-x on Maxine's box: {m_N} results from {m_Submitted} submissions over {frames} frames ({m_N / Mathf.Max(0.1f, elapsed):0.0} per second), {m_Body.Skipped} skipped (both slots busy), {m_Ms / Mathf.Max(1, m_N):0.0} ms schedule to readback, mean body score {m_BodyScore / Mathf.Max(1, m_N):0.00}, hand score {m_HandScore / Mathf.Max(1, m_N):0.00}");
                sb.AppendLine($"  raw wrist roughness in the image (px, 5-sample moving-average deviation): Maxine {0.5 * (Roughness(m_MxWrist[0]) + Roughness(m_MxWrist[1])):0.00}, RTMW3D {0.5 * (Roughness(m_RtmWrist[0]) + Roughness(m_RtmWrist[1])):0.00}");
                sb.AppendLine($"  raw wrist depth from the hips, roughness (mm): Maxine {0.5 * (Roughness(m_MxDepth[0]) + Roughness(m_MxDepth[1])):0.0}, RTMW3D {0.5 * (Roughness(m_RtmDepth[0]) + Roughness(m_RtmDepth[1])):0.0}");
                var tiles = m_Tiles.ToArray();
                if (tiles.Length > 0) TestSupport.SaveContactSheet(tiles, 8, 270, 480, Path.Combine(TestSupport.OutputDir, tag + "_rtmw3d.png"));
                sb.AppendLine($"  tiles: {tag}_rtmw3d.png ({tiles.Length}, body orange, hands magenta)");
                return sb.ToString();
            }

            public void Dispose()
            {
                m_Body?.Dispose();
                foreach (var t in m_Tiles) if (t != null) Object.Destroy(t);
                m_Tiles.Clear();
            }
        }

        /// <summary>
        /// SPIKE: RTMPose-m hand5 next to MediaPipe on the same crops. Each RTMPose result is paired with the MediaPipe pass
        /// that used the same frame (same timestamp), so agreement and jitter are measured on identical pictures.
        /// </summary>
        sealed class RtmSpike : System.IDisposable
        {
            Spike.RtmHandLandmarker m_Rtm;
            readonly Dictionary<double, HandFrame[]> m_Mp = new Dictionary<double, HandFrame[]>();
            readonly List<Spike.RtmHandLandmarker.Result> m_Queue = new List<Spike.RtmHandLandmarker.Result>();
            // The exact crop region each result came from, upright, so tiles show what the models saw.
            readonly Dictionary<(double, int), (RenderTexture rt, Vector2 centre, float side)> m_Crops = new Dictionary<(double, int), (RenderTexture, Vector2, float)>();
            readonly Queue<RenderTexture> m_FreeRts = new Queue<RenderTexture>();
            readonly Queue<(double, int)> m_CropOrder = new Queue<(double, int)>();
            public readonly Spike.RtmHandLandmarker.Result[] LatestConfident = new Spike.RtmHandLandmarker.Result[2];
            public bool Active => m_Rtm != null;
            public float Threshold = 0.3f;

            int m_Paired, m_Unpaired, m_MpSeen, m_MpMiss, m_RecoverGate, m_GateFail;
            readonly List<float> m_ScoreSeen = new List<float>(), m_ScoreMiss = new List<float>();
            double m_AgreeFrac, m_AgreePx; int m_AgreeN;
            double m_TipAgreeFrac;
            readonly List<Vector3>[] m_MpTip = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Vector3>[] m_RtmTip = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Vector3>[] m_MpTipPx = { new List<Vector3>(), new List<Vector3>() };
            readonly List<Vector3>[] m_RtmTipPx = { new List<Vector3>(), new List<Vector3>() };
            double m_Ms; int m_MsN;
            readonly List<Texture2D> m_RecoverTiles = new List<Texture2D>(), m_BothTiles = new List<Texture2D>();
            int m_BothSeen;

            public void Start(Unity.InferenceEngine.ModelAsset asset, BlazeHand.HandTracker hands)
            {
                m_Rtm = new Spike.RtmHandLandmarker();
                if (TryF(System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_RTM_SCALE"), out var scale)) m_Rtm.Scale = scale;
                m_Rtm.Upright = System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_RTM_UPRIGHT") == "1";
                if (TryF(System.Environment.GetEnvironmentVariable("MIMIC_SPIKE_RTM_THR"), out var thr)) Threshold = thr;
                m_Rtm.Initialize(asset);
                Assert.IsTrue(m_Rtm.IsAvailable, m_Rtm.Status);
                for (var i = 0; i < 16; i++) m_FreeRts.Enqueue(new RenderTexture(192, 192, 0, RenderTextureFormat.ARGB32));
                hands.CropPrepared += (frame, crop) =>
                {
                    m_Rtm.Run(frame, crop);
                    // Keep a copy of the region for tiles (a cheap GPU blit; read back only for the few tiles saved).
                    if (m_FreeRts.Count == 0) { var old = m_CropOrder.Dequeue(); if (m_Crops.TryGetValue(old, out var o)) { m_FreeRts.Enqueue(o.rt); m_Crops.Remove(old); } }
                    var rt = m_FreeRts.Dequeue();
                    var side = crop.Side * m_Rtm.Scale;
                    Graphics.Blit(frame, rt, new Vector2(side / frame.width, side / frame.height), new Vector2((crop.Centre.x - 0.5f * side) / frame.width, (crop.Centre.y - 0.5f * side) / frame.height));
                    var key = (crop.Timestamp, crop.Hand);
                    if (m_Crops.TryGetValue(key, out var prev)) m_FreeRts.Enqueue(prev.rt); else m_CropOrder.Enqueue(key);
                    m_Crops[key] = (rt, crop.Centre, side);
                };
                hands.PassPublished += (t, l, r) =>
                {
                    var a = new HandFrame(); var b = new HandFrame();
                    l.CopyTo(a); r.CopyTo(b);
                    m_Mp[t] = new[] { a, b };
                };
                m_Rtm.Completed += r => m_Queue.Add(r);
            }

            /// <summary>Pair finished RTMPose results with their MediaPipe pass; with <paramref name="final"/> the rest are dropped.</summary>
            public void Drain(bool final)
            {
                if (m_Rtm == null) return;
                for (var i = 0; i < m_Queue.Count; i++)
                {
                    var r = m_Queue[i];
                    if (!m_Mp.TryGetValue(r.Timestamp, out var mp))
                    {
                        if (!final && Time.timeAsDouble - r.Timestamp < 1.0) continue;
                        m_Unpaired++;
                        m_Queue.RemoveAt(i--);
                        continue;
                    }
                    m_Queue.RemoveAt(i--);
                    Score(r, mp[r.Hand]);
                }
            }

            void Score(Spike.RtmHandLandmarker.Result r, HandFrame mp)
            {
                m_Paired++;
                m_Ms += r.Ms; m_MsN++;
                var W = r.TexW; var H = r.TexH;
                Vector2 Px(Vector2 n) => new Vector2(n.x * W, n.y * H);
                var wrist = Px(r.Image[0]);
                var mpSide = r.Side / Mathf.Max(1e-3f, m_Rtm.Scale);
                var gateOk = !r.HasBodyWrist || Vector2.Distance(wrist, r.BodyWristPx) <= 0.4f * mpSide;
                var confident = r.Score >= Threshold && gateOk;
                if (r.Score >= Threshold && !gateOk) m_GateFail++;
                LatestConfident[r.Hand] = confident ? r : null;
                if (mp.Tracked && mp.HasImageLandmarks)
                {
                    m_MpSeen++;
                    m_ScoreSeen.Add(r.Score);
                    var mw = Px(mp.ImageLandmarks[0]);
                    var extent = 0f;
                    for (var k = 0; k < HandFrame.LandmarkCount; k++) extent = Mathf.Max(extent, Vector2.Distance(Px(mp.ImageLandmarks[k]), mw));
                    extent = Mathf.Max(extent, 1f);
                    if (confident)
                    {
                        var sum = 0f;
                        for (var k = 0; k < HandFrame.LandmarkCount; k++) sum += Vector2.Distance(Px(r.Image[k]), Px(mp.ImageLandmarks[k]));
                        m_AgreePx += sum / HandFrame.LandmarkCount;
                        m_AgreeFrac += sum / HandFrame.LandmarkCount / extent;
                        m_TipAgreeFrac += Vector2.Distance(Px(r.Image[8]), Px(mp.ImageLandmarks[8])) / extent;
                        m_AgreeN++;
                        // Jitter on the shared frames: index tip relative to the wrist, in hand sizes and in pixels.
                        var mpTip = (Px(mp.ImageLandmarks[8]) - mw);
                        var rtTip = (Px(r.Image[8]) - wrist);
                        m_MpTip[r.Hand].Add(mpTip / extent); m_RtmTip[r.Hand].Add(rtTip / extent);
                        m_MpTipPx[r.Hand].Add(mpTip); m_RtmTipPx[r.Hand].Add(rtTip);
                        m_BothSeen++;
                        if (m_BothSeen % 40 == 0 && m_BothTiles.Count < 16) m_BothTiles.Add(Tile(r, mp));
                    }
                }
                else
                {
                    m_MpMiss++;
                    m_ScoreMiss.Add(r.Score);
                    if (confident)
                    {
                        m_RecoverGate++;
                        if (m_RecoverGate % 3 == 1 && m_RecoverTiles.Count < 24) m_RecoverTiles.Add(Tile(r, null));
                    }
                }
            }

            /// <summary>The crop region this result came from, with RTMPose's points (magenta) and MediaPipe's (cyan).</summary>
            Texture2D Tile(Spike.RtmHandLandmarker.Result r, HandFrame mp)
            {
                if (!m_Crops.TryGetValue((r.Timestamp, r.Hand), out var c)) return null;
                var tile = TestSupport.ReadableCopy(c.rt, 192, 192);
                Vector2 ToTile(Vector2 n) => new Vector2((n.x * r.TexW - (c.centre.x - 0.5f * c.side)) / c.side, (n.y * r.TexH - (c.centre.y - 0.5f * c.side)) / c.side);
                var pts = new Vector2[HandFrame.LandmarkCount];
                if (mp != null)
                {
                    for (var k = 0; k < pts.Length; k++) pts[k] = ToTile(mp.ImageLandmarks[k]);
                    TestSupport.Annotate(tile, pts, null, HandFrame.Bones, new Color(0.2f, 1f, 0.9f), new Color(0.2f, 1f, 0.9f), 2);
                }
                for (var k = 0; k < pts.Length; k++) pts[k] = ToTile(r.Image[k]);
                TestSupport.Annotate(tile, pts, null, HandFrame.Bones, new Color(1f, 0.2f, 0.9f), new Color(1f, 0.2f, 0.9f), 2);
                tile.Apply();
                return tile;
            }

            static string Percentiles(List<float> v)
            {
                if (v.Count == 0) return "n/a";
                var s = new List<float>(v); s.Sort();
                float P(float q) => s[Mathf.Clamp(Mathf.RoundToInt(q * (s.Count - 1)), 0, s.Count - 1)];
                return $"p10 {P(0.1f):0.00} p50 {P(0.5f):0.00} p90 {P(0.9f):0.00}";
            }

            static string AboveRates(List<float> v)
            {
                var sb = new StringBuilder();
                foreach (var t in new[] { 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.8f })
                {
                    var n = 0; foreach (var x in v) if (x >= t) n++;
                    sb.Append($" >={t:0.0} {Pct(n, v.Count)}");
                }
                return sb.ToString();
            }

            public string Report(string tag)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"SPIKE RTMPose-m hand5 on the tracker's first-choice crop (scale {m_Rtm.Scale}, {(m_Rtm.Upright ? "upright" : "rotated like MediaPipe's")}, confidence threshold {Threshold}): {m_Paired} results paired with MediaPipe, {m_Unpaired} unpaired, {m_Rtm.Skipped} skipped (all slots busy), {m_Ms / Mathf.Max(1, m_MsN):0.0} ms per hand from schedule to readback");
                sb.AppendLine($"  score where MediaPipe found the hand ({m_MpSeen}): {Percentiles(m_ScoreSeen)};{AboveRates(m_ScoreSeen)}");
                sb.AppendLine($"  score where MediaPipe missed ({m_MpMiss}): {Percentiles(m_ScoreMiss)};{AboveRates(m_ScoreMiss)}");
                sb.AppendLine($"  confident and at the body wrist where MediaPipe missed: {m_RecoverGate} of {m_MpMiss} ({Pct(m_RecoverGate, m_MpMiss)}); confident but failed the wrist gate: {m_GateFail}");
                sb.AppendLine($"  agreement where both found it ({m_AgreeN}): mean landmark distance {m_AgreePx / Mathf.Max(1, m_AgreeN):0.0} px = {100 * m_AgreeFrac / Mathf.Max(1, m_AgreeN):0.0}% of hand size, index tip {100 * m_TipAgreeFrac / Mathf.Max(1, m_AgreeN):0.0}%");
                sb.AppendLine($"  index tip jitter on those shared frames (wrist-relative, 5-sample moving-average deviation): MediaPipe {100 * 0.5 * (Roughness(m_MpTip[0]) + Roughness(m_MpTip[1])):0.00}% of hand size ({0.5 * (Roughness(m_MpTipPx[0]) + Roughness(m_MpTipPx[1])):0.00} px), RTMPose {100 * 0.5 * (Roughness(m_RtmTip[0]) + Roughness(m_RtmTip[1])):0.00}% ({0.5 * (Roughness(m_RtmTipPx[0]) + Roughness(m_RtmTipPx[1])):0.00} px)");
                var recover = m_RecoverTiles.FindAll(t => t != null).ToArray();
                var both = m_BothTiles.FindAll(t => t != null).ToArray();
                if (recover.Length > 0) TestSupport.SaveContactSheet(recover, 8, 192, 192, Path.Combine(TestSupport.OutputDir, tag + "_rtm_recovered.png"));
                if (both.Length > 0) TestSupport.SaveContactSheet(both, 8, 192, 192, Path.Combine(TestSupport.OutputDir, tag + "_rtm_both.png"));
                sb.AppendLine($"  tiles: {tag}_rtm_recovered.png ({recover.Length}, RTMPose magenta where MediaPipe missed), {tag}_rtm_both.png ({both.Length}, MediaPipe cyan)");
                return sb.ToString();
            }

            public void Dispose()
            {
                m_Rtm?.Dispose();
                foreach (var t in m_RecoverTiles) if (t != null) Object.Destroy(t);
                foreach (var t in m_BothTiles) if (t != null) Object.Destroy(t);
                foreach (var c in m_Crops.Values) { c.rt.Release(); Object.Destroy(c.rt); }
                foreach (var rt in m_FreeRts) { rt.Release(); Object.Destroy(rt); }
                m_Crops.Clear(); m_FreeRts.Clear();
            }
        }
        static bool TryF(string s, out float v) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

        /// <summary>Mean deviation from a 5-sample centred moving average: motion is smooth over 170 ms, noise is not.</summary>
        static double Roughness(List<Vector3> s)
        {
            if (s.Count < 7) return 0.0;
            var sum = 0.0;
            for (var i = 2; i < s.Count - 2; i++)
                sum += Vector3.Distance(s[i], (s[i - 2] + s[i - 1] + s[i] + s[i + 1] + s[i + 2]) / 5f);
            return sum / (s.Count - 4);
        }

        /// <summary>Samples of delay between the shown series and the raw one (best match over a 0..12 sample shift).</summary>
        /// <summary>Samples of delay between the shown series and the raw one, to a tenth of a sample (raw interpolated between samples, shifts 0..8).</summary>
        static float LagFrames(List<Vector3> shown, List<Vector3> raw)
        {
            var n = Mathf.Min(shown.Count, raw.Count);
            if (n < 30) return 0;
            var best = 0f; var bestErr = double.MaxValue;
            for (var k10 = 0; k10 <= 80; k10++)
            {
                var k = k10 / 10f;
                var whole = Mathf.FloorToInt(k); var frac = k - whole;
                var err = 0.0; var c = 0;
                for (var t = whole + 2; t < n; t++)
                {
                    var r = Vector3.Lerp(raw[t - whole], raw[t - whole - 1], frac);
                    err += Vector3.Distance(shown[t], r); c++;
                }
                err /= Mathf.Max(1, c);
                if (err < bestErr) { bestErr = err; best = k; }
            }
            return best;
        }

        static double MeanDistance(List<Vector3> a, List<Vector3> b)
        {
            var n = Mathf.Min(a.Count, b.Count);
            if (n == 0) return 0.0;
            var sum = 0.0;
            for (var i = 0; i < n; i++) sum += Vector3.Distance(a[i], b[i]);
            return sum / n;
        }
        static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";
        static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

        static IEnumerable<KeyValuePair<string, int>> Sorted(Dictionary<string, int> d)
        {
            var list = new List<KeyValuePair<string, int>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>Reason without the numbers, so a histogram groups them: "presence 0.21 (forearm)" -> "presence (forearm)".</summary>
        static string Bucket(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "none";
            if (reason.StartsWith("presence"))
            {
                var p = reason.IndexOf('(');
                return p >= 0 ? "presence " + reason.Substring(p) : "presence";
            }
            if (reason.StartsWith("too small")) return "too small";
            return reason;
        }

        static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        /// <summary>Per second, the fraction of video frames with a tracked body (0-9).</summary>
        static string BodyTimeline(Dictionary<int, (int seen, int total)> rows, int lastSecond)
        {
            var sb = new StringBuilder();
            for (var s = 0; s <= lastSecond; s++)
            {
                if (!rows.TryGetValue(s, out var v) || v.total == 0) { sb.Append(' '); continue; }
                sb.Append((char)('0' + Mathf.Clamp(Mathf.RoundToInt(9f * v.seen / v.total), 0, 9)));
            }
            return sb.ToString();
        }

        static string Timeline(Dictionary<int, (int seen, int total)> d, int lastSecond)
        {
            var sb = new StringBuilder();
            for (var s = 0; s <= lastSecond; s++)
            {
                if (!d.TryGetValue(s, out var v) || v.total == 0) { sb.Append('-'); continue; }
                sb.Append((char)('0' + Mathf.Clamp(Mathf.RoundToInt(9f * v.seen / v.total), 0, 9)));
            }
            return sb.ToString();
        }
    }
}
