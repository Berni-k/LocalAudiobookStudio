"""Breeze TTS 2 worker (https://github.com/breezeblue-ai/breeze-tts, model BreezeBlue/Breeze-TTS-2).

The inference code isn't a package: the worker downloads it at BREEZE_COMMIT once and puts it on sys.path
(like fish_s2). Each request picks one of upstream's templates:

- clip + transcript              -> ref_clone_tata  (voice clone)
- clip + transcript + instruction -> ref_edit_tata   (voice direction)
- instruction only               -> tts_instruction (voice design)
- neither                        -> tts_plain       (the model picks a voice)

Without a transcript, the clip is transcribed with Whisper first (upstream requires one).

Speed: upstream's fast path captures CUDA graphs of each stage, after torch.compile for the depth decoder and
codec. torch.compile needs Triton, which Windows doesn't have, so the worker turns torch.compile into a no-op and
captures the graphs from the eager modules. With every stage but the codec that runs at RTF ~0.6 on an RTX 3090
(eager: ~3.2); the codec's graph would add ~4 GB of VRAM for ~10 % more. The "fastPath" worker setting turns it off.
"""

import io
import os
import re
import shutil
import sys
import urllib.request
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, run_worker, worker_settings, write_wav  # noqa: E402

MODEL_ID = "breeze-tts-2"
ASR_MODEL_ID = "whisper-large-v3-turbo"
BREEZE_COMMIT = "58ec70ce5fa4cc361bdebf77ec40d1365da00ab2"
SOURCE_URL = f"https://codeload.github.com/breezeblue-ai/breeze-tts/zip/{BREEZE_COMMIT}"
# Upstream's infer.py limits. The codec makes 12.5 frames per second, so 1500 frames is 2 minutes.
MAX_NEW_TOKENS = 1500
MAX_SEQ_LEN = 2048
REPETITION_PENALTY = 1.1
FRAME_RATE = 12.5
CJK = re.compile(r"[㐀-鿿豈-﫿]")


def ensure_source():
    """Downloads breeze-tts at BREEZE_COMMIT once and puts it on sys.path."""
    root = os.path.join(os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface"),
                        "breeze-tts-src")
    target = os.path.join(root, BREEZE_COMMIT[:12])
    if not os.path.isfile(os.path.join(target, "infer.py")):
        os.makedirs(root, exist_ok=True)
        log(f"Downloading breeze-tts source {SOURCE_URL}")
        data = urllib.request.urlopen(SOURCE_URL, timeout=120).read()
        tmp = target + ".partial"
        shutil.rmtree(tmp, ignore_errors=True)
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            z.extractall(tmp)
        inner = os.path.join(tmp, os.listdir(tmp)[0])  # the archive's single top folder
        shutil.rmtree(target, ignore_errors=True)
        os.replace(inner, target)
        shutil.rmtree(tmp, ignore_errors=True)
    # First on the path: upstream's top-level package is called "models".
    if target not in sys.path:
        sys.path.insert(0, target)
    return target


def _no_compile(model=None, **kwargs):
    """torch.compile that returns the module (or function) unchanged: see the module docstring."""
    return model if model is not None else (lambda fn: fn)


def expected_frames(text):
    """A rough length for the progress bar: ~4 Chinese characters or ~15 other characters per second."""
    cjk = len(CJK.findall(text))
    seconds = cjk / 4.0 + (len(text) - cjk) / 15.0
    return max(1.0, seconds * FRAME_RATE)


