# OpenRewind

**Rewind your Mac or PC.** OpenRewind records your screen and audio, pulls the text
out, and lets you search or scroll back through everything you saw and heard.
100% local. Open source. A small Swift app on the Mac, a native .NET app on
[Windows](windows/README.md).

OpenRewind was called openscreen until October 2026. On first launch it moves
`~/.openscreen` to `~/.openrewind` and keeps every recording and your menu settings.
macOS treats it as a new app, so it asks for Screen Recording, Microphone and
Accessibility again; you can then delete the old OpenScreen.app (and its Login Item).

## The story

### 2022: a search engine for your life

On November 1, 2022, [Rewind](https://techcrunch.com/2022/11/01/rewind-wants-to-revamp-how-you-remember-with-millions-from-a16z/)
launched with **$10M from Andreessen Horowitz**, at a $75M valuation.
The founders were Dan Siroker (co-founder of Optimizely) and Brett Bejcek.

The idea was simple and a little wild. Your Mac records everything on your
screen and everything said in your meetings. Then you can search any word and
jump back to that moment. It all ran on your own Mac, made possible by Apple
Silicon. Rewind promised it would "never sell your data or do advertising."

### 2023: the hottest pitch on Twitter

In April 2023, Rewind raised its Series A in public. Siroker posted the pitch
deck and asked investors to send offers. [More than a thousand investors showed
interest and 170 made offers](https://fortune.com/2023/05/26/a-i-startup-founder-trend/).
NEA led the round at a **$350M valuation**.

### 2024: the pivot away from the Mac

In April 2024, Rewind [renamed itself Limitless](https://techcrunch.com/2024/04/17/a16z-backed-rewind-pivots-to-build-ai-powered-pendant-to-record-your-conversations)
and moved to a $99 AI pendant that records your conversations. The Mac app
[stopped getting updates](https://sacra.com/research/why-meta-bought-limitless/).

### 2025: lights out

On December 5, 2025, [Meta bought Limitless](https://9to5mac.com/2025/12/05/rewind-limitless-meta-acquisition/).
The team moved to Meta's wearables group. The Rewind Mac app was shut off:
**on December 19, 2025, all screen and audio capture stopped.**
Users in the EU, UK, and some other countries had two weeks to export their
data before it was deleted.

### Now: OpenRewind

A tool that remembers your whole digital life should not be able to vanish
when a company is sold. So we built it again, in the open:

- **Local only.** Your data stays in `~/.openrewind`. No account. No cloud.
- **Open source ([MIT](LICENSE)).** Read the code. Change it. Nobody can switch it off.
- **Plain files.** SQLite and JPEGs. Your history is yours to keep or move.

### How it was built

OpenRewind is new code. It does not contain any Rewind code. The idea and the
full-screen timeline (⌥⌘T) are modeled on Rewind's.

OpenRewind is not made by or connected to Rewind AI, Limitless, or Meta.

## What it does (v0.1)

- Takes a screenshot every 2 s (ScreenCaptureKit).
- Skips frames that did not change (64-bit dHash).
- Reads text from the focused window's **accessibility tree**. Falls back to
  **Apple Vision OCR**. Never reads password fields.
- Saves a JPEG and a row in SQLite with an **FTS5** index.
- Can serve a local API + search page at `http://127.0.0.1:3031` (off in the app until you turn it on).
- Skips password managers by default (`OPENREWIND_EXCLUDE` to change).
- Records **microphone** (AVAudioEngine) and **system audio** (ScreenCaptureKit)
  in 30 s chunks, 16 kHz mono WAV. Silent chunks are dropped.
- Transcribes each chunk with **local Whisper** (`whisper-cli` from
  whisper.cpp). Transcripts go into their own FTS5 index and show up in the
  same search as screen text.

## Mac app

```bash
scripts/build-app.sh
```

This makes `dist/OpenRewind.app` and `dist/openrewind-<version>.dmg`.
Copy the app to `/Applications` and open it. It lives in the menu bar:

- **Record Screen** and **Record Audio** switch on and off on their own (remembered).
- **Open Timeline** (or **⌥⌘T** from anywhere) shows a full-screen overlay.
  Scroll or use ← → to move through time, ⇧ for 1 minute, ↑ ↓ for 10 minutes,
  drag or click the bar, **space** plays the audio at that moment (the picture
  follows along), **+ / −** zoom, **N** jumps to now, **O** opens the search page in the browser, **Esc** closes.
- **Search…** opens the timeline with the cursor in its search box, or the web search page
  when the local search server is on.
- **Settings → Keep Screen / Audio Recordings For**: 1 Day, 1 Week, 1 Month,
  1 Year, or Until I Delete Them (default). Older recordings are deleted at
  launch and every hour.
- **Settings → Delete Recordings**: last 15 minutes, last hour, today, all
  screen, all audio, or everything (asks first).
- **Settings → Local Search Server** turns on the API and browser search page at
  `http://127.0.0.1:3031`. Off by default. Only this Mac can connect. The terminal
  commands `record` and `serve` always start it.
- **Settings → Launch at Login** starts it with your Mac.
- The lock screen and screen saver are never recorded.
- Log: `~/.openrewind/openrewind.log`.

### Put it online for others

Downloads open without a warning only when the app is signed with a
**Developer ID Application** certificate and notarized by Apple:

1. Create the certificate at developer.apple.com → Certificates (needs the paid program).
2. Save notary login once:

```bash
xcrun notarytool store-credentials openrewind --apple-id YOU@EXAMPLE.COM --team-id TEAMID
```

3. Build, sign, notarize, staple:

```bash
NOTARY_PROFILE=openrewind scripts/build-app.sh
```

Upload the `.dmg` (GitHub Releases, your site, etc.).
Without these steps the app still works, but other people must right-click → Open.

## Windows app

Windows 10 (2004+) and 11, x64 and ARM64. Same timeline, search page, API and data
format as the Mac app; the timeline opens with **Ctrl+Alt+T**. Download, setup and
build steps are in [windows/README.md](windows/README.md):

```powershell
scripts\build-windows.ps1
```

## Run from the terminal

```bash
swift build -c release
```

```bash
.build/release/openrewind doctor
```

```bash
.build/release/openrewind
```

Then open http://127.0.0.1:3031.

### Whisper

Needs `whisper-cli` (`brew install whisper-cpp`) and a ggml model.
OpenRewind looks for a model in this order:

1. `--whisper-model PATH` or `$OPENREWIND_WHISPER_MODEL`
2. `~/.openrewind/models/ggml-*.bin`
3. VoiceInk's models folder (already on this Mac: `ggml-large-v3-turbo-q5_0.bin`)

Test it without a mic:

```bash
.build/release/openrewind ingest-audio /opt/homebrew/share/whisper-cpp/jfk.wav
```

macOS asks for **Screen Recording** (screen + system audio) and
**Microphone** permission for the app that runs the
command (for example Terminal). Grant it, then run again. **Accessibility**
permission is optional; it gives cleaner text than OCR.

## API

On when you run `record` or `serve` from the terminal, or after **Settings → Local Search Server** in the app.

| Route | What |
|-------|------|
| `GET /health` | Status, frame count, permissions |
| `GET /search?q=&app_name=&window_name=&start_time=&end_time=&limit=&offset=&max_content_length=` | Full-text search. Times: ISO, `2h ago`, `today`, `yesterday`, `YYYY-MM-DD` |
| `GET /frames/{id}` | Snapshot JPEG |
| `GET /apps?start_time=` | Apps with frame counts |
| `GET /audio/{chunk_id}` | Audio chunk WAV |

`content_type`: `all` (screen + audio), `ocr` (screen only), `audio`.

Response: `{"data":[{"type":"OCR","content":{"frame_id","text","timestamp","app_name","window_name","file_path","text_source"}},{"type":"Audio","content":{"chunk_id","transcription","timestamp","start_time","end_time","device_name","device_type"}}],"pagination":{"limit","offset","total"}}`

## Data

`~/.openrewind/db.sqlite`, `~/.openrewind/data/<date>/*.jpg`, and
`~/.openrewind/data/audio/<date>/*.wav`.
Delete the folder to wipe everything, or from the terminal:

```bash
.build/release/openrewind delete older:week
```

(`15m`, `1h`, `today`, `all`, `older:day|week|month|year`; add `--screen-only` or `--audio-only`.)

## Not yet (Rewind had these)

- Speaker identification (who said what)
- Meeting summaries and AI chat over your history ("Ask Rewind")
- UI input events (clicks, keys)
- Browser URL per frame
- Video chunk encoding (ffmpeg) instead of JPEGs
- Plugins

## License

[MIT](LICENSE). Use it, change it, share it.
