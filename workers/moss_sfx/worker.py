"""MOSS-SoundEffect v2.0 sound-effect worker.

Upstream: https://github.com/OpenMOSS/MOSS-TTS/tree/main/moss_soundeffect_v2 (commit 4cf2dab),
weights OpenMOSS-Team/MOSS-SoundEffect-v2.0. A DiT + flow-matching text-to-audio model with a
Qwen3 text encoder and a DAC VAE; it denoises a fixed 30 s latent and crops to the requested length.

The segment's text (``req.text``) is the prompt, ``req.duration_sec`` the length.
"""

import os
import sys

# The DiT step is wrapped in torch.compile with Triton CUDA graphs, and Triton isn't available on
# Windows (nor useful on a CPU slot). Upstream's own scripts disable TorchDynamo the same way.
# Must be set before torch loads.
if os.name == "nt" or os.environ.get("LAS_DEVICE") == "cpu" or os.environ.get("LAS_MOSS_NO_COMPILE") == "1":
    os.environ.setdefault("TORCHDYNAMO_DISABLE", "1")

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, run_worker, torch_device, write_wav  # noqa: E402

MODEL = "moss-soundeffect-2.0"  # OpenMOSS-Team/MOSS-SoundEffect-v2.0 (workers/models.json)
DEFAULT_SECONDS = 5.0


class MossSfxWorker:
    def __init__(self):
        self.pipe = None
        self.model_dir = None

    def selftest(self):
        from moss_soundeffect_v2 import MossSoundEffectPipeline  # noqa: F401
        import diffusers

        log(f"moss_soundeffect_v2 imported, diffusers {diffusers.__version__}")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import torch
        from moss_soundeffect_v2 import MossSoundEffectPipeline

        device = torch_device()
        # bfloat16 weights are fast only on the GPU; the pipeline still autocasts to bfloat16 on the CPU.
        dtype = torch.bfloat16 if device == "cuda" else torch.float32
        report(0.05, "Loading MOSS-SoundEffect v2.0")
        self.pipe = MossSoundEffectPipeline.from_pretrained(self.model_dir or ensure_model(MODEL), torch_dtype=dtype, device=device)
        report(1.0, "Ready")
        log(f"MOSS-SoundEffect v2.0 loaded on {device}, {self.pipe.sample_rate} Hz, "
            f"max {self.pipe.max_inference_seconds} s, TorchDynamo "
            f"{'off' if os.environ.get('TORCHDYNAMO_DISABLE') == '1' else 'on'}")

    def generate(self, req, report):
        prompt = " ".join((req.text or "").split())
        if not prompt:
            raise ValueError("empty prompt")
        seconds = float(req.duration_sec or DEFAULT_SECONDS)
        limit = float(self.pipe.max_inference_seconds)
        if seconds > limit:
            raise ValueError(f"MOSS-SoundEffect generates at most {limit:g} s; this segment asks for {seconds:g} s. "
                             "Shorten it, or loop the sound as a background.")
        seconds = max(0.1, seconds)

        s = req.settings
        steps = int(s.get("steps", 100))

        def progress(iterable, *args, **kwargs):
            """Replaces the pipeline's tqdm: reports each diffusion step and lets a cancel stop it."""
            for i, x in enumerate(iterable):
                report(0.02 + 0.93 * i / max(1, steps), "Generating")
                yield x

        report(0.02, "Generating")
        audio = self.pipe(
            prompt=prompt,
            seconds=seconds,
            num_inference_steps=steps,
            cfg_scale=float(s.get("cfgScale", 4.0)),
            negative_prompt=str(s.get("negativePrompt") or ""),
            seed=int(req.seed) % (2**31),
            progress_bar_cmd=progress,
        )
        wav = audio[0].float().cpu().numpy()  # (B, C, T) -> (C, T)
        duration_ms = write_wav(req.out_path, wav, self.pipe.sample_rate)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": self.pipe.sample_rate}


if __name__ == "__main__":
    run_worker(MossSfxWorker(), name="moss_sfx", model_version="v2.0")
