<#
.SYNOPSIS
  Batch-mode pipeline for MimicMocap: regenerate the scene, place the characters, run the
  EditMode and PlayMode tests, optionally build the Windows player. Prints a compact summary.

.EXAMPLE
  tools\run_pipeline.ps1                      # scene + characters + all tests
  tools\run_pipeline.ps1 -SkipScene -Build    # tests then a player build
  tools\run_pipeline.ps1 -SkipScene -TestFilter "StabilityTests"

  Set $env:UNITY_EDITOR to a Unity.exe to override the auto-detected 6000.3 editor.
#>
param(
    [switch]$SkipScene,
    [switch]$SkipTests,
    [switch]$Build,
    [string]$TestFilter = ""
)
$ErrorActionPreference = "Continue"

$p = Split-Path -Parent $PSScriptRoot
$u = $env:UNITY_EDITOR
if (-not $u) {
    # Use exactly the editor the project was last saved with (ProjectSettings/ProjectVersion.txt); any other
    # version rewrites the project files on launch. Fall back to the newest installed 6000.3.x by numeric version.
    $hub = Join-Path ${env:ProgramFiles} "Unity\Hub\Editor"
    $wanted = (Select-String -Path (Join-Path $p "ProjectSettings\ProjectVersion.txt") -Pattern "^m_EditorVersion:\s*(\S+)" | Select-Object -First 1).Matches[0].Groups[1].Value
    if ($wanted -and (Test-Path (Join-Path $hub "$wanted\Editor\Unity.exe"))) {
        $u = Join-Path $hub "$wanted\Editor\Unity.exe"
    } else {
        $candidate = Get-ChildItem $hub -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^6000\.3\.(\d+)" } |
            Sort-Object { [int]([regex]::Match($_.Name, "^6000\.3\.(\d+)").Groups[1].Value) } -Descending |
            Select-Object -First 1
        if ($candidate) { $u = Join-Path $candidate.FullName "Editor\Unity.exe" }
        if ($wanted) { Write-Output "warning: project wants Unity $wanted, which is not installed; using $u" }
    }
}
if (-not $u -or -not (Test-Path $u)) { Write-Output "Unity editor not found; set `$env:UNITY_EDITOR"; exit 1 }
Write-Output ("unity: " + $u)
$logs = Join-Path $p "Logs"
New-Item -ItemType Directory -Force $logs | Out-Null
$script:code = 0

function Run-Unity([string]$label, [string[]]$unityArgs, [string]$log) {
    Remove-Item $log -ErrorAction SilentlyContinue
    & $u -batchmode -projectPath $p -logFile $log @unityArgs | Out-Null
    $script:code = $LASTEXITCODE
    Write-Output ("{0} exit={1}" -f $label, $script:code)
    Select-String -Path $log -Pattern "error CS|Scripts have compiler errors|\[MimicMocap\]" | Select-Object -First 40 | ForEach-Object { Write-Output ("  " + $_.Line) }
}

function Report-Tests([string]$label, [string]$xml, [string]$log) {
    if (Test-Path $xml) {
        $x = [xml](Get-Content $xml)
        $r = $x.'test-run'
        Write-Output ("{0}: total {1} passed {2} failed {3} inconclusive {4} skipped {5}" -f $label, $r.total, $r.passed, $r.failed, $r.inconclusive, $r.skipped)
        $x.SelectNodes("//test-case") | ForEach-Object {
            Write-Output ("  {0}: {1} ({2}s)" -f $_.name, $_.result, $_.duration)
            if ($_.result -ne 'Passed') {
                if ($_.failure) {
                    Write-Output ("    " + $_.failure.message.'#cdata-section')
                    $st = $_.failure.'stack-trace'.'#cdata-section'
                    if ($st) { Write-Output ("    " + (($st -split "`n" | Select-Object -First 3) -join " | ")) }
                }
                if ($_.reason) { Write-Output ("    " + $_.reason.message.'#cdata-section') }
            }
        }
    } else {
        Write-Output ("{0}: no results xml" -f $label)
        Select-String -Path $log -Pattern "error CS|Exception|Scripts have compiler errors" | Select-Object -First 30 | ForEach-Object { Write-Output ("  " + $_.Line) }
    }
}

if (-not $SkipScene) {
    Run-Unity "scene" @("-quit", "-executeMethod", "MimicMocap.Editor.MimicBuildTools.CreateScene") "$logs\scene.log"
    if ($script:code -ne 0) { exit $script:code }
    Run-Unity "chars" @("-quit", "-executeMethod", "MimicMocap.Editor.MimicCharacterTools.SetupCharacters") "$logs\chars.log"
    if ($script:code -ne 0) { exit $script:code }
}

if (-not $SkipTests) {
    $filterArgs = @()
    if ($TestFilter -ne "") { $filterArgs = @("-testFilter", $TestFilter) }
    Remove-Item "$logs\editmode_results.xml" -ErrorAction SilentlyContinue
    Run-Unity "editmode" (@("-runTests", "-testPlatform", "EditMode", "-testResults", "$logs\editmode_results.xml") + $filterArgs) "$logs\editmode.log"
    Report-Tests "editmode" "$logs\editmode_results.xml" "$logs\editmode.log"
    Remove-Item "$logs\playmode_results.xml" -ErrorAction SilentlyContinue
    Run-Unity "playmode" (@("-runTests", "-testPlatform", "PlayMode", "-testResults", "$logs\playmode_results.xml") + $filterArgs) "$logs\playmode.log"
    Report-Tests "playmode" "$logs\playmode_results.xml" "$logs\playmode.log"
    Select-String -Path "$logs\playmode.log" -Pattern "Character menu report|VMC loopback|worst limb|worst finger|Stability report|Two performer report|Maxine multi-person report" -Context 0,8 | Select-Object -First 8 | ForEach-Object { Write-Output $_.Line; $_.Context.PostContext | ForEach-Object { Write-Output ("    " + $_) } }
}

if ($Build) {
    Run-Unity "build" @("-quit", "-executeMethod", "MimicMocap.Editor.MimicBuildTools.BuildPlayer") "$logs\build.log"
    if ($script:code -ne 0) { exit $script:code }
}
