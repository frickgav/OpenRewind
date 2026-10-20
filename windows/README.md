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
- **Pause Recording**: for 30 minutes, 1 hour, 1 day, or until you resume. Both screen and
  audio stop; the menu shows **Resume Recording** and when recording comes back on (also
  after a restart). The switches still pick what comes back on.
- **Open Timeline**, a left-click on the tray icon, or **Ctrl+Alt+T** from anywhere shows a
  full-screen overlay. Scroll or use ← → to move through time, Shift for 1 minute, ↑ ↓ for
  10 minutes, drag or click the bar, **space** plays the audio at that moment (the picture
  follows along), **+ / −** zoom, **N** jumps to now, **/** or **Ctrl+F** searches,
  **?** lists every key, **Esc** closes.
- **O** (or the button at the bottom right of the screenshot) goes back to that moment: it
  reopens the browser tab in the same browser (Chrome, Edge, Arc, Brave, Firefox, Vivaldi, Opera),
  or brings the app's window with that title to the front, starting the app if it is closed.
  The tab's address is saved with each screenshot. Windows apps do not say which file a window
  shows, so documents come back as their app window (the Mac app reopens the file itself).
- **App filter**: the **All apps** button next to the search box (or **A** for the app in the
  shown screenshot) limits the timeline to the moments when one app was in front. Time spent in
  other apps shrinks to breaks on the bar, ← → skip straight between that app's screenshots, and
  search only finds that app's screen text (and speech heard while it was in front).
- **Copy and export**: drag across text on a screenshot (Windows OCR reads it when you stop on it)
  and **Ctrl+C** copies it; with nothing selected **Ctrl+C** copies the screenshot, **Ctrl+S** saves
  it as a PNG, **[** and **]** mark a clip and **Ctrl+E** exports it as an MP4 (H.264, with the encoder
  built into Windows) or an animated GIF, at real time or sped up. It shows the size and time to
  expect first, then the frame it is on and the time left, with Cancel.
  Right-click the screenshot or use the share button at the top for the same, plus **Copy All Text**.
  Clips use only the screenshots already saved and the app filter, and leave out blocked apps and
  sites; see [Copy and export](../README.md#copy-and-export).
- **Search…** opens the timeline with the cursor in its search box, or the web search page
  when the local search server is on.
- **Journal…** shows one page per day: top apps, sites and windows worked out on this PC, plus an
  AI recap when you ask for one (see [Journal](../README.md#journal)).
  **Settings → Write Each Day's Recap Automatically** is off by default, since it spends tokens;
  when on, it sends yesterday's history to the AI provider it was turned on for, once a day.
- **Ask AI…** answers questions about your history with your own key, and **Meetings…** lists your
  calls with their transcripts (see [Ask AI](../README.md#ask-ai) and [Meetings](../README.md#meetings)).
  Within seconds of joining a call, a popup above the tray asks whether to take notes (**Settings → Ask Before Taking
  Meeting Notes**, on by default). **Settings → Summarize Meetings with AI** (off by default) sends the
  transcript of each call you take notes for to your AI provider when it ends, for a summary and
  action items.
- **Insights…** shows where your time went today or this week: active time, time per app and
  website, when you were active, focus sessions and app switches, compared with the day or week
  before. Worked out on your PC without AI; blocked apps and sites are left out (see the notes there
on older recordings and on how time is counted). Click an app, site
  or focus session to see it in the timeline. See [Insights](../README.md#insights).
- **Settings → Timeline Shortcut**: **Change Shortcut…** waits for the new key combination
  (Ctrl, Alt or Win with a key, or an F-key alone); **Reset** goes back to Ctrl+Alt+T.
- **Settings → Screenshot Resolution**: Full (100%), High (75%), Medium (50%, default) or
  Low (33%), with the pixel size each gives on your main display. Higher looks sharper but
  uses more disk space. Applies to the next screenshot; text search works the same at every size.
- **Settings → Timeline Look**: Match System, Light or Dark.
- **Settings → Permissions** shows whether Windows lets OpenRewind use the microphone (the only
  permission Windows asks for); click it to open the microphone privacy page. **Settings** shows ⚠
  when it is off.
- **Settings → Keep Screen / Audio Recordings For**: 1 Day, 1 Week, 1 Month, 1 Year,
  or Until I Delete Them (default). Older recordings are deleted at launch and every hour.
- **Settings → Delete Recordings**: last 15 minutes, last hour, today, all screen,
  all audio, or everything (asks first).
- **Settings → Transcription Model**: Whisper (Automatic, the default), Whisper Base, Small or
  Large v3 Turbo, or NVIDIA Parakeet v3, which needs no other setup: pick it and it downloads
  (670 MB). **Re-transcribe Recent Audio** runs older clips through the new model; the new text
  replaces the old for good. Parakeet stays loaded (about 1 GB) while audio recording is on. See
  [Choosing a transcription model](../README.md#choosing-a-transcription-model).
- **Settings → Local Search Server** turns on the API and browser search page at
  http://127.0.0.1:3031. Off by default. Only this PC can connect. The terminal commands
  `record` and `serve` always start it.
- **Settings → Search by Meaning** (off by default) indexes your recordings in the background so
  the timeline's search box gets a **Keyword | Meaning** switch. Turning it on downloads a model once
  (about 90 MB, all-MiniLM-L6-v2 from Hugging Face, checked against a fixed SHA-256) into
  `%USERPROFILE%\.openrewind\models`; it runs on your PC with ONNX Runtime and nothing you record
  is uploaded. The line under the switch shows the download and indexing progress, with **Try
  Again** if the download fails. See [Search by Meaning](../README.md#search-by-meaning).
- **Settings → Allow AI Apps to Read My History (MCP)** lets Claude Desktop, Claude Code, ChatGPT,
  Cursor and other MCP apps search your history with the Ask AI lookups. Off by default. Blocked
  apps and sites are left out of screen text (a blocked app shows as "(blocked app)" in the activity
  summary); audio transcripts are not filtered by app.
  **Settings → Connect AI Apps…** has one button per app (**Add to Claude**, **Add to Claude Code**,
  **Add to ChatGPT**, **Add to Cursor**) that adds OpenRewind to its setup with the path of your
  `OpenRewind.exe`, and **Remove** to take it out again. It finds the Microsoft Store version of
  Claude Desktop too. **Restart** asks first, closes the app's window, and only ends it if it is
  still open after 5 seconds; for ChatGPT it restarts only the ChatGPT desktop app, never Codex in
  a terminal or an editor. Files that aren't saved as UTF-8 (Notepad's old "ANSI") are left alone.
  Other apps get the lines to paste, with a Copy button. The app starts
  `OpenRewind.exe mcp` itself, with no tray icon, and reads the database read-only, so OpenRewind
  does not have to be running. See
  [Use with Claude and other AI apps (MCP)](../README.md#use-with-claude-and-other-ai-apps-mcp).
- **Settings → Launch at Login** starts it when you sign in.
- **Updates**: a newer release shows a banner at the top of the timeline (✕ hides it until the
  next version) and a row in the tray menu. **Settings → Download Updates Automatically** (on by
  default) downloads it in the background; **Update** / **Restart to Update** swaps `OpenRewind.exe`
  in place and restarts. Keep the app in a folder you can write to (not Program Files), or the button
  opens the download in the browser instead.
- The lock screen and screen saver are never recorded. Password fields are never read.
- **Settings → Blocked Apps & Sites**: blocked apps are blurred in every screenshot, wherever
  they are on screen, and their text is never saved. First launch asks what to block (password
  managers, money apps, private browsing windows and bank sites are ticked). Words in the list
  hide any window whose title has them. Websites and links hide a browser window while its tab in
  front shows them, in front or behind other windows; a window's address is read again whenever its
  title changes, and a window whose new address can't be read is hidden too (Add sites… lists the
  sites and pages you visited lately).
  `OPENREWIND_EXCLUDE` adds more app names.
- **Settings → Show Tutorial** walks through the timeline again; **Run Setup Again…** reopens setup.
- Log: `%USERPROFILE%\.openrewind\openrewind.log`.

## How it works on Windows

| | Mac | Windows |
|---|---|---|
| Screenshot | ScreenCaptureKit | GDI screen copy of the monitor under the mouse |
| Text | Accessibility tree, then Apple Vision OCR | UI Automation tree, then Windows OCR (`Windows.Media.Ocr`) |
| Microphone | AVAudioEngine | WASAPI |
| System audio | ScreenCaptureKit | WASAPI loopback (no permission needed) |
| Speech to text | whisper.cpp `whisper-cli`, or Parakeet v3 on ONNX Runtime | whisper.cpp `whisper-cli.exe`, or Parakeet v3 on ONNX Runtime |
| Search by Meaning | macOS sentence embedding (built in) | all-MiniLM-L6-v2 with ONNX Runtime (downloaded once) |
| Database, API, search page | SQLite + FTS5, port 3031 | the same |
| App | Menu bar, ⌥⌘T | Tray, Ctrl+Alt+T |

Windows OCR uses the languages installed for your Windows display language. If
`OpenRewind.exe doctor` says OCR is missing, add a language with the OCR feature in
**Settings → Time & language → Language & region**.

If the microphone does not record, allow it in **Settings → Privacy & security →
Microphone → Let desktop apps access your microphone**.

## Whisper

The easiest way to get transcripts: **Settings → Transcription Model → NVIDIA Parakeet v3**. It
downloads by itself and needs nothing else (25 European languages). For Whisper (99 languages),
speech is turned into text by [whisper.cpp](https://github.com/ggml-org/whisper.cpp), on your PC.
OpenRewind records audio without it; the clips are transcribed once it is set up:

1. From the [whisper.cpp releases](https://github.com/ggml-org/whisper.cpp/releases), download
   `whisper-bin-x64.zip` and unzip it into `%USERPROFILE%\.openrewind\whisper`
   (so that `whisper-cli.exe` is in that folder or in `whisper\Release`).
2. Pick a Whisper size in **Settings → Transcription Model** to download it, or download a model
   yourself, for example
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
.\OpenRewind.exe mcp             # MCP server for AI apps over stdin/stdout (what they start)
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

Unsigned downloads get the SmartScreen "Windows protected your PC" warning. The Release
workflow signs `OpenRewind.exe` with [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
once these secrets are set on the `release` environment (Settings → Environments → release; see
"Signing" in the main README for how that environment is locked down). Branch and PR builds stay
unsigned so they never see the secrets:

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
