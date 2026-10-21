using System.Collections.Generic;
using System.IO;
using System.Linq;
using MimicMocap.BlazeHand;
using MimicMocap.BlazePose;
using MimicMocap.Maxine;
using Unity.InferenceEngine;
using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Owns the capture -> pose -> smooth -> view pipeline and the party hotkeys.
    /// </summary>
    public sealed class MocapDirector : MonoBehaviour
    {
        public enum InputKind { Webcam, VideoFile, StillImage }
        public enum SourceKind { Auto, BlazePose, Maxine, Vmc }
        public enum VisualMode { Skeleton, Character, Both }

        [Header("Input")]
        public InputKind input = InputKind.Webcam;
        public int webcamIndex = 0;
        [Tooltip("Pick the first camera whose name contains this (case-insensitive), e.g. 'elgato'. Empty = use the preference below.")]
        public string webcamName = "";
        [Tooltip("Cameras to start on when plugged in, by name part, most wanted first (comma-separated, case-insensitive). The install camera is the Arducam day/night camera.")]
        public string preferredCameras = "arducam";
        [Tooltip("Otherwise start on the first physical camera that is not the laptop's integrated one. Virtual cameras (Elgato, OBS, NVIDIA Broadcast) are only used when named.")]
        public bool preferExternalCamera = true;
        [Tooltip("Rotation for the laptop's integrated camera, degrees counter-clockwise. O cycles the current camera's rotation and remembers it per camera.")]
        public int cameraRotation = 0;
        [Tooltip("Default rotation for external cameras (the install camera is mounted on its side).")]
        public int externalCameraRotation = 90;
        [Tooltip("Requested capture size; the camera picks its closest supported mode. Bigger frames mean bigger hands for the finger tracker.")]
        public int webcamWidth = 1920;
        public int webcamHeight = 1080;
        public int webcamFps = 30;
        [Tooltip("Absolute path, or relative to the project/StreamingAssets folder.")]
        public string videoPath = "";
        [Tooltip("Folder of test videos for the P/N hotkeys. Empty = look for a TestVideos folder next to the project or player.")]
        public string videoFolder = "";
        public Texture2D stillImage;

        /// <summary>Videos found in <see cref="videoFolder"/>, cycled with N.</summary>
        public string[] Videos { get; private set; } = System.Array.Empty<string>();
        public int VideoIndex { get; private set; }

        [Header("Pose source")]
        public SourceKind source = SourceKind.Auto;
        public BlazePoseSource.Variant blazeVariant = BlazePoseSource.Variant.Full;
        public ModelAsset blazeDetector;
        public ModelAsset blazeLandmarkerLite;
        public ModelAsset blazeLandmarkerFull;
        public ModelAsset blazeLandmarkerHeavy;
        public TextAsset blazeAnchors;
        public MaxinePoseSource.Quality maxineQuality = MaxinePoseSource.Quality.HighQuality;
        [Tooltip("UDP port the VMC receiver source listens on. XR Animator, VSeeFace and TDPT send to 39539 by default.")]
        public int vmcPort = Vmc.VmcPoseSource.DefaultPort;

        [Header("Performers")]
        [Range(1, 2)]
        [Tooltip("Two performers need the Maxine source (its multi-person tracker). The second person gets their own skeleton or character, placed where they stand.")]
        public int performers = 1;
        [Tooltip("Track everyone in frame even when one performer is shown, so the avatar stays with its person when somebody else walks in and passes to the newcomer only once its person has gone (Maxine's multi-person tracker; also 33 ms less body lag than its single-person mode).")]
        public bool stableIdentity = true;
        [Tooltip("Seconds performer 1's person may be missing before another tracked person takes over the avatar.")]
        public float performerSwitchDelay = 0.5f;

        [Header("Hands")]
        [Tooltip("Run the MediaPipe hand landmarker on crops around each wrist for finger detail.")]
        public bool trackHands = true;
        public ModelAsset handLandmarker;
        [Tooltip("Seconds a hand keeps its last good landmarks when the tracker briefly loses it.")]
        public float handHoldSeconds = 0.5f;
        [Tooltip("Project detections onto an anatomical hand model, smooth in angle space, and keep a body-driven hand when the detector loses it.")]
        public bool stabilizeHands = true;
        public HandStabilizer.Settings handStabilizer = HandStabilizer.Settings.Default;
        [Tooltip("When MediaPipe loses a hand, try a second hand model (RTMPose-m hand5) on the same crop. Finds motion-blurred and side-on hands MediaPipe drops (86% to 91% of frames on the install footage). K toggles, -nofallback turns it off.")]
        public bool handFallback = true;
        [Tooltip("RTMPose-m hand5 ONNX (Assets/MimicMocap/Models/RTMPose/rtmpose_m_hand5.onnx, downloaded by tools/get_models.ps1).")]
        public ModelAsset handFallbackModel;

        [Header("Smoothing")]
        public PoseSmoother.Settings smoothing = PoseSmoother.Settings.Default;

        [Header("Output")]
        [Tooltip("Mirror like a magic mirror: your left hand drives the character's right hand on your left of the screen.")]
        public bool mirror = true;
        public VisualMode visual = VisualMode.Skeleton;
        public ProceduralSkeletonView skeletonView;
        public Transform characterRoot;
        [Range(0.3f, 1.5f)]
        [Tooltip("Head scale for a bare rig under CharacterRoot without a CharacterSlot (slots carry their own).")]
        public float characterHeadScale = 1f;
        [Tooltip("Floating buttons the avatar's hands press to switch characters. Created at runtime if missing.")]
        public HandMenu handMenu;

        [Header("Stage")]
        [Tooltip("Pure black stage for a projector or a Pepper's ghost: no ground, moon, fog or film grain. Off shows the moonlit graveyard.")]
        public bool ghostStage = true;
        [Tooltip("Fraction of the view height a standing figure fills; the camera's field of view is set from it (-fill, or - and = at runtime). 0 leaves the camera alone.")]
        [Range(0f, 0.98f)] public float figureFill = k_DefaultFill;
        [Tooltip("Metres to move the figure on screen (x right, y up): the arrow keys change it, 0 resets. Saved with the fill in PlayerPrefs.")]
        public Vector2 frameShift;
        public const float k_DefaultFill = 0.78f;
        [Tooltip("Lift the avatar when the subject jumps (the floor tracker tells a jump from walking toward a tilted camera). -nojump turns it off.")]
        public bool jump = true;
        [Tooltip("When nobody has been tracked for idleDelay seconds, show a slowly breathing, swaying, occasionally waving skeleton so the screen is never empty.")]
        public bool idleAttract = true;
        public float idleDelay = 30f;
        [Tooltip("Frames wider than this are downscaled before the body source (hand crops still use the full frame). Keeps 4K cameras cheap. 0 = never.")]
        public int bodyProcessingWidth = 1920;

        [Header("Debug")]
        public bool showHud = true;
        public bool logToConsole = false;

        public IPoseSource ActiveSource { get; private set; }
        public IFrameSource ActiveInput { get; private set; }
        public PoseFrame RawPose { get; } = new PoseFrame();
        public PoseFrame SmoothedPose { get; } = new PoseFrame();
        public float InferenceFps { get; private set; }
        public float LastInferenceMs { get; private set; }
        public string LastMessage { get; private set; } = "";
        public bool CharacterReady => m_Retargeter != null && m_Retargeter.IsValid;
        public BlazePoseSource BlazeSource => ActiveSource as BlazePoseSource;
        public HandTracker Hands => m_Hands;
        public HandStabilizer HandStabilizerP1 => m_HandStab;
        /// <summary>Performer 1's body smoother (diagnostics: joints held as untrusted).</summary>
        public PoseSmoother SmootherP1 => m_Smoother;
        /// <summary>True while an external VMC sender drives the pose (its hands replace the in-process hand tracker).</summary>
        public bool UsingVmc => m_Vmc != null;
        HandStabilizer m_HandStab;
        HandStabilizer m_HandStab2;

        /// <summary>True while the attract loop is driving the avatar because nobody is tracked.</summary>
        public bool IdleActive { get; private set; }
        readonly PoseFrame m_IdlePose = new PoseFrame();
        readonly Vector3[] m_IdleHand = new Vector3[HandFrame.LandmarkCount];
        float m_LastLiveTime = -100f;
        float m_IdleStart;
        GameObject m_Ground, m_Moon;
        UnityEngine.Rendering.Volume m_Volume;
        RenderTexture m_BodyRt;

        // Second performer (opt-in via performers = 2; only the Maxine source can supply a second person).
        public PoseFrame RawPose2 { get; } = new PoseFrame();
        public PoseFrame SmoothedPose2 { get; } = new PoseFrame();
        /// <summary>True while a second person can actually be tracked: two performers requested and the source runs multi-person.</summary>
        public bool Performer2Active => performers > 1 && ActiveSource is IMultiPersonSource m && m.MultiPersonActive;
        public ProceduralSkeletonView SkeletonView2 => m_Skeleton2;
        /// <summary>Performer 2's character slot index, or -1 for the bare bones.</summary>
        public int CurrentCharacter2 { get; private set; } = -1;
        public Transform Performer2Root => m_Performer2Root;
        public Animator Performer2Animator => m_Char2 != null ? m_Char2.GetComponentInChildren<Animator>(true) : null;
        public bool Performer2ShowsCharacter => m_Char2 != null && m_Retargeter2 != null;

        PoseSmoother m_Smoother2;
        HandTracker m_Hands2;
        readonly HandFrame m_LatestLeftHand2 = new HandFrame();
        readonly HandFrame m_LatestRightHand2 = new HandFrame();
        readonly HandFrame m_ScratchLeftHand2 = new HandFrame();
        readonly HandFrame m_ScratchRightHand2 = new HandFrame();
        double m_LeftHandTime2 = -100;
        double m_RightHandTime2 = -100;
        Transform m_Performer2Root;
        ProceduralSkeletonView m_Skeleton2;
        GameObject m_Char2;
        HumanoidRetargeter m_Retargeter2;
        bool m_Performer2Chosen;
        float m_LastTracked2Time = -100f;

        HandTracker m_Hands;
        Spike.RtmHandLandmarker m_Fallback, m_Fallback2;
        /// <summary>True while the second hand model is standing by for lost hands.</summary>
        public bool HandFallbackActive => m_Hands != null && m_Hands.Fallback != null;
        readonly HandFrame m_LatestLeftHand = new HandFrame();
        readonly HandFrame m_LatestRightHand = new HandFrame();
        readonly HandFrame m_ScratchLeftHand = new HandFrame();
        readonly HandFrame m_ScratchRightHand = new HandFrame();
        double m_LeftHandTime = -100;
        double m_RightHandTime = -100;

        PoseSmoother m_Smoother;
        HumanoidRetargeter m_Retargeter;
        Animator m_CharacterAnimator;
        double m_SubmitTime;
        int m_InferenceCount;
        float m_FpsWindowStart;
        MaxinePoseSource m_Maxine;
        BlazePoseSource m_Blaze;
        Vmc.VmcPoseSource m_Vmc;
        bool m_MaxineUnavailable;
        bool m_MenuHiddenByArgs;
        bool m_MenuShownByArgs;
        bool m_FillFromArgs;
        readonly Dictionary<KeyCode, float> m_KeyRepeat = new Dictionary<KeyCode, float>();
        readonly FloorTracker m_Floor = new FloorTracker();
        readonly FloorTracker m_Floor2 = new FloorTracker();
        float m_LastLift, m_LastLift2;
        /// <summary>Metres performer 1's lowest foot is above the floor right now.</summary>
        public float Lift => m_Floor.Lift;
        string m_StartCharacter;

        void Start()
        {
            Application.targetFrameRate = 60;
            ApplyCommandLine();
            ScanVideos();
            if (input == InputKind.Webcam && WebCamTexture.devices.Length == 0 && Videos.Length > 0)
            {
                Log("No webcam found; playing test videos instead (P toggles, N next).");
                input = InputKind.VideoFile;
                videoPath = Videos[VideoIndex];
            }
            if (handMenu == null) handMenu = GetComponent<HandMenu>();
            if (handMenu == null) handMenu = gameObject.AddComponent<HandMenu>();
            if (m_MenuHiddenByArgs) handMenu.visible = false;
            if (m_MenuShownByArgs) handMenu.visible = true;
            m_Ground = GameObject.Find("Ground");
            m_Moon = GameObject.Find("Moon");
            m_Volume = FindFirstObjectByType<UnityEngine.Rendering.Volume>();
            ApplyStage();
            LoadFraming();
            FrameStage();
            RestartPipeline();
            if (!string.IsNullOrEmpty(m_StartCharacter) && !SelectCharacterByName(m_StartCharacter)) Log("No character matches -character " + m_StartCharacter);
        }

        /// <summary>Re-create input, pose source and character binding from the current public fields.</summary>
        public void RestartPipeline()
        {
            m_Smoother = new PoseSmoother(smoothing) { SmoothHands = !stabilizeHands };
            m_Smoother2 = new PoseSmoother(smoothing) { SmoothHands = !stabilizeHands };
            m_HandStab = new HandStabilizer(handStabilizer);
            m_HandStab2 = new HandStabilizer(handStabilizer);
            StartInput();
            StartSource(source);
            StartHands();
            SetupCharacter();
            SetupPerformer2();
            ApplyVisualMode();
        }

        void StartHands()
        {
            m_Hands?.Dispose();
            m_Hands = null;
            m_Hands2?.Dispose();
            m_Hands2 = null;
            m_Fallback?.Dispose();
            m_Fallback = null;
            m_Fallback2?.Dispose();
            m_Fallback2 = null;
            m_LatestLeftHand.Clear();
            m_LatestRightHand.Clear();
            m_LatestLeftHand2.Clear();
            m_LatestRightHand2.Clear();
            if (!trackHands || handLandmarker == null) return;
            var h = new HandTracker(handLandmarker);
            h.Initialize();
            if (h.IsAvailable) m_Hands = h;
            else { Log("Hands unavailable: " + h.Status); h.Dispose(); return; }
            if (performers > 1)
            {
                var h2 = new HandTracker(handLandmarker);
                h2.Initialize();
                if (h2.IsAvailable) m_Hands2 = h2; else h2.Dispose();
            }
            ApplyHandFallback();
        }

        /// <summary>Attach (or detach) the second hand model to the hand trackers, creating it the first time it is wanted.</summary>
        void ApplyHandFallback()
        {
            if (handFallback && handFallbackModel != null)
            {
                if (m_Hands != null && m_Fallback == null) m_Fallback = CreateFallback();
                if (m_Hands2 != null && m_Fallback2 == null) m_Fallback2 = CreateFallback();
            }
            else if (handFallback && m_Hands != null) Log("Hand fallback model not assigned (handFallbackModel); MediaPipe only.");
            if (m_Hands != null) m_Hands.Fallback = handFallback ? m_Fallback : null;
            if (m_Hands2 != null) m_Hands2.Fallback = handFallback ? m_Fallback2 : null;
        }

        /// <summary>Turn the second hand model on or off at runtime (K).</summary>
        public void SetHandFallback(bool on)
        {
            handFallback = on;
            ApplyHandFallback();
            Log("Hand fallback (RTMPose) " + (HandFallbackActive ? "on" : "off"));
        }

        Spike.RtmHandLandmarker CreateFallback()
        {
            var f = new Spike.RtmHandLandmarker();
            f.Initialize(handFallbackModel, 224);   // runs on the MediaPipe tracker's own 224 px crop tensor
            if (f.IsAvailable) return f;
            Log("Hand fallback unavailable: " + f.Status);
            f.Dispose();
            return null;
        }

        /// <summary>Switch between one and two performers. Two need Maxine, so the source is re-created in multi-person mode.</summary>
        public void SetPerformers(int count)
        {
            count = Mathf.Clamp(count, 1, 2);
            if (count == performers && (count == 1 || Performer2Active)) return;
            performers = count;
            var kind = ActiveSource is MaxinePoseSource || count > 1 ? SourceKind.Maxine
                : ActiveSource is Vmc.VmcPoseSource ? SourceKind.Vmc : SourceKind.BlazePose;
            if (count > 1 && !(ActiveSource is MaxinePoseSource)) Log("Two performers need the Maxine source; switching.");
            StartSource(kind);
            StartHands();
            SetupPerformer2();
            if (count > 1 && !Performer2Active) Log("Second performer unavailable: " + (ActiveSource != null ? ActiveSource.Status : "no source"));
            else Log(count > 1 ? "Two performers" : "One performer");
        }

        /// <summary>Create (or hide) the second performer's avatar objects: its own procedural skeleton and, when chosen, a clone of a character rig.</summary>
        void SetupPerformer2()
        {
            if (m_Performer2Root == null)
            {
                m_Performer2Root = new GameObject("Performer2").transform;
                m_Performer2Root.position = Vector3.zero;
            }
            if (m_Skeleton2 == null && skeletonView != null)
            {
                var go = new GameObject("Skeleton (performer 2)");
                go.transform.SetParent(m_Performer2Root, false);
                m_Skeleton2 = go.AddComponent<ProceduralSkeletonView>();
                // Same look and placement settings as the first skeleton (materials are references, so they copy too).
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(skeletonView), m_Skeleton2);
            }
            // Default second look: a different rig from performer 1's so two people never match, else the bones.
            if (!m_Performer2Chosen)
                CurrentCharacter2 = Slots.Length > 1 ? (CurrentCharacter == 1 ? 0 : 1) : (Slots.Length == 1 ? 0 : -1);
            BuildPerformer2Character();
            m_Performer2Root.gameObject.SetActive(Performer2Active);
            if (m_Skeleton2 != null) m_Skeleton2.gameObject.SetActive(Performer2Active && m_Retargeter2 == null);
            if (m_Char2 != null) m_Char2.SetActive(false); // shown once the second person is tracked
        }

        void BuildPerformer2Character()
        {
            if (m_Char2 != null) { Destroy(m_Char2); m_Char2 = null; }
            m_Retargeter2 = null;
            if (CurrentCharacter2 < 0 || CurrentCharacter2 >= Slots.Length || Slots[CurrentCharacter2] == null) return;
            var slot = Slots[CurrentCharacter2];
            // Clone from the rig's rest pose so the clone's retargeter captures a clean bind.
            if (m_Retargeters.TryGetValue(slot, out var rt) && rt != null) rt.ResetToRest();
            m_Char2 = Instantiate(slot.gameObject, m_Performer2Root);
            m_Char2.name = slot.name + " (performer 2)";
            var cloneSlot = m_Char2.GetComponent<CharacterSlot>();
            if (cloneSlot != null) Destroy(cloneSlot); // not a selectable slot
            var rootWasActive = m_Performer2Root.gameObject.activeSelf;
            m_Performer2Root.gameObject.SetActive(true);
            m_Char2.SetActive(true);
            var animator = m_Char2.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.enabled = false;
                var r = new HumanoidRetargeter(animator) { HeadScale = slot.headScale };
                if (r.IsValid) m_Retargeter2 = r;
            }
            m_Performer2Root.gameObject.SetActive(rootWasActive);
            if (m_Retargeter2 == null) { Destroy(m_Char2); m_Char2 = null; Log(slot.DisplayName + " could not be cloned for performer 2."); }
        }

        /// <summary>Choose what the second performer looks like: a character slot index, or -1 for the bones.</summary>
        public void SelectCharacter2(int index)
        {
            CurrentCharacter2 = index < 0 || index >= Slots.Length ? -1 : index;
            m_Performer2Chosen = true;
            BuildPerformer2Character();
            if (m_Skeleton2 != null) m_Skeleton2.gameObject.SetActive(Performer2Active && m_Retargeter2 == null);
            if (m_Char2 != null) m_Char2.SetActive(false);
            Log("Performer 2: " + (CurrentCharacter2 < 0 ? "Bones" : Slots[CurrentCharacter2].DisplayName));
            CharactersChanged?.Invoke();
        }

        /// <summary>Character choice for a given performer (1 or 2). Bones for performer 1 means the skeleton view.</summary>
        public void SelectCharacterFor(int performer, int index)
        {
            if (performer >= 2) SelectCharacter2(index);
            else if (index < 0) SetVisualMode(VisualMode.Skeleton);
            else SelectCharacter(index);
        }

        void UpdatePerformer2()
        {
            if (!Performer2Active || !(ActiveSource is IMultiPersonSource multi)) return;
            if (!multi.TryGetPerson(1, RawPose2)) return;
            m_Smoother2.SmoothImageJoints(RawPose2);

            if (m_Hands2 != null && m_Vmc == null)
            {
                var now = Time.timeAsDouble;
                if (m_Hands2.TryGetLatest(m_ScratchLeftHand2, m_ScratchRightHand2))
                {
                    if (m_ScratchLeftHand2.Tracked) { m_ScratchLeftHand2.CopyTo(m_LatestLeftHand2); m_LeftHandTime2 = now; }
                    if (m_ScratchRightHand2.Tracked) { m_ScratchRightHand2.CopyTo(m_LatestRightHand2); m_RightHandTime2 = now; }
                }
                if (now - m_LeftHandTime2 < handHoldSeconds) m_LatestLeftHand2.CopyTo(RawPose2.LeftHand);
                if (now - m_RightHandTime2 < handHoldSeconds) m_LatestRightHand2.CopyTo(RawPose2.RightHand);
                if (RawPose2.Tracked && !m_Hands2.IsBusy && ActiveInput.IsReady)
                    m_Hands2.Submit(ActiveInput.Current, RawPose2, now);
            }

            if (mirror) PoseTransforms.Mirror(RawPose2);
            m_Smoother2.Smooth(RawPose2, SmoothedPose2);
            if (!SmoothedPose2.Tracked) m_Smoother2.Reset();
            else m_LastTracked2Time = Time.time;
            m_LastLift2 = LiftFor(RawPose2, SmoothedPose2, m_Floor2, m_LastLift2);
            SmoothedPose2.Lift = m_LastLift2;
            if (stabilizeHands && m_HandStab2 != null) m_HandStab2.Apply(SmoothedPose2, Time.timeAsDouble);

            var showChar = m_Char2 != null && m_Retargeter2 != null;
            if (m_Skeleton2 != null)
            {
                if (m_Skeleton2.gameObject.activeSelf != !showChar) m_Skeleton2.gameObject.SetActive(!showChar);
                if (!showChar) m_Skeleton2.Apply(SmoothedPose2);
            }
            if (showChar)
            {
                var visible = SmoothedPose2.Tracked || Time.time - m_LastTracked2Time < 1.5f;
                if (m_Char2.activeSelf != visible) m_Char2.SetActive(visible);
                if (SmoothedPose2.Tracked) m_Retargeter2.Apply(SmoothedPose2);
            }
        }

        // ---------------------------------------------------------------- performer 1's person

        int m_PerformerSlot;                 // the multi-person slot driving performer 1
        int m_PerformerSlotId = -1;          // the tracker id that was in it, to notice a different person taking the slot
        float m_PerformerLostTime = -1f;     // when performer 1's slot stopped being tracked (-1 = tracked)
        readonly PoseFrame m_SlotScratch = new PoseFrame();

        /// <summary>Slot of the multi-person source driving performer 1 (0 unless the avatar has passed to another person).</summary>
        public int PerformerSlot => m_PerformerSlot;

        /// <summary>
        /// Performer 1's pose for this frame. On a single-person source it is simply the latest pose. On a multi-person
        /// source showing one performer, the avatar stays with the person in its slot while they are tracked; once they
        /// have been missing for <see cref="performerSwitchDelay"/>, or a different person has taken their slot, it passes
        /// to whoever is tracked, and the smoothers and hand trackers forget the previous person so nothing blends between
        /// two bodies. Two performers keep the fixed slot 0 / slot 1 mapping.
        /// </summary>
        bool ReadPerformer1(PoseFrame into)
        {
            if (performers != 1 || !(ActiveSource is IMultiPersonSource multi) || !multi.MultiPersonActive)
            {
                m_PerformerSlot = 0;
                return ActiveSource.TryGetLatest(into);
            }
            var got = multi.TryGetPerson(m_PerformerSlot, into);
            if (got && into.Tracked)
            {
                var id = multi.SlotId(m_PerformerSlot);
                if (m_PerformerSlotId >= 0 && id >= 0 && id != m_PerformerSlotId) OnPerformerChanged();   // a newcomer took the vacated slot
                m_PerformerSlotId = id;
                m_PerformerLostTime = -1f;
                // Drain the other slots so their frames do not pile up (each slot publishes once per run).
                for (var s = 0; s < multi.SlotCount; s++) if (s != m_PerformerSlot) multi.TryGetPerson(s, m_SlotScratch);
                return true;
            }
            if (got && m_PerformerLostTime < 0f) m_PerformerLostTime = Time.time;
            // Missing: look for anyone else, and take them over once the grace has run out.
            var candidate = -1;
            for (var s = 0; s < multi.SlotCount; s++)
            {
                if (s == m_PerformerSlot) continue;
                if (multi.TryGetPerson(s, m_SlotScratch) && m_SlotScratch.Tracked && candidate < 0) { candidate = s; m_SlotScratch.CopyTo(into); }
            }
            if (candidate >= 0 && m_PerformerLostTime >= 0f && Time.time - m_PerformerLostTime >= performerSwitchDelay)
            {
                Log($"Performer 1 passes to the person in slot {candidate}");
                m_PerformerSlot = candidate;
                m_PerformerSlotId = multi.SlotId(candidate);
                m_PerformerLostTime = -1f;
                OnPerformerChanged();
                return true;
            }
            if (candidate >= 0) { into.Clear(); into.Timestamp = Time.timeAsDouble; }   // someone else is there but the grace is still running: performer 1 shows nobody this frame
            return got;
        }

        /// <summary>
        /// This frame's lift from the floor tracker. A pose held through a brief dropout keeps the lift it had, so one
        /// missed frame mid-jump neither drops the figure nor makes the tracker forget the launch.
        /// </summary>
        float LiftFor(PoseFrame raw, PoseFrame smoothed, FloorTracker floor, float lastLift)
        {
            if (!jump) return 0f;
            if (!raw.Tracked && smoothed.Held) return lastLift;
            return floor.Update(raw, Time.deltaTime);
        }

        /// <summary>A different person now drives performer 1: nothing learned from the previous one applies.</summary>
        void OnPerformerChanged()
        {
            m_Hands?.ResetTracking();
            m_HandStab?.Reset();
            m_Smoother?.Reset();
            m_Floor.Reset();
            m_LatestLeftHand.Clear();
            m_LatestRightHand.Clear();
            m_LeftHandTime = m_RightHandTime = -100;
        }

        /// <summary>Re-create only the input (webcam/video/still) from the current public fields.</summary>
        public void RestartInput()
        {
            StartInput();
            ResetTrackingState();
        }

        /// <summary>
        /// Forget everything learned from the previous frames: BlazePose's crop, Maxine's home depth, the hand
        /// trackers' crops (pixel coordinates of the old camera), the hand models and the smoothers. Needed whenever
        /// the camera, its resolution or the scene in front of it changes.
        /// </summary>
        public void ResetTrackingState()
        {
            m_Blaze?.ResetTracking();
            m_Maxine?.ResetDepthReference();
            m_Vmc?.ResetTravelReference();
            m_Hands?.ResetTracking();
            m_Hands2?.ResetTracking();
            m_HandStab?.Reset();
            m_HandStab2?.Reset();
            m_Smoother?.Reset();
            m_Smoother2?.Reset();
            m_Floor.Reset();
            m_Floor2.Reset();
            m_LastLift = m_LastLift2 = 0f;
            m_LatestLeftHand.Clear();
            m_LatestRightHand.Clear();
            m_LatestLeftHand2.Clear();
            m_LatestRightHand2.Clear();
            m_LeftHandTime = m_RightHandTime = m_LeftHandTime2 = m_RightHandTime2 = -100;
            m_PerformerSlot = 0;
            m_PerformerSlotId = -1;
            m_PerformerLostTime = -1f;
        }

        /// <summary>
        /// -video &lt;file&gt;  -videoDir &lt;dir&gt;  -webcam &lt;index&gt;  -camera &lt;name part&gt;  -rotate 0|90|180|270  -source blaze|maxine|vmc|auto  -vmcport &lt;udp port&gt;  -people 1|2  -mirror 0|1  -nohud  -nomenu|-menu  -ghost|-stage  -noidle  -nojump  -fill <0..0.98>  -bodyWidth &lt;px&gt;  -visual skeleton|character|both  -character &lt;name|bones&gt;
        /// </summary>
        void ApplyCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string flag)
            {
                for (var i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            bool Has(string flag) => System.Array.Exists(args, a => string.Equals(a, flag, System.StringComparison.OrdinalIgnoreCase));

            var v = Arg("-video");
            if (!string.IsNullOrEmpty(v)) { input = InputKind.VideoFile; videoPath = v; }
            var dir = Arg("-videoDir");
            if (!string.IsNullOrEmpty(dir)) videoFolder = dir;
            var cam = Arg("-webcam");
            if (!string.IsNullOrEmpty(cam) && int.TryParse(cam, out var camIdx)) { input = InputKind.Webcam; webcamIndex = camIdx; preferExternalCamera = false; }
            var camName = Arg("-camera");
            if (!string.IsNullOrEmpty(camName)) { input = InputKind.Webcam; webcamName = camName; }
            var rot = Arg("-rotate");
            if (!string.IsNullOrEmpty(rot) && int.TryParse(rot, out var rotDeg)) m_RotationOverride = FrameRotator.Normalise(rotDeg);
            var src = Arg("-source");
            if (!string.IsNullOrEmpty(src))
            {
                source = src.ToLowerInvariant() switch { "blaze" => SourceKind.BlazePose, "blazepose" => SourceKind.BlazePose, "maxine" => SourceKind.Maxine, "vmc" => SourceKind.Vmc, _ => SourceKind.Auto };
            }
            var vp = Arg("-vmcport");
            if (!string.IsNullOrEmpty(vp) && int.TryParse(vp, out var vmcPortArg) && vmcPortArg > 0 && vmcPortArg < 65536) vmcPort = vmcPortArg;
            var mir = Arg("-mirror");
            if (!string.IsNullOrEmpty(mir)) mirror = mir != "0" && !mir.Equals("false", System.StringComparison.OrdinalIgnoreCase);
            if (Has("-nohud")) showHud = false;
            if (Has("-nomenu")) m_MenuHiddenByArgs = true;
            if (Has("-menu")) m_MenuShownByArgs = true;
            if (Has("-ghost")) ghostStage = true;
            if (Has("-stage")) ghostStage = false;
            if (Has("-noidle")) idleAttract = false;
            if (Has("-nojump")) jump = false;
            if (Has("-nofallback")) handFallback = false;
            var fill = Arg("-fill");
            if (!string.IsNullOrEmpty(fill) && float.TryParse(fill, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fillValue)) { figureFill = Mathf.Clamp(fillValue, 0f, 0.98f); m_FillFromArgs = true; }
            var bodyW = Arg("-bodyWidth");
            if (!string.IsNullOrEmpty(bodyW) && int.TryParse(bodyW, out var bw)) bodyProcessingWidth = Mathf.Max(0, bw);
            var people = Arg("-people");
            if (!string.IsNullOrEmpty(people) && int.TryParse(people, out var peopleCount)) performers = Mathf.Clamp(peopleCount, 1, 2);
            var ch = Arg("-character");
            if (!string.IsNullOrEmpty(ch)) m_StartCharacter = ch;
            var vis = Arg("-visual");
            if (!string.IsNullOrEmpty(vis))
            {
                visual = vis.ToLowerInvariant() switch { "character" => VisualMode.Character, "both" => VisualMode.Both, _ => VisualMode.Skeleton };
            }
        }

        /// <summary>Find test videos: explicit folder, else TestVideos next to the project/player or two levels up.</summary>
        public void ScanVideos()
        {
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(videoFolder)) candidates.Add(videoFolder);
            var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            candidates.Add(Path.Combine(root, "TestVideos"));
            candidates.Add(Path.Combine(Directory.GetParent(root)?.FullName ?? root, "TestVideos"));
            candidates.Add(Path.Combine(Application.streamingAssetsPath, "TestVideos"));

            foreach (var c in candidates)
            {
                if (!Directory.Exists(c)) continue;
                var files = Directory.GetFiles(c).Where(f => f.EndsWith(".mp4", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mov", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webm", System.StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToArray();
                if (files.Length == 0) continue;
                Videos = files;
                videoFolder = c;
                if (!string.IsNullOrEmpty(videoPath))
                {
                    var idx = System.Array.FindIndex(files, f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(videoPath), System.StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) VideoIndex = idx;
                }
                return;
            }
            Videos = System.Array.Empty<string>();
        }

        public void NextVideo(int step = 1)
        {
            if (Videos.Length == 0) return;
            VideoIndex = ((VideoIndex + step) % Videos.Length + Videos.Length) % Videos.Length;
            videoPath = Videos[VideoIndex];
            input = InputKind.VideoFile;
            RestartInput();
            Log($"Video {VideoIndex + 1}/{Videos.Length}: {Path.GetFileName(videoPath)}");
        }

        public void ToggleWebcamVideo()
        {
            if (input == InputKind.VideoFile)
            {
                if (WebCamTexture.devices.Length == 0) { Log("No webcam devices."); return; }
                input = InputKind.Webcam;
                RestartInput();
                Log("Input: webcam");
            }
            else
            {
                if (Videos.Length == 0) ScanVideos();
                if (Videos.Length == 0) { Log("No test videos found (put mp4s in a TestVideos folder next to the project)."); return; }
                videoPath = Videos[VideoIndex];
                input = InputKind.VideoFile;
                RestartInput();
                Log($"Video {VideoIndex + 1}/{Videos.Length}: {Path.GetFileName(videoPath)}");
            }
        }

        /// <summary>Change the visual mode at runtime.</summary>
        public void SetVisualMode(VisualMode mode)
        {
            visual = mode;
            ApplyVisualMode();
        }

        /// <summary>Pose the character rig directly from a frame (tests and tools). Returns false if there is no valid rig.</summary>
        public bool ApplyPoseToCharacter(PoseFrame pose)
        {
            if (m_Retargeter == null || !m_Retargeter.IsValid) return false;
            m_Retargeter.Apply(pose);
            return true;
        }

        public bool CharacterHasFingers => m_Retargeter != null && m_Retargeter.HasFingers;

        void StartInput()
        {
            ActiveInput?.Dispose();
            switch (input)
            {
                case InputKind.Webcam:
                    if (!m_WebcamChosen)
                    {
                        webcamIndex = ChooseWebcam(WebcamFrameSource.DeviceNames(), webcamIndex, webcamName, preferExternalCamera, preferredCameras);
                        m_WebcamChosen = true;
                    }
                    ActiveInput = new WebcamFrameSource(webcamIndex, webcamWidth, webcamHeight, webcamFps) { Rotation = RotationForCamera(webcamIndex) };
                    break;
                case InputKind.VideoFile:
                    var path = videoPath;
                    if (!Path.IsPathRooted(path)) path = Path.Combine(Application.streamingAssetsPath, path);
                    ActiveInput = new VideoFileFrameSource(path, gameObject);
                    break;
                case InputKind.StillImage:
                    ActiveInput = new StillImageFrameSource(stillImage, stillImage != null ? stillImage.name : "none");
                    break;
            }
            ActiveInput.Start();
            Log("Input: " + ActiveInput.Name);
        }

        bool m_WebcamChosen;

        /// <summary>
        /// Which camera to open: a name match if given, else the first of the preferred cameras that is plugged in, else
        /// (with the external preference) the first physical external camera, then the integrated one, else the requested
        /// index. Virtual cameras are never picked by the heuristics: the Elgato Virtual Camera used to be chosen as "the
        /// first external camera" ahead of the install camera, and it only relays some other app's picture.
        /// </summary>
        public static int ChooseWebcam(string[] names, int requested, string wanted, bool preferExternal, string preferred = null)
        {
            if (names == null || names.Length == 0) return 0;
            var byName = IndexOfCamera(names, wanted);
            if (byName >= 0) return byName;
            if (!string.IsNullOrEmpty(preferred))
            {
                foreach (var part in preferred.Split(','))
                {
                    var i = IndexOfCamera(names, part.Trim());
                    if (i >= 0) return i;
                }
            }
            if (preferExternal)
            {
                var external = System.Array.FindIndex(names, n => n != null && !IsIntegratedCamera(n) && !IsVirtualCamera(n));
                if (external >= 0) return external;
                var integrated = System.Array.FindIndex(names, n => n != null && IsIntegratedCamera(n));
                if (integrated >= 0) return integrated;
            }
            return Mathf.Clamp(requested, 0, names.Length - 1);
        }

        /// <summary>Index of the first camera whose name contains <paramref name="part"/> (case-insensitive), or -1.</summary>
        static int IndexOfCamera(string[] names, string part)
        {
            if (string.IsNullOrEmpty(part)) return -1;
            return System.Array.FindIndex(names, n => n != null && n.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsIntegratedCamera(string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(name, "integrated|built-?in|internal|facetime|laptop|notebook", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Software cameras that relay another app's picture (Elgato Virtual Camera, OBS, NVIDIA Broadcast, ...).</summary>
        public static bool IsVirtualCamera(string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(name, @"virtual|\bobs\b|broadcast|snap camera|xsplit|manycam|streamlabs", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Rotation set from the command line this run, overriding remembered and default values.</summary>
        int? m_RotationOverride;

        static string CameraName(int index)
        {
            var names = WebcamFrameSource.DeviceNames();
            return index >= 0 && index < names.Length ? names[index] : "";
        }

        /// <summary>
        /// Rotation to use for a camera: the -rotate override, else what O last saved for this camera name,
        /// else the external/integrated default.
        /// </summary>
        public int RotationForCamera(int index)
        {
            if (m_RotationOverride.HasValue) return m_RotationOverride.Value;
            var name = CameraName(index);
            if (!string.IsNullOrEmpty(name) && PlayerPrefs.HasKey("rotation:" + name)) return FrameRotator.Normalise(PlayerPrefs.GetInt("rotation:" + name));
            return FrameRotator.Normalise(!string.IsNullOrEmpty(name) && !IsIntegratedCamera(name) ? externalCameraRotation : cameraRotation);
        }

        /// <summary>Current camera's rotation on screen, degrees counter-clockwise.</summary>
        public int CurrentCameraRotation => ActiveInput is WebcamFrameSource cam ? cam.Rotation : 0;

        /// <summary>Set the current camera's rotation, remember it for that camera, and forget the old frame geometry.</summary>
        public void SetCameraRotation(int degrees)
        {
            var rotation = FrameRotator.Normalise(degrees);
            if (ActiveInput is WebcamFrameSource cam)
            {
                cam.Rotation = rotation;
                var name = CameraName(webcamIndex);
                if (!string.IsNullOrEmpty(name))
                {
                    PlayerPrefs.SetInt("rotation:" + name, rotation);
                    PlayerPrefs.Save();
                }
            }
            m_RotationOverride = null;
            ResetTrackingState();
            Log("Camera rotation " + rotation + " (remembered for this camera)");
        }

        BlazePoseSource CreateBlaze(BlazePoseSource.Variant variant)
        {
            var landmarker = variant switch
            {
                BlazePoseSource.Variant.Lite => blazeLandmarkerLite,
                BlazePoseSource.Variant.Heavy => blazeLandmarkerHeavy,
                _ => blazeLandmarkerFull,
            };
            if (landmarker == null) landmarker = blazeLandmarkerFull != null ? blazeLandmarkerFull : blazeLandmarkerLite;
            var s = new BlazePoseSource(blazeDetector, landmarker, blazeAnchors, variant);
            s.Initialize();
            return s;
        }

        void StartSource(SourceKind kind)
        {
            ActiveSource?.Dispose();
            ActiveSource = null;
            m_Blaze = null;
            m_Maxine = null;
            m_Vmc = null;

            if (kind == SourceKind.Vmc)
            {
                // External tracker (XR Animator etc.) streaming VMC over UDP; it also supplies the fingers.
                var vmc = new Vmc.VmcPoseSource(vmcPort);
                vmc.Initialize();
                if (vmc.IsAvailable)
                {
                    m_Vmc = vmc;
                    ActiveSource = vmc;
                }
                else
                {
                    Log("VMC unavailable: " + vmc.Status + " — falling back to BlazePose.");
                    vmc.Dispose();
                }
            }

            if (ActiveSource == null && (kind == SourceKind.Maxine || kind == SourceKind.Auto))
            {
                var mx = new MaxinePoseSource { Mode = maxineQuality, MaxPeople = stableIdentity ? Mathf.Max(2, performers) : performers };
                mx.Initialize();
                if (mx.IsAvailable)
                {
                    m_Maxine = mx;
                    ActiveSource = mx;
                }
                else
                {
                    m_MaxineUnavailable = true;
                    Log("Maxine unavailable: " + mx.Status);
                    mx.Dispose();
                    if (kind == SourceKind.Maxine) Log("Falling back to BlazePose.");
                }
            }

            if (ActiveSource == null)
            {
                m_Blaze = CreateBlaze(blazeVariant);
                ActiveSource = m_Blaze;
            }

            m_Smoother.Reset();
            Log("Source: " + ActiveSource.Name + " — " + ActiveSource.Status);
        }

        /// <summary>Switchable characters under <see cref="characterRoot"/>, one <see cref="CharacterSlot"/> each.</summary>
        public CharacterSlot[] Slots { get; private set; } = System.Array.Empty<CharacterSlot>();
        /// <summary>Index into <see cref="Slots"/> of the character being driven, or -1.</summary>
        public int CurrentCharacter { get; private set; } = -1;
        public CharacterSlot CurrentSlot => CurrentCharacter >= 0 && CurrentCharacter < Slots.Length ? Slots[CurrentCharacter] : null;
        /// <summary>Raised when the slot list is rebuilt or another character is selected.</summary>
        public event System.Action CharactersChanged;
        readonly System.Collections.Generic.Dictionary<CharacterSlot, HumanoidRetargeter> m_Retargeters = new System.Collections.Generic.Dictionary<CharacterSlot, HumanoidRetargeter>();

        void SetupCharacter()
        {
            m_Retargeter = null;
            m_CharacterAnimator = null;
            if (characterRoot == null)
            {
                Slots = System.Array.Empty<CharacterSlot>();
                CurrentCharacter = -1;
                CharactersChanged?.Invoke();
                return;
            }

            var slots = characterRoot.GetComponentsInChildren<CharacterSlot>(true);
            if (slots.Length == 0)
            {
                // Older scene with a bare rig under CharacterRoot: wrap it in a slot at runtime.
                var animator = characterRoot.GetComponentInChildren<Animator>(true);
                if (animator != null)
                {
                    var t = animator.transform;
                    while (t.parent != null && t.parent != characterRoot) t = t.parent;
                    var slot = t.gameObject.AddComponent<CharacterSlot>();
                    slot.displayName = t.name;
                    slot.headScale = characterHeadScale;
                    slots = new[] { slot };
                }
            }
            Slots = slots;
            foreach (var stale in new System.Collections.Generic.List<CharacterSlot>(m_Retargeters.Keys))
                if (stale == null || System.Array.IndexOf(slots, stale) < 0) m_Retargeters.Remove(stale);

            // Keep whichever slot the scene left active, else the first one; keep the current visual mode.
            var active = System.Array.FindIndex(slots, s => s.gameObject.activeSelf);
            var index = active >= 0 ? active : (slots.Length > 0 ? 0 : -1);
            CurrentCharacter = -1;
            if (index >= 0) SelectCharacter(index, keepVisual: true);
            else CharactersChanged?.Invoke();
        }

        /// <summary>
        /// Show character <paramref name="index"/>, hide the others and drive it from the pose. Switches the view
        /// to Character mode unless <paramref name="keepVisual"/> is set or the view already shows characters.
        /// </summary>
        public bool SelectCharacter(int index, bool keepVisual = false)
        {
            if (index < 0 || index >= Slots.Length || Slots[index] == null) return false;
            var slot = Slots[index];
            for (var i = 0; i < Slots.Length; i++)
            {
                if (i == index || Slots[i] == null || !Slots[i].gameObject.activeSelf) continue;
                if (m_Retargeters.TryGetValue(Slots[i], out var old) && old != null) old.ResetToRest();
                Slots[i].gameObject.SetActive(false);
            }

            // Binding reads bone transforms through the Animator, which needs the hierarchy active.
            var rootWasActive = characterRoot.gameObject.activeSelf;
            if (!rootWasActive) characterRoot.gameObject.SetActive(true);
            slot.gameObject.SetActive(true);
            if (!m_Retargeters.TryGetValue(slot, out var rt) || rt == null)
            {
                var animator = slot.Animator;
                if (animator != null) animator.enabled = false; // we drive the bones directly
                rt = animator != null ? new HumanoidRetargeter(animator) : null;
                if (rt == null || !rt.IsValid) Log(slot.DisplayName + " is not a valid Humanoid rig.");
                m_Retargeters[slot] = rt;
            }
            m_Retargeter = rt;
            m_CharacterAnimator = slot.Animator;
            if (m_Retargeter != null) m_Retargeter.HeadScale = slot.headScale;
            CurrentCharacter = index;
            if (!keepVisual && visual == VisualMode.Skeleton) visual = VisualMode.Character;
            ApplyVisualMode();
            if (!keepVisual) Log("Character: " + slot.DisplayName);
            CharactersChanged?.Invoke();
            return true;
        }

        public void NextCharacter(int step = 1)
        {
            if (Slots.Length == 0) return;
            var next = ((CurrentCharacter + step) % Slots.Length + Slots.Length) % Slots.Length;
            SelectCharacter(next);
        }

        /// <summary>Select a character by (partial, case-insensitive) display or object name; "bones" shows the procedural skeleton.</summary>
        public bool SelectCharacterByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Equals("bones", System.StringComparison.OrdinalIgnoreCase) || name.Equals("skeleton", System.StringComparison.OrdinalIgnoreCase) && Slots.Length == 0)
            {
                SetVisualMode(VisualMode.Skeleton);
                return true;
            }
            var idx = System.Array.FindIndex(Slots, s => s != null && (s.DisplayName.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0 || s.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0));
            return idx >= 0 && SelectCharacter(idx);
        }

        void ApplyVisualMode()
        {
            var showSkeleton = visual == VisualMode.Skeleton || visual == VisualMode.Both || !CharacterReady;
            var showCharacter = (visual == VisualMode.Character || visual == VisualMode.Both) && CharacterReady;
            var sideBySide = showSkeleton && showCharacter;
            if (skeletonView != null)
            {
                skeletonView.gameObject.SetActive(showSkeleton);
                skeletonView.transform.localPosition = sideBySide ? new Vector3(-1.1f, 0f, 0f) : Vector3.zero;
            }
            if (characterRoot != null)
            {
                characterRoot.gameObject.SetActive(showCharacter);
                characterRoot.localPosition = sideBySide ? new Vector3(1.1f, 0f, 0f) : Vector3.zero;
            }
        }

        void Update()
        {
            HandleHotkeys();
            if (ActiveInput == null || ActiveSource == null) return;

            ActiveInput.Update();

            // Feed the new camera frame first, then collect: Maxine reads the frame back synchronously and runs on it when
            // polled, so the pose shown (and the hand crops cut from this same frame) are for the newest picture rather than
            // the previous one. Asynchronous sources simply deliver whatever finished.
            if (ActiveInput.IsReady && ActiveInput.HasNewFrame && !ActiveSource.IsBusy)
            {
                ActiveInput.Consume();
                m_SubmitTime = Time.realtimeSinceStartupAsDouble;
                ActiveSource.Submit(BodyFrame(ActiveInput.Current), Time.timeAsDouble);
            }
            var gotPose = ReadPerformer1(RawPose);

            if (gotPose)
            {
                LastInferenceMs = (float)((Time.realtimeSinceStartupAsDouble - m_SubmitTime) * 1000.0);
                m_InferenceCount++;
                if (Time.time - m_FpsWindowStart >= 1f)
                {
                    InferenceFps = m_InferenceCount / (Time.time - m_FpsWindowStart);
                    m_InferenceCount = 0;
                    m_FpsWindowStart = Time.time;
                }

                // Steady the image-space joints first (MediaPipe-style, scaled by the person's size): the hand crops
                // and the HUD dots are placed from them.
                m_Smoother.SmoothImageJoints(RawPose);

                // Hands: collect the last finished result, attach it to this body pose (both still unmirrored),
                // then start the next hand pass on the current frame using this pose's wrist positions.
                // A VMC sender delivers its own fingers inside the frame, so the in-process tracker stays out of the way.
                if (m_Hands != null && m_Vmc == null)
                {
                    // Each hand keeps its last good landmarks through brief misses, so a single low-presence frame
                    // doesn't snap the fingers back to the fork.
                    var now = Time.timeAsDouble;
                    if (m_Hands.TryGetLatest(m_ScratchLeftHand, m_ScratchRightHand))
                    {
                        if (m_ScratchLeftHand.Tracked) { m_ScratchLeftHand.CopyTo(m_LatestLeftHand); m_LeftHandTime = now; }
                        if (m_ScratchRightHand.Tracked) { m_ScratchRightHand.CopyTo(m_LatestRightHand); m_RightHandTime = now; }
                    }
                    if (now - m_LeftHandTime < handHoldSeconds) m_LatestLeftHand.CopyTo(RawPose.LeftHand);
                    if (now - m_RightHandTime < handHoldSeconds) m_LatestRightHand.CopyTo(RawPose.RightHand);
                    if (RawPose.Tracked && !m_Hands.IsBusy && ActiveInput.IsReady)
                        m_Hands.Submit(ActiveInput.Current, RawPose, now);
                }

                if (mirror) PoseTransforms.Mirror(RawPose);
                // A brief tracking loss keeps the last smoothed pose moving (PoseSmoother holds it); a long one resets.
                m_Smoother.Smooth(RawPose, SmoothedPose);
                if (!SmoothedPose.Tracked) m_Smoother.Reset();
                m_LastLift = LiftFor(RawPose, SmoothedPose, m_Floor, m_LastLift);
                SmoothedPose.Lift = m_LastLift;
                // Anatomical hand model on the smoothed body: steady fingers, and a hand that stays when the detector drops out.
                if (stabilizeHands && m_HandStab != null) m_HandStab.Apply(SmoothedPose, Time.timeAsDouble);

                if (skeletonView != null && skeletonView.gameObject.activeSelf) skeletonView.Apply(SmoothedPose);
                if (m_Retargeter != null && characterRoot != null && characterRoot.gameObject.activeSelf) m_Retargeter.Apply(SmoothedPose);

                // The same SDK run delivered the second person's slot, if there is one.
                UpdatePerformer2();
            }

            if (gotPose && SmoothedPose.Tracked) m_LastLiveTime = Time.time;
            if (Performer2Active && SmoothedPose2.Tracked) m_LastLiveTime = Time.time;
            UpdateIdle();
            FallBackFromDeadWebcam();
        }

        readonly System.Collections.Generic.HashSet<int> m_DeadWebcams = new System.Collections.Generic.HashSet<int>();
        float m_WebcamOpenedAt;

        /// <summary>A camera that delivers no frames for 6 s (busy in another app, unplugged mid-run) is swapped for the next one.</summary>
        void FallBackFromDeadWebcam()
        {
            if (input != InputKind.Webcam || !(ActiveInput is WebcamFrameSource cam)) return;
            if (cam.FrameCount > 0) { m_WebcamOpenedAt = Time.realtimeSinceStartup; return; }
            if (m_WebcamOpenedAt <= 0f) { m_WebcamOpenedAt = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - m_WebcamOpenedAt < 6f) return;
            var count = WebCamTexture.devices.Length;
            m_DeadWebcams.Add(webcamIndex);
            if (m_DeadWebcams.Count >= count) { m_WebcamOpenedAt = Time.realtimeSinceStartup; return; } // nothing else to try; keep waiting
            var next = webcamIndex;
            for (var i = 1; i < count; i++)
            {
                var candidate = (webcamIndex + i) % count;
                if (!m_DeadWebcams.Contains(candidate)) { next = candidate; break; }
            }
            if (next == webcamIndex) return;
            Log($"Camera {webcamIndex} delivered no frames; switching to camera {next}");
            webcamIndex = next;
            StartInput();
            ResetTrackingState();
            m_WebcamOpenedAt = Time.realtimeSinceStartup;
        }

        /// <summary>Downscaled copy of the camera frame for the body source when the camera is bigger than needed.</summary>
        Texture BodyFrame(Texture src)
        {
            if (src == null || bodyProcessingWidth <= 0 || src.width <= bodyProcessingWidth) return src;
            var h = Mathf.Max(16, Mathf.RoundToInt(src.height * (bodyProcessingWidth / (float)src.width)));
            if (m_BodyRt == null || m_BodyRt.width != bodyProcessingWidth || m_BodyRt.height != h)
            {
                if (m_BodyRt != null) { m_BodyRt.Release(); Destroy(m_BodyRt); }
                m_BodyRt = new RenderTexture(bodyProcessingWidth, h, 0, RenderTextureFormat.ARGB32) { name = "BodyFrame" };
                m_BodyRt.Create();
            }
            Graphics.Blit(src, m_BodyRt);
            return m_BodyRt;
        }

        // ---------------------------------------------------------------- stage

        /// <summary>Black stage on/off (projector or Pepper's ghost): hides the ground and moon, drops fog and grain.</summary>
        public void SetGhostStage(bool on)
        {
            ghostStage = on;
            ApplyStage();
            Log(on ? "Ghost stage: black" : "Stage: graveyard");
        }

        /// <summary>
        /// Point the main camera so a standing figure (characters are normalised to 1.75 m) fills <see cref="figureFill"/> of
        /// the view height, with a little floor under the feet and most of the spare room above for raised arms and jumps,
        /// then move the figure on screen by <see cref="frameShift"/>: sideways by panning the camera (world +X is
        /// screen-left, so the camera moves with the shift), vertically by aiming lower to move the figure up.
        /// </summary>
        public void FrameStage()
        {
            var cam = Camera.main;
            if (cam == null || figureFill < 0.05f) return;
            const float figureHeight = 1.75f;
            var viewHeight = figureHeight / Mathf.Clamp(figureFill, 0.3f, 0.98f);
            var spare = viewHeight - figureHeight;
            var bottom = -0.25f * spare;
            var centreY = bottom + 0.5f * viewHeight;
            var position = cam.transform.position;
            position.x = frameShift.x;
            cam.transform.position = position;
            var distance = Mathf.Max(0.5f, Mathf.Abs(position.z));
            cam.fieldOfView = 2f * Mathf.Atan(0.5f * viewHeight / distance) * Mathf.Rad2Deg;
            cam.transform.LookAt(new Vector3(frameShift.x, centreY - frameShift.y, 0f));
        }

        /// <summary>Saved framing from the last session; a -fill on the command line wins for this run without being saved.</summary>
        void LoadFraming()
        {
            if (!m_FillFromArgs && PlayerPrefs.HasKey("framing:fill")) figureFill = Mathf.Clamp(PlayerPrefs.GetFloat("framing:fill"), 0.3f, 0.98f);
            frameShift = new Vector2(PlayerPrefs.GetFloat("framing:shiftX", 0f), PlayerPrefs.GetFloat("framing:shiftY", 0f));
        }

        /// <summary>Zoom (fill 0.3..0.98) and move the figure on screen (metres right/up); saved so the install keeps it.</summary>
        public void SetFraming(float fill, Vector2 shift, bool save = true)
        {
            figureFill = Mathf.Clamp(fill, 0.3f, 0.98f);
            frameShift = new Vector2(Mathf.Clamp(shift.x, -2f, 2f), Mathf.Clamp(shift.y, -2f, 2f));
            FrameStage();
            if (save)
            {
                PlayerPrefs.SetFloat("framing:fill", figureFill);
                PlayerPrefs.SetFloat("framing:shiftX", frameShift.x);
                PlayerPrefs.SetFloat("framing:shiftY", frameShift.y);
                PlayerPrefs.Save();
            }
            Log($"Framing: fill {figureFill:0.00}, figure {frameShift.x:+0.00;-0.00} m right, {frameShift.y:+0.00;-0.00} m up");
        }

        public void ResetFraming() => SetFraming(k_DefaultFill, Vector2.zero);

        /// <summary>True on the key's press, then again every 80 ms while it stays held after 0.4 s, like a text cursor.</summary>
        bool KeyStep(KeyCode key)
        {
            if (Input.GetKeyDown(key)) { m_KeyRepeat[key] = Time.unscaledTime + 0.4f; return true; }
            if (Input.GetKey(key) && m_KeyRepeat.TryGetValue(key, out var next) && Time.unscaledTime >= next) { m_KeyRepeat[key] = Time.unscaledTime + 0.08f; return true; }
            return false;
        }

        void ApplyStage()
        {
            if (m_Ground != null) m_Ground.SetActive(!ghostStage);
            if (m_Moon != null) m_Moon.SetActive(!ghostStage);
            RenderSettings.fog = !ghostStage;
            if (m_Volume != null && m_Volume.profile != null)
            {
                // .profile (not sharedProfile) so the asset on disk is left alone.
                if (m_Volume.profile.TryGet<UnityEngine.Rendering.Universal.FilmGrain>(out var grain)) grain.active = !ghostStage;
                if (m_Volume.profile.TryGet<UnityEngine.Rendering.Universal.Vignette>(out var vignette)) vignette.active = !ghostStage;
            }
        }

        // ---------------------------------------------------------------- attract loop

        void UpdateIdle()
        {
            var wantIdle = idleAttract && Time.time - m_LastLiveTime > idleDelay;
            if (!wantIdle)
            {
                IdleActive = false;
                return;
            }
            if (!IdleActive)
            {
                IdleActive = true;
                m_IdleStart = Time.time;
                m_Smoother.Reset();
            }
            BuildIdlePose(Time.time - m_IdleStart, m_IdlePose);
            if (skeletonView != null && skeletonView.gameObject.activeSelf) skeletonView.Apply(m_IdlePose);
            if (m_Retargeter != null && characterRoot != null && characterRoot.gameObject.activeSelf) m_Retargeter.Apply(m_IdlePose);
        }

        /// <summary>A standing skeleton that breathes, sways, looks around and waves now and then.</summary>
        public void BuildIdlePose(float t, PoseFrame pose)
        {
            StandingPose.Fill(pose);
            var breathe = 0.008f * Mathf.Sin(t * Mathf.PI * 2f / 4.2f);
            var sway = 0.03f * Mathf.Sin(t * Mathf.PI * 2f / 7.3f);
            var headTurn = 22f * Mathf.Sin(t * Mathf.PI * 2f / 9.1f);
            var head = pose[HumanJoint.Head];
            var turn = Quaternion.Euler(0f, headTurn, 0f);
            for (var i = 0; i < JointTopology.Count; i++)
            {
                var j = (HumanJoint)i;
                var p = pose.Joints[i];
                if (p.y > 0.05f) p.x += sway * Mathf.Clamp01(p.y / 0.6f);            // lean from the hips
                if (p.y > 0.3f) p.y += breathe * Mathf.Clamp01((p.y - 0.3f) / 0.3f);   // chest rises
                if (j == HumanJoint.Nose || j == HumanJoint.LeftEye || j == HumanJoint.RightEye || j == HumanJoint.LeftEar || j == HumanJoint.RightEar)
                    p = head + new Vector3(sway, breathe, 0f) + turn * (pose.Joints[i] - head);
                pose.Joints[i] = p;
            }

            // A three-second wave with the right hand (the audience's left when unmirrored) as the loop starts, then every ten seconds.
            var cycle = Mathf.Repeat(t, 10f);
            var wave = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(cycle / 0.6f)) * Mathf.SmoothStep(1f, 0f, Mathf.Clamp01((cycle - 2.4f) / 0.6f));
            if (wave > 0.001f)
            {
                var swing = 0.09f * Mathf.Sin(t * Mathf.PI * 2f / 0.9f);
                var elbow = Vector3.Lerp(pose[HumanJoint.RightElbow], new Vector3(0.36f + sway, 0.46f, 0.08f), wave);
                var wrist = Vector3.Lerp(pose[HumanJoint.RightWrist], new Vector3(0.42f + sway + swing, 0.74f, 0.12f), wave);
                var up = (wrist - elbow).normalized;
                pose[HumanJoint.RightElbow] = elbow;
                pose[HumanJoint.RightWrist] = wrist;
                pose[HumanJoint.RightIndex] = wrist + up * 0.085f + new Vector3(-0.02f, 0f, 0f);
                pose[HumanJoint.RightPinky] = wrist + up * 0.075f + new Vector3(0.03f, 0f, 0f);
                pose[HumanJoint.RightHandTip] = pose[HumanJoint.RightIndex];
                pose[HumanJoint.RightThumb] = wrist + up * 0.05f + new Vector3(-0.05f, 0f, 0.03f);
            }

            IdleHand(pose, true);
            IdleHand(pose, false);
            pose.Tracked = true;
            pose.Score = 1f;
            pose.Timestamp = Time.timeAsDouble;
        }

        /// <summary>Relaxed template fingers on an idle hand, oriented from the pose's wrist and knuckles.</summary>
        void IdleHand(PoseFrame pose, bool right)
        {
            var w = pose[right ? HumanJoint.RightWrist : HumanJoint.LeftWrist];
            var index = pose[right ? HumanJoint.RightIndex : HumanJoint.LeftIndex];
            var pinky = pose[right ? HumanJoint.RightPinky : HumanJoint.LeftPinky];
            var forward = 0.5f * (index + pinky) - w;
            var palmar = (right ? 1f : -1f) * Vector3.Cross(pinky - w, index - w);
            if (forward.sqrMagnitude < 1e-6f || palmar.sqrMagnitude < 1e-8f) return;
            var palm = Quaternion.LookRotation(forward.normalized, palmar.normalized);
            HandStabilizer.TemplateHand(right, palm, 0.09f, m_IdleHand, 18f);
            var hand = right ? pose.RightHand : pose.LeftHand;
            System.Array.Copy(m_IdleHand, hand.Landmarks, HandFrame.LandmarkCount);
            hand.Tracked = true;
            hand.Score = 1f;
            hand.HasImageLandmarks = false;
            hand.Timestamp = Time.timeAsDouble;
        }

        void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
            if (Input.GetKeyDown(KeyCode.H)) showHud = !showHud;
            if (Input.GetKeyDown(KeyCode.M)) { mirror = !mirror; m_Smoother.Reset(); Log("Mirror " + (mirror ? "on" : "off")); }
            if (Input.GetKeyDown(KeyCode.Alpha1)) { visual = VisualMode.Skeleton; ApplyVisualMode(); }
            if (Input.GetKeyDown(KeyCode.Alpha2)) { visual = VisualMode.Character; ApplyVisualMode(); }
            if (Input.GetKeyDown(KeyCode.Alpha3)) { visual = VisualMode.Both; ApplyVisualMode(); }
            if (Input.GetKeyDown(KeyCode.F)) Screen.fullScreen = !Screen.fullScreen;
            if (Input.GetKeyDown(KeyCode.R)) { ResetTrackingState(); Log("Tracking reset"); }
            if (Input.GetKeyDown(KeyCode.Y))
            {
                stabilizeHands = !stabilizeHands;
                m_Smoother.SmoothHands = !stabilizeHands;
                if (m_Smoother2 != null) m_Smoother2.SmoothHands = !stabilizeHands;
                m_HandStab?.Reset();
                m_HandStab2?.Reset();
                Log("Steady hands " + (stabilizeHands ? "on" : "off"));
            }
            if (Input.GetKeyDown(KeyCode.S)) StartSource(NextSourceKind());
            if (Input.GetKeyDown(KeyCode.V) && m_Blaze != null)
            {
                blazeVariant = (BlazePoseSource.Variant)(((int)blazeVariant + 1) % 3);
                StartSource(SourceKind.BlazePose);
            }
            if (Input.GetKeyDown(KeyCode.C) && input == InputKind.Webcam)
            {
                var count = WebCamTexture.devices.Length;
                if (count > 1)
                {
                    webcamIndex = (webcamIndex + 1) % count;
                    StartInput();
                    ResetTrackingState();
                    Log("Camera: " + (ActiveInput != null ? ActiveInput.Name : "?"));
                }
            }
            if (Input.GetKeyDown(KeyCode.LeftBracket)) AdjustSmoothing(-1);
            if (Input.GetKeyDown(KeyCode.RightBracket)) AdjustSmoothing(+1);
            if (Input.GetKeyDown(KeyCode.G)) { trackHands = !trackHands; StartHands(); Log("Hand tracking " + (trackHands ? "on" : "off")); }
            if (Input.GetKeyDown(KeyCode.K)) SetHandFallback(!handFallback);
            if (Input.GetKeyDown(KeyCode.P)) ToggleWebcamVideo();
            if (Input.GetKeyDown(KeyCode.N)) NextVideo(+1);
            if (Input.GetKeyDown(KeyCode.B)) NextVideo(-1);
            if (Input.GetKeyDown(KeyCode.Tab)) NextCharacter(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : +1);
            if (Input.GetKeyDown(KeyCode.U) && handMenu != null) { handMenu.visible = !handMenu.visible; Log("Hand menu " + (handMenu.visible ? "on" : "off")); }
            if (Input.GetKeyDown(KeyCode.T)) SetPerformers(performers == 1 ? 2 : 1);
            if (Input.GetKeyDown(KeyCode.X)) SetGhostStage(!ghostStage);
            if (Input.GetKeyDown(KeyCode.O)) SetCameraRotation(CurrentCameraRotation + 90);
            if (Input.GetKeyDown(KeyCode.I)) { idleAttract = !idleAttract; Log("Attract loop " + (idleAttract ? "on" : "off")); }

            // Framing: - and = zoom, the arrows move the figure on screen, 0 resets; held keys repeat.
            const float zoomStep = 0.02f, moveStep = 0.05f;
            if (KeyStep(KeyCode.Minus) || KeyStep(KeyCode.KeypadMinus)) SetFraming(figureFill - zoomStep, frameShift);
            if (KeyStep(KeyCode.Equals) || KeyStep(KeyCode.Plus) || KeyStep(KeyCode.KeypadPlus)) SetFraming(figureFill + zoomStep, frameShift);
            if (KeyStep(KeyCode.UpArrow)) SetFraming(figureFill, frameShift + Vector2.up * moveStep);
            if (KeyStep(KeyCode.DownArrow)) SetFraming(figureFill, frameShift - Vector2.up * moveStep);
            if (KeyStep(KeyCode.RightArrow)) SetFraming(figureFill, frameShift + Vector2.right * moveStep);
            if (KeyStep(KeyCode.LeftArrow)) SetFraming(figureFill, frameShift - Vector2.right * moveStep);
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)) ResetFraming();
        }

        /// <summary>S cycles BlazePose -> Maxine -> VMC -> BlazePose, skipping Maxine once it has failed to start.</summary>
        SourceKind NextSourceKind()
        {
            if (ActiveSource is BlazePoseSource) return m_MaxineUnavailable ? SourceKind.Vmc : SourceKind.Maxine;
            if (ActiveSource is MaxinePoseSource) return SourceKind.Vmc;
            return SourceKind.BlazePose;
        }

        void AdjustSmoothing(int dir)
        {
            smoothing.minCutoff = Mathf.Clamp(smoothing.minCutoff * (dir > 0 ? 1.25f : 0.8f), 0.2f, 10f);
            m_Smoother.ApplySettings(smoothing);
            Log($"Smoothing min cutoff {smoothing.minCutoff:0.00} Hz");
        }

        void Log(string msg)
        {
            LastMessage = msg;
            if (logToConsole) Debug.Log("[MimicMocap] " + msg);
        }

        void OnDestroy()
        {
            ActiveSource?.Dispose();
            ActiveInput?.Dispose();
            m_Hands?.Dispose();
            m_Hands2?.Dispose();
            m_Fallback?.Dispose();
            m_Fallback2?.Dispose();
            if (m_BodyRt != null) { m_BodyRt.Release(); Destroy(m_BodyRt); }
        }
    }
}
