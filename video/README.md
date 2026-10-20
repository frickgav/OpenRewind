# OpenRewind launch video

A 17.7 second launch video built with plain HTML, CSS and [GSAP](https://gsap.com) 3.12.5
(loaded from cdnjs). No framework and no build step. The stage is a fixed 1920x1080 that
scales to fit the window.

| File | What it is |
| --- | --- |
| `index.html` | The stage and all scene markup (the Mac, menus, Rewind view, Ask AI, outro) |
| `styles.css` | Layout, mockup styling and color tokens (logo blue `#1059fb` from `branding/logo.png`) |
| `main.js` | The animation: one master timeline built from `scene1()` to `scene4()` |
| `render.mjs` | Frame-perfect MP4 render with headless Chromium and ffmpeg |

## Scenes

| Time | Scene |
| --- | --- |
| 0–3 s | Hook: "You saw it. You read it. You forgot it." *forgot* glitches, dissolves, and the hook rewinds |
| 3–7 s | Logo shrinks into the menu bar of a MacBook; the cursor opens **Open Timeline (⌥⌘T)** |
| 6.5–14.8 s | Rewind view: a snapshot every 2 s, scrub back through the day, search screen text and audio, Ask AI with your own key, local only (with Meetings, Insights, Clips, MCP, Mac & Windows) |
| 14.8–17.7 s | Floating windows from three moments, "Your screen, rewindable.", Free & Open Source, github.com/frickgav/OpenRewind |

The video runs 17.7 s instead of 15 s so the Search and Ask AI beats are long enough to read.
Change `DURATION` and the scene labels in `main.js` to retime it.

Everything shown is on `main` today. Video storage, encryption, semantic search and the journal
are not shown because they are not merged yet. Counters are illustrative (2 s snapshots give at
most 1,800 an hour).

## Preview

Open `index.html` in a browser, or serve the folder:

```sh
cd video
python3 -m http.server 8000     # then open http://localhost:8000
```

Controls: **Space** plays or pauses, **R** restarts, the slider scrubs, **Loop** repeats.
Add `?clean` to hide the controls for screen recording, `?loop` to loop.

## Export

### Frame-perfect MP4 (recommended)

`render.mjs` seeks the timeline to every frame, screenshots the stage and pipes the frames into
ffmpeg, so dropped frames are impossible.

```sh
cd video
npm install
npx playwright install chromium
node render.mjs                          # openrewind-launch.mp4, 1920x1080, 60 fps
node render.mjs --fps 30 --out x.mp4
node render.mjs --stills 1,4.5,9,16      # PNG stills in stills/
```

### WebM from the browser

**Export WebM** in the controls records this tab with the MediaRecorder API (the browser asks
which tab to share: pick this one). On Chrome it crops to the stage. It plays in real time, so a
busy machine can drop frames.

### Screen recording plus ffmpeg

1. Open `index.html?clean` and make the window 1920x1080 (or go full screen on a 1080p display).
2. Record it with QuickTime (**File → New Screen Recording**) or OBS at 60 fps, then press **R**.
3. Convert and trim:

```sh
ffmpeg -i recording.mov -ss 0.5 -t 17.7 -vf "fps=60,scale=1920:1080:flags=lanczos" \
  -c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p -movflags +faststart openrewind-launch.mp4
```

Use `-ss` to skip whatever was recorded before you pressed R. For a WebM from Export WebM:

```sh
ffmpeg -i openrewind-launch.webm -r 60 -c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p -movflags +faststart openrewind-launch.mp4
```

Made by Agility Resources LLC. MIT licensed, like the rest of the repo.
