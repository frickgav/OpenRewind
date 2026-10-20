// Frame-perfect render of the launch video to MP4 (or PNG stills).
// Opens index.html?render in headless Chromium, seeks the GSAP timeline to each frame,
// screenshots the 1920x1080 stage and pipes the frames into ffmpeg.
//
//   npm install            (once, installs Playwright)
//   npx playwright install chromium
//   node render.mjs                         -> openrewind-launch.mp4, 60 fps
//   node render.mjs --fps 30 --out x.mp4
//   node render.mjs --stills 1,4.5,9,16     -> stills/frame-1.00.png ...
//   node render.mjs --gsap-dir node_modules/gsap/dist   (serve GSAP from disk instead of the CDN)
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : fallback;
};
const fps = Number(opt('fps', 60));
const out = resolve(opt('out', 'openrewind-launch.mp4'));
const stills = opt('stills', '');
const gsapDir = opt('gsap-dir', '');
const executablePath = opt('chromium', process.env.CHROMIUM_PATH || undefined);

const browser = await chromium.launch({ executablePath });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
page.on('console', (m) => { if (m.type() === 'warning' || m.type() === 'error') console.log(`[page] ${m.text()}`); });
page.on('pageerror', (e) => console.log(`[page error] ${e.message}`));

if (gsapDir) {
  await page.route(/cdnjs\.cloudflare\.com\/ajax\/libs\/gsap\/[^/]+\/(.+)$/, (route) => {
    const file = route.request().url().split('/').pop();
    route.fulfill({ contentType: 'application/javascript', body: readFileSync(join(resolve(gsapDir), file)) });
  });
}

await page.goto(pathToFileURL(join(here, 'index.html')).href + '?render');
await page.waitForFunction(() => window.__ready === true, null, { timeout: 30000 });
const duration = await page.evaluate(() => window.__duration);
const stage = page.locator('#stage');

async function frameAt(t) {
  await page.evaluate((time) => window.__seek(time), t);
  return stage.screenshot({ type: 'png', animations: 'disabled' });
}

if (stills) {
  const dir = resolve('stills');
  mkdirSync(dir, { recursive: true });
  for (const t of stills.split(',').map(Number)) {
    const png = await frameAt(t);
    const file = join(dir, `frame-${t.toFixed(2)}.png`);
    writeFileSync(file, png);
    console.log(file);
  }
} else {
  const total = Math.round(duration * fps);
  const ff = spawn('ffmpeg', [
    '-y', '-f', 'image2pipe', '-framerate', String(fps), '-i', '-',
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '16', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', out,
  ], { stdio: ['pipe', 'inherit', 'inherit'] });
  for (let f = 0; f < total; f++) {
    const png = await frameAt(f / fps);
    if (!ff.stdin.write(png)) await new Promise((r) => ff.stdin.once('drain', r));
    if (f % fps === 0) process.stdout.write(`\r${(f / fps).toFixed(0)}s / ${duration}s`);
  }
  ff.stdin.end();
  await new Promise((r) => ff.on('close', r));
  console.log(`\nWrote ${out} (${total} frames at ${fps} fps)`);
}
await browser.close();
