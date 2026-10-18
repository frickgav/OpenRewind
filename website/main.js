// OpenRewind site: scroll reveals, OS-aware download buttons, and the animated timeline mock.
(function () {
  "use strict";
  document.documentElement.classList.remove("no-js");

  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // Nav border once the page scrolls.
  const nav = document.querySelector(".nav");
  const onScroll = () => nav.classList.toggle("scrolled", window.scrollY > 8);
  onScroll();
  window.addEventListener("scroll", onScroll, { passive: true });

  // Reveal on scroll.
  const items = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window && !reduce) {
    const io = new IntersectionObserver((entries) => {
      entries.forEach((e) => {
        if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); }
      });
    }, { rootMargin: "0px 0px -8% 0px" });
    items.forEach((el, i) => { el.style.transitionDelay = (i % 4) * 60 + "ms"; io.observe(el); });
  } else {
    items.forEach((el) => el.classList.add("in"));
  }

  // Put the visitor's OS first.
  const ua = navigator.userAgent;
  const os = /Windows/i.test(ua) ? "win" : /Mac/i.test(ua) ? "mac" : null;
  if (os) {
    const card = document.querySelector('.dl[data-os="' + os + '"]');
    if (card) {
      card.classList.add("suggested");
      const b = document.createElement("span");
      b.className = "badge";
      b.textContent = "Recommended for you";
      card.insertBefore(b, card.querySelector(".dl-icon").nextSibling);
    }
    if (os === "win") {
      const mac = document.querySelector('.hero-cta [data-dl="mac"]');
      const win = document.querySelector('.hero-cta [data-dl="win"]');
      mac.className = "btn btn-ghost"; win.className = "btn btn-dark";
      win.parentNode.insertBefore(win, mac);
    }
  }

  document.getElementById("yr").textContent = new Date().getFullYear();

  // Audio waveform bars.
  const wave = document.getElementById("wave");
  for (let i = 0; i < 32; i++) {
    const bar = document.createElement("i");
    bar.style.height = 20 + Math.round(Math.abs(Math.sin(i * 1.7)) * 50) + "px";
    bar.style.animationDelay = (i * 0.07).toFixed(2) + "s";
    wave.appendChild(bar);
  }

  // Timeline bar: app segments and audio runs.
  const colors = ["#6d4aff", "#22c55e", "#f97316", "#0ea5e9", "#ec4899", "#eab308", "#94a3b8"];
  const segs = document.getElementById("tl-segs");
  const audio = document.getElementById("tl-audio");
  let seed = 7;
  const rnd = () => (seed = (seed * 9301 + 49297) % 233280) / 233280;
  for (let i = 0; i < 46; i++) {
    const s = document.createElement("i");
    s.style.flex = (0.4 + rnd() * 2.2).toFixed(2);
    s.style.background = colors[Math.floor(rnd() * colors.length)];
    if (rnd() < 0.12) s.style.opacity = "0";
    segs.appendChild(s);
  }
  for (let i = 0; i < 22; i++) {
    const a = document.createElement("i");
    a.style.flex = (0.5 + rnd() * 3).toFixed(2);
    a.style.background = rnd() < 0.5 ? "#f97316" : "#22c55e";
    if (rnd() < 0.45) a.style.opacity = "0";
    audio.appendChild(a);
  }

  // Search demo: type a query, jump the playhead to the match, swap the frame.
  const scenes = [
    { q: "beta launch", title: "Q3 planning · Notes", hit: "launch the <mark>beta</mark> on Thursday after the review", time: "Tuesday, 2:14 PM · 3 days ago", at: 34 },
    { q: "that recipe", title: "Safari · Weeknight dinners", hit: "lemon garlic pasta, the <mark>recipe</mark> you scrolled past", time: "Wednesday, 7:42 PM · 2 days ago", at: 58 },
    { q: "api key rotation", title: "Standup call · Audio", hit: "“we’re doing the <mark>key rotation</mark> on Friday”", time: "Thursday, 9:31 AM · yesterday", at: 79 },
    { q: "flight confirmation", title: "Mail · Your trip", hit: "<mark>Confirmation</mark> code K7QX2M, seat 14A", time: "Monday, 11:05 AM · 4 days ago", at: 12 },
  ];
  const typed = document.getElementById("typed");
  const head = document.getElementById("tl-head");
  const body = document.getElementById("frame-body");
  const hitEl = body.querySelector(".fb-hit");
  const titleEl = document.getElementById("frame-title");
  const timeEl = document.getElementById("frame-time");

  function show(s) {
    body.classList.add("fade");
    setTimeout(() => {
      hitEl.innerHTML = s.hit;
      titleEl.textContent = s.title;
      timeEl.textContent = s.time;
      body.classList.remove("fade");
    }, 300);
    head.style.left = s.at + "%";
  }

  if (reduce) {
    typed.textContent = scenes[0].q;
    return;
  }

  let n = 0;
  function play() {
    const s = scenes[n % scenes.length];
    let i = 0;
    typed.textContent = "";
    const t = setInterval(() => {
      typed.textContent = s.q.slice(0, ++i);
      if (i >= s.q.length) {
        clearInterval(t);
        setTimeout(() => show(s), 250);
        setTimeout(() => { n++; play(); }, 3600);
      }
    }, 70);
  }
  play();

  // Let people scrub the bar themselves.
  const bar = document.getElementById("tl-bar");
  bar.addEventListener("pointermove", (e) => {
    if (e.buttons !== 1) return;
    const r = bar.getBoundingClientRect();
    head.style.transition = "none";
    head.style.left = Math.max(0, Math.min(100, ((e.clientX - r.left) / r.width) * 100)) + "%";
  });
  bar.addEventListener("pointerup", () => { head.style.transition = ""; });
})();
