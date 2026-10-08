"""ACE-Step 1.5 music worker.

Runs ACE-Step 1.5 XL (https://github.com/ace-step/ACE-Step-1.5) through diffusers' ``AceStepPipeline``
(diffusers 0.40) with the official diffusers-format weights: ACE-Step/acestep-v15-xl-turbo-diffusers
(default, 8 steps, guidance distilled in) or ACE-Step/acestep-v15-xl-sft-diffusers (more steps, CFG).
The upstream ``ace-step`` package isn't used: it needs a bundled nano-vllm that only installs from a
source checkout. The diffusers pipeline is the DiT alone, without upstream's optional 5 Hz "planner" LM.

The segment's text (``req.text``) is the style prompt and ``req.duration_sec`` the
length (10 s to 10 min). Without lyrics the piece is instrumental (upstream's "[Instrumental]").
Output: stereo, 48 kHz.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, run_worker, torch_device, write_wav  # noqa: E402

DEFAULT_MODEL = "turbo"
# ACE-Step/acestep-v15-xl-{turbo,sft}-diffusers, by their ids in workers/models.json.
MODELS = {"turbo": "ace-step-1.5-xl-turbo", "sft": "ace-step-1.5-xl-sft"}
DEFAULT_STEPS = {"turbo": 8, "sft": 50}
MIN_SECONDS, MAX_SECONDS = 10.0, 600.0
INSTRUMENTAL = "[Instrumental]"


class AceStepWorker:
    def __init__(self):
        self.pipe = None
        self.model = None
        self.device = "cpu"

    def selftest(self):
        from diffusers import AceStepPipeline  # noqa: F401
        import diffusers

        log(f"diffusers {diffusers.__version__} with AceStepPipeline")

    def prepare(self, report):
        self._download(DEFAULT_MODEL, report)

    def _download(self, name, report=None):
        return ensure_model(MODELS[name])

    def load(self, report):
        self._load(DEFAULT_MODEL, report)
        report(1.0, "Ready")

    def _load(self, name, report):
        if self.model == name and self.pipe is not None:
            return self.pipe
        import torch
        from diffusers import AceStepPipeline

        if self.pipe is not None:
            log(f"Unloading ACE-Step {self.model}")
            self.pipe = None
            self.model = None
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        self.device = torch_device()
        path = self._download(name)
        report(0.1, f"Loading ACE-Step 1.5 XL {name}")
        dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        pipe = AceStepPipeline.from_pretrained(path, torch_dtype=dtype).to(self.device)
        # Long pieces: decode the latents in tiles so the VAE's memory stays bounded.
        pipe.vae.enable_tiling()
        pipe.set_progress_bar_config(disable=True)
        self.pipe, self.model = pipe, name
        log(f"ACE-Step 1.5 XL {name} loaded on {self.device}")
        return pipe

    def generate(self, req, report):
        import torch

        prompt = " ".join((req.text or "").split())
        if not prompt:
            raise ValueError("empty prompt")
        s = req.settings
        name = str(s.get("model") or DEFAULT_MODEL)
        if name not in MODELS:
            raise ValueError(f"unknown model {name}")
        seconds = float(req.duration_sec or 30.0)
        if not MIN_SECONDS <= seconds <= MAX_SECONDS:
            raise ValueError(f"ACE-Step generates {MIN_SECONDS:g} s to {MAX_SECONDS:g} s; this segment asks for {seconds:g} s.")

        lyrics = str(s.get("lyrics") or "").strip() or INSTRUMENTAL
        steps = int(s.get("steps") or 0) or DEFAULT_STEPS[name]
        bpm = int(s.get("bpm") or 0) or None
        key = str(s.get("key") or "").strip() or None

        pipe = self._load(name, lambda f, m=None: report(0.0, m))

        def on_step(_pipe, step, _timestep, callback_kwargs):
            # report() raises Cancelled when the app cancels; that ends the denoising loop.
            report(0.05 + 0.85 * (step + 1) / max(1, steps), "Generating")
            return callback_kwargs

        report(0.05, "Generating")
        generator = torch.Generator(device=self.device).manual_seed(int(req.seed) % (2**63))
        output = pipe(
            prompt=prompt,
            lyrics=lyrics,
            audio_duration=seconds,
            vocal_language=str(s.get("language") or "en"),
            num_inference_steps=steps,
            guidance_scale=float(s.get("guidance", 7.0)),  # ignored by turbo (guidance distilled)
            bpm=bpm,
            keyscale=key,
            generator=generator,
            callback_on_step_end=on_step,
        )
        report(0.95, "Decoding")
        audio = output.audios[0]  # (channels, samples), stereo
        if hasattr(audio, "detach"):
            audio = audio.detach().float().cpu().numpy()
        audio = audio[:, : int(round(seconds * pipe.sample_rate))]
        duration_ms = write_wav(req.out_path, audio, pipe.sample_rate)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": int(pipe.sample_rate)}


if __name__ == "__main__":
    run_worker(AceStepWorker(), name="ace_step", model_version="1.5-xl")
