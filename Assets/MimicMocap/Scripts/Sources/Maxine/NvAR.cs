using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MimicMocap.Maxine
{
    /// <summary>
    /// P/Invoke bindings for the NVIDIA Maxine AR SDK (nvARPose.dll) and NvCVImage (NVCVImage.dll).
    /// The DLLs are installed by the public "NVIDIA AR SDK" Broadcast redistributable into
    /// %ProgramFiles%\NVIDIA Corporation\NVIDIA AR SDK, or wherever %NV_AR_SDK_PATH% points.
    /// Header reference: https://github.com/NVIDIA-Maxine/Maxine-AR-SDK (MIT).
    /// </summary>
    public static class NvAR
    {
        const string ArLib = "nvARPose";
        const string CvLib = "NVCVImage";

        // Feature IDs (NvAR_FeatureID is a const char*)
        public const string FeatureBodyDetection = "BodyDetection";
        public const string FeatureBodyPoseEstimation = "BodyPoseEstimation";

        // Parameter names, matching the NvAR_Parameter_Config/Input/Output macros.
        public static class Config
        {
            public const string CUDAStream = "NvAR_Parameter_Config_CUDAStream";
            public const string ModelDir = "NvAR_Parameter_Config_ModelDir";
            public const string TRTModelDir = "NvAR_Parameter_Config_TRTModelDir";
            public const string BatchSize = "NvAR_Parameter_Config_BatchSize";
            public const string Mode = "NvAR_Parameter_Config_Mode";
            public const string NumKeyPoints = "NvAR_Parameter_Config_NumKeyPoints";
            public const string ReferencePose = "NvAR_Parameter_Config_ReferencePose";
            public const string Temporal = "NvAR_Parameter_Config_Temporal";
            public const string UseCudaGraph = "NvAR_Parameter_Config_UseCudaGraph";
            public const string FocalLength = "NvAR_Parameter_Config_FocalLength";
            public const string TrackPeople = "NvAR_Parameter_Config_TrackPeople";
            public const string ShadowTrackingAge = "NvAR_Parameter_Config_ShadowTrackingAge";
            public const string ProbationAge = "NvAR_Parameter_Config_ProbationAge";
            public const string MaxTargetsTracked = "NvAR_Parameter_Config_MaxTargetsTracked";
        }

        public static class Input
        {
            public const string Image = "NvAR_Parameter_Input_Image";
            public const string BoundingBoxes = "NvAR_Parameter_Input_BoundingBoxes";
            public const string FocalLength = "NvAR_Parameter_Input_FocalLength";
        }

        public static class Output
        {
            public const string BoundingBoxes = "NvAR_Parameter_Output_BoundingBoxes";
            public const string BoundingBoxesConfidence = "NvAR_Parameter_Output_BoundingBoxesConfidence";
            public const string TrackingBoundingBoxes = "NvAR_Parameter_Output_TrackingBoundingBoxes";
            public const string KeyPoints = "NvAR_Parameter_Output_KeyPoints";
            public const string KeyPoints3D = "NvAR_Parameter_Output_KeyPoints3D";
            public const string JointAngles = "NvAR_Parameter_Output_JointAngles";
            public const string KeyPointsConfidence = "NvAR_Parameter_Output_KeyPointsConfidence";
        }

        public const int NVCV_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public float x, y, width, height; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BBoxes
        {
            public IntPtr boxes;   // NvAR_Rect*
            public byte num_boxes;
            public byte max_boxes;
        }

        /// <summary>NvAR_TrackingBBox: a box plus the multi-person tracker's id (stable across frames).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct TrackingBBox
        {
            public Rect bbox;
            public ushort tracking_id;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TrackingBBoxes
        {
            public IntPtr boxes;   // NvAR_TrackingBBox*
            public byte num_boxes;
            public byte max_boxes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point2f { public float x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point3f { public float x, y, z; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Quat { public float x, y, z, w; }

        /// <summary>Mirror of the C NvCVImage struct (fields only; C++ methods don't affect layout).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CVImage
        {
            public uint width;
            public uint height;
            public int pitch;
            public int pixelFormat;     // NvCVImage_PixelFormat
            public int componentType;   // NvCVImage_ComponentType
            public byte pixelBytes;
            public byte componentBytes;
            public byte numComponents;
            public byte planar;
            public byte gpuMem;
            public byte colorspace;
            public byte reserved0;
            public byte reserved1;
            public IntPtr pixels;
            public IntPtr deletePtr;
            public IntPtr deleteProc;
            public ulong bufferBytes;
        }

        public const int NVCV_BGR = 5;
        public const int NVCV_RGB = 4;
        public const int NVCV_BGRA = 7;
        public const int NVCV_RGBA = 6;
        public const int NVCV_U8 = 1;
        public const uint NVCV_CHUNKY = 0;
        public const uint NVCV_CPU = 0;
        public const uint NVCV_GPU = 1;

        // ---- nvARPose.dll ----
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvAR_GetVersion(out uint version);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_Create(string featureID, out IntPtr handle);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvAR_Load(IntPtr handle);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvAR_Run(IntPtr handle);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvAR_Destroy(IntPtr handle);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvAR_CudaStreamCreate(out IntPtr stream);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvAR_CudaStreamDestroy(IntPtr stream);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetU32(IntPtr handle, string name, uint val);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetS32(IntPtr handle, string name, int val);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetF32(IntPtr handle, string name, float val);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetObject(IntPtr handle, string name, IntPtr ptr, uint typeSize);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetString(IntPtr handle, string name, string str);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetCudaStream(IntPtr handle, string name, IntPtr stream);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_SetF32Array(IntPtr handle, string name, IntPtr vals, int count);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_GetU32(IntPtr handle, string name, out uint val);
        [DllImport(ArLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public static extern int NvAR_GetObject(IntPtr handle, string name, out IntPtr ptr, uint typeSize);

        // ---- NVCVImage.dll ----
        [DllImport(CvLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvCVImage_Alloc(ref CVImage im, uint width, uint height, int format, int type, uint layout, uint memSpace, uint alignment);
        [DllImport(CvLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvCVImage_Init(ref CVImage im, uint width, uint height, int pitch, IntPtr pixels, int format, int type, uint layout, uint memSpace);
        [DllImport(CvLib, CallingConvention = CallingConvention.Cdecl)] public static extern int NvCVImage_Transfer(ref CVImage src, ref CVImage dst, float scale, IntPtr stream, ref CVImage tmp);
        [DllImport(CvLib, CallingConvention = CallingConvention.Cdecl)] public static extern void NvCVImage_Dealloc(ref CVImage im);

        // ---- kernel32 helpers so the loader finds the SDK folder ----
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);

        static bool s_Probed;
        static bool s_Loaded;
        static string s_SdkPath;
        static string s_Error;

        public static string SdkPath => s_SdkPath;
        public static string LoadError => s_Error;

        /// <summary>Locate the SDK folder and preload its DLLs. Safe to call repeatedly.</summary>
        public static bool EnsureLoaded()
        {
            if (s_Probed) return s_Loaded;
            s_Probed = true;

            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("NV_AR_SDK_PATH"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", @"NVIDIA Corporation\NVIDIA AR SDK"),
            };

            foreach (var dir in candidates)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var dll = Path.Combine(dir, "nvARPose.dll");
                if (!File.Exists(dll)) continue;

                try
                {
                    SetDllDirectoryW(dir);
                    var cv = LoadLibraryW(Path.Combine(dir, "NVCVImage.dll"));
                    var ar = LoadLibraryW(dll);
                    if (ar == IntPtr.Zero || cv == IntPtr.Zero)
                    {
                        s_Error = $"LoadLibrary failed in {dir} (err {Marshal.GetLastWin32Error()})";
                        continue;
                    }
                    var status = NvAR_GetVersion(out var version);
                    if (status != NVCV_SUCCESS)
                    {
                        s_Error = $"NvAR_GetVersion returned {status}";
                        continue;
                    }
                    s_SdkPath = dir;
                    s_Loaded = true;
                    Debug.Log($"[MimicMocap] NVIDIA AR SDK {FormatVersion(version)} loaded from {dir}");
                    return true;
                }
                catch (Exception e)
                {
                    s_Error = e.GetType().Name + ": " + e.Message;
                }
            }

            if (string.IsNullOrEmpty(s_Error))
                s_Error = "nvARPose.dll not found. Install tools/nvidia_ar_sdk_installer_v0.8.7_ada.exe";
            return false;
        }

        public static string ModelDir => s_SdkPath == null ? null : Path.Combine(s_SdkPath, "models");

        static string FormatVersion(uint v) => $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

        public static void Check(int status, string what)
        {
            if (status != NVCV_SUCCESS) throw new InvalidOperationException($"{what} failed with NvCV status {status}");
        }
    }
}
