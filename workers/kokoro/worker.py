"""Kokoro-82M text-to-speech worker (https://github.com/hexgrad/kokoro).

Kokoro is small (82M parameters), fast and deterministic. It uses built-in voices (no cloning);
the voice id's first letter selects the language pipeline ('a' = American English,
'b' = British English).
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, fix_espeak_data_path, log, model_file, run_worker, torch_device, write_wav  # noqa: E402

REPO_ID = "hexgrad/Kokoro-82M"
MODEL = "kokoro-82m"  # workers/models.json; voices are extra files of it, fetched when first used
SAMPLE_RATE = 24000
DEFAULT_VOICE = "af_heart"


class KokoroWorker:
    def __init__(self):
        self.model = None
        self.model_dir = None
        self.voice_packs = {}
        self.pipelines = {}
        self.device = "cpu"

    def selftest(self):
        import kokoro  # noqa: F401
        import misaki  # noqa: F401

        log(f"kokoro {getattr(kokoro, '__version__', '?')} imported")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        from kokoro import KModel

        if self.model_dir is None:
            self.prepare(report)
        self.device = torch_device()
        report(0.1, "Loading Kokoro-82M")
        # Local files, not the repo id: kokoro would fetch the repo's latest commit itself.
        self.model = KModel(repo_id=REPO_ID, config=os.path.join(self.model_dir, "config.json"),
                            model=os.path.join(self.model_dir, "kokoro-v1_0.pth")).to(self.device).eval()
        report(0.8, "Preparing English pipeline")
        self._pipeline("a")
        report(1.0, "Ready")
        log(f"Kokoro loaded on {self.device}")

    def _pipeline(self, lang_code):
        from kokoro import KPipeline

        p = self.pipelines.get(lang_code)
        if p is None:
            fix_espeak_data_path()
            p = KPipeline(lang_code=lang_code, repo_id=REPO_ID, model=self.model)
            self.pipelines[lang_code] = p
        return p

    def _voice(self, name):
        """The voice tensor, from the app (downloaded into the model folder the first time). Given a voice
        name, kokoro would fetch it from the repo's latest commit itself."""
        import torch

        pack = self.voice_packs.get(name)
        if pack is None:
            if not name.replace("_", "").isalnum():
                raise ValueError(f"unknown Kokoro voice: {name}")
            try:
                path = model_file(MODEL, f"voices/{name}.pt")
            except RuntimeError as e:
                raise ValueError(f"unknown Kokoro voice: {name} ({e})") from e
            pack = torch.load(path, weights_only=True)
            self.voice_packs[name] = pack
        return pack

    def list_voices(self):
        import json

        with open(os.path.join(os.path.dirname(__file__), "manifest.json"), encoding="utf-8") as f:
            return json.load(f).get("voices", [])

    def generate(self, req, report):
        import numpy as np
        import torch

        text = (req.text or "").strip()
        if not text:
            raise ValueError("empty text")
        voice = (req.voice or req.settings.get("voice") or DEFAULT_VOICE).strip()
        lang = voice[0] if voice and voice[0] in "ab" else "a"
        speed = float(req.settings.get("speed", 1.0))
        pack = self._voice(voice)

        pipeline = self._pipeline(lang)
        chunks = []
        report(0.05, "Synthesizing")
        # split_pattern: split long text at newlines only; the app already keeps segments short.
        with torch.inference_mode():
            for i, result in enumerate(pipeline(text, voice=pack, speed=speed, split_pattern=r"\n+")):
                audio = result.audio if hasattr(result, "audio") else result[2]
                if audio is None:
                    continue
                if hasattr(audio, "detach"):
                    audio = audio.detach().float().cpu().numpy()
                chunks.append(np.asarray(audio, dtype=np.float32).reshape(-1))
                report(min(0.95, 0.1 + 0.2 * (i + 1)))

        if not chunks:
            raise RuntimeError("Kokoro produced no audio (text may contain no pronounceable words)")
        audio = np.concatenate(chunks)
        duration_ms = write_wav(req.out_path, audio, SAMPLE_RATE)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": SAMPLE_RATE}


if __name__ == "__main__":
    run_worker(KokoroWorker(), name="kokoro", model_version="v1.0")
