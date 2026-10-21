using System;
using UnityEngine;
using UnityEngine.Video;

namespace MimicMocap
{
    /// <summary>Supplies camera frames as a Unity Texture.</summary>
    public interface IFrameSource : IDisposable
    {
        string Name { get; }
        Texture Current { get; }
        bool IsReady { get; }
        int Width { get; }
        int Height { get; }
        /// <summary>True once per new image; resets after <see cref="Consume"/>.</summary>
        bool HasNewFrame { get; }
        void Start();
        void Stop();
        void Update();
        void Consume();
    }

    /// <summary>
    /// Turns a frame by quarter turns and/or flips it vertically on the GPU, for cameras mounted on their side
    /// (portrait gives the body the sensor's long axis) and capture devices that deliver upside-down frames.
    /// </summary>
    public sealed class FrameRotator : IDisposable
    {
        static ComputeShader s_Shader;
        static int s_Kernel;
        RenderTexture m_Output;

        /// <summary>Degrees counter-clockwise as seen on screen: 0, 90, 180 or 270.</summary>
        public int Rotation;
        public bool FlipVertical;
        public bool Active => Normalise(Rotation) != 0 || FlipVertical;

        public static int Normalise(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

        /// <summary>The source itself when nothing needs doing, else the transformed copy.</summary>
        public Texture Apply(Texture src)
        {
            if (src == null || !Active) return src;
            if (s_Shader == null)
            {
                s_Shader = Resources.Load<ComputeShader>("ComputeShaders/FrameRotate");
                if (s_Shader == null) { Debug.LogWarning("[MimicMocap] Missing Resources/ComputeShaders/FrameRotate.compute; frames not rotated."); return src; }
                s_Kernel = s_Shader.FindKernel("Rotate");
            }
            var quarter = Normalise(Rotation) / 90;
            var swap = quarter == 1 || quarter == 3;
            var w = swap ? src.height : src.width;
            var h = swap ? src.width : src.height;
            if (m_Output == null || m_Output.width != w || m_Output.height != h)
            {
                Release();
                m_Output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "RotatedFrame", enableRandomWrite = true };
                m_Output.Create();
            }
            s_Shader.SetTexture(s_Kernel, "Source", src);
            s_Shader.SetTexture(s_Kernel, "Result", m_Output);
            s_Shader.SetInt("SrcWidth", src.width);
            s_Shader.SetInt("SrcHeight", src.height);
            s_Shader.SetInt("DstWidth", w);
            s_Shader.SetInt("DstHeight", h);
            s_Shader.SetInt("Rotation", quarter);
            s_Shader.SetInt("FlipVertical", FlipVertical ? 1 : 0);
            s_Shader.Dispatch(s_Kernel, (w + 7) / 8, (h + 7) / 8, 1);
            return m_Output;
        }

        void Release()
        {
            if (m_Output == null) return;
            m_Output.Release();
            UnityEngine.Object.Destroy(m_Output);
            m_Output = null;
        }

        public void Dispose() => Release();
    }

    /// <summary>Live webcam via <see cref="WebCamTexture"/>.</summary>
    public sealed class WebcamFrameSource : IFrameSource
    {
        readonly int m_DeviceIndex;
        readonly int m_RequestedWidth;
        readonly int m_RequestedHeight;
        readonly int m_RequestedFps;
        WebCamTexture m_Texture;
        // Some capture devices (Elgato and other UVC bridges) deliver frames upside down and say so through
        // videoVerticallyMirrored; those, and a camera mounted on its side, go through the rotator so every
        // consumer sees an upright image.
        readonly FrameRotator m_Rotator = new FrameRotator();
        Texture m_Current;
        bool m_HasNew;

        public WebcamFrameSource(int deviceIndex, int width, int height, int fps)
        {
            m_DeviceIndex = deviceIndex;
            m_RequestedWidth = width;
            m_RequestedHeight = height;
            m_RequestedFps = fps;
        }

