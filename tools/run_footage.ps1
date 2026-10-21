<#
.SYNOPSIS
  Run HandFootageTests once on the install footage with an experiment's settings, and print its report.
  Each run is about 72 s of Unity batch time; the outputs land in Logs\PoseCheck\<clip>_<tag>_report.txt.

.EXAMPLE
  tools\run_footage.ps1 -Tag hold
  tools\run_footage.ps1 -Tag w540 -Vars @{ MIMIC_BODY_WIDTH = '540' }
  tools\run_footage.ps1 -Tag night -Vars @{ MIMIC_TEST_CLIP = 'Night.mp4'; MIMIC_BODY_PROFILE = '1.0,0.5,0,0' }

  The variables HandFootageTests reads are listed in the README under "Experiments on the install footage".
  Set $env:UNITY_EDITOR to a Unity.exe to override the auto-detected editor.
#>
param([string]$Tag = "run", [hashtable]$Vars = @{})
$p = Split-Path -Parent $PSScriptRoot
$u = $env:UNITY_EDITOR
if (-not $u) {
    $wanted = (Select-String -Path (Join-Path $p "ProjectSettings\ProjectVersion.txt") -Pattern "^m_EditorVersion:\s*(\S+)" | Select-Object -First 1).Matches[0].Groups[1].Value
    $u = Join-Path ${env:ProgramFiles} "Unity\Hub\Editor\$wanted\Editor\Unity.exe"
}
if (-not (Test-Path $u)) { Write-Output "Unity editor not found; set `$env:UNITY_EDITOR"; exit 1 }
foreach ($k in $Vars.Keys) { Set-Item -Path "env:$k" -Value $Vars[$k] }
$env:MIMIC_RUN_TAG = $Tag
$logs = Join-Path $p "Logs"
New-Item -ItemType Directory -Force $logs | Out-Null
$log = "$logs\footage_$Tag.log"
$xml = "$logs\footage_$Tag.xml"
Remove-Item $xml -ErrorAction SilentlyContinue
$t0 = Get-Date
& $u -batchmode -projectPath $p -logFile $log -runTests -testPlatform PlayMode -testResults $xml -testFilter HandFootageTests | Out-Null
Write-Output ("== {0} exit={1} in {2} s" -f $Tag, $LASTEXITCODE, [int]((Get-Date) - $t0).TotalSeconds)
Select-String -Path $log -Pattern "error CS" | Select-Object -First 10 | ForEach-Object { Write-Output ("  " + $_.Line) }
if (Test-Path $xml) {
    [xml]$x = Get-Content $xml
    $x.SelectNodes("//test-case") | ForEach-Object { Write-Output ("  {0}: {1}" -f $_.name, $_.result); if ($_.result -ne 'Passed' -and $_.failure) { Write-Output ("    " + $_.failure.message.'#cdata-section') } }
}
$clip = if ($Vars.ContainsKey('MIMIC_TEST_CLIP')) { $Vars['MIMIC_TEST_CLIP'] } else { 'TestHands.mp4' }
$report = Join-Path $logs ("PoseCheck\" + [System.IO.Path]::GetFileNameWithoutExtension($clip).ToLowerInvariant() + "_" + $Tag + "_report.txt")
if (Test-Path $report) { Get-Content $report } else { Write-Output "  no report at $report" }
foreach ($k in $Vars.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
Remove-Item env:MIMIC_RUN_TAG -ErrorAction SilentlyContinue
