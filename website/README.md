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
- `styles.css`: design tokens and layout (light slate canvas, blue accent, Figtree from Google Fonts)
- `main.js`: scroll reveals, OS-aware download buttons, and the animated timeline demo
- `assets/`: logo, favicons, and app icon, made from `branding/logo.png` by `scripts/make-icons.py`
- `screenshots/`: reference screenshots of the page

## Downloads

The buttons download straight from the latest GitHub Release:

- `releases/latest/download/OpenRewind-mac.dmg`
- `releases/latest/download/OpenRewind-windows-x64.zip` (and `-arm64.zip`)

`.github/workflows/release.yml` builds those files and attaches them. To cut a release, push a tag
(`git tag v0.2.0 && git push origin v0.2.0`) or run **Actions → Release → Run workflow**.
The repo must be public for visitors to download release files.

## Deploy on Vercel

`vercel.json` at the repo root tells Vercel to serve this folder as-is, with no build.

1. On vercel.com, choose **Add New → Project** and import `frickgav/openscreen`.
2. Leave every setting at its default and click **Deploy**.

Every push to `main` then redeploys the site. Add your own domain under the project's **Settings → Domains**.

Any other static host (GitHub Pages, Netlify, Cloudflare Pages) also works: point it at the `website/` folder.
