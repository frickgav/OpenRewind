# openscreen

**Rewind your Mac.** openscreen records your screen and audio, pulls the text
out, and lets you search or scroll back through everything you saw and heard.
100% local. One small Swift binary. Open source.

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

### Now: openscreen

A tool that remembers your whole digital life should not be able to vanish
when a company is sold. So we built it again, in the open:

- **Local only.** Your data stays in `~/.openscreen`. No account. No cloud.
- **Open source ([MIT](LICENSE)).** Read the code. Change it. Nobody can switch it off.
- **Plain files.** SQLite and JPEGs. Your history is yours to keep or move.

### How it was built

openscreen is new code. It does not contain any Rewind code.

- **The idea and the timeline** come from Rewind. The full-screen timeline
  (⌥⌘T) is modeled on Rewind's.
- **The internals** come from a teardown of [screenpipe](https://screenpi.pe),
  another open screen-memory app, done with [REA](https://github.com/morluto/rea).
  The database layout and the local API copy screenpipe's shape.
  See [docs/re/FINDINGS.md](docs/re/FINDINGS.md).

openscreen is not made by or connected to Rewind AI, Limitless, Meta, or screenpipe.

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

## Not yet (Rewind or screenpipe had these)

- Speaker identification (who said what)
- Meeting summaries and AI chat over your history ("Ask Rewind")
- UI input events (clicks, keys)
- Browser URL per frame
- Video chunk encoding (ffmpeg) instead of JPEGs
- Plugins ("pipes")

## License

[MIT](LICENSE). Use it, change it, share it.
