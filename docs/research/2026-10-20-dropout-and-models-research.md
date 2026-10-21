# Brief tracking loss, and models beyond BlazePose and the MediaPipe hand

Research notes gathered on 2026-10-20 for the sidewalk mirror. Question one: what do the VTuber and mocap apps do when
the body tracker drops the person for a frame or ten, and what does MediaPipe itself do? Question two: which newer
models could realistically replace BlazePose or the MediaPipe hand landmarker inside Unity Inference Engine? What we
adopted is in **bold**; the earlier notes in `2026-10-01-hand-stability-research.md` are not repeated.

## 1. What happens on a brief tracking loss

Nobody extrapolates. Every app surveyed holds the last pose for a short grace window and only then changes state, and
MediaPipe does the opposite of carrying a pose: it drops the frame and resets its filter.

- **XR Animator** (`js/mocap_lib_module.js`, `js/SA_system_emulation.min.js`): no "lost" branch, no decay, no
  prediction. A frame with no pose leaves the avatar where it was. `data_detected_stable` stays true until 250 ms
  after the first missing frame; a hand stays "visible" for 200 ms and its last palm position is reused for
  `stabilize_hand_percent` x 10 ms (default 200 ms). The per-bone slerp step is clamped to 16.7 to 200 ms, so a gap can
  never produce a jump larger than one 200 ms slerp. `hide_avatar_on_tracking_loss` defaults to on. Hands are dropped
  after `no_hand_countdown_max = 3` frames when the body says they are out of view.
