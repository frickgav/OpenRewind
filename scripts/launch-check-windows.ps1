# Start the packaged tray app the way a double-click does and make sure it is still running later.
#
#   scripts\launch-check-windows.ps1
#   scripts\launch-check-windows.ps1 -ExePath dist\win-x64\OpenRewind.exe -Seconds 30
#
# Run this against the exe that is about to be shipped, so a build that crashes on startup fails
# the workflow instead of being published. Exits non-zero if the process is gone or wrote a crash.log.
param(
  [string]$ExePath = "dist\win-x64\OpenRewind.exe",
  [int]$Seconds = 30
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$exe = (Resolve-Path $ExePath).Path
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$home_ = Join-Path $tmp "openrewind-home"
New-Item -ItemType Directory -Force $home_ | Out-Null
$env:OPENREWIND_HOME = $home_
$crash = Join-Path $env:LOCALAPPDATA "OpenRewind\crash.log"
Remove-Item $crash -ErrorAction SilentlyContinue

Write-Host "→ launching $exe for $Seconds s (OPENREWIND_HOME=$home_)"
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds $Seconds
$alive = -not $p.HasExited
Write-Host "--- openrewind.log"; Get-Content (Join-Path $home_ "openrewind.log") -ErrorAction SilentlyContinue
Write-Host "--- crash.log"; Get-Content $crash -ErrorAction SilentlyContinue
if ($alive) { Stop-Process -Id $p.Id -Force } else { Write-Host "exit code $($p.ExitCode)" }
if (-not $alive -or (Test-Path $crash)) { throw "OpenRewind did not keep running after launch" }
Write-Host "✓ still running after $Seconds s"
