"""MOSS-TTS v1.5 worker (https://github.com/OpenMOSS/MOSS-TTS).

Two checkpoints share one request format and are chosen by the ``model`` setting:
- ``delay``: MOSS-TTS-v1.5, 8B, one Qwen3 backbone with a delay pattern over 32 codebooks, 24 kHz mono.
- ``local``: MOSS-TTS-Local-Transformer-v1.5, 4B, a backbone plus a small local transformer over 12 codebooks,
  48 kHz stereo.

Both are Hugging Face remote-code models; the shared loading and sampling code is ``common/moss.py``. A segment's
text is the user message, the speaker's reference clip (``req.voice``) the voice to clone, ``req.duration_sec`` a
duration target (tokens at 12.5 per second). Only one checkpoint is held in memory at a time.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common import moss  # noqa: E402
from common.protocol import ensure_model, log, run_worker, seed_everything, torch_device, write_wav  # noqa: E402

MODELS = {
    "delay": ("moss-tts-1.5", "moss-audio-tokenizer"),
    "local": ("moss-tts-local-1.5", "moss-audio-tokenizer-v2"),
}


class MossTtsWorker:
    def __init__(self):
        self.device = "cpu"
        self.kind = None
        self.processor = None
        self.model = None
        self.refs = moss.ReferenceCache()

    def selftest(self):
        import torch
        import transformers

        log(f"torch {torch.__version__}, transformers {transformers.__version__}")

    def prepare(self, report):
        for model_id in MODELS["delay"]:
            ensure_model(model_id)

    def load(self, report):
        self.device = torch_device()
        report(1.0, "Ready")

    def _ensure(self, kind, report):
        """Loads the checkpoint ``kind`` (dropping the other one first)."""
        if self.kind == kind:
            return
        if self.model is not None:
            self.processor = self.model = None
            self.refs.clear()
            moss.unload()
        model_id, codec_id = MODELS[kind]
        report(0.01, "Fetching the model")
        model_dir = ensure_model(model_id)
        codec_dir = ensure_model(codec_id)
        report(0.02, "Loading MOSS-TTS")
        self.processor, self.model = moss.load(model_dir, codec_dir, self.device)
        self.kind = kind

    def generate(self, req, report):
        text = (req.text or "").strip()
        if not text:
            raise ValueError("empty text")
        s = req.settings
        kind = "local" if str(s.get("model")) == "local" else "delay"
        self._ensure(kind, report)

        reference = None
        clip = (req.voice or "").strip()
        if clip:
            report(0.03, "Encoding reference clip")
            reference = [self.refs.get(self.processor, self.device, clip)]

        language = str(s.get("language") or "Auto")
        tokens = None
        if req.duration_sec:
            tokens = max(1, int(round(float(req.duration_sec) * moss.FRAMES_PER_SECOND)))
        message = self.processor.build_user_message(
            text=text, reference=reference, tokens=tokens, language=None if language == "Auto" else language)

        sampling = dict(
            audio_temperature=float(s.get("temperature", 1.7)),
            audio_top_p=float(s.get("topP", 0.8)),
            audio_top_k=int(s.get("topK", 25)),
            audio_repetition_penalty=float(s.get("repetitionPenalty", 1.0)),
            text_temperature=float(s.get("textTemperature", 1.0)),
        )
        if kind == "local":
            sampling["do_sample"] = True

        max_new, expected = moss.frame_budget(text, req.duration_sec)
        seed_everything(int(req.seed))
        audio, sr = moss.generate(self.processor, self.model, self.device, [message], "generation",
                                  max_new, expected, report, **sampling)
        duration_ms = write_wav(req.out_path, audio.numpy(), sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sr}


if __name__ == "__main__":
    run_worker(MossTtsWorker(), name="moss_tts", model_version="v1.5")
