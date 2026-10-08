"""Stable Audio 3 sound-effect worker.

Upstream: https://github.com/Stability-AI/stable-audio-3 (commit 3a82c80), models
stabilityai/stable-audio-3-small-sfx, -small-music and -medium. A latent diffusion model with a
T5Gemma text encoder and the SAME autoencoder; stereo 44.1 kHz, up to 120 s (Small) or 380 s (Medium).

The models are gated on Hugging Face ("auto" approval): the user accepts the terms on each model page
once, and the app's downloads need an access token (Settings → Hugging Face token).

The segment's text (``req.text``) is the prompt, ``req.duration_sec`` the length.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import Cancelled, ensure_model, log, run_worker, torch_device, write_wav  # noqa: E402

DEFAULT_MODEL = "small-sfx"
REPOS = {
    "small-sfx": "stabilityai/stable-audio-3-small-sfx",
    "small-music": "stabilityai/stable-audio-3-small-music",
    "medium": "stabilityai/stable-audio-3-medium",
}
MODELS = {name: "stable-audio-3-" + name for name in REPOS}  # ids in workers/models.json
MAX_SECONDS = {"small-sfx": 120.0, "small-music": 120.0, "medium": 380.0}


class LocalModelConfig:
    """Stands in for a stable_audio_3 ModelConfig: returns files from the pinned download instead of
    calling hf_hub_download (which fetches the repo's latest commit)."""

    def __init__(self, config_path, ckpt_path):
        self.config_path, self.ckpt_path = config_path, ckpt_path

    def resolve(self):
        return self.config_path, self.ckpt_path


def use_local_files(name, path):
    """Makes StableAudioModel.from_pretrained(name) load from the pinned snapshot at ``path``. The
    T5Gemma text encoder is a subfolder of the same repo, named by repo id in model_config.json; a
    copy of the config with ``model_path`` set (the conditioner prefers it) loads it locally too."""
    import json

    from stable_audio_3 import model_configs

    with open(os.path.join(path, "model_config.json"), encoding="utf-8") as f:
        config = json.load(f)

    def point_to_local(node):
        if isinstance(node, dict):
            if node.get("repo_id") == REPOS[name]:
                node["model_path"] = path
            for v in node.values():
                point_to_local(v)
        elif isinstance(node, list):
            for v in node:
                point_to_local(v)

    point_to_local(config)
    import tempfile

    local_config = os.path.join(tempfile.gettempdir(), f"las-stable-audio-3-{name}-model_config.json")
    with open(local_config, "w", encoding="utf-8") as f:
        json.dump(config, f)
    # model.py imported this same dict, so the entry it looks up is replaced.
    model_configs.all_models[name] = LocalModelConfig(local_config, os.path.join(path, "model.safetensors"))


class StableAudio3Worker:
    def __init__(self):
        self.models = {}  # model name -> StableAudioModel
        self.device = "cpu"

    def selftest(self):
        from stable_audio_3 import StableAudioModel  # noqa: F401
        import transformers

        log(f"stable_audio_3 imported, transformers {transformers.__version__}")

    def prepare(self, report):
        self._download(DEFAULT_MODEL)

    def _download(self, name):
        return ensure_model(MODELS[name])

    def load(self, report):
        self._model(DEFAULT_MODEL, report)
        report(1.0, "Ready")

    def _model(self, name, report):
        model = self.models.get(name)
        if model is not None:
            return model
        import torch
        from stable_audio_3 import StableAudioModel

        self.device = torch_device()
        if name == "medium":
            try:
                import flash_attn  # noqa: F401
            except ImportError:
                raise RuntimeError("Stable Audio 3 Medium needs flash-attention (Linux with a CUDA GPU). "
                                   "Use Small SFX or Small Music.") from None
        use_local_files(name, self._download(name))
        report(0.1, f"Loading Stable Audio 3 {name}")
        # Half precision only helps on the GPU; upstream also turns it off without CUDA.
        model = StableAudioModel.from_pretrained(name, device=self.device, model_half=self.device == "cuda")
        self.models = {name: model}  # one model at a time keeps memory predictable
        if self.device == "cuda":
            torch.cuda.empty_cache()
        log(f"Stable Audio 3 {name} loaded on {self.device}")
        return model

    def generate(self, req, report):
        prompt = " ".join((req.text or "").split())
        if not prompt:
            raise ValueError("empty prompt")
        name = str(req.settings.get("model") or DEFAULT_MODEL)
        if name not in REPOS:
            raise ValueError(f"unknown model {name}")
        seconds = float(req.duration_sec or 5.0)
        if seconds > MAX_SECONDS[name]:
            raise ValueError(f"Stable Audio 3 {name} generates at most {MAX_SECONDS[name]:g} s; "
                             f"this segment asks for {seconds:g} s.")
        steps = int(req.settings.get("steps", 8))

        model = self._model(name, lambda f, m=None: report(0.0, m))

        def on_step(info):
            # Called by the sampler after every step; report() raises Cancelled when the app cancels.
            report(0.05 + 0.85 * (info.get("i", 0) + 1) / max(1, steps), "Generating")

        report(0.05, "Generating")
        try:
            audio = model.generate(prompt=prompt, duration=max(0.5, seconds), steps=steps,
                                   seed=int(req.seed) % (2**31), callback=on_step)
        except Cancelled:
            raise
        wav = audio[0].float().cpu().numpy()  # (batch, channels, samples) -> (channels, samples): stereo
        sample_rate = int(model.model.sample_rate)
        duration_ms = write_wav(req.out_path, wav, sample_rate)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sample_rate}


if __name__ == "__main__":
    run_worker(StableAudio3Worker(), name="stable_audio_3", model_version="3.0")
