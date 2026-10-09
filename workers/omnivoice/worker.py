"""OmniVoice worker (https://github.com/k2-fsa/OmniVoice, package ``omnivoice`` 0.2.1).

One model does all three modes, picked per request:

- reference clip set -> voice cloning (a cached ``VoiceClonePrompt`` per clip and transcript; without a
                        transcript the clip is transcribed with Whisper first)
- voice attributes   -> voice design (``instruct``, e.g. "female, elderly, low pitch")
- neither            -> the model picks a voice itself

The model is a diffusion LM decoded in ``num_step`` passes (more for long text, which it splits into ~15 s
chunks itself). A forward pre-hook counts the passes for progress and lets a cancel stop it mid-segment.
"""

import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from common.protocol import ensure_model, log, run_worker, seed_everything, torch_device, write_wav  # noqa: E402
from options import read_options  # noqa: E402

MODEL_ID = "omnivoice"
ASR_MODEL_ID = "whisper-large-v3-turbo"
# The package's defaults for splitting long text (OmniVoiceGenerationConfig): above 30 s of estimated
# audio it generates ~15 s chunks one after the other. Only used to estimate the number of passes.
CHUNK_THRESHOLD_SEC = 30.0
CHUNK_SEC = 15.0


class OmniVoiceWorker:
    def __init__(self):
        self.model = None
        self.asr_loaded = False
        self.prompts = {}   # voice clone prompts, keyed by (clip, mtime, transcript)
        self.progress = None  # set while generating: called by the forward hook once per decoding pass

    def selftest(self):
        import omnivoice
        import transformers

        log(f"omnivoice {omnivoice.__version__} imported, transformers {transformers.__version__}")

    def prepare(self, report):
        # Whisper is only needed for clips without a transcript, so it downloads when first used.
        ensure_model(MODEL_ID)

    def load(self, report):
        import torch
        from omnivoice import OmniVoice

        device = "cuda:0" if torch_device() == "cuda" else "cpu"
        dtype = torch.float16 if device != "cpu" else torch.float32
        path = ensure_model(MODEL_ID)
        report(0.1, "Loading OmniVoice")
        self.model = OmniVoice.from_pretrained(path, device_map=device, dtype=dtype)
        self.model.register_forward_pre_hook(self._on_pass)
        log(f"OmniVoice loaded on {device} ({dtype}), {len(self.model.supported_language_ids())} languages")
        report(1.0, "Ready")

    def _on_pass(self, module, args):
        if self.progress is not None:
            self.progress()

    def _prompt(self, clip, ref_text, report):
        key = (clip, os.path.getmtime(clip), ref_text)
        prompt = self.prompts.get(key)
        if prompt is not None:
            return prompt
        if not ref_text and not self.asr_loaded:
            # The package would fetch Whisper by repo id, which fails offline; give it the store's folder.
            path = ensure_model(ASR_MODEL_ID)
            report(0.05, "Loading Whisper to transcribe the reference clip")
            self.model.load_asr_model(path)
            self.asr_loaded = True
        report(0.05, "Reading the reference clip")
        prompt = self.model.create_voice_clone_prompt(ref_audio=clip, ref_text=ref_text or None)
        if not ref_text:
            log(f"Reference clip transcribed as: {prompt.ref_text}")
        self.prompts[key] = prompt
        return prompt

    def _expected_passes(self, text, prompt, speed, steps):
        """Decoding passes for this text: ``steps`` per chunk, estimated the way the package splits."""
        try:
            ref_tokens = prompt.ref_audio_tokens.size(-1) if prompt is not None else None
            tokens = self.model._estimate_target_tokens(
                text, prompt.ref_text if prompt is not None else None, ref_tokens, speed=speed)
            seconds = tokens / self.model.audio_tokenizer.config.frame_rate
        except Exception:  # a private method; progress is only an estimate
            return steps
        chunks = math.ceil(seconds / CHUNK_SEC) if seconds > CHUNK_THRESHOLD_SEC else 1
        return steps * max(1, chunks)

    def generate(self, req, report):
        import numpy as np

        o = read_options(req)
        clip = (req.voice or "").strip()

        prompt = None
        if clip:
            if not os.path.isfile(clip):
                raise FileNotFoundError(f"voice reference clip not found: {clip}")
            prompt = self._prompt(clip, o.ref_text, report)

        expected = self._expected_passes(o.text, prompt, o.speed, o.steps)
        done = [0]

        def on_pass():
            done[0] += 1
            report(0.1 + 0.85 * min(1.0, done[0] / expected), "Synthesizing")  # raises when cancelled

        if req.seed is not None:
            seed_everything(int(req.seed))
        report(0.1, "Synthesizing")
        self.progress = on_pass
        try:
            audios = self.model.generate(
                text=o.text,
                language=o.language,
                voice_clone_prompt=prompt,
                instruct=o.instruct,
                speed=o.speed,
                num_step=o.steps,
            )
        finally:
            self.progress = None

        audio = np.asarray(audios[0], dtype=np.float32).reshape(-1) if audios else None
        if audio is None or audio.size == 0:
            raise RuntimeError("OmniVoice produced no audio")
        sample_rate = int(self.model.sampling_rate)
        duration_ms = write_wav(req.out_path, audio, sample_rate)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sample_rate}


if __name__ == "__main__":
    run_worker(OmniVoiceWorker(), name="omnivoice", model_version="c5fdb5cc")
