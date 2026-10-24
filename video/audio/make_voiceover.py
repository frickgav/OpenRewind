"""Records the voice-over lines in audio/vo/ with Kokoro-82M, a local open TTS model (Apache-2.0).

Setup (once, in video/audio):
    python3 -m venv venv && ./venv/bin/pip install kokoro-onnx soundfile
    npm install --no-save kokoro-js kokoro-q8-shards     # model weights + voice styles from npm
    cat node_modules/kokoro-q8-shards/kokoro-q8.part*.bin > model_quantized.onnx
Then:
    ./venv/bin/python make_voiceover.py

Edit LINES to change the script. The cue names (vo:<key>) and their times live in main.js;
after changing a line, run `node render.mjs --cues audio/cues.json` and make_soundtrack.py.
"""
import glob
import json
import os

import numpy as np
import soundfile
from kokoro_onnx import Kokoro

HERE = os.path.dirname(os.path.abspath(__file__))
VOICE = 'af_heart'
SPEED = 0.95

LINES = {
    'saw': 'You saw it.',
    'read': 'You read it.',
    'forgot': 'You forgot it.',
    'meet': 'Meet OpenRewind.',
    'memory': 'It quietly keeps a private memory of your screen.',
    'snap': 'A snapshot every two seconds.',
    'scroll': 'Scroll back to any moment of your day.',
    'search': 'Search every word you saw, or heard.',
    'ask': 'Ask AI what you worked on, with your own key.',
    'local': 'And it all stays on your computer.',
    'cta': 'OpenRewind. Free, and open source.',
}


def main():
    voices = {}
    for f in glob.glob(os.path.join(HERE, 'node_modules/kokoro-js/voices/*.bin')):
        voices[os.path.basename(f)[:-4]] = np.fromfile(f, dtype=np.float32).reshape(-1, 1, 256)
    np.savez(os.path.join(HERE, 'voices.npz'), **voices)
    tts = Kokoro(os.path.join(HERE, 'model_quantized.onnx'), os.path.join(HERE, 'voices.npz'))
    os.makedirs(os.path.join(HERE, 'vo'), exist_ok=True)
    lengths = {}
    for key, text in LINES.items():
        samples, sr = tts.create(text, voice=VOICE, speed=SPEED, lang='en-us')
        soundfile.write(os.path.join(HERE, 'vo', f'{key}.wav'), samples, sr)
        lengths[key] = round(len(samples) / sr, 2)
    print(json.dumps(lengths, indent=1))


if __name__ == '__main__':
    main()
