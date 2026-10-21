<#
.SYNOPSIS
  Record a clip from the install camera into ..\TestVideos (next to the project) with ffmpeg, so the PlayMode
  tests and the P/N hotkeys can replay exactly what the camera saw.

.EXAMPLE
  tools\record_camera.ps1                                   # 90 s from the Arducam after a 10 s lead
  tools\record_camera.ps1 -Seconds 60 -Lead 20 -Name "porch night hands"
  tools\record_camera.ps1 -Camera "Integrated" -Format yuyv422 -Rotate 0

  The install camera is mounted on its side; -Rotate (degrees clockwise, default 90) bakes the turn into the clip so
  it plays upright in the app and the tests, like the webcam path does live.

  One beep when the recording starts, two when it ends. ffmpeg comes from PATH or the winget package
  (winget install Gyan.FFmpeg). The camera must not be open in the app at the same time.
#>
param(
    [string]$Camera = "Arducam B0590 1080P Day/Night",
    [int]$Seconds = 90,
    [int]$Lead = 10,
    [string]$Name = "",
    [string]$Format = "mjpeg",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$Fps = 30,
    [int]$Rotate = 90
)
$ErrorActionPreference = "Stop"

$ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ff) {
    $ff = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ff) { Write-Output "ffmpeg not found; winget install Gyan.FFmpeg"; exit 1 }

$project = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path (Split-Path -Parent $project) "TestVideos"
New-Item -ItemType Directory -Force $outDir | Out-Null
if ($Name -eq "") { $Name = "camera " + (Get-Date -Format "yyyy-MM-dd HHmm") }
$out = Join-Path $outDir ($Name + ".mp4")

# MJPEG is the only 30 fps mode at 1080p over USB 2; raw YUY2 is offered at lower rates or sizes.
$inputFormat = if ($Format -eq "mjpeg") { @("-vcodec", "mjpeg") } else { @("-pixel_format", $Format) }
$filter = switch ((($Rotate % 360) + 360) % 360) { 90 { "transpose=1" } 180 { "hflip,vflip" } 270 { "transpose=2" } default { "null" } }

Write-Output ("camera: " + $Camera)
Write-Output ("output: " + $out)
Write-Output ("recording {0} s at {1}x{2} @ {3} fps, rotated {4} deg clockwise, after a {5} s lead" -f $Seconds, $Width, $Height, $Fps, $Rotate, $Lead)
for ($i = $Lead; $i -gt 0; $i--) { Write-Output ("  starting in {0}" -f $i); Start-Sleep -Seconds 1 }
[console]::Beep(880, 400)
Write-Output "RECORDING"

$args = @("-hide_banner", "-loglevel", "warning", "-stats", "-y",
    "-f", "dshow", "-rtbufsize", "512M", "-video_size", "${Width}x${Height}", "-framerate", "$Fps") + $inputFormat + @(
    "-i", "video=$Camera", "-t", "$Seconds", "-vf", $filter,
    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an",
    $out)
& $ff @args
$code = $LASTEXITCODE
[console]::Beep(880, 200); Start-Sleep -Milliseconds 150; [console]::Beep(880, 200)
if ($code -ne 0 -or -not (Test-Path $out)) { Write-Output "ffmpeg exit $code; no clip"; exit 1 }

$probe = Join-Path (Split-Path -Parent $ff) "ffprobe.exe"
if (Test-Path $probe) {
    $info = & $probe -v error -select_streams v:0 -show_entries "stream=width,height,avg_frame_rate,nb_frames:format=duration" -of default=noprint_wrappers=1 $out 2>&1
    Write-Output ($info -join "  ")
}
Write-Output ("saved: " + $out + " (" + [int]((Get-Item $out).Length / 1MB) + " MB)")
