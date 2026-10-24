"""Builds the launch video soundtrack: music bed, sound effects and voice-over, mixed to
48 kHz stereo and placed on the cue times the GSAP timeline reports.

    node render.mjs --cues audio/cues.json        # dump cue times from main.js
    python3 audio/make_soundtrack.py               # -> audio/soundtrack.wav + soundtrack.mp3

Everything except the voice-over is synthesized here (numpy only), so the music and effects
are original and free to use. Voice-over lines come from audio/vo/*.wav (see make_voiceover.py).
"""
import json
import os
import subprocess
import sys
import wave

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
SR = 48000
rng = np.random.default_rng(7)


def load_wav(path):
    with wave.open(path) as w:
        n, sr, ch, width = w.getnframes(), w.getframerate(), w.getnchannels(), w.getsampwidth()
        raw = w.readframes(n)
    a = np.frombuffer(raw, dtype={2: np.int16, 4: np.int32}[width]).astype(np.float32)
    a /= float(2 ** (8 * width - 1))
    if ch > 1:
        a = a.reshape(-1, ch).mean(axis=1)
    if sr != SR:  # linear resample is fine for speech at these rates
        x = np.linspace(0, len(a) - 1, int(len(a) * SR / sr))
        a = np.interp(x, np.arange(len(a)), a).astype(np.float32)
    return a


def t(d):
    return np.arange(int(d * SR)) / SR


def env(n, a=0.005, r=0.1, curve=4.0):
    """Attack then exponential-ish release over n samples."""
    e = np.ones(n, dtype=np.float32)
    na = max(1, int(a * SR))
    e[:na] = np.linspace(0, 1, na)
    tail = np.linspace(0, 1, n - na)
    e[na:] = (1 - tail) ** curve if r is None else np.exp(-tail * (n - na) / SR / max(r, 1e-4))
    return e


def lowpass(x, cutoff):
    """One-pole low-pass (cutoff may be an array for sweeps)."""
    cutoff = np.broadcast_to(np.asarray(cutoff, dtype=np.float32), x.shape)
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):
        acc = (1 - a[i]) * x[i] + a[i] * acc
        y[i] = acc
    return y


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def noise(d):
    return rng.standard_normal(int(d * SR)).astype(np.float32)


def sine(f, d, phase=0.0):
    f = np.broadcast_to(np.asarray(f, dtype=np.float64), (int(d * SR),))
    return np.sin(2 * np.pi * np.cumsum(f) / SR + phase).astype(np.float32)


def saw(f, d):
    f = np.broadcast_to(np.asarray(f, dtype=np.float64), (int(d * SR),))
    ph = np.cumsum(f) / SR
    return (2 * (ph - np.floor(ph + 0.5))).astype(np.float32)


def bell(f, d, decay=0.8):
    x = t(d)
    s = sum(a * np.sin(2 * np.pi * f * m * x) * np.exp(-x / (decay / m ** 0.5))
            for m, a in [(1, 1), (2.01, 0.45), (3.0, 0.22), (4.2, 0.12), (5.43, 0.06)])
    return (s * env(len(x), 0.002, None, 1.0)).astype(np.float32)


# ---------- sound effects ----------

def sfx_hit():
    d = 0.5
    body = sine(np.geomspace(140, 45, int(d * SR)), d) * env(int(d * SR), 0.002, 0.12)
    air = lowpass(noise(d), 1800) * env(int(d * SR), 0.001, 0.05) * 0.5
    return body * 0.9 + air


def sfx_glitch():
    d = 0.32
    n = int(d * SR)
    x = np.zeros(n, dtype=np.float32)
    pos = 0
    while pos < n:
        seg = int(rng.uniform(0.012, 0.04) * SR)
        f = rng.choice([220, 440, 880, 1760, 3520])
        part = np.sign(sine(f, seg / SR)) * 0.35 if rng.random() < 0.5 else noise(seg / SR) * 0.4
        k = min(len(part), n - pos)
        x[pos:pos + k] = part[:k]
        pos += seg + int(rng.uniform(0, 0.015) * SR)
    x = np.round(x * 6) / 6  # bitcrush
    return x * env(n, 0.001, None, 0.6)


def sfx_dissolve():
    d = 0.45
    n = int(d * SR)
    return lowpass(noise(d), np.geomspace(9000, 400, n)) * env(n, 0.01, None, 1.5) * 0.7


def sfx_rewind(dur=0.4):
    """Tape-rewind chirp: a fast warble climbing in pitch, plus a reversed swell."""
    d = max(dur, 0.3) + 0.15
    n = int(d * SR)
    wob = 1 + 0.35 * np.sin(2 * np.pi * np.linspace(0, 28 * d, n))
    f = np.geomspace(300, 2400, n) * wob
    chirp = lowpass(saw(f, d), 3500) * 0.35
    swell = lowpass(noise(d), np.linspace(500, 7000, n)) * np.linspace(0, 1, n) ** 2 * 0.5
    e = np.minimum(1, np.linspace(0, 6, n)) * np.minimum(1, np.linspace(6, 0, n))
    return (chirp + swell) * e


