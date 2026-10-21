# Hand and body stability: what MediaPipe, XR Animator and Kalidokit actually do

Research notes gathered on 2026-10-01 for the sidewalk Halloween mirror. Most points were checked against
source: the MediaPipe graphs (`google-ai-edge/mediapipe`), the Kalidokit TypeScript, and XR Animator's
`js/mocap_lib_module.js`, `js/one_euro_filter.js` and settings strings. What we adopted is in **bold**.

## 1. MediaPipe models (2025-2026)

The networks have not changed since 2020-2021. The Tasks bundles repackage the same BlazePose GHUM and
hand_landmark_full models as float16 `.task` files.

- Pose Landmarker: `pose_landmarker_lite/full/heavy.task`; detector 224, landmarker 256, 33 landmarks; model card still "BlazePose GHUM 3D".
- Hand Landmarker: `hand_landmarker.task` = palm detector (192) + hand landmarks (224); model card "Hand Tracking (Lite/Full) with Fairness, Oct 2021", i.e. hand_landmark_full, the model we run. No newer Google hand landmark model exists.
- Holistic Landmarker (2024): one bundle, adds a `HandRoiRefinementGraph` (the Tasks version of the legacy `hand_recrop.tflite`).
- Open alternatives: RTMPose-m hand5 (Apache code, 2D only, needs a hand detector), RTMW/DWPose whole-body (2D, better on far hands because the hands come from the body crop), HaMeR/WiLoR (far better 3D on awkward poses but MANO is non-commercial and they are too heavy). No permissive model clearly beats hand_landmark_full for small hands; crop placement matters more than the model.
- arXiv 2405.03545 shows Holistic's pose-to-hand ROI fails at non-ideal hand orientations and fixes it with extra pose hand points and z.

**Conclusion for us:** keep the MediaPipe hand landmarker on high-resolution crops; spend the effort on crop placement, gating and a constrained hand model instead of a different network.

## 2. How MediaPipe stabilises

- Pose landmarks get a One-Euro filter; **hand landmarks get no temporal filter at all** (legacy Hands, Tasks HandLandmarker, Holistic). Hand stability comes only from ROI tracking.
- Pose One-Euro: image landmarks min_cutoff 0.05 / beta 80; world landmarks (metres, no scaling) **0.1 / 40**; auxiliary ROI landmarks 0.01 / 10; derivative cutoff 1; visibility low-pass alpha 0.1. Values are scaled by object size so one setting works at any distance. **Our PoseSmoother now uses 0.25 / 10 in metres** (hip-centred joints are already distance-invariant); travel gets half of that.
- Hand ROI: next frame's ROI from previous landmarks, scaled 2.0, shift_y -0.1; palm detection only re-runs when hands are missing.
- Holistic hand ROI from pose: wrist/pinky/index box scaled 2.7, recrop model refines the rotation; hysteresis keeps the previous rectangle unless rotation changes by more than **40 degrees**, translation by **0.2**, or scale by **0.4**. **Our tracked crop now has dead zones of 8% (centre), 15% (size) and 15 degrees (rotation).**

## 3. XR Animator

