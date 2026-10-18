# OpenRewind website

The marketing and download site for OpenRewind. Plain static HTML, CSS, and JS: no build step, no dependencies.

## Preview

```bash
cd website
python3 -m http.server 8080
```

Then open http://localhost:8080.

## Files

- `index.html`: the whole page
- `styles.css`: design tokens and layout (light slate canvas, violet accent, Figtree from Google Fonts)
- `main.js`: scroll reveals, OS-aware download buttons, and the animated timeline demo
- `assets/mark.svg`: the OpenRewind mark
- `screenshots/`: reference screenshots of the page

## Downloads

Every download button points at `https://github.com/frickgav/openscreen/releases/latest`.
Attach the `.dmg` (macOS) and the Windows installer to a GitHub Release and the buttons work.

## Deploy

Any static host works. Point GitHub Pages, Vercel, Netlify, or Cloudflare Pages at the `website/` folder.