def sfx_logo():
    d = 2.6
    n = int(d * SR)
    boom = sine(np.geomspace(90, 38, n), d) * env(n, 0.003, 0.6) * 0.9
    chime = (bell(659.25, d, 1.4) * 0.35 + bell(987.77, d, 1.2) * 0.25 + bell(1318.5, d, 1.0) * 0.15)
    return boom + chime


def sfx_whoosh():
    d = 0.6
    n = int(d * SR)
    shape = np.sin(np.linspace(0, np.pi, n)) ** 2
    return lowpass(highpass(noise(d), 300), np.interp(np.linspace(0, 1, n), [0, 0.6, 1], [800, 5000, 1500])) * shape * 0.55


def sfx_pop():
    d = 0.12
    n = int(d * SR)
    return sine(np.geomspace(900, 380, n), d) * env(n, 0.001, 0.03) * 0.5


def sfx_click():
    d = 0.06
    n = int(d * SR)
    return (highpass(noise(d), 2000) * env(n, 0.0005, 0.006) * 0.6 + sine(2400, d) * env(n, 0.0005, 0.01) * 0.25)


def sfx_shutter():
    out = np.zeros(int(0.16 * SR), dtype=np.float32)
    for off in (0.0, 0.07):
        c = highpass(noise(0.05), 1200) * env(int(0.05 * SR), 0.0005, 0.008) * 0.55
        i = int(off * SR)
        out[i:i + len(c)] += c
    return out


def sfx_type(dur=0.5, n=10):
    out = np.zeros(int((dur + 0.08) * SR), dtype=np.float32)
    for k in range(n):
        c = highpass(noise(0.03), 2500) * env(int(0.03 * SR), 0.0003, 0.005) * rng.uniform(0.25, 0.4)
        i = int((k / max(n - 1, 1)) * dur * SR + rng.uniform(-0.008, 0.008) * SR)
        i = max(0, i)
        out[i:i + len(c)] += c[: len(out) - i]
    return out


def sfx_ding():
    return bell(1174.66, 1.2, 0.6) * 0.3 + bell(1567.98, 1.2, 0.5) * 0.18


def sfx_lock():
    a = bell(880, 1.4, 0.7) * 0.3
    b = bell(1318.5, 1.4, 0.7) * 0.3
    out = np.zeros(int(1.6 * SR), dtype=np.float32)
    out[: len(a)] += a
    out[int(0.12 * SR): int(0.12 * SR) + len(b)] += b
    return out


def sfx_impact():
    d = 3.5
    n = int(d * SR)
    boom = sine(np.geomspace(70, 32, n), d) * env(n, 0.004, 0.9) * 1.0
    shimmer = sum(bell(f, d, 2.2) for f in (523.25, 783.99, 1046.5, 1567.98)) * 0.12
    air = lowpass(noise(d), 2500) * env(n, 0.002, 0.25) * 0.25
    return boom + shimmer + air


SFX = {
    'hit': sfx_hit, 'glitch': sfx_glitch, 'dissolve': sfx_dissolve, 'rewind': sfx_rewind, 'logo': sfx_logo,
    'whoosh': sfx_whoosh, 'pop': sfx_pop, 'click': sfx_click, 'shutter': sfx_shutter, 'type': sfx_type,
    'ding': sfx_ding, 'lock': sfx_lock, 'impact': sfx_impact,
}
SFX_GAIN = {'logo': 0.8, 'impact': 0.8, 'hit': 0.7, 'whoosh': 0.5, 'type': 0.6, 'rewind': 0.6, 'glitch': 0.45}


# ---------- music ----------

