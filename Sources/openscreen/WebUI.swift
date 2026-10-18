enum WebUI {
    static let html = #"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>openscreen</title>
<style>
:root {
  --bg: #f6f5f2; --panel: #ffffff; --ink: #1b1b1a; --muted: #6d6b66; --line: #e3e1db;
  --accent: #2f6df6; --accent-ink: #ffffff; --mark: #ffe58a;
}
@media (prefers-color-scheme: dark) {
  :root { --bg: #121212; --panel: #1c1c1c; --ink: #ecebe8; --muted: #9a9892; --line: #2c2c2b;
          --accent: #6f9bff; --accent-ink: #0d0d0d; --mark: #6b5a12; }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--ink);
       font: 14px/1.45 -apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif; }
header { position: sticky; top: 0; z-index: 2; background: var(--bg); border-bottom: 1px solid var(--line);
         padding: 14px 16px; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
h1 { font-size: 15px; margin: 0 8px 0 0; letter-spacing: -0.01em; }
h1 span { color: var(--accent); }
input, select, button { font: inherit; color: var(--ink); background: var(--panel);
  border: 1px solid var(--line); border-radius: 8px; padding: 7px 10px; }
#q { flex: 1 1 260px; min-width: 0; }
button.primary { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); cursor: pointer; }
#status { color: var(--muted); font-size: 12px; margin-left: auto; }
main { padding: 16px; max-width: 1400px; margin: 0 auto; }
#meta { color: var(--muted); margin-bottom: 12px; }
#grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 14px; }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; overflow: hidden;
        cursor: pointer; display: flex; flex-direction: column; }
.card:hover { border-color: var(--accent); }
.card img { width: 100%; aspect-ratio: 16/10; object-fit: cover; object-position: top; background: var(--line); display: block; }
.card .body { padding: 10px 12px; }
.wave { aspect-ratio: 16/10; display: flex; align-items: center; justify-content: center; gap: 3px;
        background: linear-gradient(135deg, color-mix(in srgb, var(--accent) 18%, var(--panel)), var(--panel)); }
.wave i { width: 4px; border-radius: 2px; background: var(--accent); opacity: .75; }
.tag { display: inline-block; font-size: 11px; padding: 1px 6px; border-radius: 99px; border: 1px solid var(--line); color: var(--muted); }
.dlg.audio { grid-template-columns: 1fr; }
.dlg.audio img { display: none; }
#daudio { width: 100%; margin-top: 10px; }
.app { font-weight: 600; }
.win, .when { color: var(--muted); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.snip { margin-top: 6px; font-size: 12px; color: var(--ink); display: -webkit-box; -webkit-line-clamp: 3;
        -webkit-box-orient: vertical; overflow: hidden; }
mark { background: var(--mark); color: inherit; border-radius: 2px; }
#more { display: block; margin: 18px auto; }
#more[hidden] { display: none; }
dialog { border: 1px solid var(--line); border-radius: 12px; padding: 0; background: var(--panel); color: var(--ink);
         width: min(1200px, 94vw); max-height: 92vh; }
dialog::backdrop { background: rgba(0,0,0,.55); }
.dlg { display: grid; grid-template-columns: 2fr 1fr; max-height: 92vh; }
.dlg img { width: 100%; height: 100%; object-fit: contain; background: #000; max-height: 92vh; }
.dlg .side { padding: 14px; overflow: auto; border-left: 1px solid var(--line); }
.dlg pre { white-space: pre-wrap; font: 12px/1.5 ui-monospace, Menlo, monospace; margin: 10px 0 0; }
.nav { display: flex; gap: 8px; margin-top: 10px; }
.empty { color: var(--muted); padding: 40px 0; text-align: center; }
@media (max-width: 720px) { .dlg { grid-template-columns: 1fr; } .dlg .side { border-left: 0; border-top: 1px solid var(--line); } }
</style>
</head>
<body>
<header>
  <h1>open<span>screen</span></h1>
  <input id="q" type="search" placeholder="Search everything you saw and heard…" autofocus>
  <select id="range">
    <option value="30m ago">Last 30 min</option>
    <option value="2h ago">Last 2 hours</option>
    <option value="today" selected>Today</option>
    <option value="yesterday">Since yesterday</option>
    <option value="7d ago">Last 7 days</option>
    <option value="365d ago">All time</option>
  </select>
  <select id="type">
    <option value="all">Screen + audio</option>
    <option value="ocr">Screen only</option>
    <option value="audio">Audio only</option>
  </select>
  <select id="app"><option value="">All apps</option></select>
  <button class="primary" id="go">Search</button>
  <span id="status"></span>
</header>
<main>
  <div id="meta"></div>
  <div id="grid"></div>
  <button id="more" hidden>Load more</button>
</main>
<dialog id="dlg">
  <div class="dlg">
    <img id="dimg" alt="">
    <div class="side">
      <div class="app" id="dapp"></div>
      <div class="win" id="dwin"></div>
      <div class="when" id="dwhen"></div>
      <audio id="daudio" controls preload="none" hidden></audio>
      <div class="nav"><button id="prev">← Newer</button><button id="next">Older →</button><button id="close">Close</button></div>
      <pre id="dtext"></pre>
    </div>
  </div>
</dialog>
<script>
const $ = (id) => document.getElementById(id);
let rows = [], offset = 0, total = 0, current = -1, seq = 0;
const PAGE = 40;

function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
function words() { return $('q').value.trim().split(/\s+/).filter(Boolean); }
function highlight(text) {
  let h = esc(text);
  for (const w of words()) {
    const re = new RegExp('(' + w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'gi');
    h = h.replace(re, '<mark>$1</mark>');
  }
  return h;
}
function snippet(text) {
  const ws = words().map(w => w.toLowerCase());
  const lower = text.toLowerCase();
  let at = -1;
  for (const w of ws) { const i = lower.indexOf(w); if (i >= 0 && (at < 0 || i < at)) at = i; }
  const start = Math.max(0, at - 60);
  return (start > 0 ? '…' : '') + text.slice(start, start + 240).replace(/\s+/g, ' ');
}
function when(ts) { return new Date(ts).toLocaleString(); }
function bars(seed) {
  let h = '', x = (seed * 9301 + 49297) % 233280;
  for (let i = 0; i < 28; i++) { x = (x * 9301 + 49297) % 233280; h += '<i style="height:' + (12 + (x / 233280) * 52) + '%"></i>'; }
  return h;
}

async function load(reset) {
  const my = ++seq;
  if (reset) offset = 0;
  const p = new URLSearchParams({ limit: PAGE, offset, start_time: $('range').value, max_content_length: 4000 });
  if ($('q').value.trim()) p.set('q', $('q').value.trim());
  if ($('app').value) p.set('app_name', $('app').value);
  p.set('content_type', $('type').value);
  const r = await fetch('/search?' + p).then(r => r.json());
  if (my !== seq) return;  // a newer search started; drop this result
  if (reset) { rows = []; $('grid').innerHTML = ''; }
  if (r.error) { $('meta').textContent = r.error; return; }
  total = r.pagination.total;
  const start = rows.length;
  rows = rows.concat(r.data.map(d => Object.assign({ kind: d.type }, d.content)));
  offset = rows.length;
  $('meta').textContent = total + ' result' + (total === 1 ? '' : 's');
  if (!rows.length) { $('grid').innerHTML = '<div class="empty">Nothing here yet. Keep openscreen running and come back.</div>'; }
  rows.slice(start).forEach((c, i) => {
    const el = document.createElement('div');
    el.className = 'card';
    if (c.kind === 'Audio') {
      el.innerHTML = '<div class="wave">' + bars(c.transcription_id) + '</div>' +
        '<div class="body"><div class="app">' + esc(c.device_name) + ' <span class="tag">audio</span></div>' +
        '<div class="when">' + when(c.timestamp) + '</div>' +
        '<div class="snip">' + highlight(snippet(c.transcription)) + '</div></div>';
    } else {
      el.innerHTML = '<img loading="lazy" src="/frames/' + c.frame_id + '" alt="">' +
        '<div class="body"><div class="app">' + esc(c.app_name || 'Unknown') + '</div>' +
        '<div class="win">' + esc(c.window_name || '') + '</div>' +
        '<div class="when">' + when(c.timestamp) + ' · ' + c.text_source + '</div>' +
        '<div class="snip">' + highlight(snippet(c.text)) + '</div></div>';
    }
    el.onclick = () => open(start + i);
    $('grid').appendChild(el);
  });
  $('more').hidden = rows.length >= total;
}

function open(i) {
  if (i < 0 || i >= rows.length) return;
  current = i;
  const c = rows[i];
  const audio = c.kind === 'Audio';
  document.querySelector('.dlg').classList.toggle('audio', audio);
  $('daudio').hidden = !audio;
  if (audio) {
    $('dimg').removeAttribute('src');
    $('daudio').src = '/audio/' + c.chunk_id;
    $('daudio').onloadedmetadata = () => { $('daudio').currentTime = c.start_time || 0; };
    $('dapp').textContent = c.device_name;
    $('dwin').textContent = c.device_type === 'Input' ? 'You (microphone)' : 'System audio';
    $('dwhen').textContent = when(c.timestamp) + ' · chunk ' + c.chunk_id + ' · ' + (c.start_time || 0).toFixed(1) + 's–' + (c.end_time || 0).toFixed(1) + 's';
    $('dtext').innerHTML = highlight(c.transcription);
  } else {
    $('daudio').pause(); $('daudio').removeAttribute('src');
    $('dimg').src = '/frames/' + c.frame_id;
    $('dapp').textContent = c.app_name || 'Unknown';
    $('dwin').textContent = c.window_name || '';
    $('dwhen').textContent = when(c.timestamp) + ' · frame ' + c.frame_id + ' · ' + c.text_source;
    $('dtext').innerHTML = highlight(c.text);
  }
  if (!$('dlg').open) $('dlg').showModal();
}

async function loadApps() {
  const r = await fetch('/apps?start_time=' + encodeURIComponent($('range').value)).then(r => r.json());
  const keep = $('app').value;
  $('app').innerHTML = '<option value="">All apps</option>' +
    r.data.map(a => '<option value="' + esc(a.app_name) + '">' + esc(a.app_name || 'Unknown') + ' (' + a.frames + ')</option>').join('');
  $('app').value = keep;
}

async function health() {
  const h = await fetch('/health').then(r => r.json()).catch(() => null);
  if (!h) { $('status').textContent = 'offline'; return; }
  const warn = [];
  if (!h.screen_permission) warn.push('no screen permission');
  if (!h.accessibility_permission) warn.push('OCR only');
  if (h.audio_chunks_pending) warn.push(h.audio_chunks_pending + ' audio pending');
  $('status').textContent = h.frames_total + ' frames · ' + h.audio_segments_total + ' audio' + (warn.length ? ' · ' + warn.join(' · ') : '');
}

let t;
$('q').addEventListener('input', () => { clearTimeout(t); t = setTimeout(() => load(true), 250); });
$('go').onclick = () => load(true);
$('range').onchange = () => { loadApps(); load(true); };
$('app').onchange = () => load(true);
$('more').onclick = () => load(false);
$('prev').onclick = () => open(current - 1);
$('next').onclick = async () => { if (current + 1 >= rows.length && rows.length < total) await load(false); open(current + 1); };
$('close').onclick = () => $('dlg').close();
$('dlg').addEventListener('close', () => $('daudio').pause());
$('type').onchange = () => load(true);
document.addEventListener('keydown', e => {
  if (!$('dlg').open) return;
  if (e.key === 'ArrowLeft') $('prev').click();
  if (e.key === 'ArrowRight') $('next').click();
});
loadApps(); load(true); health(); setInterval(health, 10000);
</script>
</body>
</html>
"""#
}