        public string Name { get; private set; } = "Webcam";
        public Texture Current => m_Current != null ? m_Current : m_Texture;
        public bool IsReady => m_Texture != null && m_Texture.isPlaying && m_Texture.width > 16;
        public int Width => Current != null ? Current.width : 0;
        public int Height => Current != null ? Current.height : 0;
        public bool HasNewFrame => m_HasNew;
        /// <summary>True when the device reports upside-down frames (they are corrected before use).</summary>
        public bool VerticallyMirrored { get; private set; }
        public int RotationAngle { get; private set; }
        /// <summary>Extra rotation to apply, degrees counter-clockwise on screen (90 or 270 for a camera on its side).</summary>
        public int Rotation
        {
            get => m_Rotator.Rotation;
            set => m_Rotator.Rotation = FrameRotator.Normalise(value);
        }

        public static string[] DeviceNames()
        {
            var devices = WebCamTexture.devices;
            var names = new string[devices.Length];
            for (var i = 0; i < devices.Length; i++) names[i] = devices[i].name;
            return names;
        }

        public void Start()
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
            {
                Debug.LogWarning("[MimicMocap] No webcam devices found.");
                return;
            }
            var idx = Mathf.Clamp(m_DeviceIndex, 0, devices.Length - 1);
            Name = "Webcam: " + devices[idx].name;
            m_Texture = new WebCamTexture(devices[idx].name, m_RequestedWidth, m_RequestedHeight, m_RequestedFps);
            m_Texture.Play();
            m_StartedAt = Time.realtimeSinceStartup;
            m_FrameCount = 0;
            m_WarnedNoFrames = false;
        }

        public void Stop()
        {
            if (m_Texture == null) return;
            m_Texture.Stop();
        }

        float m_StartedAt;
        int m_FrameCount;
        bool m_WarnedNoFrames;

        public int FrameCount => m_FrameCount;

        public void Update()
        {
            if (m_Texture == null) return;
            if (m_Texture.didUpdateThisFrame)
            {
                m_HasNew = true;
                m_FrameCount++;
                VerticallyMirrored = m_Texture.videoVerticallyMirrored;
                RotationAngle = m_Texture.videoRotationAngle;
                if (m_FrameCount == 1)
                    Debug.Log($"[MimicMocap] First webcam frame from {m_Texture.deviceName}: {m_Texture.width}x{m_Texture.height} @ {m_Texture.requestedFPS} fps, verticallyMirrored={VerticallyMirrored}, rotation={RotationAngle}");
                m_Rotator.FlipVertical = VerticallyMirrored;
                m_Current = m_Rotator.Apply(m_Texture);
                Name = $"Webcam: {m_Texture.deviceName} @{m_Texture.requestedFPS:0}{(VerticallyMirrored ? " (frames flipped, corrected)" : "")}{(Rotation != 0 ? $" rotated {Rotation}" : "")}";
            }
            else if (m_FrameCount == 0 && !m_WarnedNoFrames && Time.realtimeSinceStartup - m_StartedAt > 6f)
            {
                m_WarnedNoFrames = true;
                Debug.LogWarning($"[MimicMocap] Webcam {m_Texture.deviceName} opened ({m_Texture.width}x{m_Texture.height}) but has delivered no frames in 6 s. Check the privacy shutter, Windows camera permissions, or press C for the next device.");
            }
        }

        public void Consume() => m_HasNew = false;

