"""Debug worker: renders a short tone sequence instead of speech.

Needs only NumPy and no GPU, so it is handy for checking the app's pipeline (install, protocol,
scheduling, takes, playback) on any machine. Each character becomes a ~60 ms tone, or, when the
segment asks for a length, the tones are stretched to fill exactly that length.
"""

import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import run_worker, write_wav  # noqa: E402

SAMPLE_RATE = 24000


class ToneWorker:
    requires_cuda = False

    def selftest(self):
        import numpy  # noqa: F401

    def load(self, report):
        report(1.0, "Ready")

    def generate(self, req, report):
        import numpy as np

        rng = np.random.default_rng(req.seed)
        text = (req.text or "").strip() or "tone"
        per_char = float(req.settings.get("msPerChar", 60)) / 1000.0
        if req.duration_sec:  # requested length: 0.1 s of silence at each end, tones in between
            per_char = max(0.001, (float(req.duration_sec) - 0.2) / len(text))
        delay = float(req.settings.get("delaySec", 0.2))
        base = 110.0 * (2 ** (rng.integers(0, 12) / 12.0))
        t_char = np.arange(int(per_char * SAMPLE_RATE)) / SAMPLE_RATE
        parts = [np.zeros(int(0.1 * SAMPLE_RATE), dtype=np.float32)]
        for i, ch in enumerate(text):
            if ch.isspace():
                parts.append(np.zeros_like(t_char, dtype=np.float32))
                continue
            f = base * (2 ** ((ord(ch) % 24) / 12.0))
            env = np.minimum(1.0, np.minimum(t_char, t_char[-1] - t_char) / 0.005)
            parts.append((0.3 * np.sin(2 * np.pi * f * t_char) * env).astype(np.float32))
            if i % 20 == 0:
                report(i / max(1, len(text)))
        parts.append(np.zeros(int(0.1 * SAMPLE_RATE), dtype=np.float32))
        time.sleep(delay)
        audio = np.concatenate(parts)
        ms = write_wav(req.out_path, audio, SAMPLE_RATE)
        return {"durationMs": ms, "seed": req.seed, "sampleRate": SAMPLE_RATE}


if __name__ == "__main__":
    run_worker(ToneWorker(), name="test_tone", model_version="1")
