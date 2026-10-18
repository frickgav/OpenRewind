# OpenRewind for Windows

**Rewind your PC.** The Windows version of OpenRewind: it records your screen and
audio, pulls the text out, and lets you search or scroll back through everything
you saw and heard. 100% local. Same data format, search page and API as the Mac app.

Needs Windows 10 (version 2004 or later) or Windows 11, x64 or ARM64.

## Install

1. Download `OpenRewind-<version>-win-x64.zip` (or `-arm64`) and unzip it anywhere,
   for example `%LOCALAPPDATA%\Programs\OpenRewind`.
2. Run `OpenRewind.exe`. Windows SmartScreen may say "Windows protected your PC"
   until the app is code-signed: click **More info → Run anyway**.
3. It lives in the tray (bottom right, maybe under **^**).

## Use it

- **Record Screen** and **Record Audio** switch on and off on their own (remembered).
- **Open Timeline**, a left-click on the tray icon, or **Ctrl+Alt+T** from anywhere shows a
  full-screen overlay. Scroll or use ← → to move through time, Shift for 1 minute, ↑ ↓ for
  10 minutes, drag or click the bar, **space** plays the audio at that moment (the picture
  follows along), **+ / −** zoom, **N** jumps to now, **O** opens the search page in the browser (and turns the local search server on), **/** or **Ctrl+F** searches,
  **?** lists every key, **Esc** closes.
- **Search…** opens the timeline with the cursor in its search box, or the web search page
  when the local search server is on.
- **Settings → Keep Screen / Audio Recordings For**: 1 Day, 1 Week, 1 Month, 1 Year,
  or Until I Delete Them (default). Older recordings are deleted at launch and every hour.
- **Settings → Delete Recordings**: last 15 minutes, last hour, today, all screen,
  all audio, or everything (asks first).
- **Settings → Local Search Server** turns on the API and browser search page at
  http://127.0.0.1:3031. Off by default. Only this PC can connect. The terminal commands
  `record` and `serve` always start it.
- **Settings → Launch at Login** starts it when you sign in.
- **Updates**: a newer release shows a banner at the top of the timeline (✕ hides it until the
  next version) and a row in the tray menu. **Settings → Download Updates Automatically** (on by
  default) downloads it in the background; **Update** / **Restart to Update** swaps `OpenRewind.exe`
  in place and restarts. Keep the app in a folder you can write to (not Program Files), or the button
  opens the download in the browser instead.
- The lock screen and screen saver are never recorded. Password fields are never read.
  Password managers (1Password, Bitwarden, KeePass, Dashlane, LastPass) are skipped;
  set `OPENREWIND_EXCLUDE` to your own comma list to change that.
- Log: `%USERPROFILE%\.openrewind\openrewind.log`.

## How it works on Windows

| | Mac | Windows |
|---|---|---|
| Screenshot | ScreenCaptureKit | GDI screen copy of the monitor under the mouse |
| Text | Accessibility tree, then Apple Vision OCR | UI Automation tree, then Windows OCR (`Windows.Media.Ocr`) |
| Microphone | AVAudioEngine | WASAPI |
| System audio | ScreenCaptureKit | WASAPI loopback (no permission needed) |
| Speech to text | whisper.cpp `whisper-cli` | whisper.cpp `whisper-cli.exe` |
| Database, API, search page | SQLite + FTS5, port 3031 | the same |
| App | Menu bar, ⌥⌘T | Tray, Ctrl+Alt+T |

Windows OCR uses the languages installed for your Windows display language. If
`OpenRewind.exe doctor` says OCR is missing, add a language with the OCR feature in
**Settings → Time & language → Language & region**.

If the microphone does not record, allow it in **Settings → Privacy & security →
Microphone → Let desktop apps access your microphone**.

## Whisper

Speech is turned into text by [whisper.cpp](https://github.com/ggml-org/whisper.cpp),
on your PC. OpenRewind records audio without it; the clips are transcribed once it is set up:

1. From the [whisper.cpp releases](https://github.com/ggml-org/whisper.cpp/releases), download
   `whisper-bin-x64.zip` and unzip it into `%USERPROFILE%\.openrewind\whisper`
   (so that `whisper-cli.exe` is in that folder or in `whisper\Release`).
2. Download a model, for example
   [`ggml-large-v3-turbo-q5_0.bin`](https://huggingface.co/ggerganov/whisper.cpp/tree/main)
   (good, ~550 MB) or `ggml-base.en.bin` (fast, English only, ~150 MB), into
   `%USERPROFILE%\.openrewind\models`.
3. Quit and reopen OpenRewind. `OpenRewind.exe doctor` shows what it found.

`--whisper-bin` and `--whisper-model` (or `OPENREWIND_WHISPER_MODEL`) point at other places.

## Run from the terminal

`OpenRewind.exe` with no command runs the tray app. Commands print to the terminal:

```powershell
.\OpenRewind.exe doctor
.\OpenRewind.exe record          # record + API, no tray icon
.\OpenRewind.exe delete older:week
.\OpenRewind.exe --help
```

## Build it

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
(`winget install Microsoft.DotNet.SDK.8`).

```powershell
scripts\build-windows.ps1              # → dist\OpenRewind-<version>-win-x64.zip
scripts\build-windows.ps1 -Arch arm64
```

Or open `windows\OpenRewind.sln` in Visual Studio 2022 or Rider.
GitHub Actions builds both zips on every change (`.github/workflows/windows.yml`).

The code:

- `windows/OpenRewind.Core`: everything that is not Windows-specific (database, search,
  API server, Whisper, timeline math). It builds and its tests run on Windows, macOS
  and Linux: `dotnet test windows/OpenRewind.Tests`.
- `windows/OpenRewind`: the Windows app (capture, OCR, UI Automation, audio, tray, timeline).
  It builds on any OS (`EnableWindowsTargeting`) but only runs on Windows.
- The search page is not copied: the build reads it straight out of
  `Sources/openrewind/WebUI.swift`, so a change there shows up on both.

## Code signing

Unsigned downloads get the SmartScreen "Windows protected your PC" warning. The GitHub
Actions build signs `OpenRewind.exe` with [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
once these repository secrets are set (Settings → Secrets and variables → Actions):

| Secret | What it is |
| --- | --- |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | An app registration that has the *Trusted Signing Certificate Profile Signer* role on the signing account |
| `AZURE_SIGNING_ENDPOINT` | The account's region endpoint, e.g. `https://eus.codesigning.azure.net/` |
| `AZURE_SIGNING_ACCOUNT` | The Trusted Signing account name |
| `AZURE_SIGNING_PROFILE` | The certificate profile name (Public Trust) |

Without them the build still works and the exe is unsigned. To sign a local build with a
certificate file instead: `$env:SIGN_CERT = "cert.pfx"; $env:SIGN_PASSWORD = "..."` before
running `scripts\build-windows.ps1`.

## Not yet on Windows

- An installer
- Screens with protected video (some streaming apps) come out black, as in other screen recorders
- Only the monitor under the mouse is recorded (same as the Mac app)
