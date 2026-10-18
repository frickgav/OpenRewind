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

## Deploy on Vercel

`vercel.json` at the repo root tells Vercel to serve this folder as-is, with no build.

1. On vercel.com, choose **Add New → Project** and import `frickgav/openscreen`.
2. Leave every setting at its default and click **Deploy**.

Every push to `main` then redeploys the site. Add your own domain under the project's **Settings → Domains**.

Any other static host (GitHub Pages, Netlify, Cloudflare Pages) also works: point it at the `website/` folder.