- **OpenSeeFace** (VSeeFace's tracker, `tracker.py`): `discard_after = 5`: the face and its last box are kept, and
  re-fed as the next crop with a +0.1 confidence bonus, for five consecutive no-detect frames, then dropped. No
  time-based timeout, no extrapolation; features are EMA-smoothed with alpha 0.2.
- **VTube Studio**: a user option on loss, freeze in place (default) or return to the default pose.
- **Warudo**: no loss timeout documented; a "Hand Y Threshold" ignores a hand near the frame edge "to avoid jitter
  problems caused by unstable tracking".
- **Meta Quest hands**: gate on `IsDataHighConfidence` rather than `IsTracked`, because the frames just before a loss
  are already wrong.
- **MediaPipe**: the pose graph gates the landmarks on presence 0.5 (`ThresholdingCalculator`); below it no landmark
  packet is emitted, the ROI loopback is empty and the detector re-runs next frame. `min_tracking_confidence` is that
  same single threshold (Tasks API: an IoU association threshold), with no hysteresis. `LandmarksSmoothingCalculator`
  on an empty packet either fails or resets its state, and its One-Euro `dt` comes straight from the packet timestamps
  with no upper bound, so after a gap the first new sample is treated as very low frequency, alpha goes to about 1,
  and the output snaps to it.
- **Kinect joint filtering** (Holt double exponential, MSDN): parameter sets (smoothing, correction, prediction,
  jitter radius, max deviation radius) default 0.5 / 0.5 / 0.5 / 0.05 m / 0.04 m; "prediction" above 0.5 frames
  "will likely lead to overshooting when moving quickly" unless damped by a small max deviation radius; the white
  paper calls forecasting practical for about two frames (66 ms). Constant-velocity models bridge gaps between
  detections in the per-joint Kalman and UKF trackers (arXiv 1710.06235, WACV 2020 Buizza et al.).

**What we did** (`PoseSmoother`, `PoseHoldTests`): the install footage has three or four one-to-three-frame dropouts
a minute between the real walk-outs, and every one of them reset the smoother, so the next frame came out raw
(occluded joints at their garbage positions, every joint jittering until the filters had settled). Now a loss shorter
than `holdSeconds` (0.5 s) keeps the last smoothed pose, carried along its low-passed velocity with a decaying time
constant of `extrapolateSeconds` (0.08 s: a wave keeps waving for a few frames, then stops; the carried distance is
bounded by velocity x 0.08 s, about the Kinect max deviation radius at arm speeds), confidence and the last-good memory
intact. When tracking returns the filters are primed with what was shown a frame ago, so the pose eases onto the new
detection over a few frames instead of snapping to it; MediaPipe's unbounded `dt` snap is exactly what this avoids.
A longer loss still resets, so the next person starts from their own pose. `predictSeconds` (Kinect-style lag
compensation, off by default) is there for measurement.

## 2. Models that could run in Unity Inference Engine

Permissive licence, ONNX available, fast on an RTX 4080.

### Body, single RGB camera

| Model | Output | Licence | ONNX | Speed |
|---|---|---|---|---|
| **RTMW3D-x / -l** (MMPose rtmpose3d) | 133 keypoints (17 body, 6 feet, 68 face, 42 hand), 2D plus root-relative z in metres, single pass, 384x288 top-down | Apache 2.0 | huggingface.co/Soykaf/RTMW3D-x (about 369 MB); in rtmlib | -l about 30 fps on an RTX 3090 in PyTorch; runs in the browser on WebGPU |
| RTMO-s/m/l | 2D 17 keypoints, one-stage multi-person, 640 square | Apache 2.0 | OpenMMLab zips, huggingface.co/pesi/rtmo | 8.9 / 12.4 / 19.1 ms on a V100 |
| RTMPose-m/l | 2D 17 or 133 keypoints, top-down | Apache 2.0 | OpenMMLab onnx_sdk zips | 2.3 / 3.5 ms at 256x192 TensorRT on a GTX 1660 Ti |
| ViTPose | 2D 17/25/133 | Apache 2.0 | huggingface.co/JunkyByte/easy_ViTPose | ViT-S 175 fps with TensorRT on a 1080 Ti |
| YOLO11-pose | 2D 17 | AGPL-3.0 or paid | Ultralytics export | 12 ms T4 | 
| MoveNet | 2D 17, 192/256 | Apache 2.0 | PINTO model zoo 115 | very fast |
| MotionBERT (2D to 3D lifter) | H36M-17 3D from a window of up to 243 frames, non-causal | Apache 2.0 | none published | n/a |
| Sapiens | 308 keypoints | CC BY-NC 4.0 | TorchScript only | out |

Unity's own Hugging Face organisation ships only BlazePose for Inference Engine. If an op fails to import, the
fallback runtime is asus4/onnxruntime-unity (MIT; CUDA and TensorRT in player builds).

### Hands

| Model | Output | Licence | ONNX |
|---|---|---|---|
| **RTMPose-m hand5** | 2D 21 keypoints on a 256x256 crop, PCK@0.2 81.5 on COCO-WholeBody-Hand, 2.6 GFLOPs | Apache 2.0 | download.openmmlab.com/mmpose/v1/projects/rtmposev1/onnx_sdk/rtmpose-m_simcc-hand5_pt-aic-coco_210e-256x256-74fb594_20230320.zip; a blur-robust fine-tune at huggingface.co/tasmulaev/rtmpose-m-distill |
| HaMeR | MANO mesh / 3D joints, ViT-H | MIT code, MANO research licence | none |
| WiLoR | MANO 3D | CC-BY-NC-ND | none |
| InterWild | two-hand MANO | CC-BY-NC 4.0, archived | none |
| MediaPipe HandLandmarker | the one "full" model we run | Apache 2.0 | no newer architecture published |

### Recommendation

Body: **RTMW3D-x** (or -l if the frame budget is tight). It is the only permissively licensed, already-ONNX,
single-pass model that gives 3D (root-relative z) for the body and 42 hand keypoints at once, trained on fourteen
datasets, and it is a plain SimCC top-down head, so it slots into the existing crop pipeline: reuse the Maxine or
BlazePose box as the ROI, no new detector. Hands: **RTMPose-m hand5**, same SimCC family, same per-wrist 256 crop we
already make, trained on five hand datasets including in-the-wild COCO-WholeBody-Hand, so it should hold up better on
60 to 120 px hands; it is 2D only, so MediaPipe's z or RTMW3D's hand z would supply depth. Pre- and post-processing
are a few lines (mean/std normalise, SimCC argmax divided by the split ratio); the one unknown is Inference Engine op
coverage for the mmdeploy export, to be verified in the first hour of any integration. The non-commercial mesh models
are not worth a day. Neither is a one-hour job; this note is the starting point for a day's spike.

## Sources

- github.com/ButzYung/SystemAnimatorOnline (js/mocap_lib_module.js, js/SA_system_emulation.min.js)
- github.com/emilianavt/OpenSeeFace (tracker.py); vseeface.icu; github.com/DenchiSoft/VTubeStudio/wiki/VTube-Studio-Settings; docs.warudo.app/docs/mocap/mediapipe
- MediaPipe: modules/pose_landmark/tensors_to_pose_landmarks_and_segmentation.pbtxt, pose_landmark_gpu.pbtxt, python/solutions/pose.py, tasks/cc/vision/pose_landmarker/pose_landmarker_graph.cc, calculators/util/landmarks_smoothing_calculator.cc and .proto, util/filtering/one_euro_filter.cc
- Kinect 1.8 "Joint Filtering" and "Skeletal Joint Smoothing White Paper" (learn.microsoft.com/en-us/previous-versions/windows/kinect-1.8/jj131024 and jj131429); arXiv 1710.06235; Buizza et al., WACV 2020; arXiv 2503.10450
- github.com/open-mmlab/mmpose (projects/rtmpose, projects/rtmo, projects/rtmpose3d); huggingface.co/Soykaf/RTMW3D-x; huggingface.co/pesi/rtmo; huggingface.co/JunkyByte/easy_ViTPose; huggingface.co/unity/inference-engine-blaze-pose; github.com/asus4/onnxruntime-unity; github.com/geopavlakos/hamer; github.com/rolpotamias/WiLoR; github.com/facebookresearch/InterWild; developers.google.com/edge/mediapipe/solutions/vision/hand_landmarker

## 3. Spike results (2026-10-20, install footage, RTX 4080, Unity Inference Engine GPUCompute)

Both models imported into Inference Engine without changes (opset 11 mmdeploy export for the hand, rtmlib export for
RTMW3D). Normalisation, the layout change and the SimCC argmax decode run inside the graph, so only the decoded points
are read back. Code: `Scripts/Spike/RtmHandLandmarker.cs`, `Scripts/Spike/RtmBodyLandmarker.cs`; models are git-ignored
under `Assets/MimicMocap/Spike/Models`; `HandFootageTests` switches: `MIMIC_SPIKE_RTM=1` (side by side),
`=2` (fallback), `MIMIC_SPIKE_BODY=1`.

**RTMPose-m hand5, side by side with MediaPipe on the same crop (TestHands.mp4).** Where both found the hand they agree
to 8.5% of the hand's size; index-tip jitter on those shared frames is the same (6.0% vs 6.7% of hand size). Where
MediaPipe missed, RTMPose was confident and at the body's wrist on 176 of 414 frames (43%), and the tiles show real
hands: mid-wave motion blur, hands hanging at the sides, hands at the frame edge. Running it on every crop cost about
30 ms per hand from schedule to readback and slowed everything sharing the GPU (Maxine 1.1 to 6.8 ms).

**RTMPose as the fallback for a lost hand.** When every MediaPipe crop misses, RTMPose runs on the first-choice crop,
which is still on the GPU as MediaPipe's own input tensor (224 px, resized to 256 in its graph); its 2D points are lifted
to 3D with the hand's last MediaPipe scale and depths and handed to the steady-hands model. Starting it on the following
frame instead found almost nothing (13 hands in 435 runs): most recoveries are on the first missed frame, with the
tracked crop.

| Clip | Hands, MediaPipe only | With the fallback | Fingertip shown jitter |
|---|---|---|---|
| TestHands.mp4 (lit) | 86% / 85% | 91% / 90% (173 hands from 400 runs) | 5.7 to 6.4 mm |
| Night.mp4 (infrared) | 75% / 74% | 78% / 81% (121 hands from 540 runs) | 8.3 to 9.2 mm |

**RTMW3D-x on Maxine's person box.** Accurate 2D for body and hands on every tile, including the walk-by in profile and
the far segment. Raw image-space wrist roughness 3.6 px against Maxine's 4.6 px; but depth relative to the hips is twice
as rough (13.2 against 6.5 mm), and at 46 ms from schedule to readback it delivered 20 poses a second while slowing
Maxine to 4 ms. Not a drop-in body source as the -x export; only the -x is published as ONNX (the -l is a PyTorch
checkpoint that would need an mmdeploy export), so its 2D could at most refine Maxine's.
