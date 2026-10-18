# openscreen

Our own take on [screenpipe](https://screenpi.pe): record your screen, pull the
text out, and search everything you saw. 100% local. One small Swift binary.

Built from a teardown of `/Applications/screenpipe.app` with
[REA](https://github.com/morluto/rea). See [docs/re/FINDINGS.md](docs/re/FINDINGS.md).

## What it does (v0.1)

- Takes a screenshot every 2 s (ScreenCaptureKit).
- Skips frames that did not change (64-bit dHash, like screenpipe's simhash).
- Reads text from the focused window's **accessibility tree**. Falls back to
  **Apple Vision OCR**. Never reads password fields.
- Saves a JPEG and a row in SQLite with an **FTS5** index.
- Serves a local API + search page at `http://127.0.0.1:3031`.
- Skips password managers by default (`OPENSCREEN_EXCLUDE` to change).
- Records **microphone** (AVAudioEngine) and **system audio** (ScreenCaptureKit)
  in 30 s chunks, 16 kHz mono WAV. Silent chunks are dropped.
- Transcribes each chunk with **local Whisper** (`whisper-cli` from
  whisper.cpp). Transcripts go into their own FTS5 index and show up in the
  same search as screen text.

## Mac app

```bash
scripts/build-app.sh
```

This makes `dist/OpenScreen.app` and `dist/openscreen-<version>.dmg`.
Copy the app to `/Applications` and open it. It lives in the menu bar:

- **Record Screen** and **Record Audio** switch on and off on their own (remembered).
- **Open Timeline** (or **⌥⌘T** from anywhere) shows a full-screen overlay.
  Scroll or use ← → to move through time, ⇧ for 1 minute, ↑ ↓ for 10 minutes,
  drag or click the bar, **space** plays the audio at that moment (the picture
  follows along), **+ / −** zoom, **N** jumps to now, **Esc** closes.
- **Search…** opens the web search page.
- **Settings → Keep Screen / Audio Recordings For**: 1 Day, 1 Week, 1 Month,
  1 Year, or Until I Delete Them (default). Older recordings are deleted at
  launch and every hour.
- **Settings → Delete Recordings**: last 15 minutes, last hour, today, all
  screen, all audio, or everything (asks first).
- **Settings → Launch at Login** starts it with your Mac.
- The lock screen and screen saver are never recorded.
- Log: `~/.openscreen/openscreen.log`.

### Put it online for others

Downloads open without a warning only when the app is signed with a
**Developer ID Application** certificate and notarized by Apple:

1. Create the certificate at developer.apple.com → Certificates (needs the paid program).
2. Save notary login once:

```bash
xcrun notarytool store-credentials openscreen --apple-id YOU@EXAMPLE.COM --team-id TEAMID
```

3. Build, sign, notarize, staple:

```bash
NOTARY_PROFILE=openscreen scripts/build-app.sh
```

Upload the `.dmg` (GitHub Releases, your site, etc.).
Without these steps the app still works, but other people must right-click → Open.

## Run from the terminal

```bash
swift build -c release
```

```bash
.build/release/openscreen doctor
```

```bash
.build/release/openscreen
```

Then open http://127.0.0.1:3031.

### Whisper

Needs `whisper-cli` (`brew install whisper-cpp`) and a ggml model.
openscreen looks for a model in this order:

1. `--whisper-model PATH` or `$OPENSCREEN_WHISPER_MODEL`
2. `~/.openscreen/models/ggml-*.bin`
3. VoiceInk's models folder (already on this Mac: `ggml-large-v3-turbo-q5_0.bin`)

Test it without a mic:

```bash
.build/release/openscreen ingest-audio /opt/homebrew/share/whisper-cpp/jfk.wav
```

macOS asks for **Screen Recording** (screen + system audio) and
**Microphone** permission for the app that runs the
command (for example Terminal). Grant it, then run again. **Accessibility**
permission is optional; it gives cleaner text than OCR.

## API (screenpipe-shaped)

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

`~/.openscreen/db.sqlite`, `~/.openscreen/data/<date>/*.jpg`, and
`~/.openscreen/data/audio/<date>/*.wav`.
Delete the folder to wipe everything, or from the terminal:

```bash
.build/release/openscreen delete older:week
```

(`15m`, `1h`, `today`, `all`, `older:day|week|month|year`; add `--screen-only` or `--audio-only`.)

## Not yet (screenpipe has these)

- Speaker identification (who said what)
- UI input events (clicks, keys)
- Browser URL per frame
- Video chunk encoding (ffmpeg) instead of JPEGs
- Menu bar app, auto-start, retention / cleanup
- Plugins ("pipes"), AI chat over your history
