# MimicMocap — Design

Date: 2026-09-26. Author: Claude (autonomous overnight session), decisions made on the owner's behalf while they were away.

## Goal

A single Windows app that takes one person from a webcam, estimates their 3D pose in real time, and projects a spooky skeleton or zombie mirroring them on a big screen at a Halloween party.

## Decisions taken without input

| Decision | Choice | Why |
|---|---|---|
| Project | Fresh Unity 6000.3.21f1 project `MimicMocap`, URP | An earlier private prototype is 6.4GB, 6.1GB of it unrelated game content. The owner OK'd 6000.3.21f1 and modifying freely. |
| Old code | Not copied. Clean-room rewrite of filters and retargeting. | The old runner and avatar controller are decompiled from Digital Standard's licensed DLL (non-commercial model). Knowledge reused, code not. |
| Pose source 1 | BlazePose GHUM on Unity Inference Engine 2.6.1 | Official Unity sample, Apache-2.0 models, testable tonight without a webcam via extracted video frames. World-landmark output (`Identity_4`, metres, hip-centred) confirmed present in the ONNX. |
| Pose source 2 | NVIDIA Maxine AR SDK BodyPoseEstimation via P/Invoke | 34 3D keypoints + per-joint rotations + multi-person tracking, NVIDIA-optimised for the RTX 4080. Runtime DLLs come from the public Broadcast redistributable installer (v0.8.7 Ada), downloaded to `tools/`. Needs admin to install, so the owner runs it in the morning. Source self-disables if the DLL is absent. |
| Visual 1 | Procedural glowing skeleton built from joint positions | Zero external assets, guaranteed to work, reads as "skeleton". |
| Visual 2 | Rigged humanoid (Mixamo/CC0 zombie or skeleton) via Mecanim retargeting | Drop-in: any Humanoid-rigged FBX works. The owner chose Mixamo; download needs an Adobe login so a placeholder rig is set up and the owner swaps the FBX. |
| Tracking | One person at a time | The owner's choice. BlazePose is single-person by design; Maxine is configured for 1 target. |
| HUD | OnGUI overlay, toggleable | Zero-asset, one class. |

## Architecture

```
FrameSource (WebcamFrameSource | VideoFileFrameSource | StillImageFrameSource)
      │  Texture per frame
      ▼
IPoseSource (BlazePoseSource | MaxinePoseSource)
      │  PoseFrame { Vector3[] joints (canonical HumanJoint layout, metres, hip-centred, Unity axes),
      │              float[] confidence, bool tracked, Quaternion[] rotations (optional) }
      ▼
PoseSmoother (One-Euro per joint, velocity-adaptive)
      │
      ├──► ProceduralSkeletonView   (bones + joints + skull, emissive, bloom)
      └──► HumanoidRetargeter       (positions → Mecanim bone rotations on any Humanoid Animator)
                                    (+ RotationRetargeter for Maxine joint angles, later)

MocapDirector  — owns the pipeline, hotkeys, source/visual switching, mirror, fullscreen
MocapHud       — OnGUI overlay: fps, source, confidence, hotkey help
```

### Canonical joint layout (`HumanJoint`)

Pelvis, Spine, Chest, Neck, Head, Nose, LeftEye, RightEye, LeftEar, RightEar, LeftShoulder, RightShoulder, LeftElbow, RightElbow, LeftWrist, RightWrist, LeftHandTip, RightHandTip, LeftThumb, RightThumb, LeftHip, RightHip, LeftKnee, RightKnee, LeftAnkle, RightAnkle, LeftHeel, RightHeel, LeftToe, RightToe. Derived joints (Pelvis, Spine, Chest, Neck, Head) are synthesised from hips/shoulders/ears when a source lacks them.

Coordinate convention: metres, origin at pelvis midpoint, +Y up, +X to the *subject's* left as seen by the camera (camera-space x), +Z toward the camera. Mirror mode flips X at the view layer only.