def note(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def pad_chord(midis, d):
    n = int(d * SR)
    x = np.zeros(n, dtype=np.float32)
    for m in midis:
        for det in (-0.08, 0.0, 0.08):
            x += saw(note(m + det), d) * 0.12
    x = lowpass(lowpass(x, 1400), 1400)
    a = min(0.6, d / 3)
    e = np.minimum(1, np.minimum(np.linspace(0, d / a, n), np.linspace(d / a, 0, n)))
    return x * e


def music(duration, reveal, showcase, outro):
    total = int(duration * SR)
    out = np.zeros(total, dtype=np.float32)

    def put(sig, at, gain=1.0):
        i = int(at * SR)
        if i >= total:
            return
        seg = sig[: total - i]
        out[i:i + len(seg)] += seg * gain

    # Hook: low drone with a slow swell into the reveal.
    d = reveal + 0.5
    drone = (sine(note(33), d) * 0.5 + sine(note(45), d) * 0.2)
    put(drone * np.linspace(0.2, 1, len(drone)) * np.minimum(1, np.linspace(10, 0, len(drone))), 0, 0.5)

    bpm = 100.0
    beat = 60 / bpm
    bar = beat * 4
    chords = [[57, 60, 64], [53, 57, 60], [48, 52, 55], [55, 59, 62]]  # Am F C G
    bass = [45, 41, 36, 43]
    t0 = reveal
    i = 0
    while t0 < outro:
        put(pad_chord(chords[i % 4], bar * 2 + 0.6), t0, 0.55)
        b = sine(note(bass[i % 4] - 12), bar * 2) * env(int(bar * 2 * SR), 0.02, None, 0.8)
        put(b, t0, 0.35)
        t0 += bar * 2
        i += 1

    # Rhythm from the showcase to the outro: soft kick on the beat, hats on the offbeat, an arpeggio.
    kick = sine(np.geomspace(120, 42, int(0.3 * SR)), 0.3) * env(int(0.3 * SR), 0.002, 0.07)
    hat = highpass(noise(0.05), 7000) * env(int(0.05 * SR), 0.0005, 0.012)
    arp_notes = [69, 72, 76, 72, 65, 69, 72, 69, 60, 64, 67, 64, 67, 71, 74, 71]
    k = 0
    tb = showcase
    while tb < outro - 0.05:
        put(kick, tb, 0.55)
        put(hat, tb + beat / 2, 0.12)
        for half in (0, 1):
            m = arp_notes[(k * 2 + half) % len(arp_notes)]
            ta = tb + half * beat / 2
            pl = lowpass(saw(note(m + 12), 0.25), 2600) * env(int(0.25 * SR), 0.002, 0.08)
            put(pl, ta, 0.09)
        tb += beat
        k += 1

    # Outro: bright C major lift that rings out to the end.
    d = duration - outro + 0.2
    lift = pad_chord([48, 55, 60, 64, 67, 72], d)
    put(lift * np.minimum(1, np.linspace(d / 1.5, 0, len(lift))), outro, 0.6)
    put(sine(note(36), d) * np.linspace(1, 0, int(d * SR)) ** 1.5, outro, 0.3)
    return out


# ---------- mix ----------

def main():
    cues_path = os.path.join(HERE, 'cues.json')
    data = json.load(open(cues_path))
    duration, cues, labels = data['duration'], data['cues'], data['labels']
    total = int((duration + 0.2) * SR)

    voice = np.zeros(total, dtype=np.float32)
    fx = np.zeros(total, dtype=np.float32)
    for c in cues:
        kind, name = c['name'].split(':')
        i = int(c['t'] * SR)
        if kind == 'vo':
            sig = load_wav(os.path.join(HERE, 'vo', f'{name}.wav'))
            sig = sig / (np.abs(sig).max() + 1e-9) * 0.9
            dest, gain = voice, 1.0
        else:
            fn = SFX[name]
            kw = {k: c[k] for k in ('dur', 'n') if k in c}
            sig = fn(**kw)
            dest, gain = fx, SFX_GAIN.get(name, 0.5)
        seg = sig[: total - i]
        dest[i:i + len(seg)] += seg * gain

    mus = music(duration + 0.2, labels['reveal'], labels['showcase'], labels['outro'])[:total]

    # Duck the music under the voice (smoothed envelope of the voice).
    vo_env = lowpass(np.abs(voice), 6.0)
    vo_env = vo_env / (vo_env.max() + 1e-9)
    duck = 1 - 0.55 * np.clip(vo_env * 4, 0, 1)
    mix_mono_music = mus * duck * 0.55

    # Stereo: music slightly wide (Haas on the pad), voice and effects centered.
    delay = int(0.012 * SR)
    left = mix_mono_music + fx * 0.8 + voice
    right = np.concatenate([np.zeros(delay, np.float32), mix_mono_music[:-delay]]) + fx * 0.8 + voice
    st = np.stack([left, right], axis=1)
    fade = int(1.2 * SR)
    st[-fade:] *= np.linspace(1, 0, fade)[:, None]
    st = np.tanh(st * 1.1) / np.tanh(1.1)  # gentle limiter
    st = st / (np.abs(st).max() + 1e-9) * 0.89  # about -1 dBFS

    wav_path = os.path.join(HERE, 'soundtrack.wav')
    with wave.open(wav_path, 'wb') as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((st * 32767).astype(np.int16).tobytes())
    mp3 = os.path.join(HERE, 'soundtrack.mp3')
    subprocess.run(['ffmpeg', '-loglevel', 'error', '-y', '-i', wav_path, '-c:a', 'libmp3lame', '-b:a', '192k', mp3], check=True)
    os.remove(wav_path)
    print(f'Wrote {mp3} ({duration:.2f} s, {len(cues)} cues)')


if __name__ == '__main__':
    sys.exit(main())
