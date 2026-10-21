<#
.SYNOPSIS
  Download the ONNX models the app runs (about 155 MB) into Assets/MimicMocap/Models and verify each one.

  Run this once after cloning, BEFORE opening the project in Unity: the scene refers to the models through the
  .meta files that are checked in, and Unity deletes a .meta whose asset is missing. (If you opened the project
  first, run this, then MimicMocap > Create Scene to re-link the models.)

  Sources (all Apache-2.0):
    BlazePose detector + landmarkers   https://huggingface.co/unity/inference-engine-blaze-pose
    MediaPipe hand landmarker          https://huggingface.co/unity/inference-engine-blaze-hand
    RTMPose-m hand5 (hand fallback)    https://github.com/open-mmlab/mmpose/tree/main/projects/rtmpose

.EXAMPLE
  tools\get_models.ps1            # download what is missing, verify everything
  tools\get_models.ps1 -Force     # download again even if present
#>
param([switch]$Force)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root "Assets\MimicMocap\Models"
$hfPose = "https://huggingface.co/unity/inference-engine-blaze-pose/resolve/main/models"
$hfHand = "https://huggingface.co/unity/inference-engine-blaze-hand/resolve/main/models"

# Path under Models, URL, SHA-256 of the exact file the project was tested with.
$files = @(
    @("BlazePose\pose_detection.onnx",                "$hfPose/pose_detection.onnx",                "72081da8481170bc6d8fafa716455ee210b61a8cefed84c67fcbbf889a4c38cf"),
    @("BlazePose\pose_landmarks_detector_lite.onnx",  "$hfPose/pose_landmarks_detector_lite.onnx",  "c9661bfb07f5baf0638cd67263d16b71d6512ae94908a069be0ade7f529b7248"),
    @("BlazePose\pose_landmarks_detector_full.onnx",  "$hfPose/pose_landmarks_detector_full.onnx",  "ae17ee8f076a5bbc28f65b939f46139c10f10c51ec4392a011e56d06d3f76c5d"),
    @("BlazePose\pose_landmarks_detector_heavy.onnx", "$hfPose/pose_landmarks_detector_heavy.onnx", "717c32b11d95e214612e2ff0edf825c38a8dd24c9b7c99f34f52f89431c68453"),
    @("BlazeHand\hand_landmarks_detector.onnx",       "$hfHand/hand_landmarks_detector.onnx",       "e18a95135b40c732ea53d2dd6af66cbec6d3f8bf0296bb529a4c4be0e8349ec1")
)
# RTMPose ships inside the OpenMMLab ONNX SDK zip; the model is its end2end.onnx.
$rtmZip = "https://download.openmmlab.com/mmpose/v1/projects/rtmposev1/onnx_sdk/rtmpose-m_simcc-hand5_pt-aic-coco_210e-256x256-74fb594_20230320.zip"
$rtmZipSha = "45c9e1aa21b8fe33859973c49bce4647645d312e7a226e398a42f9137d44a56d"
$rtmPath = "RTMPose\rtmpose_m_hand5.onnx"
$rtmSha = "39e858936bca0f94c09847d4e70b68a51d6c0adac61f36b457fcadb54621cd29"

function Sha([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }

function Fetch([string]$url, [string]$dest) {
    $tmp = "$dest.download"
    Write-Output "  downloading $url"
    $old = $ProgressPreference; $ProgressPreference = "SilentlyContinue"   # the progress bar makes Invoke-WebRequest very slow
    try { Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing } finally { $ProgressPreference = $old }
    Move-Item -Force $tmp $dest
}

$failed = 0
foreach ($f in $files) {
    $dest = Join-Path $models $f[0]
    New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
    if ($Force -or -not (Test-Path $dest) -or (Sha $dest) -ne $f[2]) { Fetch $f[1] $dest }
    if ((Sha $dest) -eq $f[2]) { Write-Output "ok   $($f[0])" } else { Write-Output "FAIL $($f[0]): checksum mismatch"; $failed++ }
}

$dest = Join-Path $models $rtmPath
New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
if ($Force -or -not (Test-Path $dest) -or (Sha $dest) -ne $rtmSha) {
    $zip = Join-Path ([System.IO.Path]::GetTempPath()) "rtmpose_hand5.zip"
    Fetch $rtmZip $zip
    if ((Sha $zip) -ne $rtmZipSha) { Write-Output "FAIL RTMPose zip: checksum mismatch"; $failed++ }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -like "*end2end.onnx" } | Select-Object -First 1
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        } finally { $archive.Dispose() }
    }
    Remove-Item $zip -ErrorAction SilentlyContinue
}
if ((Test-Path $dest) -and (Sha $dest) -eq $rtmSha) { Write-Output "ok   $rtmPath" } else { Write-Output "FAIL $rtmPath"; $failed++ }

if ($failed -gt 0) { Write-Output "$failed model(s) failed; see above."; exit 1 }
Write-Output "All models present and verified."