### Sources

**BlazePoseSource**: port of Unity's sample. Detector runs at 224 px every N frames or when tracking is lost; landmarker runs every frame on an ROI derived from the previous frame's landmarks (hip midpoint + torso scale), falling back to the detector box. Reads `Identity` (195 = 39 × x,y,z,vis,presence) for 2D/visibility and `Identity_4` (117 = 39 × xyz metres) for 3D. Model variant (lite/full/heavy) selectable.

**MaxinePoseSource**: P/Invoke bindings for `nvARPose.dll` + `NVCVImage.dll` (loaded from `%NV_AR_SDK_PATH%` or `%ProgramFiles%\NVIDIA Corporation\NVIDIA AR SDK`). Creates `BodyDetection` + `BodyPoseEstimation` features, Mode=1 (high quality), Temporal on, BatchSize 1, TrackPeople off. Feeds BGR U8 CPU images from `WebCamTexture` pixels. Reads KeyPoints3D (34, Maxine layout, converted to canonical) and JointAngles. If `NvAR_GetVersion` fails to load, `IsAvailable=false` and the director falls back to BlazePose.

### Retargeting (positions → rotations)

For each bone chain segment, compute the direction from parent joint to child joint in the source frame, and an "up" hint from an adjacent joint (e.g. for the upper arm: forward = elbow−shoulder, up = normal of shoulder–elbow–wrist triangle). Build `Quaternion.LookRotation(dir, up)`, multiply by the inverse of the same rotation computed on the character's T-pose, and apply to the bone's world rotation. Hips get position (scaled by character height / subject torso length) plus rotation from hip-line × spine-up. This is the standard technique; it's what the old controller did.

### Smoothing

One-Euro filter per joint (min cutoff 1.0 Hz, beta 0.02, d-cutoff 1.0), tuned so slow motion is rock steady and fast motion (jumps, waves) has low latency. Confidence gating: joints below 0.4 confidence hold their last smoothed value.

## Verification plan

- `Unity -batchmode -executeMethod Halloween.Build.CompileCheck` — compile gate.
- EditMode tests (`Tests/`): One-Euro filter behaviour; joint layout mapping round-trips; retargeter yields identity on T-pose input.
- PlayMode test: run BlazePoseSource on the four extracted video frames, assert `tracked` and plausible joint spread, and write annotated PNGs to `Logs/PoseCheck/` for visual review.
- Player build to `Build/MimicMocap.exe`.

## Verification outcome (2026-09-27, 00:00)

| Check | Result |
|---|---|
| Batch-mode compile, scene generation, URP setup | pass |
| EditMode unit tests | 7/7 |
| BlazePose on 4 extracted video frames | 3/3 real-footage frames tracked; the stylised anime render scores 0.49 vs 0.5 detector threshold and is marked optional |
| Canonical frame sanity (head up, pelvis at origin, X sign) | pass |
| Humanoid import of KayKit skeletons | 4/4 valid avatars, all required bones mapped |
| Retargeter limb directions vs tracked pose | all limbs 0.0 deg, spine 2.0 deg, shoulder line 14.7 deg |
| Rendered scene images | `Logs/PoseCheck/scene_*.png` |
| Windows player build | `Build/MimicMocap.exe` |
| Live webcam, Maxine SDK | not testable overnight (night 1) |

## Verification outcome, night 2 (2026-09-27, SDK installed)

| Check | Result |
|---|---|
| NVIDIA BodyTrack sample on `Kung Fu Girl.mp4` | 790 frames, 3D output; convention y-down / x-right / z-depth / mm confirmed over 753 frames |
| Maxine inside Unity on 3 test frames | tracked 0.80 to 0.88 score, 3.8 to 4.5 ms per frame; median 12 deg limb agreement with BlazePose |
| Video pipeline, both clips, both sources | 30 poses/s; BlazePose 90% / 100% tracked, Maxine 84% / 100% |
| Built player on a clip with Maxine | SDK loaded, tracked, no errors |
| Webcam capture path | built player opened Integrated Webcam 1280x720 @ 30 fps, SDK loaded, no errors; batch mode does not stream cameras |