        public void Dispose()
        {
            Stop();
            if (m_Texture != null) UnityEngine.Object.Destroy(m_Texture);
            m_Texture = null;
            m_Rotator.Dispose();
            m_Current = null;
        }
    }

    /// <summary>Plays a video file (mp4) through a <see cref="VideoPlayer"/> rendering to a RenderTexture.</summary>
    public sealed class VideoFileFrameSource : IFrameSource
    {
        readonly string m_Path;
        readonly GameObject m_Host;
        VideoPlayer m_Player;
        RenderTexture m_Target;
        long m_LastFrame = -1;
        bool m_HasNew;

        public VideoFileFrameSource(string path, GameObject host)
        {
            m_Path = path;
            m_Host = host;
        }

        string m_Error;
        public string Name => "Video: " + System.IO.Path.GetFileName(m_Path) + (m_Error != null ? " (ERROR: " + m_Error + ")" : "");
        public string Error => m_Error;
        public Texture Current => m_Target;
        public bool IsReady => m_Player != null && m_Player.isPrepared && m_Target != null;
        public int Width => m_Target != null ? m_Target.width : 0;
        public int Height => m_Target != null ? m_Target.height : 0;
        public bool HasNewFrame => m_HasNew;
        /// <summary>Clip frame index of the current picture (-1 before the player is ready); tests key their logs on it.</summary>
        public long Frame => m_Player != null && m_Player.isPrepared ? m_Player.frame : -1;
        /// <summary>Clip time in seconds of the current picture.</summary>
        public double ClipTime => m_Player != null && m_Player.isPrepared ? m_Player.time : 0.0;
        /// <summary>Play the clip once and stop at the end instead of looping.</summary>
        public bool Loop = true;

        public void Start()
        {
            // Always a fresh component: a previous source's player may still be pending its deferred Destroy.
            m_Player = m_Host.AddComponent<VideoPlayer>();
            m_Player.playOnAwake = false;
            m_Player.isLooping = Loop;
            m_Player.audioOutputMode = VideoAudioOutputMode.None;
            m_Player.source = VideoSource.Url;
            m_Player.url = m_Path;
            m_Player.renderMode = VideoRenderMode.RenderTexture;
            m_Player.prepareCompleted += OnPrepared;
            m_Player.errorReceived += OnError;
            m_Player.Prepare();
        }

        void OnError(VideoPlayer vp, string message)
        {
            m_Error = message;
            Debug.LogWarning("[MimicMocap] Video error for " + m_Path + ": " + message);
        }

        void OnPrepared(VideoPlayer vp)
        {
            var w = (int)vp.width;
            var h = (int)vp.height;
            m_Target = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            m_Target.Create();
            vp.targetTexture = m_Target;
            vp.Play();
        }

        public void Stop()
        {
            if (m_Player != null) m_Player.Stop();
        }

        public void Update()
        {
            if (m_Player == null || !m_Player.isPrepared) return;
            if (m_Player.frame != m_LastFrame)
            {
                m_LastFrame = m_Player.frame;
                m_HasNew = true;
            }
        }

        public void Consume() => m_HasNew = false;

        public void Dispose()
        {
            if (m_Player != null)
            {
                m_Player.prepareCompleted -= OnPrepared;
                m_Player.errorReceived -= OnError;
                m_Player.Stop();
                UnityEngine.Object.Destroy(m_Player);
            }
            if (m_Target != null)
            {
                m_Target.Release();
                UnityEngine.Object.Destroy(m_Target);
            }
        }
    }

    /// <summary>A single still image, reported as a new frame every update (useful for tests).</summary>
    public sealed class StillImageFrameSource : IFrameSource
    {
        readonly Texture m_Texture;

        public StillImageFrameSource(Texture texture, string name = "Still")
        {
            m_Texture = texture;
            Name = name;
        }

        public string Name { get; }
        public Texture Current => m_Texture;
        public bool IsReady => m_Texture != null;
        public int Width => m_Texture != null ? m_Texture.width : 0;
        public int Height => m_Texture != null ? m_Texture.height : 0;
        public bool HasNewFrame { get; private set; }
        public void Start() { HasNewFrame = true; }
        public void Stop() { }
        public void Update() { HasNewFrame = true; }
        public void Consume() { HasNewFrame = false; }
        public void Dispose() { }
    }
}
