# Start the packaged exe as an AI app does (`OpenRewind.exe mcp`, stdin/stdout as pipes) and talk MCP to it.
#
#   scripts\mcp-check-windows.ps1
#   scripts\mcp-check-windows.ps1 -ExePath dist\win-x64\OpenRewind.exe
#
# Checks that a WinExe answers over redirected pipes with no tray or window, that tools say how to turn
# the switch on while it is off, and that they read the history once it is on. Exits non-zero on failure.
param(
  [string]$ExePath = "dist\win-x64\OpenRewind.exe"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$exe = (Resolve-Path $ExePath).Path
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$home_ = Join-Path $tmp "openrewind-mcp-home"
Remove-Item -Recurse -Force $home_ -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $home_ | Out-Null
$env:OPENREWIND_HOME = $home_

function Start-Mcp([string]$flag) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
  $psi.Arguments = $flag
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
  $p = [System.Diagnostics.Process]::Start($psi)
  # Read stderr in the background so a full pipe never blocks the server.
  $null = $p.StandardError.ReadToEndAsync()
  return $p
}

function Ask($p, [string]$json) {
  $p.StandardInput.Write($json + "`n")
  $p.StandardInput.Flush()
  $task = $p.StandardOutput.ReadLineAsync()
  if (-not $task.Wait(20000)) { throw "no reply within 20 s to $json" }
  if ($null -eq $task.Result) { throw "the server closed stdout (exit code $($p.ExitCode))" }
  return $task.Result | ConvertFrom-Json
}

function Tell($p, [string]$json) { $p.StandardInput.Write($json + "`n"); $p.StandardInput.Flush() }

function Stop-Mcp($p) {
  $p.StandardInput.Close()
  if (-not $p.WaitForExit(10000)) { $p.Kill(); throw "the server did not exit when stdin closed" }
  if ($p.ExitCode -ne 0) { throw "the server exited with code $($p.ExitCode)" }
}

foreach ($flag in @("mcp", "--mcp")) {
  Write-Host "→ $exe $flag (switch off)"
  $p = Start-Mcp $flag
  $init = Ask $p '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ci","version":"1"}}}'
  if ($init.result.protocolVersion -ne "2025-06-18") { throw "initialize: unexpected reply $($init | ConvertTo-Json -Depth 6)" }
  Tell $p '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  $list = Ask $p '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  $names = $list.result.tools | ForEach-Object { $_.name }
  if (-not ($names -contains "search_screen")) { throw "tools/list: search_screen missing ($names)" }
  $off = Ask $p '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"activity_summary","arguments":{}}}'
  if (-not $off.result.isError -or $off.result.content[0].text -notmatch "Allow AI Apps") { throw "switch off: unexpected reply $($off | ConvertTo-Json -Depth 6)" }
  Stop-Mcp $p
  Write-Host "✓ answered over pipes and asked to turn the switch on"
}

Write-Host "→ $exe mcp (switch on)"
'{ "McpEnabled": true }' | Set-Content -Encoding utf8 (Join-Path $home_ "settings.json")
$p = Start-Mcp "mcp"
$null = Ask $p '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}'
$none = Ask $p '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_screen","arguments":{"query":"x"}}}'
if (-not $none.result.isError -or $none.result.content[0].text -notmatch "not recorded anything") { throw "no history: unexpected reply $($none | ConvertTo-Json -Depth 6)" }
Stop-Mcp $p
Write-Host "✓ said there is no history yet"

# Make a history the way the app does, then read it back through MCP.
$null = Start-Process $exe -ArgumentList "delete", "15m" -PassThru -Wait
if (-not (Test-Path (Join-Path $home_ "db.sqlite"))) { throw "the app did not create the database" }
$p = Start-Mcp "mcp"
$null = Ask $p '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}'
$sum = Ask $p '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"activity_summary","arguments":{"start":"today"}}}'
if ($sum.result.isError -or $sum.result.content[0].text -notmatch "Nothing was recorded") { throw "switch on: unexpected reply $($sum | ConvertTo-Json -Depth 6)" }
Stop-Mcp $p
Write-Host "✓ read the history read-only"