class BreezeTts2Worker:
    def __init__(self):
        self.tokenizer = None
        self.model = None
        self.audio_tokenizer = None
        self.runtime = None
        self.asr = None
        self.transcripts = {}  # (clip, mtime) -> Whisper transcript
        self.codes = {}        # (clip, mtime) -> encoded reference clip

    def selftest(self):
        ensure_source()
        import qwen_tts  # noqa: F401
        import transformers
        from breeze_infer.runtime import load_runtime  # noqa: F401
        from models.fast_streaming import FastBreezeStreamingRuntime  # noqa: F401

        log(f"breeze-tts {BREEZE_COMMIT[:12]} imported, transformers {transformers.__version__}")

    def prepare(self, report):
        ensure_source()
        ensure_model(MODEL_ID)

    def load(self, report):
        from pathlib import Path

        ensure_source()
        import breeze_infer.templates as templates
        from breeze_infer.runtime import load_runtime
        from models.fast_streaming import FastBreezeStreamingRuntime, FastStreamingConfig

        fast = bool(worker_settings().get("fastPath", True))
        path = ensure_model(MODEL_ID)
        report(0.1, "Loading Breeze TTS 2")
        # CUDA_VISIBLE_DEVICES already pins the app's chosen GPU, so it is device 0 here.
        self.tokenizer, self.model, self.audio_tokenizer = load_runtime(
            Path(path), device="cuda:0", attn_implementation="eager")
        stages = dict(fast_text_encoder=fast, fast_backbone_prefill=fast, fast_backbone_decode=fast,
                      fast_depth_decoder=fast, fast_codec=False)
        self.runtime = FastBreezeStreamingRuntime(
            self.model, self.audio_tokenizer,
            FastStreamingConfig(max_new_tokens=MAX_NEW_TOKENS, max_seq_len=MAX_SEQ_LEN,
                                repetition_penalty=REPETITION_PENALTY, **stages),
            tokenizer=self.tokenizer)
        if fast:
            import torch
            from dataclasses import replace
            from models.warmup_profile import load_warmup_profile

            # For the whole process: graphs for a new batch size are captured later, and a real torch.compile
            # there would fail without Triton.
            torch.compile = _no_compile
            report(0.5, "Capturing CUDA graphs")
            profile = load_warmup_profile(Path(ensure_source()) / "configs" / "fast.json")
            self.runtime.warmup_from_profile(replace(profile, codec_chunk_frames=self.runtime.codec_chunk_frames))

        # Upstream encodes the reference clip on every request; keep the codes per clip instead.
        encode = templates._encode_prompt_audio

        def cached_encode(audio_tokenizer, audio_path):
            key = (audio_path, os.path.getmtime(audio_path))
            if key not in self.codes:
                self.codes[key] = encode(audio_tokenizer, audio_path)
            return self.codes[key]

        templates._encode_prompt_audio = cached_encode
        log(f"Breeze TTS 2 loaded on cuda (bfloat16, {'CUDA graphs' if fast else 'eager'})")
        report(1.0, "Ready")

    def _transcribe(self, clip, report):
        key = (clip, os.path.getmtime(clip))
        if key in self.transcripts:
            return self.transcripts[key]
        if self.asr is None:
            import torch
            from transformers import pipeline

            report(0.05, "Loading Whisper to transcribe the reference clip")
            self.asr = pipeline("automatic-speech-recognition", model=ensure_model(ASR_MODEL_ID),
                                dtype=torch.float16, device="cuda:0")
        import soundfile as sf

        report(0.05, "Transcribing the reference clip")
        audio, sr = sf.read(clip, dtype="float32", always_2d=True)
        text = self.asr({"raw": audio.mean(axis=1), "sampling_rate": sr})["text"].strip()
        log(f"Reference clip transcribed as: {text}")
        self.transcripts[key] = text
        return text

    def generate(self, req, report):
        import numpy as np
        from breeze_infer.runtime import update_generation_config_for_breeze
        from breeze_infer.templates import get_template, prepare_inputs, select_template_name

        text = " ".join((req.text or "").split())
        if not text:
            raise ValueError("empty text")
        s = req.settings
        instruction = " ".join(str(s.get("instruction") or "").split())
        temperature = float(s.get("temperature", 0.9))
        clip = (req.voice or "").strip()

        request = {"id": req.id, "text": text, "speaker": "S0"}
        if instruction:
            request["instruction"] = instruction
        if clip:
            if not os.path.isfile(clip):
                raise FileNotFoundError(f"voice reference clip not found: {clip}")
            request["ref_audio_path"] = clip
            request["ref_text"] = str(s.get("refText") or "").strip() or self._transcribe(clip, report)
        template = select_template_name(request)
        # Upstream's CLI default is 1 (no guidance); 4 is what it recommends for design and direction.
        cfg_scale = float(s.get("cfgScale", 4.0)) if instruction else 1.0

        update_generation_config_for_breeze(self.model, {
            "depth_decoder_do_sample": True, "depth_decoder_temperature": temperature,
            "depth_decoder_top_p": 1.0, "depth_decoder_top_k": 50,
            "do_sample": True, "top_p": 1.0, "top_k": 50, "max_new_tokens": MAX_NEW_TOKENS,
            "temperature": temperature,
        })
        seed = None if req.seed is None else int(req.seed) % (2**31)
        report(0.05, "Synthesizing")
        inputs = prepare_inputs(self.tokenizer, self.audio_tokenizer, self.model, [request], get_template(template),
                                guidance_scale=cfg_scale, guidance_scale_ref=None, guidance_scale_ins=None)

        expected = expected_frames(text)
        frames = [0]

        def on_frame(frame):
            frames[0] += 1
            report(0.05 + 0.9 * min(1.0, frames[0] / expected), "Synthesizing")  # raises when cancelled

        chunks = [chunk.audio for chunk in self.runtime.iter_audio_chunks(
            inputs, request_id=req.id, seed=seed, token_observer=on_frame)]
        audio = np.concatenate([np.asarray(c, dtype=np.float32).reshape(-1) for c in chunks]) if chunks else None
        if audio is None or audio.size == 0:
            raise RuntimeError("Breeze TTS 2 produced no audio")
        if frames[0] >= MAX_NEW_TOKENS:
            log(f"Hit the {MAX_NEW_TOKENS / FRAME_RATE:.0f} s length limit; the segment may be cut off. Split it.")
        duration_ms = write_wav(req.out_path, audio, self.runtime.sample_rate)
        log(f"{template}, cfg {cfg_scale}, {frames[0]} frames")
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": self.runtime.sample_rate}


if __name__ == "__main__":
    run_worker(BreezeTts2Worker(), name="breeze_tts2", model_version="3e28c515")
