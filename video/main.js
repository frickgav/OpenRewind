/* OpenRewind launch video: one master GSAP timeline, one child timeline per scene.
   Everything is driven by the timeline (no CSS animations), so seeking to any time
   renders the same frame. That is what render.mjs relies on for a frame-perfect MP4. */
(() => {
  const params = new URLSearchParams(location.search);
  const RENDER = params.has('render');
  const CLEAN = RENDER || params.has('clean');
  if (CLEAN) document.body.classList.add('clean');

  gsap.registerPlugin(MotionPathPlugin);

  const DURATION = 29;               // seconds; scenes 1-4 below add up to this

  // Reading time inserted into each scene: [scene time, seconds of hold]. The voice-over
  // lines are timed to fit inside these holds.
  const HOLDS = {
    s1: [[0.72, 0.35], [1.42, 0.35], [1.98, 0.3]],
    s2: [[0.92, 0.6], [2.1, 0.6], [3.05, 0.3]],
    s3: [[1.9, 1.2], [3.35, 1.0], [4.8, 1.4], [7.25, 1.6], [8.75, 1.2]],
  };
  const $ = (s) => document.querySelector(s);
  const $$ = (s, root = document) => [...root.querySelectorAll(s)];
  const stage = $('#stage');

  // ---------- helpers ----------

  /** Deterministic random numbers, so every render scatters the same way. */
  function rng(seed) {
    return () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296);
  }

  /** Damped spring as a GSAP ease. The tween's duration maps to the spring's settle time. */
  function spring({ stiffness = 170, damping = 14, mass = 1 } = {}) {
    const w0 = Math.sqrt(stiffness / mass);
    const zeta = damping / (2 * Math.sqrt(stiffness * mass));
    let x, settle;
    if (zeta < 1) {
      const wd = w0 * Math.sqrt(1 - zeta * zeta);
      x = (t) => 1 - Math.exp(-zeta * w0 * t) * (Math.cos(wd * t) + (zeta * w0 / wd) * Math.sin(wd * t));
      settle = Math.log(1000) / (zeta * w0);
    } else {
      x = (t) => 1 - Math.exp(-w0 * t) * (1 + w0 * t);
      settle = 10 / w0;
    }
    return (p) => (p >= 1 ? 1 : x(p * settle));
  }
  const POP = spring({ stiffness: 220, damping: 14 });   // lively, visible overshoot
  const SPRING = spring({ stiffness: 170, damping: 18 }); // UI entrances
  const SOFT = spring({ stiffness: 120, damping: 22 });   // big objects

  /** Center of an element in stage pixels (1920x1080), whatever the current scale. */
  function stagePoint(el, fx = 0.5, fy = 0.5) {
    const s = stage.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    const k = s.width / 1920;
    return { x: (r.left - s.left + r.width * fx) / k, y: (r.top - s.top + r.height * fy) / k };
  }

  /** Time warp for a scene: inserts each hold (seconds) at its breakpoint, so motion keeps
      its speed and the viewer gets time to read. holds = [[breakpoint, seconds], ...]. */
  function warp(holds) {
    return (t) => t + holds.reduce((sum, [at, hold]) => sum + (t >= at - 1e-9 ? hold : 0), 0);
  }

  /** Sound cues (voice-over lines and effects). Resolved to master time after build and
      read by render.mjs, which hands them to audio/make_soundtrack.py. */
  const CUES = [];
  function cue(tl, name, at, extra = {}) {
    CUES.push({ tl, name, at, ...extra });
  }

  function splitChars(text) {
    return [...text].map((c) => `<span class="ch">${c}</span>`).join('');
  }

  // The screens "you saw". Designed at 900x540 and scaled to fit wherever they appear.
  const MOMENTS = {
    chat: () => `<div class="m m-chat">
      <div class="side"><b>Acme</b><span class="on"># launch</span><span># design</span><span># general</span><span># support</span></div>
      <div class="msgs">
        <div class="msg"><i class="av" style="background:#f472b6"></i><div><div class="who">Maya</div><div class="txt">When is the beta launch again? I can’t find it anywhere.</div></div></div>
        <div class="msg"><i class="av" style="background:#60a5fa"></i><div><div class="who">Leo</div><div class="txt">It’s in the Q3 plan doc, I read it this morning.</div></div></div>
        <div class="msg"><i class="av" style="background:#34d399"></i><div><div class="who">Sam</div><div class="txt">Who’s booking flights for the Lisbon offsite?</div></div></div>
      </div></div>`,
    code: () => {
      const L = [
        ['#bb9af7', 'func', '#7aa2f7', ' capture', '#c0caf5', '(every: 2.0) {'],
        ['#c0caf5', '    let frame = ', '#7aa2f7', 'screen', '#c0caf5', '.snapshot()'],
        ['#c0caf5', '    guard frame.', '#7dcfff', 'changed', '#c0caf5', ' else { return }'],
        ['#c0caf5', '    let text = ', '#7aa2f7', 'ocr', '#c0caf5', '(frame)'],
        ['#c0caf5', '    db.', '#7dcfff', 'save', '#c0caf5', '(frame, text)'],
        ['#c0caf5', '}', '#565f89', '', '#c0caf5', ''],
        ['#565f89', '// stays on this Mac', '#565f89', '', '#565f89', ''],
      ];
      return `<div class="m m-code">${L.map((l, i) => `<div class="ln"><span class="n">${i + 1}</span><span><span style="color:${l[0]}">${l[1]}</span><span style="color:${l[2]}">${l[3]}</span><span style="color:${l[4]}">${l[5]}</span></span></div>`).join('')}</div>`;
    },
    web: () => `<div class="m m-web"><div class="url">flights.example/lisbon</div><h4>Flights to Lisbon</h4>
      <div class="cards">
        <div class="card"><b>Tue 7:05 AM</b><span class="price">SFO → LIS</span><i class="ln2"></i><i class="ln2" style="width:70%"></i><i class="btn"></i></div>
        <div class="card feat"><b>Tue 1:40 PM</b><span class="price">SFO → LIS</span><i class="ln2"></i><i class="ln2" style="width:80%"></i><i class="btn"></i></div>
        <div class="card"><b>Wed 9:15 AM</b><span class="price">SFO → LIS</span><i class="ln2"></i><i class="ln2" style="width:60%"></i><i class="btn"></i></div>
      </div></div>`,
    repo: () => `<div class="m m-repo"><div class="url">github.com/frickgav/OpenRewind</div>
      <div class="repo-head"><span class="repo-name">frickgav / <b>OpenRewind</b></span><span class="repo-pub">Public</span></div>
      <div class="repo-tags"><span>MIT License</span><span>Free</span><span>Open source</span><span>Mac &amp; Windows</span></div>
      <div class="repo-readme"><b>Rewind your Mac or PC.</b><p>Records your screen and audio, pulls the text out, and lets you search or scroll back through everything. 100% local.</p>
      <i class="ln2"></i><i class="ln2" style="width:82%"></i><i class="ln2" style="width:64%"></i></div></div>`,
    doc: () => `<div class="m m-doc"><h3>Q3 launch plan</h3><div class="meta">Docs · edited 10:18 AM</div>
      <p class="hl-wrap">The <mark>beta<i></i></mark> <mark>launch<i></i></mark> is Thursday, right after the design review.</p>
      <div class="bar" style="width:92%"></div><div class="bar" style="width:78%"></div>
      <p>Press kit goes out Friday morning. Pricing stays at $0.</p>
      <div class="bar" style="width:86%"></div><div class="bar" style="width:64%"></div></div>`,
  };

  function fitMoment(kind, w) {
    const wrap = document.createElement('div');
    wrap.className = 'fit';
    wrap.style.transform = `scale(${w / 900})`;
    wrap.innerHTML = MOMENTS[kind]();
    return wrap;
  }

  /** A stylized macOS window: traffic lights, frosted title bar, rounded corners, soft shadow. */
  function createMacWindow({ title, kind, x, y, w, h, dark = false, parent }) {
    const win = document.createElement('div');
    win.className = 'mac' + (dark ? ' dark' : '');
    Object.assign(win.style, { left: x + 'px', top: y + 'px', width: w + 'px', height: h + 'px' });
    win.innerHTML = `<div class="mac-bar"><i class="tl r"></i><i class="tl y"></i><i class="tl g"></i><span class="mac-title">${title}</span></div><div class="mac-body"></div>`;
    win.querySelector('.mac-body').appendChild(fitMoment(kind, w));
    parent.appendChild(win);
    return win;
  }

  const THUMB = {
    chat: { bg: '#1a1d29', bars: ['#ffffff', '#5865f2', '#5865f2', '#5865f2'], side: '#12141d' },
    code: { bg: '#0f1117', bars: ['#bb9af7', '#7aa2f7', '#9ece6a', '#e0af68'] },
    web: { bg: '#eef2f7', bars: ['#0f172a', '#cbd5e1', '#cbd5e1', '#1059fb'] },
    repo: { bg: '#0d1117', bars: ['#e6edf3', '#3fb950', '#30363d', '#30363d'] },
    doc: { bg: '#ffffff', bars: ['#0f172a', '#d1d5db', '#d1d5db', '#d1d5db'] },
  };
  const KINDS = ['chat', 'code', 'web', 'doc'];

  function thumbEl(kind, r) {
    const t = THUMB[kind];
    const d = document.createElement('div');
    d.className = 'thumb';
    d.style.background = t.bg;
    if (t.side) d.insertAdjacentHTML('beforeend', `<i style="left:0;top:0;height:100%;width:22%;border-radius:0;background:${t.side}"></i>`);
    t.bars.forEach((c, i) => {
      d.insertAdjacentHTML('beforeend', `<i style="left:${t.side ? 30 : 12}%;top:${18 + i * 17}%;width:${(i ? 35 : 25) + r() * 35}%;background:${c}"></i>`);
    });
    return d;
  }

  /** Film-strip thumbnails along the timeline, oldest on the left. */
  function buildFilmStrip(n, seed = 7) {
    const r = rng(seed);
    const strip = $('#strip');
    const thumbs = [];
    for (let i = 0; i < n; i++) {
      const t = thumbEl(KINDS[Math.floor(i / 2) % 4], r);
      t.style.left = i * 120 + 'px';
      strip.appendChild(t);
      thumbs.push(t);
    }
    strip.style.width = n * 120 + 'px';
    return thumbs;
  }

  /** Simulated cursor along a curved path. Points are where the arrow's tip should go. */
  function moveCursor(points, duration = 0.7) {
    const tip = (p) => ({ x: p.x - 8, y: p.y - 5 });
    return gsap.timeline().to('#cursor', {
      duration, ease: 'power2.inOut',
      motionPath: { path: points.map(tip), curviness: 1.3 },
    });
  }

  /** Cursor press plus an expanding ring at (x, y). */
  function click(p) {
    return gsap.timeline()
      .to('#cursor', { scale: 0.82, duration: 0.07, ease: 'power2.out', transformOrigin: '20% 12%' })
      .to('#cursor', { scale: 1, duration: 0.14, ease: 'power2.out' })
      .set('#ripple', { x: p.x, y: p.y, scale: 0.3, opacity: 1 }, 0)
      .to('#ripple', { scale: 1.7, opacity: 0, duration: 0.45, ease: 'power2.out' }, 0);
  }

  /** Counts el up from `from` to `to`, with thousands separators. */
  function animateCounter(el, from, to, duration) {
    const o = { v: from };
    return gsap.to(o, {
      v: to, duration, ease: 'power2.out',
      onUpdate: () => { el.textContent = Math.round(o.v).toLocaleString('en-US'); },
    });
  }

  /** Types `text` into el, one character at a time. */
  function typeText(el, text, duration) {
    const o = { n: 0 };
    return gsap.to(o, {
      n: text.length, duration, ease: 'none',
      onUpdate: () => { el.textContent = text.slice(0, Math.round(o.n)); },
    });
  }

  function clock(min) {
    const h = Math.floor(min / 60), m = Math.round(min % 60);
    return `${((h + 11) % 12) + 1}:${String(m).padStart(2, '0')} ${h >= 12 ? 'PM' : 'AM'}`;
  }

  /** Drags the playhead back through the day: strip slides, motion blur, screens rewind. */
  function scrubTimeline({ playhead, strip, slots, order, label, duration = 1.1 }) {
    const tl = gsap.timeline();
    const from = 15 * 60 + 42, to = 10 * 60 + 18;     // 3:42 PM back to 10:18 AM
    const o = { p: 0 };
    tl.to(playhead, { x: -1180, duration, ease: 'power3.inOut' }, 0)
      .to(strip, { x: '+=980', duration, ease: 'power3.inOut' }, 0)
      .to(strip, { filter: 'blur(6px)', duration: duration * 0.35, ease: 'power2.in' }, 0)
      .to(strip, { filter: 'blur(0px)', duration: duration * 0.4, ease: 'power2.out' }, duration * 0.6)
      .to(o, { p: 1, duration, ease: 'power3.inOut', onUpdate: () => { label.textContent = 'Today · ' + clock(from + (to - from) * o.p); } }, 0)
      .to('#rvRewBadge', { opacity: 1, duration: 0.12 }, 0.05)
      .to('#rvRewBadge', { opacity: 0, duration: 0.2 }, duration - 0.1);
    // Screens swap with directional blur, faster in the middle of the drag.
    const cuts = [0.22, 0.48, 0.76].map((f) => f * duration);
    order.slice(1).forEach((kind, i) => {
      const out = slots[order[i]], inn = slots[kind], at = cuts[i];
      tl.to(out, { opacity: 0, x: 140, filter: 'blur(14px)', duration: 0.14, ease: 'power1.in' }, at)
        .fromTo(inn, { opacity: 0, x: -140, filter: 'blur(14px)' }, { opacity: 1, x: 0, filter: 'blur(0px)', duration: 0.16, ease: 'power1.out', immediateRender: false }, at + 0.04);
    });
    // Land on the page you were looking for with a small settle.
    tl.to('#rvFrame', { scale: 1.025, duration: 0.12, ease: 'power2.out' }, duration - 0.05)
      .to('#rvFrame', { scale: 1, duration: 0.5, ease: POP }, duration + 0.07);
    return tl;
  }

  /** Slow breathing glow on a button, `cycles` times. */
  function glowPulse(el, glow, cycles = 2, period = 0.9) {
    return gsap.timeline()
      .to(el, { scale: 1.035, duration: period / 2, ease: 'sine.inOut', yoyo: true, repeat: cycles * 2 - 1 }, 0)
      .to(glow, { opacity: 0.85, scale: 1.08, duration: period / 2, ease: 'sine.inOut', yoyo: true, repeat: cycles * 2 - 1 }, 0);
  }

  function capIn(tl, el, at) {
    tl.to(el, { opacity: 1, y: 0, filter: 'blur(0px)', duration: 0.45, ease: SPRING }, at);
  }
  function capOut(tl, el, at) {
    tl.to(el, { opacity: 0, y: -24, filter: 'blur(8px)', duration: 0.2, ease: 'power2.in' }, at);
  }

  // ---------- build the DOM pieces the scenes need ----------

  const hook3 = $('#hook3');
  hook3.innerHTML = splitChars('You ') +
    `<span class="glitch" id="forgot"><span class="g g1">forgot</span><span class="g g2">forgot</span><span class="gw">${splitChars('forgot')}</span></span>` +
    splitChars(' it.');

  const desk = $('#desk');
  const deskWins = [
    createMacWindow({ title: 'Flights to Lisbon', kind: 'web', x: 70, y: 96, w: 640, h: 420, parent: desk }),
    createMacWindow({ title: '#launch · Chat', kind: 'chat', x: 650, y: 140, w: 600, h: 400, dark: true, parent: desk }),
    createMacWindow({ title: 'Q3 launch plan · Docs', kind: 'doc', x: 330, y: 330, w: 660, h: 430, parent: desk }),
  ];

  const slots = {};
  ['chat', 'code', 'web', 'doc'].forEach((kind) => {
    const s = document.createElement('div');
    s.className = 'slot';
    s.appendChild(fitMoment(kind, 1174));
    $('#moments').appendChild(s);
    slots[kind] = s;
  });

  const N_THUMBS = 30;
  const thumbs = buildFilmStrip(N_THUMBS);
  const STRIP_X = 1760 - N_THUMBS * 120 + 8;         // newest thumbnail flush with the right edge
  const firstVisible = thumbs.findIndex((_, i) => i * 120 + STRIP_X > -112);

  ['9 AM', '10 AM', '11 AM', '12 PM', '1 PM', '2 PM', '3 PM', 'Now'].forEach((t) => $('#tlTicks').insertAdjacentHTML('beforeend', `<span>${t}</span>`));

  const vaultThumbs = ['doc', 'chat', 'web', 'code'].map((k, i) => {
    const t = thumbEl(k, rng(20 + i));
    t.style.left = '0px';
    $('#vaultStack').appendChild(t);
    return t;
  });

  const stackWins = [
    createMacWindow({ title: '#launch · 3:42 PM', kind: 'chat', x: 250, y: 150, w: 600, h: 370, dark: true, parent: $('#stack') }),
    createMacWindow({ title: 'Docs · 10:18 AM', kind: 'doc', x: 660, y: 70, w: 600, h: 370, parent: $('#stack') }),
    createMacWindow({ title: 'OpenRewind on GitHub', kind: 'repo', x: 1070, y: 150, w: 600, h: 370, parent: $('#stack') }),
  ];

  // ---------- scenes ----------

  /** Scene 1 (0-3s): kinetic hook. "forgot" glitches, dissolves, then rewinds. */
  function scene1() {
    const tl = gsap.timeline();
    const w = warp(HOLDS.s1);
    const [h1, h2] = [$('#hook1'), $('#hook2')];
    const chars = $$('.ch', hook3);
    const r = rng(3);
    gsap.set([h1, h2, hook3], { y: 70, opacity: 0, filter: 'blur(18px)' });

    const dissolve = gsap.timeline({ paused: true }).to(chars, {
      x: () => (r() - 0.5) * 520, y: () => (r() - 0.5) * 360, rotation: () => (r() - 0.5) * 120,
      opacity: 0, filter: 'blur(14px)', duration: 0.4, ease: 'power2.in',
      stagger: { each: 0.012, from: 'center' },
    });

    const inn = { y: 0, opacity: 1, filter: 'blur(0px)', duration: 0.42, ease: POP };
    const cut = { y: -40, opacity: 0, filter: 'blur(10px)', duration: 0.1, ease: 'power2.in' };
    tl.to(h1, inn, w(0.08)).to(h1, cut, w(0.72))
      .to(h2, inn, w(0.8)).to(h2, cut, w(1.42))
      .to(hook3, inn, w(1.5))
      // Glitch: RGB-split copies jitter, the word skews.
      .to('.glitch .g1', { keyframes: [{ x: -12, opacity: 0.9, duration: 0.04 }, { x: 8, duration: 0.04 }, { x: -5, duration: 0.04 }, { x: 14, duration: 0.04 }, { x: -3, duration: 0.04 }, { x: 0, opacity: 0, duration: 0.05 }] }, w(1.98))
      .to('.glitch .g2', { keyframes: [{ x: 10, opacity: 0.9, duration: 0.04 }, { x: -9, duration: 0.04 }, { x: 6, duration: 0.04 }, { x: -12, duration: 0.04 }, { x: 4, duration: 0.04 }, { x: 0, opacity: 0, duration: 0.05 }] }, w(1.98))
      .to('.glitch .gw', { keyframes: [{ skewX: 14, duration: 0.05 }, { skewX: -10, duration: 0.05 }, { skewX: 6, duration: 0.05 }, { skewX: 0, duration: 0.06 }] }, w(1.98))
      .to(dissolve, { progress: 1, duration: 0.36, ease: 'none' }, w(2.2))
      // Rewind: flash, scanlines, and the dissolve plays backwards.
      .to('#rewFlash', { opacity: 0.9, scale: 1.15, duration: 0.12, ease: 'power2.out' }, w(2.56))
      .to('#scanlines', { opacity: 1, duration: 0.1 }, w(2.56))
      .to(dissolve, { progress: 0, duration: 0.22, ease: 'power2.inOut' }, w(2.58))
      .to('#rewFlash', { opacity: 0, scale: 1.5, duration: 0.25, ease: 'power2.in' }, w(2.66))
      // ...and the hook itself plays back in reverse, fast.
      .to(hook3, { x: 160, opacity: 0, filter: 'blur(12px)', duration: 0.07, ease: 'none' }, w(2.8))
      .set(h2, { y: 0, x: -160, filter: 'blur(12px)', opacity: 0 }, w(2.8))
      .to(h2, { x: 0, opacity: 1, filter: 'blur(3px)', duration: 0.05, ease: 'none' }, w(2.83))
      .to(h2, { x: 160, opacity: 0, filter: 'blur(12px)', duration: 0.05, ease: 'none' }, w(2.88))
      .set(h1, { y: 0, x: -160, filter: 'blur(12px)', opacity: 0 }, w(2.88))
      .to(h1, { x: 0, opacity: 1, filter: 'blur(3px)', duration: 0.05, ease: 'none' }, w(2.91))
      .to(h1, { x: 160, opacity: 0, filter: 'blur(12px)', duration: 0.06, ease: 'none' }, w(2.96))
      .to('#scanlines', { opacity: 0, duration: 0.15 }, w(2.95))
      .to('#bgGlow', { opacity: 1, duration: 0.4 }, w(2.8))
      .set('#s1', { opacity: 0 }, w(3.0));
    cue(tl, 'vo:saw', w(0.1)); cue(tl, 'vo:read', w(0.8) + 0.02); cue(tl, 'vo:forgot', w(1.5) + 0.02);
    [w(0.08), w(0.8), w(1.5)].forEach((t) => cue(tl, 'sfx:hit', t));
    cue(tl, 'sfx:glitch', w(1.98)); cue(tl, 'sfx:dissolve', w(2.2)); cue(tl, 'sfx:rewind', w(2.56));
    return tl;
  }

  /** Scene 2 (3-7s): logo, then into the Mac menu bar; cursor opens the timeline. */
  function scene2() {
    const tl = gsap.timeline();
    const w = warp(HOLDS.s2);
    const { icon, item } = P;
    const menu = $('#mbMenu');

    gsap.set('#logo', { scale: 0.2, opacity: 0 });
    gsap.set('#logoRing', { scale: 0.5, opacity: 0 });
    gsap.set(['#wordmark', '#tagline'], { y: 30, opacity: 0, filter: 'blur(10px)' });
    gsap.set('#laptop', { y: 1000 });
    gsap.set('#mbIcon', { opacity: 0 });
    gsap.set(deskWins, { opacity: 0, scale: 0.9, y: 40 });
    gsap.set('#recPill', { opacity: 0, x: 30 });
    gsap.set(menu, { opacity: 0, scale: 0.92, y: -8 });
    gsap.set('#cursor', { x: 900 - 8, y: 760 - 5, opacity: 0 });
    gsap.set('#s3', { scale: 1320 / 1920, opacity: 0, y: -6 });

    tl.set('#s2', { opacity: 1 }, w(0))
      .to('#logo', { opacity: 1, scale: 1, duration: 0.8, ease: POP }, w(0))
      .to('#logoRing', { opacity: 0.9, duration: 0.05 }, w(0.05))
      .to('#logoRing', { scale: 2.1, opacity: 0, duration: 0.7, ease: 'power2.out' }, w(0.08))
      .to('#wordmark', { opacity: 1, y: 0, filter: 'blur(0px)', duration: 0.5, ease: SPRING }, w(0.22))
      .to('#tagline', { opacity: 1, y: 0, filter: 'blur(0px)', duration: 0.5, ease: SPRING }, w(0.36))
      .to(['#wordmark', '#tagline'], { opacity: 0, y: -20, filter: 'blur(8px)', duration: 0.25, ease: 'power2.in' }, w(0.92))
      // Laptop springs up; the logo shrinks into the menu bar.
      .to('#laptop', { y: 0, duration: 1.0, ease: SOFT }, w(0.95))
      .to('#bgGlow', { opacity: 0.45, duration: 0.6 }, w(0.95))
      .to('#logo', { x: icon.x - 960, y: icon.y - 540, scale: 26 / 220, duration: 0.75, ease: 'power3.inOut' }, w(1.05))
      .to('#mbIcon', { opacity: 1, duration: 0.1 }, w(1.78))
      .to('#logo', { opacity: 0, duration: 0.1 }, w(1.8))
      .to('#mbIcon', { backgroundColor: 'rgba(255,255,255,.22)', duration: 0.12, yoyo: true, repeat: 1 }, w(1.8))
      .to(deskWins, { opacity: 1, scale: 1, y: 0, duration: 0.6, ease: SPRING, stagger: 0.09 }, w(1.45))
      .to('#recPill', { opacity: 1, x: 0, duration: 0.4, ease: SPRING }, w(1.85))
      .to('#recDot', { opacity: 0.25, scale: 0.7, duration: 0.3, ease: 'sine.inOut', yoyo: true, repeat: 15 }, w(1.9))
      // Cursor to the menu-bar icon, click, menu opens.
      .to('#cursor', { opacity: 1, duration: 0.15 }, w(2.1))
      .add(moveCursor([{ x: 1180, y: 520 }, icon], 0.75), w(2.12))
      .add(click(icon), w(2.88))
      .to('#mbIcon', { backgroundColor: 'rgba(255,255,255,.25)', duration: 0.05 }, w(2.9))
      .to(menu, { opacity: 1, scale: 1, y: 0, duration: 0.3, ease: SPRING }, w(2.92))
      .add(moveCursor([{ x: (icon.x + item.x) / 2 - 20, y: (icon.y + item.y) / 2 }, item], 0.32), w(3.05))
      .to('#miTimeline', { backgroundColor: '#1059fb', duration: 0.06 }, w(3.3))
      .add(click(item), w(3.4))
      .to(menu, { opacity: 0, duration: 0.15 }, w(3.5))
      .to('#mbIcon', { backgroundColor: 'rgba(255,255,255,0)', duration: 0.1 }, w(3.5))
      // Expand into the full-screen Rewind view.
      .to('#cursor', { opacity: 0, duration: 0.15 }, w(3.5))
      .to('#laptop', { scale: 1.48, duration: 0.6, ease: 'power3.in' }, w(3.52))
      .to('#laptop', { opacity: 0, duration: 0.3, ease: 'power1.in' }, w(3.6))
      .to('#bgGlow', { opacity: 0, duration: 0.4 }, w(3.55))
      .to('#s3', { opacity: 1, duration: 0.22, ease: 'power1.out' }, w(3.56))
      .to('#s3', { scale: 1, y: 0, duration: 0.6, ease: 'power3.inOut' }, w(3.55))
      .set('#s2', { opacity: 0 }, w(4.2));
    cue(tl, 'sfx:logo', 0); cue(tl, 'vo:meet', 0.15); cue(tl, 'sfx:whoosh', w(0.95)); cue(tl, 'vo:memory', w(1.1));
    cue(tl, 'sfx:pop', w(1.45)); cue(tl, 'sfx:click', w(2.88)); cue(tl, 'sfx:click', w(3.4)); cue(tl, 'sfx:whoosh', w(3.52));
    return tl;
  }

  /** Scene 3 (6.5-14.8s): capture, scrub, search, Ask AI, local. */
  function scene3() {
    const tl = gsap.timeline();
    const w = warp(HOLDS.s3);
    const caps = $$('.cap');
    gsap.set(caps, { opacity: 0, y: 26, filter: 'blur(10px)' });
    gsap.set('#counter', { opacity: 0, y: 16, xPercent: -50 });
    gsap.set(slots.chat, { opacity: 1 });
    gsap.set('#strip', { x: STRIP_X });
    gsap.set(thumbs.slice(firstVisible), { opacity: 0, x: 80, scale: 0.4 });
    gsap.set('#playhead', { x: -1700 + (firstVisible * 120 + STRIP_X) });
    gsap.set('#rvHits', { opacity: 0, x: -10 });
    gsap.set('#rvAudio', { opacity: 0, y: 40 });
    gsap.set('#ai', { opacity: 0, x: 480 });
    gsap.set(['#aiQ', '#aiTool', '#aiA'], { opacity: 0, y: 14 });
    gsap.set('.ai-line', { opacity: 0, y: 8 });
    gsap.set('#vaultTile', { opacity: 0, scale: 0.7 });
    gsap.set(vaultThumbs, { opacity: 0, y: 460, x: (i) => (i - 1.5) * 300, scale: 0.56, rotation: 0 });
    gsap.set('#lockBadge', { opacity: 0, scale: 0.5, xPercent: -50 });
    gsap.set('#vaultSub', { opacity: 0, y: 20 });
    gsap.set('.chip', { opacity: 0, y: 30, scale: 0.8 });

    const visible = thumbs.slice(firstVisible);
    const T = 0.5; // scene starts at 6.5s on the master timeline; times below are master time - 6.5

    // Beat 1 (7.0-8.5): a snapshot every 2 seconds.
    tl.to('#cam', { scale: 1.2, transformOrigin: '960px 1010px', duration: 0.6, ease: 'power3.inOut' }, w(T + 0.05));
    capIn(tl, '#cap1', w(T + 0.05));
    tl.to('#rvFrame', { opacity: 0.25, duration: 0.4 }, w(T + 0.05))
      .to('#rvFrame', { opacity: 1, duration: 0.4 }, w(T + 1.45))
      .to('#counter', { opacity: 1, y: 0, duration: 0.4, ease: SPRING }, w(T + 0.15))
      .add(animateCounter($('#counterNum'), 0, 1800, 1.05), w(T + 0.2))
      .to(visible, { opacity: 1, x: 0, scale: 1, duration: 0.45, ease: POP, stagger: 0.062 }, w(T + 0.15))
      .to('#playhead', { x: 0, duration: visible.length * 0.062 + 0.1, ease: 'none' }, w(T + 0.15));
    [0.35, 0.7, 1.05].forEach((t) => tl.to('#shutter', { opacity: 0.35, duration: 0.04, yoyo: true, repeat: 1, ease: 'power1.out' }, w(T + t)));
    capOut(tl, '#cap1', w(T + 1.45));
    tl.to('#counter', { opacity: 0, y: -10, duration: 0.2 }, w(T + 1.45));

    // Beat 2 (8.5-9.9): scroll back through your day.
    tl.to('#cam', { scale: 1, duration: 0.45, ease: 'power3.inOut' }, w(T + 1.4));
    capIn(tl, '#cap2', w(T + 1.55));
    tl.add(scrubTimeline({
      playhead: '#playhead', strip: '#strip', slots, order: ['chat', 'code', 'web', 'doc'], label: $('#rvTime'), duration: 1.05,
    }), w(T + 1.75));
    capOut(tl, '#cap2', w(T + 2.85));

    // Beat 3 (9.9-11.3): search every word you saw or heard.
    capIn(tl, '#cap3', w(T + 2.95));
    tl.to('#rvSearch', { borderColor: 'rgba(77,134,255,.8)', backgroundColor: 'rgba(16,89,251,.12)', duration: 0.2 }, w(T + 3.0))
      .to('#caret', { opacity: 1, duration: 0.01 }, w(T + 3.0))
      .add(typeText($('#searchText'), 'beta launch', 0.5), w(T + 3.08))
      .to('#caret', { opacity: 0, duration: 0.01 }, w(T + 3.62))
      .to('#rvHits', { opacity: 1, x: 0, duration: 0.35, ease: SPRING }, w(T + 3.62))
      .to('#rvFrame mark i', { scaleX: 1, duration: 0.3, ease: 'power3.out', stagger: 0.08 }, w(T + 3.65))
      .to('#rvAudio', { opacity: 1, y: 0, duration: 0.5, ease: POP }, w(T + 3.8));
    capOut(tl, '#cap3', w(T + 4.3));

    // Beat 4 (11.3-12.7): Ask AI, with your own key.
    const btn = P.askBtn;
    capIn(tl, '#cap4', w(T + 4.4));
    tl.set('#cursor', { x: 1360 - 8, y: 560 - 5 }, w(T + 4.35))
      .to('#cursor', { opacity: 1, duration: 0.12 }, w(T + 4.38))
      .add(moveCursor([{ x: 1560, y: 330 }, btn], 0.38), w(T + 4.4))
      .add(click(btn), w(T + 4.78))
      .to('#rvAskBtn', { backgroundColor: 'rgba(16,89,251,.6)', duration: 0.08 }, w(T + 4.8))
      .to('#cursor', { opacity: 0, duration: 0.15 }, w(T + 4.95))
      .to(['#rvFrame', '#rvSearch', '#rvAudio'], { x: -230, duration: 0.55, ease: SOFT }, w(T + 4.85))
      .to('#rvAskBtn', { opacity: 0, duration: 0.2 }, w(T + 4.85))
      .to('#ai', { opacity: 1, x: 0, duration: 0.6, ease: SPRING }, w(T + 4.85))
      .to('#aiQ', { opacity: 1, y: 0, duration: 0.3, ease: SPRING }, w(T + 5.0))
      .add(typeText($('#aiQText'), 'What did I work on today?', 0.38), w(T + 5.02))
      .to('#aiTool', { opacity: 1, y: 0, duration: 0.25, ease: SPRING }, w(T + 5.42))
      .to('#aiA', { opacity: 1, y: 0, duration: 0.35, ease: SPRING }, w(T + 5.52))
      .to('.ai-line', { opacity: 1, y: 0, duration: 0.3, ease: SPRING, stagger: 0.09 }, w(T + 5.58));
    capOut(tl, '#cap4', w(T + 6.75));

    // Beat 5 (13.3-14.8): private and local. Screens fold into one tile on your computer.
    tl.to('#cam', { scale: 0.9, opacity: 0, filter: 'blur(6px)', duration: 0.4, ease: 'power2.in' }, w(T + 6.80));
    capIn(tl, '#cap5', w(T + 6.88));
    tl.to('#vaultTile', { opacity: 1, scale: 1, duration: 0.6, ease: SOFT }, w(T + 6.90))
      .to(vaultThumbs, { opacity: 1, y: (i) => -i * 7, x: (i) => i * 6, rotation: (i) => (i - 1.5) * 4, scale: 1, duration: 0.55, ease: SPRING, stagger: 0.05 }, w(T + 6.85))
      .to('#lockBadge', { opacity: 1, scale: 1, duration: 0.55, ease: POP }, w(T + 7.00))
      .to('#vaultSub', { opacity: 1, y: 0, duration: 0.4, ease: SPRING }, w(T + 7.12))
      .to('.chip', { opacity: 1, y: 0, scale: 1, duration: 0.45, ease: POP, stagger: 0.05 }, w(T + 7.18));
    capOut(tl, '#cap5', w(T + 8.25));
    tl.to('#vault', { opacity: 0, scale: 0.96, duration: 0.25, ease: 'power2.in' }, w(T + 8.25))
      .set('#s3', { opacity: 0 }, w(T + 8.55));
    cue(tl, 'vo:snap', w(T + 0.15));
    [0.35, 0.7, 1.05].forEach((t) => cue(tl, 'sfx:shutter', w(T + t)));
    cue(tl, 'sfx:whoosh', w(T + 1.4)); cue(tl, 'vo:scroll', w(T + 1.55)); cue(tl, 'sfx:rewind', w(T + 1.75), { dur: 1.05 });
    cue(tl, 'vo:search', w(T + 2.95) + 0.05); cue(tl, 'sfx:type', w(T + 3.08), { dur: 0.5, n: 11 }); cue(tl, 'sfx:ding', w(T + 3.62));
    cue(tl, 'vo:ask', w(T + 4.4)); cue(tl, 'sfx:click', w(T + 4.78)); cue(tl, 'sfx:whoosh', w(T + 4.85));
    cue(tl, 'sfx:type', w(T + 5.02), { dur: 0.38, n: 25 }); cue(tl, 'sfx:pop', w(T + 5.52));
    cue(tl, 'sfx:whoosh', w(T + 6.8)); cue(tl, 'vo:local', w(T + 6.88)); cue(tl, 'sfx:lock', w(T + 7.0)); cue(tl, 'sfx:pop', w(T + 7.18));
    return tl;
  }

  /** Scene 4 (14.8-17.7s): floating windows from different moments, headline, CTA. */
  function scene4() {
    const tl = gsap.timeline();
    gsap.set(stackWins, { opacity: 0, y: 220, rotationY: (i) => (1 - i) * 18, rotationX: 8, z: (i) => (i === 1 ? 60 : -40) });
    gsap.set('#headline', { opacity: 0, y: 40, filter: 'blur(14px)' });
    gsap.set('#pill', { opacity: 0, scale: 0.6 });
    gsap.set('#pillGlow', { opacity: 0, scale: 0.8 });
    gsap.set(['#url', '#fine'], { opacity: 0, y: 20 });

    tl.set('#s4', { opacity: 1 }, 0)
      .to('#bgGlow', { opacity: 0.9, duration: 0.8 }, 0)
      .to('#bgGrid', { opacity: 1, duration: 0.8 }, 0)
      .to(stackWins, { opacity: 1, y: 0, duration: 0.9, ease: SOFT, stagger: 0.08 }, 0.05)
      // Slow parallax drift for the rest of the video.
      .to(stackWins[0], { x: -50, y: -10, rotationY: 22, duration: 4.7, ease: 'sine.inOut' }, 0.2)
      .to(stackWins[1], { x: 0, y: -24, duration: 4.7, ease: 'sine.inOut' }, 0.2)
      .to(stackWins[2], { x: 50, y: -10, rotationY: -22, duration: 4.7, ease: 'sine.inOut' }, 0.2)
      .to('#bgGrid', { y: -60, duration: 4.9, ease: 'none' }, 0)
      .to('#headline', { opacity: 1, y: 0, filter: 'blur(0px)', duration: 0.6, ease: SPRING }, 0.35)
      .to('#pill', { opacity: 1, scale: 1, duration: 0.7, ease: POP }, 0.65)
      .to('#pillGlow', { opacity: 0.45, scale: 1, duration: 0.5 }, 0.7)
      .to('#url', { opacity: 1, y: 0, duration: 0.45, ease: SPRING }, 0.85)
      .to('#fine', { opacity: 1, y: 0, duration: 0.45, ease: SPRING }, 1.0)
      .add(glowPulse($('#pill'), $('#pillGlow'), 3, 1.2), 1.2);
    cue(tl, 'sfx:impact', 0.35); cue(tl, 'vo:cta', 0.4); cue(tl, 'sfx:pop', 0.65);
    return tl;
  }

  // ---------- master timeline ----------

  // Measure targets before any scene moves or scales its layers.
  let P;
  function measure() {
    const icon = stagePoint($('#mbIcon'));
    const screen = stagePoint($('#screen'), 0, 0);
    $('#mbMenu').style.left = icon.x - screen.x - 250 + 'px';
    P = { icon, item: stagePoint($('#miTimeline'), 0.35, 0.5), askBtn: stagePoint($('#rvAskBtn')) };
  }

  let master;
  function build() {
    measure();
    master = gsap.timeline({ paused: true });
    // Each scene starts where the previous one hands over (times include the holds).
    const reveal = warp(HOLDS.s1)(3.0);
    const showcase = reveal + warp(HOLDS.s2)(3.5);
    const outro = showcase + warp(HOLDS.s3)(8.75) - 0.45;
    master.addLabel('hook', 0).add(scene1(), 'hook')
      .addLabel('reveal', reveal).add(scene2(), 'reveal')
      .addLabel('showcase', showcase).add(scene3(), 'showcase')
      .addLabel('outro', outro).add(scene4(), 'outro');
    const d = master.duration();
    if (d > DURATION + 1e-6) console.warn(`timeline runs ${d.toFixed(3)}s, longer than ${DURATION}s`);
    if (d < DURATION) master.to({}, { duration: DURATION - d }, d); // hold the last frame
    window.__labels = { reveal, showcase, outro };
    window.__cues = CUES.map(({ tl, ...c }) => ({ ...c, t: +(tl.startTime() + c.at).toFixed(3) })).sort((a, b) => a.t - b.t);
    return master;
  }

  // ---------- stage scaling and review controls ----------

  function fitStage() {
    if (RENDER) { stage.style.transform = 'none'; return; }
    const vp = $('#viewport').getBoundingClientRect();
    stage.style.transform = `scale(${Math.min(vp.width / 1920, vp.height / 1080)})`;
  }

  // Soundtrack (music, effects, voice-over) follows the timeline. Browsers only allow sound
  // after a click or key press, so it starts off.
  const audio = $('#soundtrack');
  let soundOn = false;
  function syncAudio(force = false) {
    if (!soundOn || master.paused() || master.progress() >= 1) { audio.pause(); return; }
    if (force || Math.abs(audio.currentTime - master.time()) > 0.15) audio.currentTime = master.time();
    if (audio.paused) audio.play().catch(() => {});
  }
  function setSound(on) {
    soundOn = on;
    $('#btnSound').textContent = on ? 'Sound on' : 'Sound off';
    syncAudio(true);
  }

  function setupControls() {
    const play = $('#btnPlay'), scrub = $('#scrub'), readout = $('#readout'), loop = $('#loop');
    const sync = () => {
      syncAudio();
      scrub.value = Math.round(master.progress() * 1000);
      readout.textContent = `${master.time().toFixed(2)} / ${DURATION.toFixed(2)} s`;
      play.textContent = master.paused() ? 'Play' : 'Pause';
    };
    const toggle = () => {
      if (master.progress() >= 1) master.restart();
      else master.paused(!master.paused());
      sync();
      syncAudio(true);
    };
    const restart = () => { master.restart(); sync(); syncAudio(true); };
    master.eventCallback('onUpdate', sync);
    master.eventCallback('onComplete', () => { if (loop.checked) master.restart(); sync(); });
    play.addEventListener('click', toggle);
    $('#btnRestart').addEventListener('click', restart);
    scrub.addEventListener('input', () => { master.pause(); master.seek((scrub.value / 1000) * DURATION, false); sync(); });
    $('#btnSound').addEventListener('click', () => setSound(!soundOn));
    // With ?clean there are no controls: a click restarts from the top with sound.
    if (CLEAN) stage.addEventListener('click', () => { setSound(true); restart(); });
    try { loop.checked = localStorage.getItem('orv-loop') === '1'; } catch (e) { /* storage blocked */ }
    if (params.has('loop')) loop.checked = true;
    loop.addEventListener('change', () => { try { localStorage.setItem('orv-loop', loop.checked ? '1' : '0'); } catch (e) { /* storage blocked */ } });
    window.addEventListener('keydown', (e) => {
      if (e.target.tagName === 'INPUT' && e.target.type === 'range' && e.code !== 'Space') return;
      if (e.code === 'Space') { e.preventDefault(); toggle(); }
      if (e.key === 'r' || e.key === 'R') restart();
      if (e.key === 'm' || e.key === 'M') setSound(!soundOn);
    });
    $('#btnExport').addEventListener('click', exportWebM);
    sync();
  }

  /** Records the stage to WebM. The browser asks which tab to share: pick this one.
      For a frame-perfect 60 fps MP4, use render.mjs instead (see README). */
  async function exportWebM() {
    const msg = $('#exportMsg');
    if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia || !window.MediaRecorder) {
      msg.textContent = 'This browser cannot record the tab. Use render.mjs (see README).';
      return;
    }
    let stream;
    try {
      stream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 60 }, audio: false, preferCurrentTab: true });
    } catch (e) {
      msg.textContent = 'Recording was blocked or cancelled. Open the page locally, or use render.mjs.';
      return;
    }
    const [track] = stream.getVideoTracks();
    document.body.classList.add('clean');
    fitStage();
    try {
      if (window.CropTarget && track.cropTo) await track.cropTo(await CropTarget.fromElement(stage));
    } catch (e) { /* cropping unsupported: the whole tab is recorded */ }
    // Mix the soundtrack into the recording.
    try {
      const a = audio.captureStream ? audio.captureStream() : audio.mozCaptureStream();
      a.getAudioTracks().forEach((tr) => stream.addTrack(tr));
      soundOn = true;
    } catch (e) { /* no audio capture: video only */ }
    const type = ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm'].find((t) => MediaRecorder.isTypeSupported(t));
    const rec = new MediaRecorder(stream, { mimeType: type, videoBitsPerSecond: 24e6 });
    const chunks = [];
    rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
    rec.onstop = () => {
      track.stop();
      document.body.classList.remove('clean');
      fitStage();
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob(chunks, { type: 'video/webm' }));
      a.download = 'openrewind-launch.webm';
      document.body.appendChild(a);
      a.click();
      a.remove();
      msg.textContent = 'Saved openrewind-launch.webm.';
    };
    master.pause(0);
    await new Promise((r) => setTimeout(r, 400)); // let the capture settle on frame 0
    rec.start();
    master.play(0);
    syncAudio(true);
    setTimeout(() => rec.stop(), DURATION * 1000 + 300);
    msg.textContent = 'Recording…';
  }

  // ---------- boot ----------

  (document.fonts ? document.fonts.ready : Promise.resolve()).then(() => {
    fitStage();
    window.addEventListener('resize', fitStage);
    build();
    // Hooks for render.mjs: seek to an exact time and render it.
    window.__duration = DURATION;
    window.__seek = (t) => { master.seek(t, false); };
    window.__ready = true;
    if (RENDER) { master.seek(0, false); return; }
    setupControls();
    master.play(0);
  });
})();