Design changes from night 2: Maxine frames are read back asynchronously via `AsyncGPUReadback` callback (readback data is only valid for one frame), RGBA to BGR conversion happens inside `NvCVImage_Transfer`, the feature is re-created when the input size changes (its CUDA graph is bound to the first size), depth travel is relative to the first tracked depth, the `TrackPeople` selector is absent from the redistributable build and is ignored, and a fresh `VideoPlayer` component is added per video source because Unity's deferred Destroy leaves the old one disabled but present. The headless-launched editor never dispatches MCP commands, so verification stayed on batch mode.

Findings that changed the design (night 1): the landmarker ONNX carries the metric world-landmark output (`Identity_4`), so no depth heuristic is needed; hip-centred output means sideways travel comes from the 2D hip position (`PoseFrame.RootOffset`); KayKit rigs map Hips to a floor-level root, so hip height is measured between the upper-leg bones; chibi skulls hide arms, hence `HeadScale`.

## Addendum, 2026-09-27: finger tracking

Added on request. `HandTracker` runs MediaPipe's hand landmarker (21 landmarks, image and metric world coordinates) on a rotated square crop per wrist. The crop is derived from the body source's 2D joints, which both sources now publish in `PoseFrame.ImageJoints`: direction from wrist to knuckles when reliable, else along the forearm; side = max(3.2 x wrist-to-knuckle, 1.1 x forearm); centre 0.35 x side from the wrist along the fingers, matching MediaPipe's own crops where the wrist sits near the bottom edge. World landmarks are rotated into texture orientation, converted to the canonical frame like the pose, and made wrist-relative so the body supplies the wrist position. `HandFrame` rides in `PoseFrame`, is mirrored and One-Euro smoothed with the body, and `ProceduralSkeletonView` swaps the wrist fork for 20 finger bones when present. No palm detector is used.

Verified: reference hand photo tracked at presence 0.99 with the 2D index tip within 1% of the photo's fingertip and a 17.5 cm span; dance-clip hands (25 to 45 px) mostly fall below the landmarker's useful resolution, 1 of 6 tracked. The feature is therefore a close-range flourish; the fork remains the fallback.

Follow-up the same day: per-hand tracking state (crop from the hand's own previous landmarks, keep threshold 0.35, three frames of grace, body-derived crop as fallback without requiring the elbow) raised the dance-clip result to 2 of 6 and removes the failure mode where a close-up person's elbows leave the frame. `HumanoidRetargeter` gained finger rules (15 Mecanim finger bones per hand, palm normal as the twist hint, hand bone re-aimed from wrist to middle knuckle) for rigs that have fingers; untested for lack of such a rig locally, Mixamo's Skeletonzombie is the intended target. `ProceduralSkeletonView` was rebuilt on procedural meshes (knobbed bones, semi-elliptical ribs with sternum, vertebrae, sacrum and iliac wings, skull details) with bone emission reduced so lighting reveals the forms; render checks in `docs/images/scene_*.png`.

Live-test fixes (2026-09-27): HUD thumbnail mirrored in mirror mode to match overlay space; hand tracker snapshots the body pose and samples both crops into two tensors at submit time (the right hand's crop had been computed after an await, from the already-mirrored pose). Mixamo Skeletonzombie_T_Avelange imported as the character (52 Humanoid bones incl. fingers, textures extracted from the FBX); finger retargeting verified numerically against the reference-hand landmarks in `SceneRenderSmokeTests`.

## Out of scope tonight

Face expression, hands/fingers detail, multi-person, recording to animation clips, network streaming. The IPoseSource interface leaves room for them.
