using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MimicMocap.Maxine
{
    /// <summary>
    /// NVIDIA Maxine AR SDK "BodyPoseEstimation" (34 keypoints, 3D, joint angles) via P/Invoke.
    /// Frames are read back from the GPU asynchronously (one frame of latency), converted RGBA->BGR by the SDK
    /// on the GPU, then run through the TensorRT engine; the SDK call itself takes a few milliseconds on an RTX GPU.
    /// If the SDK DLLs are not installed, <see cref="IsAvailable"/> is false and nothing else is touched.
    ///
    /// With <see cref="MaxPeople"/> above 1 the SDK's multi-person tracker is requested (batch 8, temporal smoothing
    /// off, tracking ids); people are then exposed through stable slots via <see cref="IMultiPersonSource"/>.
    /// If the installed SDK refuses that mode, the source silently runs single-person as before.
    /// </summary>
    public sealed class MaxinePoseSource : IPoseSource, IMultiPersonSource
    {
        const int k_MaxBoxes = 25;
        const int k_MultiBatch = 8;
        /// <summary>Frames a slot keeps its id after the tracker stops reporting it.</summary>
        const int k_SlotHoldFrames = 45;
        /// <summary>
        /// A tracker id is only taken back by its slot if the person's box is within a plausible distance of where the slot's
        /// person was last seen: <see cref="k_StepPerFrame"/> of the frame per frame missing, at most
        /// <see cref="k_ReturnDistance"/> (normalised image units). The SDK's tracker re-identifies lost tracks loosely: on the
        /// two-person install footage it handed a man's id to the woman entering at the other edge, and later moved it back to
        /// him while both were in view, so performer 1 hopped 1.4 m across the stage between two people in one frame.
        /// </summary>
        const float k_StepPerFrame = 0.15f;
        const float k_ReturnDistance = 0.3f;

        /// <summary>SDK Config(Mode): 0 = high quality model, 1 = high performance model (per the BodyTrack sample usage).</summary>
        public enum Quality { HighQuality = 0, Performance = 1 }

        public Quality Mode = Quality.HighQuality;
        public bool Temporal = true;
        public bool UseCudaGraph = true;
        /// <summary>
        /// Wait for the frame's GPU readback in Submit instead of picking it up a render frame later, so the pose for a camera
        /// frame is ready in the same Update (about 17 ms sooner at 60 fps, and the hand crops are cut from the frame the pose
        /// came from). Costs a short CPU wait on the GPU copy.
        /// </summary>
        public bool SynchronousReadback = true;
        /// <summary>1 = single person (temporal smoothing on). 2..8 = ask for the SDK's multi-person tracker.</summary>
        public int MaxPeople = 1;
        /// <summary>Focal length in pixels for a 1280-wide image; scaled to the actual width.</summary>
        public float FocalLengthAt1280 = 800.79041f;
        public float ConfidenceScale = 1f;
        /// <summary>
        /// Axis signs from SDK camera space (x right, y down, z away from camera, mm) to the canonical frame
        /// (+X subject's right, +Y up, +Z toward camera). Verified against the SDK's own sample output.
        /// </summary>
        public Vector3 AxisSigns = new Vector3(-1f, -1f, -1f);
        public float UnitsToMetres = 0.001f;

        IntPtr m_Handle;
        IntPtr m_Stream;
        uint m_NumKeyPoints;
        int m_Batch = 1;

        NvAR.CVImage m_GpuImage;
        NvAR.CVImage m_CpuImage;
        NvAR.CVImage m_TmpImage;
        GCHandle m_GpuImagePin;
        bool m_ImageReady;
        int m_Width, m_Height;

        byte[] m_Rgba;            // readback staging, top-down rows, filled by the readback callback
        byte[] m_Bgr;             // only used if the SDK cannot convert RGBA itself
        GCHandle m_PixelsPin;     // pins whichever buffer m_CpuImage points at
        bool m_UseRgbaTransfer = true;
        RenderTexture m_ReadRt;
        bool m_ReadbackPending;   // GPU still copying
        bool m_DataReady;         // bytes landed in m_Rgba, SDK not yet run
        int m_Generation;         // invalidates callbacks from before a resize/dispose
        double m_PendingTimestamp;

        NvAR.Point2f[] m_Kp2D;
        NvAR.Point3f[] m_Kp3D;
        NvAR.Quat[] m_Angles;
        float[] m_KpConf;
        NvAR.Rect[] m_Boxes;
        float[] m_BoxConf;
        NvAR.BBoxes m_BBoxes;
        NvAR.TrackingBBox[] m_TrackBoxes;
        NvAR.TrackingBBoxes m_TrackBBoxes;
        GCHandle m_Kp2DPin, m_Kp3DPin, m_AnglesPin, m_KpConfPin, m_BoxesPin, m_BoxConfPin, m_BBoxesPin, m_TrackBoxesPin, m_TrackBBoxesPin;
        public NvAR.Point3f[] ReferencePose { get; private set; }

        // Per-slot results. Slot 0 is also what TryGetLatest returns.
        PoseFrame[] m_SlotFrames = { new PoseFrame() };
        bool[] m_SlotNew = { false };
        int[] m_SlotIds = { -1 };
        int[] m_SlotMissing = { 0 };
        Rect[] m_SlotBox = { default };
        readonly PoseFrame m_Scratch = new PoseFrame();
        int m_DebugFramesLogged;
        bool m_HasDepthReference;
        float m_DepthReference;

        public string Name => "NVIDIA Maxine";
        public bool IsAvailable { get; private set; }
        /// <summary>True while a frame is being read back or waiting to be run; call TryGetLatest to drain.</summary>
        public bool IsBusy => m_ReadbackPending || m_DataReady;
        public string Status { get; private set; } = "not initialised";

        public int SlotCount => m_Batch;
        public int PeopleCount { get; private set; }
        public bool MultiPersonActive { get; private set; }
        /// <summary>
        /// The tracker id occupying a slot, or -1. Slots filled while the tracker has no distinct ids yet (its ten-frame
        /// probation, or a build without tracking boxes) carry a placeholder, which is reported as -1 so a consumer
        /// comparing ids over time does not mistake the arrival of the real id for a different person.
        /// </summary>
        public int SlotId(int slot) { var id = slot >= 0 && slot < m_SlotIds.Length ? m_SlotIds[slot] : -1; return id >= 1000 ? -1 : id; }

        /// <summary>Debug: last 2D keypoints of slot 0 in normalised texture coords (x right, y up), Maxine layout.</summary>
        public readonly Vector2[] LastImageKeypoints = new Vector2[(int)MaxineJoint.Count];
        public readonly float[] LastKeypointConfidence = new float[(int)MaxineJoint.Count];
        /// <summary>Milliseconds spent in the SDK (transfer + run) for the last frame.</summary>
        public float LastRunMs { get; private set; }
        /// <summary>Raw pelvis position of slot 0 in SDK camera space (mm), for diagnostics.</summary>
        public Vector3 LastRawPelvis { get; private set; }
        /// <summary>Diagnostics: people dropped because the SDK returned a non-finite pelvis, and single keypoints dropped for non-finite values.</summary>
        public int NonFiniteFrames { get; private set; }
        public int NonFiniteKeypoints { get; private set; }
        /// <summary>Diagnostics: person boxes the SDK returned on the last single-person run, tracked or not.</summary>
        public int LastBoxCount { get; private set; }
        /// <summary>Diagnostics: mean keypoint confidence of slot 0 on the last single-person run, tracked or not.</summary>
        public float LastMeanConfidence { get; private set; }

        /// <summary>Forget the reference depth so the next tracked frame becomes the new "home" position.</summary>
        public void ResetDepthReference() => m_HasDepthReference = false;

        public void Initialize()
        {
            if (!NvAR.EnsureLoaded())
            {
                IsAvailable = false;
                Status = "SDK not found: " + NvAR.LoadError;
                return;
            }

            try
            {
                NvAR.Check(NvAR.NvAR_Create(NvAR.FeatureBodyPoseEstimation, out m_Handle), "NvAR_Create");
                NvAR.Check(NvAR.NvAR_SetString(m_Handle, NvAR.Config.ModelDir, NvAR.ModelDir), "Set ModelDir");
                NvAR.Check(NvAR.NvAR_CudaStreamCreate(out m_Stream), "CudaStreamCreate");
                NvAR.Check(NvAR.NvAR_SetCudaStream(m_Handle, NvAR.Config.CUDAStream, m_Stream), "Set CUDAStream");

                // Multi-person tracking (Windows builds of the SDK): batch 8, no temporal filter, tracker on.
                // Any refusal drops back to the single-person configuration that is known to work.
                MultiPersonActive = false;
                m_Batch = 1;
                if (MaxPeople > 1)
                {
                    var st = NvAR.NvAR_SetU32(m_Handle, NvAR.Config.TrackPeople, 1u);
                    if (st == NvAR.NVCV_SUCCESS)
                    {
                        var stBatch = NvAR.NvAR_SetU32(m_Handle, NvAR.Config.BatchSize, k_MultiBatch);
                        if (stBatch == NvAR.NVCV_SUCCESS)
                        {
                            MultiPersonActive = true;
                            m_Batch = k_MultiBatch;
                            var s1 = NvAR.NvAR_SetU32(m_Handle, NvAR.Config.ShadowTrackingAge, 90u);
                            var s2 = NvAR.NvAR_SetU32(m_Handle, NvAR.Config.ProbationAge, 10u);
                            var s3 = NvAR.NvAR_SetU32(m_Handle, NvAR.Config.MaxTargetsTracked, (uint)Mathf.Clamp(MaxPeople, 2, 30));
                            Debug.Log($"[MimicMocap] Maxine multi-person tracking requested (batch {k_MultiBatch}; ages {s1}/{s2}/{s3}).");
                        }
                        else
                        {
                            Debug.Log($"[MimicMocap] Maxine BatchSize {k_MultiBatch} rejected (status {stBatch}); single-person mode.");
                            NvAR.NvAR_SetU32(m_Handle, NvAR.Config.TrackPeople, 0u);
                        }
                    }
                    else Debug.Log($"[MimicMocap] Maxine TrackPeople not settable (status {st}); single-person mode.");
                }
                if (!MultiPersonActive)
                {
                    NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.BatchSize, 1), "Set BatchSize");
                    // TrackPeople is only a valid selector on SDK builds with the multi-object tracker; it defaults to off.
                    NvAR.NvAR_SetU32(m_Handle, NvAR.Config.TrackPeople, 0u);
                }
                NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.Mode, (uint)Mode), "Set Mode");
                NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.Temporal, Temporal && !MultiPersonActive ? 1u : 0u), "Set Temporal");
                NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.UseCudaGraph, UseCudaGraph ? 1u : 0u), "Set UseCudaGraph");

                var loadStatus = NvAR.NvAR_Load(m_Handle);
                if (loadStatus != NvAR.NVCV_SUCCESS && MultiPersonActive)
                {
                    // The tracker exists as a selector but this build cannot load it: start over single-person.
                    Debug.Log($"[MimicMocap] Maxine multi-person load failed (status {loadStatus}); retrying single-person.");
                    NvAR.NvAR_Destroy(m_Handle);
                    m_Handle = IntPtr.Zero;
                    NvAR.Check(NvAR.NvAR_Create(NvAR.FeatureBodyPoseEstimation, out m_Handle), "NvAR_Create");
                    NvAR.Check(NvAR.NvAR_SetString(m_Handle, NvAR.Config.ModelDir, NvAR.ModelDir), "Set ModelDir");
                    NvAR.Check(NvAR.NvAR_SetCudaStream(m_Handle, NvAR.Config.CUDAStream, m_Stream), "Set CUDAStream");
                    NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.BatchSize, 1), "Set BatchSize");
                    NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.Mode, (uint)Mode), "Set Mode");
                    NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.Temporal, Temporal ? 1u : 0u), "Set Temporal");
                    NvAR.Check(NvAR.NvAR_SetU32(m_Handle, NvAR.Config.UseCudaGraph, UseCudaGraph ? 1u : 0u), "Set UseCudaGraph");
                    MultiPersonActive = false;
                    m_Batch = 1;
                    loadStatus = NvAR.NvAR_Load(m_Handle);
                }
                NvAR.Check(loadStatus, "NvAR_Load");

                NvAR.Check(NvAR.NvAR_GetU32(m_Handle, NvAR.Config.NumKeyPoints, out m_NumKeyPoints), "Get NumKeyPoints");
                if (m_NumKeyPoints != (uint)MaxineJoint.Count)
                    Debug.LogWarning($"[MimicMocap] Maxine reports {m_NumKeyPoints} keypoints, expected {(int)MaxineJoint.Count}");
                var n = (int)m_NumKeyPoints;
                var total = n * m_Batch;

                m_Kp2D = new NvAR.Point2f[total];
                m_Kp3D = new NvAR.Point3f[total];
                m_Angles = new NvAR.Quat[total];
                m_KpConf = new float[total];
                m_Boxes = new NvAR.Rect[k_MaxBoxes];
                m_BoxConf = new float[k_MaxBoxes];
                m_TrackBoxes = new NvAR.TrackingBBox[k_MaxBoxes];
                m_Kp2DPin = GCHandle.Alloc(m_Kp2D, GCHandleType.Pinned);
                m_Kp3DPin = GCHandle.Alloc(m_Kp3D, GCHandleType.Pinned);
                m_AnglesPin = GCHandle.Alloc(m_Angles, GCHandleType.Pinned);
                m_KpConfPin = GCHandle.Alloc(m_KpConf, GCHandleType.Pinned);
                m_BoxesPin = GCHandle.Alloc(m_Boxes, GCHandleType.Pinned);
                m_BoxConfPin = GCHandle.Alloc(m_BoxConf, GCHandleType.Pinned);
                m_TrackBoxesPin = GCHandle.Alloc(m_TrackBoxes, GCHandleType.Pinned);
                m_BBoxes = new NvAR.BBoxes { boxes = m_BoxesPin.AddrOfPinnedObject(), num_boxes = 0, max_boxes = k_MaxBoxes };
                m_BBoxesPin = GCHandle.Alloc(m_BBoxes, GCHandleType.Pinned);
                m_TrackBBoxes = new NvAR.TrackingBBoxes { boxes = m_TrackBoxesPin.AddrOfPinnedObject(), num_boxes = 0, max_boxes = k_MaxBoxes };
                m_TrackBBoxesPin = GCHandle.Alloc(m_TrackBBoxes, GCHandleType.Pinned);

                m_SlotFrames = new PoseFrame[m_Batch];
                m_SlotNew = new bool[m_Batch];
                m_SlotIds = new int[m_Batch];
                m_SlotMissing = new int[m_Batch];
                m_SlotBox = new Rect[m_Batch];
                for (var i = 0; i < m_Batch; i++) { m_SlotFrames[i] = new PoseFrame(); m_SlotIds[i] = -1; m_SlotMissing[i] = k_SlotHoldFrames; }

                NvAR.Check(NvAR.NvAR_GetObject(m_Handle, NvAR.Config.ReferencePose, out var refPtr, (uint)Marshal.SizeOf<NvAR.Point3f>()), "Get ReferencePose");
                ReferencePose = new NvAR.Point3f[n];
                for (var i = 0; i < n; i++)
                    ReferencePose[i] = Marshal.PtrToStructure<NvAR.Point3f>(refPtr + i * Marshal.SizeOf<NvAR.Point3f>());

                NvAR.Check(NvAR.NvAR_SetObject(m_Handle, NvAR.Output.KeyPoints, m_Kp2DPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<NvAR.Point2f>()), "Set KeyPoints");
                NvAR.Check(NvAR.NvAR_SetObject(m_Handle, NvAR.Output.KeyPoints3D, m_Kp3DPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<NvAR.Point3f>()), "Set KeyPoints3D");
                NvAR.Check(NvAR.NvAR_SetObject(m_Handle, NvAR.Output.JointAngles, m_AnglesPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<NvAR.Quat>()), "Set JointAngles");
                NvAR.Check(NvAR.NvAR_SetF32Array(m_Handle, NvAR.Output.KeyPointsConfidence, m_KpConfPin.AddrOfPinnedObject(), sizeof(float)), "Set KeyPointsConfidence");
                if (MultiPersonActive)
                {
                    var stTrack = NvAR.NvAR_SetObject(m_Handle, NvAR.Output.TrackingBoundingBoxes, m_TrackBBoxesPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<NvAR.TrackingBBoxes>());
                    if (stTrack != NvAR.NVCV_SUCCESS) Debug.Log($"[MimicMocap] Maxine TrackingBoundingBoxes output rejected (status {stTrack}); using plain boxes.");
                    m_UseTrackingBoxes = stTrack == NvAR.NVCV_SUCCESS;
                }
                var stBoxes = NvAR.NvAR_SetObject(m_Handle, NvAR.Output.BoundingBoxes, m_BBoxesPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<NvAR.BBoxes>());
                if (!MultiPersonActive) NvAR.Check(stBoxes, "Set BoundingBoxes");
                var stBoxConf = NvAR.NvAR_SetF32Array(m_Handle, NvAR.Output.BoundingBoxesConfidence, m_BoxConfPin.AddrOfPinnedObject(), k_MaxBoxes);
                if (!MultiPersonActive) NvAR.Check(stBoxConf, "Set BoundingBoxesConfidence");

                IsAvailable = true;
                Status = $"Maxine {Mode} ready{(MultiPersonActive ? $", multi-person x{m_Batch}" : "")} ({NvAR.SdkPath})";
            }
            catch (Exception e)
            {
                IsAvailable = false;
                Status = "init failed: " + e.Message;
                Debug.LogWarning("[MimicMocap] Maxine init failed, falling back: " + e.Message);
                Dispose();
            }
        }

        bool m_UseTrackingBoxes;

        void EnsureImage(int w, int h)
        {
            if (m_ImageReady && w == m_Width && h == m_Height) return;
            if (m_ImageReady)
            {
                // The feature (and its captured CUDA graph) is bound to the first input size; rebuild it for a new one.
                Debug.Log($"[MimicMocap] Maxine input size changed {m_Width}x{m_Height} -> {w}x{h}; re-creating the feature.");
                Dispose();
                Initialize();
                if (!IsAvailable) throw new InvalidOperationException("re-initialisation failed: " + Status);
            }
            ReleaseImage();
            m_Width = w;
            m_Height = h;

            m_GpuImage = default;
            NvAR.Check(NvAR.NvCVImage_Alloc(ref m_GpuImage, (uint)w, (uint)h, NvAR.NVCV_BGR, NvAR.NVCV_U8, NvAR.NVCV_CHUNKY, NvAR.NVCV_GPU, 1), "NvCVImage_Alloc");
            m_GpuImagePin = GCHandle.Alloc(m_GpuImage, GCHandleType.Pinned);

            AllocateStaging();
            m_TmpImage = default;

            m_ReadRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            m_ReadRt.Create();

            NvAR.Check(NvAR.NvAR_SetObject(m_Handle, NvAR.Input.Image, m_GpuImagePin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<NvAR.CVImage>()), "Set Input Image");
            // The reference focal length is for the sensor's long side; a portrait (rotated) frame has the same lens.
            NvAR.Check(NvAR.NvAR_SetF32(m_Handle, NvAR.Input.FocalLength, FocalLengthAt1280 * Mathf.Max(w, h) / 1280f), "Set FocalLength");
            m_ImageReady = true;
        }

        /// <summary>CPU staging image: RGBA straight from the readback, or BGR if the SDK refuses to convert.</summary>
        void AllocateStaging()
        {
            if (m_PixelsPin.IsAllocated) m_PixelsPin.Free();
            if (m_Rgba == null || m_Rgba.Length != m_Width * m_Height * 4) m_Rgba = new byte[m_Width * m_Height * 4];
            byte[] target;
            int channels;
            int format;
            if (m_UseRgbaTransfer)
            {
                target = m_Rgba; channels = 4; format = NvAR.NVCV_RGBA;
            }
            else
            {
                if (m_Bgr == null || m_Bgr.Length != m_Width * m_Height * 3) m_Bgr = new byte[m_Width * m_Height * 3];
                target = m_Bgr; channels = 3; format = NvAR.NVCV_BGR;
            }
            m_PixelsPin = GCHandle.Alloc(target, GCHandleType.Pinned);
            m_CpuImage = default;
            NvAR.Check(NvAR.NvCVImage_Init(ref m_CpuImage, (uint)m_Width, (uint)m_Height, m_Width * channels, m_PixelsPin.AddrOfPinnedObject(), format, NvAR.NVCV_U8, NvAR.NVCV_CHUNKY, NvAR.NVCV_CPU), "NvCVImage_Init");
        }

        void ReleaseImage()
        {
            if (!m_ImageReady) return;
            m_ImageReady = false;
            m_ReadbackPending = false;
            m_DataReady = false;
            m_Generation++;
            NvAR.NvCVImage_Dealloc(ref m_GpuImage);
            NvAR.NvCVImage_Dealloc(ref m_TmpImage);
            if (m_GpuImagePin.IsAllocated) m_GpuImagePin.Free();
            if (m_PixelsPin.IsAllocated) m_PixelsPin.Free();
            if (m_ReadRt != null) { m_ReadRt.Release(); UnityEngine.Object.Destroy(m_ReadRt); }
            m_ReadRt = null;
        }

        public void Submit(Texture frame, double timestamp)
        {
            if (!IsAvailable || frame == null || m_ReadbackPending || m_DataReady) return;
            try
            {
                EnsureImage(frame.width, frame.height);
                // Flip vertically so the readback rows come out top-down, which is what the SDK expects.
                Graphics.Blit(frame, m_ReadRt, new Vector2(1f, -1f), new Vector2(0f, 1f));
                var generation = m_Generation;
                m_ReadbackPending = true;
                m_PendingTimestamp = timestamp;
                if (SynchronousReadback)
                {
                    var request = AsyncGPUReadback.Request(m_ReadRt, 0, TextureFormat.RGBA32);
                    request.WaitForCompletion();
                    OnReadback(request, generation);
                }
                // Readback data is only valid during the frame it completes, so copy it out in the callback.
                else AsyncGPUReadback.Request(m_ReadRt, 0, TextureFormat.RGBA32, req => OnReadback(req, generation));
            }
            catch (Exception e)
            {
                Status = "error: " + e.Message;
                Debug.LogWarning("[MimicMocap] Maxine submit failed, disabling: " + e.Message);
                IsAvailable = false;
            }
        }

        void OnReadback(AsyncGPUReadbackRequest req, int generation)
        {
            if (generation != m_Generation || !m_ImageReady) return; // resized or disposed meanwhile
            m_ReadbackPending = false;
            if (req.hasError)
            {
                Status = "GPU readback error";
                return;
            }
            var data = req.GetData<byte>();
            if (data.Length != m_Rgba.Length)
            {
                Status = $"readback size mismatch {data.Length} vs {m_Rgba.Length}";
                return;
            }
            data.CopyTo(m_Rgba);
            m_DataReady = true;
        }

        /// <summary>Run the SDK on the most recently read-back frame, if there is one.</summary>
        void Poll()
        {
            if (!m_DataReady) return;
            m_DataReady = false;

            try
            {
                var t0 = Time.realtimeSinceStartupAsDouble;
                if (m_UseRgbaTransfer)
                {
                    var st = NvAR.NvCVImage_Transfer(ref m_CpuImage, ref m_GpuImage, 1f, m_Stream, ref m_TmpImage);
                    if (st != NvAR.NVCV_SUCCESS)
                    {
                        Debug.Log($"[MimicMocap] Maxine RGBA transfer unsupported (status {st}); converting to BGR on the CPU.");
                        m_UseRgbaTransfer = false;
                        AllocateStaging();
                    }
                }
                if (!m_UseRgbaTransfer)
                {
                    var count = m_Width * m_Height;
                    for (int i = 0, d = 0, s = 0; i < count; i++, s += 4)
                    {
                        m_Bgr[d++] = m_Rgba[s + 2];
                        m_Bgr[d++] = m_Rgba[s + 1];
                        m_Bgr[d++] = m_Rgba[s];
                    }
                    NvAR.Check(NvAR.NvCVImage_Transfer(ref m_CpuImage, ref m_GpuImage, 1f, m_Stream, ref m_TmpImage), "NvCVImage_Transfer");
                }
                NvAR.Check(NvAR.NvAR_Run(m_Handle), "NvAR_Run");
                LastRunMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
                if (MultiPersonActive) PublishMulti(m_PendingTimestamp);
                else PublishSingle(m_PendingTimestamp);
            }
            catch (Exception e)
            {
                Status = "error: " + e.Message;
                Debug.LogWarning("[MimicMocap] Maxine run failed, disabling: " + e.Message);
                IsAvailable = false;
            }
        }

        /// <summary>Single-person path: exactly the behaviour the app shipped with.</summary>
        void PublishSingle(double timestamp)
        {
            m_BBoxes = Marshal.PtrToStructure<NvAR.BBoxes>(m_BBoxesPin.AddrOfPinnedObject());
            var result = m_SlotFrames[0];
            var meanConf = FillImageKeypoints(0);
            var tracked = m_BBoxes.num_boxes > 0 && meanConf > 0.2f;
            LastBoxCount = m_BBoxes.num_boxes;
            LastMeanConfidence = meanConf;
            var rawPelvis = m_Kp3D[(int)MaxineJoint.Pelvis];
            LastRawPelvis = new Vector3(rawPelvis.x, rawPelvis.y, rawPelvis.z);
            PeopleCount = tracked ? 1 : 0;

            if (m_DebugFramesLogged < 3 && tracked)
            {
                m_DebugFramesLogged++;
                var nose = m_Kp3D[(int)MaxineJoint.Nose];
                var lw = m_Kp3D[(int)MaxineJoint.LeftWrist];
                Debug.Log($"[MimicMocap] Maxine raw 3D: pelvis=({rawPelvis.x:0},{rawPelvis.y:0},{rawPelvis.z:0}) nose=({nose.x:0},{nose.y:0},{nose.z:0}) leftWrist=({lw.x:0},{lw.y:0},{lw.z:0}) boxes={m_BBoxes.num_boxes} meanConf={meanConf:0.00} run={LastRunMs:0.0}ms rgbaTransfer={m_UseRgbaTransfer}");
            }

            if (!tracked)
            {
                result.Clear();
                result.Timestamp = timestamp;
                m_SlotNew[0] = true;
                Status = "Maxine searching";
                return;
            }

            var box = m_BBoxes.num_boxes > 0 ? m_Boxes[0] : default;
            FillFrame(result, 0, timestamp, meanConf, box, m_BBoxes.num_boxes > 0);
            m_SlotIds[0] = 0;
            m_SlotNew[0] = true;
            Status = $"Maxine tracking ({meanConf:0.00}, {LastRunMs:0.0} ms)";
        }

        /// <summary>Multi-person path: one frame per tracked person, assigned to stable slots by tracker id.</summary>
        void PublishMulti(double timestamp)
        {
            int count;
            if (m_UseTrackingBoxes)
            {
                m_TrackBBoxes = Marshal.PtrToStructure<NvAR.TrackingBBoxes>(m_TrackBBoxesPin.AddrOfPinnedObject());
                count = Mathf.Min(m_TrackBBoxes.num_boxes, m_Batch);
            }
            else
            {
                m_BBoxes = Marshal.PtrToStructure<NvAR.BBoxes>(m_BBoxesPin.AddrOfPinnedObject());
                count = Mathf.Min(m_BBoxes.num_boxes, m_Batch);
            }
            PeopleCount = count;

            Span<int> personSlot = stackalloc int[m_Batch];
            AssignSlots(count, personSlot);

            var tracked = 0;
            for (var s = 0; s < m_Batch; s++)
            {
                var frame = m_SlotFrames[s];
                var p = -1;
                for (var i = 0; i < count; i++) if (personSlot[i] == s) { p = i; break; }
                if (p < 0)
                {
                    if (m_SlotMissing[s] > k_SlotHoldFrames) m_SlotIds[s] = -1;
                    frame.Clear();
                    frame.Timestamp = timestamp;
                    m_SlotNew[s] = true;
                    continue;
                }
                var meanConf = MeanConfidence(p);
                if (meanConf <= 0.2f)
                {
                    frame.Clear();
                    frame.Timestamp = timestamp;
                    m_SlotNew[s] = true;
                    continue;
                }
                FillFrame(frame, p, timestamp, meanConf, BoxOf(p), true);
                m_SlotNew[s] = true;
                if (frame.Tracked) tracked++;
            }

            if (m_SlotIds[0] >= 0)
            {
                FillImageKeypoints(PersonOfSlot(0, personSlot, count));
                var raw = m_Kp3D[PersonOfSlot(0, personSlot, count) * (int)m_NumKeyPoints + (int)MaxineJoint.Pelvis];
                LastRawPelvis = new Vector3(raw.x, raw.y, raw.z);
            }
            if (m_DebugFramesLogged < 3 && count > 0)
            {
                m_DebugFramesLogged++;
                var ids = "";
                for (var p = 0; p < count; p++) ids += (p > 0 ? "," : "") + PersonId(p) + "->slot" + personSlot[p];
                Debug.Log($"[MimicMocap] Maxine multi: people={count} tracked={tracked} ids={ids} run={LastRunMs:0.0}ms trackingBoxes={m_UseTrackingBoxes}");
            }
            Status = tracked > 0 ? $"Maxine tracking {tracked} of {count} people ({LastRunMs:0.0} ms)" : "Maxine searching";
        }

        /// <summary>
        /// Map this frame's people onto the stable slots. Tracker ids are used when they are distinct; they are all 0
        /// during the tracker's probation period (and absent without tracking boxes), so people are otherwise matched
        /// to the slot whose last box is nearest. Newcomers take free slots leftmost first, so two people entering
        /// together keep their sides.
        /// </summary>
        void AssignSlots(int count, Span<int> personSlot)
        {
            for (var s = 0; s < m_Batch; s++) m_SlotMissing[s]++;
            for (var p = 0; p < m_Batch; p++) personSlot[p] = -1;
            Span<bool> slotTaken = stackalloc bool[m_Batch];
            var idsUsable = IdsDistinct(count);

            // 1. Same tracker id as last time.
            if (idsUsable)
            {
                for (var p = 0; p < count; p++)
                {
                    var id = PersonId(p);
                    for (var s = 0; s < m_Batch; s++)
                    {
                        if (slotTaken[s] || m_SlotIds[s] != id || m_SlotMissing[s] > k_SlotHoldFrames + 1) continue;
                        if (Vector2.Distance(NormBox(BoxOf(p)).center, m_SlotBox[s].center) > Mathf.Min(k_ReturnDistance, k_StepPerFrame * Mathf.Max(1, m_SlotMissing[s]))) continue;
                        personSlot[p] = s;
                        slotTaken[s] = true;
                        break;
                    }
                }
            }

            // 2. Nearest previous box among occupied slots (no ids yet, ids renumbered, or a brief dropout).
            while (true)
            {
                var bestP = -1;
                var bestS = -1;
                var bestD = 0.3f;
                for (var p = 0; p < count; p++)
                {
                    if (personSlot[p] >= 0) continue;
                    var c = NormBox(BoxOf(p)).center;
                    for (var s = 0; s < m_Batch; s++)
                    {
                        if (slotTaken[s] || m_SlotIds[s] < 0 || m_SlotMissing[s] > k_SlotHoldFrames + 1) continue;
                        var d = Vector2.Distance(c, m_SlotBox[s].center);
                        if (d < bestD) { bestD = d; bestP = p; bestS = s; }
                    }
                }
                if (bestP < 0) break;
                personSlot[bestP] = bestS;
                slotTaken[bestS] = true;
            }

            // 3. Newcomers take free slots, leftmost person first.
            while (true)
            {
                var best = -1;
                var bestX = float.MaxValue;
                for (var p = 0; p < count; p++)
                {
                    if (personSlot[p] >= 0) continue;
                    var x = BoxOf(p).x;
                    if (x < bestX) { bestX = x; best = p; }
                }
                if (best < 0) break;
                var free = -1;
                for (var s = 0; s < m_Batch; s++)
                    if (!slotTaken[s] && (m_SlotIds[s] < 0 || m_SlotMissing[s] > k_SlotHoldFrames)) { free = s; break; }
                if (free < 0) break; // more people than slots with grace left; drop them this frame
                personSlot[best] = free;
                slotTaken[free] = true;
            }

            for (var p = 0; p < count; p++)
            {
                var s = personSlot[p];
                if (s < 0) continue;
                m_SlotIds[s] = idsUsable ? PersonId(p) : (m_SlotIds[s] >= 0 ? m_SlotIds[s] : 1000 + s);
                m_SlotMissing[s] = 0;
                m_SlotBox[s] = NormBox(BoxOf(p));
            }
            for (var s = 0; s < m_Batch; s++)
                if (!slotTaken[s] && m_SlotMissing[s] > k_SlotHoldFrames) m_SlotIds[s] = -1;
        }

        bool IdsDistinct(int count)
        {
            if (!m_UseTrackingBoxes) return false;
            for (var i = 0; i < count; i++)
                for (var j = i + 1; j < count; j++)
                    if (PersonId(i) == PersonId(j)) return false;
            return true;
        }

        Rect NormBox(NvAR.Rect b) => new Rect(b.x / Mathf.Max(1, m_Width), b.y / Mathf.Max(1, m_Height), b.width / Mathf.Max(1, m_Width), b.height / Mathf.Max(1, m_Height));

        static int PersonOfSlot(int slot, Span<int> personSlot, int count)
        {
            for (var p = 0; p < count; p++) if (personSlot[p] == slot) return p;
            return 0;
        }

        int PersonId(int p) => m_UseTrackingBoxes ? m_TrackBoxes[p].tracking_id : p;
        NvAR.Rect BoxOf(int p) => m_UseTrackingBoxes ? m_TrackBoxes[p].bbox : m_Boxes[p];

        float MeanConfidence(int person)
        {
            var n = (int)m_NumKeyPoints;
            var sum = 0f;
            for (var i = 0; i < n; i++) sum += m_KpConf[person * n + i];
            return sum / Mathf.Max(1, n);
        }

        /// <summary>Refresh the debug 2D keypoints from one person's output block; returns that person's mean confidence.</summary>
        float FillImageKeypoints(int person)
        {
            var n = (int)m_NumKeyPoints;
            var baseIndex = person * n;
            var meanConf = 0f;
            for (var i = 0; i < n; i++)
            {
                meanConf += m_KpConf[baseIndex + i];
                if (i < LastImageKeypoints.Length)
                {
                    // SDK 2D keypoints are top-down pixels; store normalised y-up like the other sources.
                    LastImageKeypoints[i] = new Vector2(m_Kp2D[baseIndex + i].x / m_Width, 1f - m_Kp2D[baseIndex + i].y / m_Height);
                    LastKeypointConfidence[i] = m_KpConf[baseIndex + i];
                }
            }
            return meanConf / Mathf.Max(1, n);
        }

        /// <summary>Convert one person's output block into a canonical frame.</summary>
        void FillFrame(PoseFrame result, int person, double timestamp, float meanConf, NvAR.Rect box, bool hasBox)
        {
            var n = (int)m_NumKeyPoints;
            var baseIndex = person * n;
            result.Clear();
            result.Timestamp = timestamp;
            var rawPelvis = m_Kp3D[baseIndex + (int)MaxineJoint.Pelvis];
            // The SDK now and then returns non-finite values for a person (seen at the start of the two-person install clip).
            // One NaN reaching the smoothing filters poisoned them until the person left and the filters were reset: the
            // avatar froze or vanished. A person without a finite pelvis is reported as not tracked; other non-finite
            // keypoints get zero confidence, so the smoother holds them at their last good position.
            if (!float.IsFinite(rawPelvis.x) || !float.IsFinite(rawPelvis.y) || !float.IsFinite(rawPelvis.z)) { NonFiniteFrames++; return; }

            for (var i = 0; i < n && i < MaxineLayout.ToCanonical.Length; i++)
            {
                var canon = MaxineLayout.ToCanonical[i];
                if (canon < 0) continue;
                var k = m_Kp3D[baseIndex + i];
                var v = new Vector3(
                    AxisSigns.x * (k.x - rawPelvis.x),
                    AxisSigns.y * (k.y - rawPelvis.y),
                    AxisSigns.z * (k.z - rawPelvis.z)) * UnitsToMetres;
                var conf = m_KpConf[baseIndex + i];
                var img = new Vector2(m_Kp2D[baseIndex + i].x / m_Width, 1f - m_Kp2D[baseIndex + i].y / m_Height);
                if (!float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z) || !float.IsFinite(conf) || !float.IsFinite(img.x) || !float.IsFinite(img.y))
                {
                    NonFiniteKeypoints++;
                    continue;   // left at zero with zero confidence by Clear()
                }
                result.Joints[canon] = v;
                result.Confidence[canon] = Mathf.Clamp01(conf * ConfidenceScale);
                result.ImageJoints[canon] = img;
                var q = m_Angles[baseIndex + i];
                result.Rotations[canon] = float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) ? new Quaternion(q.x, q.y, q.z, q.w) : Quaternion.identity;
            }
            result.HasRotations = true;
            result.HasImageJoints = true;
            result.SynthesiseTorso();
            // Image positions for the synthesised torso joints too, so overlays don't draw lines to the corner.
            SynthesiseImageJoint(result, HumanJoint.Chest, HumanJoint.Pelvis, HumanJoint.Neck, 0.85f);
            SynthesiseImageJoint(result, HumanJoint.Head, HumanJoint.LeftEar, HumanJoint.RightEar, 0.5f);
            // Depth is absolute distance from the camera; travel is measured from where the first person stood,
            // so a second person further back stands further back on stage too. Height is absolute camera-space
            // too; the floor tracker turns it into a lift above the floor so jumps carry over.
            if (!m_HasDepthReference) { m_DepthReference = rawPelvis.z; m_HasDepthReference = true; }
            result.RootOffset = new Vector3(AxisSigns.x * rawPelvis.x, AxisSigns.y * rawPelvis.y, AxisSigns.z * (rawPelvis.z - m_DepthReference)) * UnitsToMetres;
            result.Tracked = true;
            result.Score = meanConf;
            if (hasBox)
            {
                // SDK boxes are top-down pixels; convert to normalised y-up.
                result.ImageBox = new Rect(box.x / m_Width, 1f - (box.y + box.height) / m_Height, box.width / m_Width, box.height / m_Height);
            }
        }

        static void SynthesiseImageJoint(PoseFrame f, HumanJoint target, HumanJoint a, HumanJoint b, float t)
        {
            if (f.ImageJoints[(int)target] != Vector2.zero || f.ConfidenceOf(target) <= 0f) return;
            if (f.ImageJoints[(int)a] == Vector2.zero || f.ImageJoints[(int)b] == Vector2.zero) return;
            f.ImageJoints[(int)target] = Vector2.Lerp(f.ImageJoints[(int)a], f.ImageJoints[(int)b], t);
        }

        public bool TryGetLatest(PoseFrame frame) => TryGetPerson(0, frame);

        public bool TryGetPerson(int slot, PoseFrame frame)
        {
            Poll();
            if (slot < 0 || slot >= m_SlotFrames.Length || !m_SlotNew[slot]) return false;
            m_SlotFrames[slot].CopyTo(frame);
            m_SlotNew[slot] = false;
            return true;
        }

        public void Dispose()
        {
            IsAvailable = false;
            ReleaseImage();
            if (m_PixelsPin.IsAllocated) m_PixelsPin.Free();
            if (m_Handle != IntPtr.Zero) { NvAR.NvAR_Destroy(m_Handle); m_Handle = IntPtr.Zero; }
            if (m_Stream != IntPtr.Zero) { NvAR.NvAR_CudaStreamDestroy(m_Stream); m_Stream = IntPtr.Zero; }
            foreach (var h in new[] { m_Kp2DPin, m_Kp3DPin, m_AnglesPin, m_KpConfPin, m_BoxesPin, m_BoxConfPin, m_BBoxesPin, m_TrackBoxesPin, m_TrackBBoxesPin })
                if (h.IsAllocated) h.Free();
        }
    }
}
