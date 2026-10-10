"""Audio helpers for transcription workers: the app sends them 16 kHz mono 16-bit WAV."""

from __future__ import annotations

import wave

SAMPLE_RATE = 16000
_HOP = SAMPLE_RATE // 50  # 20 ms frames


def read_wav(path: str):
    """The WAV the app sends a transcription worker, as float32 samples."""
    import numpy as np

    with wave.open(path, "rb") as w:
        if w.getsampwidth() != 2 or w.getnchannels() != 1:
            raise ValueError("expected 16-bit mono WAV")
        if w.getframerate() != SAMPLE_RATE:
            raise ValueError(f"expected {SAMPLE_RATE} Hz, got {w.getframerate()} Hz")
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2")
    return pcm.astype(np.float32) / 32768.0


def quiet_pieces(audio, min_sec: float, max_sec: float) -> list[tuple[int, int]]:
    """(start, end) sample ranges of at most max_sec, cut at the quietest 20 ms between min_sec and max_sec, so a
    model that does badly on long input gets pieces that end between words. No overlap: nothing to stitch."""
    import numpy as np

    total = len(audio)
    pieces, start = [], 0
    while total - start > max_sec * SAMPLE_RATE:
        lo = start + int(min_sec * SAMPLE_RATE)
        hi = start + int(max_sec * SAMPLE_RATE)
        frames = audio[lo:hi - (hi - lo) % _HOP].reshape(-1, _HOP)
        cut = lo + int(np.argmin((frames ** 2).mean(axis=1))) * _HOP + _HOP // 2
        pieces.append((start, cut))
        start = cut
    pieces.append((start, total))
    return pieces


def frame_levels(audio):
    """RMS of each 20 ms frame."""
    import numpy as np

    usable = len(audio) - len(audio) % _HOP
    if usable == 0:
        return np.zeros(1, dtype=np.float32)
    return np.sqrt((audio[:usable].reshape(-1, _HOP) ** 2).mean(axis=1))


def peak_level(levels, start_sec: float, end_sec: float) -> float:
    """The loudest 20 ms frame between two times (from frame_levels)."""
    first = max(0, int(start_sec * 50))
    last = min(len(levels), max(int(end_sec * 50) + 1, first + 1))
    return float(levels[first:last].max()) if last > first else 0.0
