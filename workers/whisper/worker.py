"""Whisper (OpenAI) through transformers: speech recognition with word timings in about 99 languages.

The transformers pipeline runs the model and finds each word's time from the decoder's cross-attention (dynamic time
warping over the alignment heads in the model's generation_config).

Long takes are cut at their quietest points into pieces of at most 20 s, recognised one by one. With word timings,
Whisper drops the last words of a window packed with speech (26 s of Kokoro lost its final word), and the pipeline's
own overlapping 30 s windows sometimes repeat words where they are stitched together.

Whisper invents text over silence now and then: a piece isn't conditioned on the text before it, and words whose
audio is far quieter than the take's speech are dropped. The text the take should contain is never given to Whisper
as a prompt: it would steer it to hear the right words and hide the mistakes the check looks for.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, frame_levels, peak_level, quiet_pieces, read_wav  # noqa: E402
from common.protocol import ensure_model, log, run_worker, torch_device, worker_settings  # noqa: E402

MODELS = {  # the "model" worker setting -> workers/models.json
    "large-v3-turbo": "whisper-large-v3-turbo",
    "large-v3": "whisper-large-v3",
    "small": "whisper-small",
}
MAX_PIECE_SEC = 20.0
MIN_PIECE_SEC = 10.0
# A word whose loudest 20 ms is this far below the take's speech (about -26 dB) was heard in silence. Relative and
# by peak, since quiet word endings ("s", "f") are real speech.
SILENT_RATIO = 0.05


class WhisperWorker:
    def __init__(self):
        self.model_key = "large-v3-turbo"
        self.model_dir = None
        self.pipe = None

    def selftest(self):
        import transformers  # noqa: F401

    def prepare(self, report):
        self.model_key = str(worker_settings().get("model") or "large-v3-turbo")
        if self.model_key not in MODELS:
            raise ValueError(f"unknown Whisper model: {self.model_key}")
        self.model_dir = ensure_model(MODELS[self.model_key])

    def load(self, report):
        import torch
        from transformers import pipeline

        if self.model_dir is None:
            self.prepare(report)
        device = torch_device()
        report(0.3, f"Loading Whisper {self.model_key}")
        self.pipe = pipeline("automatic-speech-recognition", model=self.model_dir,
                             dtype=torch.float16 if device == "cuda" else torch.float32,
                             device="cuda:0" if device == "cuda" else "cpu")
        report(1.0, "Ready")
        log(f"Whisper {self.model_key} loaded on {device}")

    def transcribe(self, req, report):
        import numpy as np

        audio = read_wav(req.audio_path)
        if len(audio) < SAMPLE_RATE // 10:
            return {"text": "", "words": [], "language": req.language}
        levels = frame_levels(audio)
        speech = float(np.percentile(levels, 95))
        generate = {"task": "transcribe"}
        if req.language:
            generate["language"] = req.language
        pieces = quiet_pieces(audio, MIN_PIECE_SEC, MAX_PIECE_SEC)
        words, dropped, detected = [], [], None
        for i, (begin, end) in enumerate(pieces):
            if req.cancelled:
                return {}
            report(i / len(pieces), "Listening" if len(pieces) == 1 else f"Listening (part {i + 1} of {len(pieces)})")
            offset = begin / SAMPLE_RATE
            result = self.pipe({"raw": audio[begin:end], "sampling_rate": SAMPLE_RATE}, return_timestamps="word",
                               return_language=True, generate_kwargs=dict(generate))
            for chunk in result.get("chunks") or []:
                text = chunk["text"].strip()
                start_sec, end_sec = chunk["timestamp"]
                if not text or start_sec is None:
                    continue
                detected = detected or chunk.get("language")
                start_sec += offset
                end_sec = start_sec if end_sec is None else end_sec + offset
                if peak_level(levels, start_sec - 0.1, end_sec + 0.1) < SILENT_RATIO * speech:
                    dropped.append(text)
                    continue
                words.append({"text": text, "startMs": int(round(start_sec * 1000)), "endMs": int(round(end_sec * 1000))})
        if dropped:
            log(f"Dropped words heard in silence: {' '.join(dropped)}")
        report(1.0)
        return {"text": " ".join(w["text"] for w in words), "words": words, "language": req.language or _code(detected)}


def _code(language):
    """The pipeline reports "english"; the app uses codes ("en")."""
    if not language:
        return None
    from transformers.models.whisper.tokenization_whisper import LANGUAGES

    by_name = {name: code for code, name in LANGUAGES.items()}
    return by_name.get(language.lower(), language)


if __name__ == "__main__":
    run_worker(WhisperWorker(), name="whisper", model_version="v1")