- Models: MediaPipe Tasks `pose_landmarker_full/heavy` and `hand_landmarker` (numHands 2) on the GPU delegate; optional Holistic.
- **Two hand landmarker instances with detection/tracking confidence 0.5 and 0.1; it switches to 0.1 when the user is far (frame / shoulder width > ~8.5), with a 1 s hysteresis.** Ours: entry threshold drops to 0.3 for crops under 100 px.
- Constrained tracking region: a square around each pose wrist (radius about shoulder width), upscaled onto a black canvas, landmarker run on that. **A detection whose palm is further than about 0.5 shoulder widths from the pose wrist is discarded.** Ours: the detected wrist must lie within 40% of the crop side of the body wrist.
- Filtering: hand landmarks wrist-relative through a vector One-Euro (30 Hz, min 1, beta 0.001); pose z through One-Euro (30, 1, 1, 2) with beta/dCutoff scaled by distance; wrist rotation offset as a slerped quaternion One-Euro (30, 0.5, 1, 1).
- Palm z correction: palm height/width ratio clamped to 1.25-1.75 by rescaling z. Minimum finger segment length: if a projected segment is shorter than 0.4 (0.2 for the last) of its reference length, the missing length is added along z (fingers pointing at the camera).
- Per-frame render blend: each bone slerps toward the new mocap value with weight 0.5 at 50 ms frame intervals rising to 1.0 at 200 ms ("Normal"); "Low" 0.75.
- Finger model: full per-joint Euler rotations with clamps. Flexion max about 100 degrees (144 for the thumb), hyperextension attenuated x0.75, **spread fades to 0 as proximal flexion goes from 30 to 90 degrees** (adopted), thumb offsets pi/8.
- Tracking loss: "hand stabilization" default 20% fades the hand-tracker override out over 200 ms; limbs enter over 250 ms and return to the default pose over 500 ms; option to hide or freeze the avatar. Ours: reacquire 0.3 s, relax 0.8 s, body-driven palm instead of a default pose.

## 4. Kalidokit

- Hand solver: one flexion value per joint from `angleBetween3DCoords` on landmark triplets (index 0-5-6, 5-6-7, 6-7-8 and so on), mapped to a single rotation axis with per-finger clamps; no abduction DOF; thumb has per-segment dampeners and offsets. Wrist from the palm plane (0, 17, 5), twist clamped to +/-0.3 rad.
- No temporal filtering in the library; the demo slerps bones toward targets with lerp 0.3 per frame (hips position 0.07).
- Arms reset to rest when wrist visibility < 0.23 or the wrist leaves the frame.

## 5. Practices we adopted

- **Low-DOF hand model** (palm orientation, size, per-finger spread + three bends with joint limits) fitted every frame, filtered in angle space (One-Euro 1.6 Hz, beta 0.003 per deg/s), with a 1.5 degree dead band on angles and 0.8 degrees on the palm. DIP coupled 60% toward 2/3 of the PIP.
- **Body-driven fallback:** palm orientation from the body's wrist and knuckle joints when the detector drops out; fingers relax to a half-curl over 0.8 s; the hand never disappears while the wrist is tracked.
- **Crop hysteresis and gating** as above.
- **Body smoothing** on MediaPipe's world-landmark profile, plus travel smoothing.
- One-Euro tuning recipe (Casiez): beta 0, lower min_cutoff until still-hand jitter is gone; then raise beta in x10 steps until fast motion stops lagging. Beta is unit-dependent.
- Anatomical limits: MCP 80-90 flexion, PIP 100-110, DIP 70-90; little hyperextension; DIP about 2/3 of PIP.

## Sources

- MediaPipe: `pose_landmark_filtering.pbtxt`, `pose_landmarks_detector_graph.cc`, `landmarks_smoothing_calculator.proto`, `hand_landmark_tracking_cpu.pbtxt`, `hand_landmark_landmarks_to_roi.pbtxt`, holistic `hand_tracking.pbtxt`, `hand_landmarks_from_pose_to_recrop_roi.pbtxt`, `hand_recrop_by_roi_cpu.pbtxt`, `holistic_hand_tracking.cc`; developers.google.com/edge/mediapipe/solutions/vision/{hand,pose,holistic}_landmarker; arXiv 2405.03545.
- huggingface.co/unity/inference-engine-blaze-hand; MMPose RTMPose project and hand 2D model zoo.
- github.com/ButzYung/SystemAnimatorOnline (js/mocap_lib_module.js, js/one_euro_filter.js, js/SA_system_emulation.min.js, translation.json).
- github.com/yeemachine/kalidokit (src/HandSolver, src/PoseSolver/calcArms.ts, docs/script.js).
- gery.casiez.net/1euro; Kinect joint filtering (MSDN Magazine, May 2012); arXiv 2309.14860 (DIP/PIP coupling).
