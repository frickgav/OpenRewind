# Build OpenRewind.exe (Windows) as one self-contained file you can put online.
#
#   scripts\build-windows.ps1                      x64
#   scripts\build-windows.ps1 -Arch arm64          Windows on ARM
#   $env:VERSION = "0.2.0"; scripts\build-windows.ps1
#
# Needs the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8). Output: dist\OpenRewind-<version>-win-<arch>.zip
param([ValidateSet("x64", "arm64")] [string]$Arch = "x64")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$Version = if ($env:VERSION) { $env:VERSION } else { "0.2.0" }
$Out = "dist\win-$Arch"
$Zip = "dist\OpenRewind-$Version-win-$Arch.zip"

Write-Host "→ testing the shared core"
dotnet test windows\OpenRewind.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "→ publishing ($Arch, self-contained, single file)"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
dotnet publish windows\OpenRewind -c Release -r "win-$Arch" --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:Version=$Version -o $Out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Optional code signing (downloads without a SmartScreen warning need a code-signing certificate):
#   $env:SIGN_CERT = "path\to\cert.pfx"; $env:SIGN_PASSWORD = "..."
if ($env:SIGN_CERT) {
  Write-Host "→ signing"
  $signtool = (Get-Command signtool -ErrorAction SilentlyContinue).Source
  if (-not $signtool) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
      Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
  }
  if (-not $signtool) { throw "signtool not found: install the Windows SDK" }
  & $signtool sign /f $env:SIGN_CERT /p $env:SIGN_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "$Out\OpenRewind.exe"
  if ($LASTEXITCODE -ne 0) { throw "signing failed" }
}

Write-Host "→ zipping $Zip"
Copy-Item windows\README.md "$Out\README.md"
Copy-Item LICENSE "$Out\LICENSE.txt"
Copy-Item THIRD_PARTY_NOTICES.txt "$Out\THIRD_PARTY_NOTICES.txt"
if (Test-Path $Zip) { Remove-Item $Zip }
Compress-Archive -Path "$Out\*" -DestinationPath $Zip
Write-Host "✓ $Out\OpenRewind.exe"
Write-Host "✓ $Zip ($([math]::Round((Get-Item $Zip).Length / 1MB)) MB)"
