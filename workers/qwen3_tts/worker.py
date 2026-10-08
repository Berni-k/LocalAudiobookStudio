"""Qwen3-TTS worker (https://github.com/QwenLM/Qwen3-TTS, package ``qwen-tts`` 0.1.1).

Qwen3-TTS ships three model types, and each request picks one:

- reference clip set   -> ``Base`` model, ``generate_voice_clone`` (ICL with the clip's transcript,
                          or x-vector only when there is no transcript)
- voice "design"       -> ``VoiceDesign`` model (1.7B only), ``generate_voice_design``
- otherwise            -> ``CustomVoice`` model, ``generate_custom_voice`` with a preset speaker

Loaded models are kept (up to MAX_MODELS) so speakers using different types don't reload each time.
"""

import os
import sys
from collections import OrderedDict

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, run_worker, torch_device, write_wav  # noqa: E402

SAMPLE_RATE = 24000
DEFAULT_VOICE = "Ryan"
DESIGN = "design"
MAX_MODELS = 3


def repo_id(kind, size):
    """The model id in workers/models.json ("qwen3-tts-1.7b-customvoice" for Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice)."""
    return f"qwen3-tts-{size.lower()}-{kind.lower()}"


class Qwen3TtsWorker:
    def __init__(self):
        self.models = OrderedDict()   # repo id -> Qwen3TTSModel, least recently used first
        self.prompts = {}             # voice clone prompts, keyed by (repo, clip, mtime, transcript)
        self.device = "cpu"

    def selftest(self):
        import qwen_tts  # noqa: F401
        import transformers

        log(f"qwen_tts imported, transformers {transformers.__version__}")

    def prepare(self, report):
        # The default model (preset voices, 1.7B). Clone, design and 0.6B models download when first used.
        ensure_model(repo_id("CustomVoice", "1.7B"))

    def load(self, report):
        self._model(repo_id("CustomVoice", "1.7B"), report)
        report(1.0, "Ready")

    def _model(self, repo, report):
        import torch
        from qwen_tts import Qwen3TTSModel

        model = self.models.get(repo)
        if model is not None:
            self.models.move_to_end(repo)
            return model

        while len(self.models) >= MAX_MODELS:
            old, _ = self.models.popitem(last=False)
            self.prompts = {k: v for k, v in self.prompts.items() if k[0] != old}
            log(f"Unloading {old}")
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        self.device = "cuda:0" if torch_device() == "cuda" else "cpu"
        dtype = torch.bfloat16 if self.device != "cpu" else torch.float32
        # flash-attention is optional upstream (less VRAM) and has no Windows wheels; SDPA otherwise.
        attn = "sdpa"
        if self.device != "cpu":
            try:
                import flash_attn  # noqa: F401

                attn = "flash_attention_2"
            except ImportError:
                pass

        path = ensure_model(repo)
        report(0.05, f"Loading {repo}")
        model = Qwen3TTSModel.from_pretrained(path, device_map=self.device, dtype=dtype, attn_implementation=attn)
        self.models[repo] = model
        log(f"{repo} loaded on {self.device} ({dtype}, {attn})")
        return model

    def generate(self, req, report):
        import numpy as np

        text = " ".join((req.text or "").split())
        if not text:
            raise ValueError("empty text")

        s = req.settings
        language = str(s.get("language") or "Auto")
        size = "0.6B" if str(s.get("size")) == "0.6B" else "1.7B"
        instruct = str(s.get("instruct") or "").strip()
        voice = str(s.get("voice") or DEFAULT_VOICE)
        gen = {"temperature": float(s.get("temperature", 0.9))}
        clip = (req.voice or "").strip()
        loading = lambda f, m=None: report(0.0, m)  # noqa: E731

        report(0.05, "Synthesizing")
        if clip:
            if not os.path.isfile(clip):
                raise FileNotFoundError(f"voice reference clip not found: {clip}")
            repo = repo_id("Base", size)
            model = self._model(repo, loading)
            ref_text = str(s.get("refText") or "").strip()
            key = (repo, clip, os.path.getmtime(clip), ref_text)
            prompt = self.prompts.get(key)
            if prompt is None:
                prompt = model.create_voice_clone_prompt(
                    ref_audio=clip, ref_text=ref_text or None, x_vector_only_mode=not ref_text)
                self.prompts[key] = prompt
            report(0.2, "Synthesizing")
            wavs, sr = model.generate_voice_clone(text=text, language=language, voice_clone_prompt=prompt, **gen)
        elif voice == DESIGN:
            model = self._model(repo_id("VoiceDesign", "1.7B"), loading)
            wavs, sr = model.generate_voice_design(text=text, language=language, instruct=instruct, **gen)
        else:
            model = self._model(repo_id("CustomVoice", size), loading)
            wavs, sr = model.generate_custom_voice(
                text=text, language=language, speaker=voice, instruct=instruct or None, **gen)

        if req.cancelled:
            report(1.0)  # raises Cancelled
        audio = np.asarray(wavs[0], dtype=np.float32).reshape(-1) if wavs else None
        if audio is None or audio.size == 0:
            raise RuntimeError("Qwen3-TTS produced no audio")
        duration_ms = write_wav(req.out_path, audio, sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": int(sr)}


if __name__ == "__main__":
    run_worker(Qwen3TtsWorker(), name="qwen3_tts", model_version="12Hz")
