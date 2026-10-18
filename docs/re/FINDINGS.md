# screenpipe teardown (with REA)

Target: `/Applications/screenpipe.app` v2.7.109, bundle id `screenpi.pe`.
Tool: REA 6.7.0 (`morluto/rea`), cloned to `.tools/rea` (git-ignored).

Raw REA evidence in this folder:

- `plist.md` — `rea inspect-plist` on the app bundle.
- `rust-deps.md` — `rea inspect-rust-binary` on `screenpipe-app`.
- `sqlite-schema.md` — `rea inspect-sqlite-database --schema-scope database`
  on a copy of the index DB. Schema only. No rows were read.

## What is in the bundle

| File | Size | Role |
|------|------|------|
| `MacOS/screenpipe-app` | 211 MB | Rust (Tauri) app. arm64 Mach-O, min macOS 14, SDK 26.4. No cargo-auditable dep list. |
| `MacOS/ffmpeg`, `ffprobe` | 47 MB each | Encode screen frames into mp4 chunks, encode audio chunks. |
| `MacOS/bun` | 58 MB | Runs "pipes" (JS plugins / agents). |
| `Resources/mlx.metallib` | 88 MB | Apple MLX GPU kernels, for local ML (Whisper / embeddings). |
| `Resources/PermissionFlow_*.bundle` | — | Permission onboarding UI. |

Plist permission strings show what it touches: screen capture, microphone,
system audio (CoreAudio process tap), Apple Events (browser URL), calendar,
reminders. URL scheme `screenpipe://`.

## How data is stored (`~/.screenpipe`)

- `storage.json` → mode `hybrid-parquet-v1`. SQLite index plus parquet payload files.
- `storage/<uuid>/index.sqlite` → main index (WAL mode).
- `data/*.mp4` → 30 s audio chunks per device, e.g. `System Audio (output)_<ts>.mp4`.
- `data/data/<date>/` → screen snapshots.

Core tables (from REA schema evidence):

- `frames` — one row per capture: `timestamp, app_name, window_name, focused,
  browser_url, device_name, snapshot_path, accessibility_text, full_text,
  content_hash, simhash, capture_trigger, text_source`.
- `frames_fts` — FTS5 over `full_text, app_name, window_name, browser_url`.
- `video_chunks` — `file_path, device_name, fps (default 0.5)`.
- `audio_chunks` — `file_path, timestamp, transcription_status
  (pending|transcribed|silent|failed)`.
- `audio_transcriptions` + `audio_transcriptions_fts` — `transcription, device,
  is_input_device, speaker_id, start_time, end_time, transcription_engine (Whisper)`.
- `speakers` — `name, centroid FLOAT[512]` (voice embeddings for diarization).
- `ui_events` — clicks, keys, scroll, app switch, clipboard, with AX element info.
- Extra: `meetings`, `memories`, `tags`, `pipe_executions`, `semantic_*`, `activity_*`.

Key ideas:

1. Capture is **event-driven** (`capture_trigger`), not just a timer.
2. Screen text comes from the **accessibility tree first**, OCR as fallback (`text_source`).
3. **Dedup** with `content_hash` / `simhash` so idle screens add no rows.
4. All text goes into **FTS5** for fast keyword search.

## Local API (`localhost:3030`)

Found by probing `/health` and by route strings in the binary:

`/health`, `/search`, `/raw_sql`, `/frames/{id}`, `/activity-summary`,
`/tags/vision/{id}`, `/memories`, `/pipes/`, `/connections/*`,
`/audio/device/status`, `/vision/device/status`, `/ws/events`, `/ws/metrics`,
plus an OpenAI-style `/v1/chat/completions`, `/v1/audio/transcriptions`.

`/search` needs a Bearer token. Its shape (from the screenpipe API skill):

```json
{"data":[{"type":"OCR","content":{"frame_id":1,"text":"...","app_name":"..."}},
         {"type":"Audio","content":{"chunk_id":1,"transcription":"..."}}],
 "pagination":{"limit":10,"offset":0,"total":42}}
```

## Limits of this teardown

- No Hopper / Ghidra / IDA installed, so no native decompile of the Rust code.
- REA's SQLite inspector needs Node ≥ 24.15. We ran it with Homebrew Node 26.
- Function-level behavior is inferred from schema, plist, routes and files on disk.
