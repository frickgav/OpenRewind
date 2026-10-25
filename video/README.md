# OpenRewind launch video

A 29 second launch video built with plain HTML, CSS and [GSAP](https://gsap.com) 3.12.5
(loaded from cdnjs), with music, sound effects and a voice-over. No framework and no build
step. The stage is a fixed 1920x1080 that scales to fit the window.

| File | What it is |
| --- | --- |
| `index.html` | The stage and all scene markup (the Mac, menus, Rewind view, Ask AI, outro) |
| `styles.css` | Layout, mockup styling and color tokens (logo blue `#1059fb` from `branding/logo.png`) |
| `main.js` | The animation: one master timeline built from `scene1()` to `scene4()`, plus sound cues |
| `render.mjs` | Frame-perfect MP4 render with headless Chromium and ffmpeg |
| `audio/soundtrack.mp3` | The mixed sound track the page and the MP4 use |
| `audio/make_soundtrack.py` | Synthesizes the music and effects and mixes in the voice-over on the cue times |
| `audio/make_voiceover.py` | Records the voice-over lines in `audio/vo/` with Kokoro, a local open TTS model |

## Scenes

| Time | Scene | Voice-over |
| --- | --- | --- |
| 0–4 s | Hook: *forgot* glitches, dissolves, and the hook rewinds | "You saw it. You read it. You forgot it." |
| 4–9 s | Logo shrinks into the menu bar of a MacBook; the cursor opens **Open Timeline (⌥⌘T)** | "Meet OpenRewind. It quietly keeps a private memory of your screen." |
| 9–24 s | Rewind view: a snapshot every 2 s, scrub back, search screen text and audio, Ask AI with your own key, local only (with Meetings, Insights, Clips, MCP, Mac & Windows) | One line per feature |
| 24–29 s | Floating windows from three moments, "Your screen, rewindable.", Free & Open Source | "OpenRewind. Free, and open source." |

`HOLDS` in `main.js` sets how long each beat stays on screen; the voice-over fits inside the holds.
Everything shown is on `main` today. Video storage, encryption, semantic search and the journal
are not shown because they are not merged yet. Counters are illustrative (2 s snapshots give at
most 1,800 an hour).

## Sound

The music and effects are synthesized from scratch in `make_soundtrack.py` (numpy only), so they
are original. The voice is Kokoro-82M (Apache-2.0, voice `af_heart`). To change the script or timing:

```sh
cd video
python3 audio/make_voiceover.py           # after editing LINES (setup steps are in the file)
node render.mjs --cues audio/cues.json    # cue times from the timeline
python3 audio/make_soundtrack.py          # writes audio/soundtrack.mp3
```

## Preview

Open `index.html` in a browser, or serve the folder:

```sh
cd video
python3 -m http.server 8000     # then open http://localhost:8000
```

Controls: **Space** plays or pauses, **R** restarts, **M** or **Sound** turns the sound on
(browsers need a click before they play sound), the slider scrubs, **Loop** repeats.
Add `?clean` to hide the controls for screen recording; then a click restarts it with sound.

## Export

### Frame-perfect MP4 (recommended)

`render.mjs` seeks the timeline to every frame, screenshots the stage and pipes the frames into
ffmpeg, so dropped frames are impossible.

```sh
cd video
npm install
npx playwright install chromium
node render.mjs                          # openrewind-launch.mp4, 1920x1080, 60 fps, with sound
node render.mjs --fps 30 --out x.mp4
node render.mjs --stills 1,4.5,9,16      # PNG stills in stills/
```

### WebM from the browser

**Export WebM** in the controls records this tab and the sound track with the MediaRecorder API
(the browser asks which tab to share: pick this one). On Chrome it crops to the stage. It plays in real time, so a
busy machine can drop frames.

### Screen recording plus ffmpeg

1. Open `index.html?clean` and make the window 1920x1080 (or go full screen on a 1080p display).
2. Record it with QuickTime (**File → New Screen Recording**) or OBS at 60 fps, then press **R**.
3. Convert and trim:

```sh
ffmpeg -i recording.mov -ss 0.5 -t 29 -vf "fps=60,scale=1920:1080:flags=lanczos" \
  -c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p -movflags +faststart openrewind-launch.mp4
```

Use `-ss` to skip whatever was recorded before you pressed R. For a WebM from Export WebM:

```sh
ffmpeg -i openrewind-launch.webm -r 60 -c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p -movflags +faststart openrewind-launch.mp4
```

Made by Agility Resources LLC. MIT licensed, like the rest of the repo.
