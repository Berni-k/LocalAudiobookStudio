"""OmniVoice on ONNX Runtime (no torch), for the onnx and onnx-cuda runtimes.

Runs OpenVoiceOS's bidirectional export (``OpenVoiceOS/phoonnx-omnivoice``: backbone, Higgs codec encoders and
decoder), whose backbone and codec match upstream PyTorch token for token. The decoding loop below follows
upstream's ``_generate_iterative`` (and phoonnx's numpy port of it, Apache 2.0); the prompt text, duration
estimate, text chunking and audio post-processing are upstream's own code, vendored in ``onnx_port/``.

Differences from the PyTorch worker: no Whisper (a clip without a transcript clones from its sound only, which
is noticeably worse), and the random numbers come from NumPy, so a seed gives a different take than PyTorch.
"""

import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from common.protocol import ensure_model, log, run_worker, write_wav  # noqa: E402
from options import read_options  # noqa: E402

MODEL_ID = "omnivoice-onnx"
SAMPLE_RATE = 24000
SEMANTIC_SAMPLE_RATE = 16000  # the Higgs semantic (HuBERT) encoder's rate
HOP = 960                     # 25 codec frames per second
FRAME_RATE = SAMPLE_RATE // HOP
CODEBOOKS = 8
MASK_ID = 1024
TARGET_RMS = 0.1              # reference clips are normalized to this before encoding
# Upstream's generation defaults (OmniVoiceGenerationConfig).
GUIDANCE_SCALE = 2.0
T_SHIFT = 0.1
LAYER_PENALTY = 5.0
POSITION_TEMPERATURE = 5.0
CHUNK_THRESHOLD_SEC = 30.0
CHUNK_SEC = 15.0


def _cuda_build():
    """True in the CUDA environment (onnxruntime-gpu). Loads NVIDIA's pip-installed libraries first."""
    import onnxruntime as ort

    if "CUDAExecutionProvider" not in ort.get_available_providers():
        return False
    if hasattr(ort, "preload_dlls"):
        ort.preload_dlls()
    return True


def _log_softmax(x):
    import numpy as np

    x = x - x.max(axis=-1, keepdims=True)
    return x - np.log(np.exp(x).sum(axis=-1, keepdims=True))


def _gumbel(logits, temperature, rng):
    import numpy as np

    u = rng.random(logits.shape)
    return logits / temperature - np.log(-np.log(u + 1e-10) + 1e-10)


class OmniVoiceOnnxWorker:
    # The protocol's default self-test would import torch to check CUDA; this environment has none.
    requires_cuda = False

    def __init__(self):
        self.model_dir = None
        self.tokenizer = None
        self.backbone = None
        self.decoder = None
        self.encoders = {}       # acoustic / semantic / quantizer, opened on the first clip
        self.references = {}     # (clip, mtime) -> (codes[8, T], rms)
        self.cuda = False
        self.estimator = None

    def selftest(self):
        import onnxruntime as ort
        import pydub  # noqa: F401
        import soundfile  # noqa: F401
        import tokenizers

        cuda = _cuda_build()
        log(f"onnxruntime {ort.__version__}, tokenizers {tokenizers.__version__}, providers {ort.get_available_providers()}")
        if "cuda" in os.environ.get("LAS_DEVICES", "") and not cuda:
            raise RuntimeError("This is the CUDA runtime, but onnxruntime has no CUDA provider (the CPU build got installed?).")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL_ID)

    def _session(self, name):
        import onnxruntime as ort

        options = ort.SessionOptions()
        threads = os.environ.get("LAS_CPU_THREADS")
        if threads and threads.isdigit() and int(threads) > 0:
            options.intra_op_num_threads = int(threads)
        # CUDA_VISIBLE_DEVICES already pins the app's chosen GPU, so it is device 0 here.
        providers = ([("CUDAExecutionProvider", {"device_id": 0})] if self.cuda else []) + ["CPUExecutionProvider"]
        session = ort.InferenceSession(os.path.join(self.model_dir, name), options, providers=providers)
        if self.cuda and "CUDAExecutionProvider" not in session.get_providers():
            raise RuntimeError("CUDA could not be used (ONNX Runtime 1.30 needs an NVIDIA driver with CUDA 13, 580 or newer).")
        return session

    def load(self, report):
        from onnx_port.duration import RuleDurationEstimator
        from tokenizers import Tokenizer

        if self.model_dir is None:
            self.prepare(report)
        self.cuda = _cuda_build() and os.environ.get("LAS_DEVICE") != "cpu"
        report(0.1, "Loading OmniVoice (ONNX)")
        self.tokenizer = Tokenizer.from_file(os.path.join(self.model_dir, "tokenizer.json"))
        self.backbone = self._session("omnivoice_backbone.onnx")
        report(0.8, "Loading the audio decoder")
        self.decoder = self._session("higgs_decoder.onnx")
        self.estimator = RuleDurationEstimator()
        log(f"OmniVoice (ONNX) loaded on {'cuda' if self.cuda else 'cpu'}, providers {self.backbone.get_providers()}")
        report(1.0, "Ready")

    # ------------------------------------------------------------------ reference clip

    def _encoder(self, name):
        if name not in self.encoders:
            self.encoders[name] = self._session(name)
        return self.encoders[name]

    def _reference(self, clip, report):
        """Upstream ``create_voice_clone_prompt`` without the transcript: (codes[8, T], original rms)."""
        import numpy as np
        import soundfile as sf
        from onnx_port.audio import remove_silence, resample

        key = (clip, os.path.getmtime(clip))
        if key in self.references:
            return self.references[key]
        report(0.05, "Reading the reference clip")
        data, sr = sf.read(clip, dtype="float32", always_2d=True)
        wav = data.mean(axis=1)
        if sr != SAMPLE_RATE:
            wav = resample(wav, sr, SAMPLE_RATE)
        wav = wav.reshape(1, -1).astype(np.float32)
        rms = float(np.sqrt(np.mean(wav ** 2)))
        if 0 < rms < TARGET_RMS:
            wav = wav * TARGET_RMS / rms
        trimmed = remove_silence(wav, SAMPLE_RATE, mid_sil=200, lead_sil=100, trail_sil=200)
        if trimmed.shape[-1] == 0:
            raise ValueError("The reference clip is silent.")
        wav = trimmed
        if wav.shape[-1] / SAMPLE_RATE > 20:
            log(f"Reference clip is {wav.shape[-1] / SAMPLE_RATE:.1f} s long; 3-10 s works best and is faster.")
        if wav.shape[-1] % HOP:
            wav = wav[:, : -(wav.shape[-1] % HOP)]
        wav = wav.reshape(-1)

        acoustic = self._encoder("acoustic_encoder.onnx").run(
            ["acoustic_features"], {"waveform_24k": wav[None, None, :]})[0]
        wav16 = resample(wav, SAMPLE_RATE, SEMANTIC_SAMPLE_RATE).astype(np.float32)
        semantic = self._encoder("semantic_encoder.onnx").run(
            ["semantic_features"], {"waveform_16k": wav16[None, :]})[0]
        frames = min(acoustic.shape[2], semantic.shape[2])
        codes = self._encoder("quantizer_encoder.onnx").run(
            ["codes"], {"acoustic_features": acoustic[:, :, :frames], "semantic_features": semantic[:, :, :frames]})[0]
        result = (np.asarray(codes).reshape(CODEBOOKS, -1).astype(np.int64), rms)
        self.references[key] = result
        return result

    # ------------------------------------------------------------------ generation

    def _target_len(self, text, ref_text, ref_codes, speed):
        """Upstream ``_estimate_target_tokens``: codec frames to generate."""
        if ref_codes is None or not ref_text:
            ref_text, ref_frames = "Nice to meet you.", 25
        else:
            ref_frames = ref_codes.shape[1]
        est = self.estimator.estimate_duration(text, ref_text, ref_frames)
        if speed > 0 and speed != 1.0:
            est = est / speed
        return max(1, int(est))

    def _prompt(self, text, target_len, ref_text, ref_codes, lang, instruct):
        """Upstream ``_prepare_inference_inputs``: (input_ids[1, 8, S], audio_mask[1, S])."""
        import numpy as np
        from onnx_port.prompt import _combine_text, _tokenize_with_nonverbal_tags

        style = "<|denoise|>" if ref_codes is not None else ""
        style += f"<|lang_start|>{lang or 'None'}<|lang_end|><|instruct_start|>{instruct or 'None'}<|instruct_end|>"
        style_ids = self.tokenizer.encode(style).ids
        text_ids = _tokenize_with_nonverbal_tags(
            f"<|text_start|>{_combine_text(ref_text=ref_text, text=text)}<|text_end|>", self.tokenizer)
        parts = [np.tile(np.asarray(style_ids, np.int64), (CODEBOOKS, 1)),
                 np.tile(np.asarray(text_ids, np.int64), (CODEBOOKS, 1))]
        if ref_codes is not None:
            parts.append(ref_codes)
        parts.append(np.full((CODEBOOKS, target_len), MASK_ID, np.int64))
        input_ids = np.concatenate(parts, axis=1)[None]
        audio_start = input_ids.shape[2] - target_len - (ref_codes.shape[1] if ref_codes is not None else 0)
        audio_mask = np.zeros((1, input_ids.shape[2]), bool)
        audio_mask[0, audio_start:] = True
        return input_ids, audio_mask

    def _decode_tokens(self, text, target_len, ref_text, ref_codes, lang, instruct, steps, rng, on_pass):
        """Upstream ``_generate_iterative`` for one item: masked diffusion over ``steps`` passes -> codes[8, T]."""
        import numpy as np

        cond_ids, cond_mask = self._prompt(text, target_len, ref_text, ref_codes, lang, instruct)
        cond_len = cond_ids.shape[2]
        # The unconditional row is the target span alone. Upstream batches both rows behind a block mask, which
        # for one item is the same as two forwards of their own lengths.
        uncond_ids = cond_ids[:, :, -target_len:].copy()
        uncond_mask = cond_mask[:, -target_len:].copy()

        t = np.linspace(0.0, 1.0, steps + 1)
        t = T_SHIFT * t / (1 + (T_SHIFT - 1) * t)
        total = target_len * CODEBOOKS
        remaining, schedule = total, []
        for step in range(steps):
            take = remaining if step == steps - 1 else min(math.ceil(total * (t[step + 1] - t[step])), remaining)
            schedule.append(int(take))
            remaining -= int(take)

        tokens = np.full((CODEBOOKS, target_len), MASK_ID, np.int64)
        layer_ids = np.arange(CODEBOOKS, dtype=np.float32).reshape(-1, 1)
        for take in schedule:
            on_pass()
            if take <= 0:
                continue
            c = self.backbone.run(None, {"input_ids": cond_ids, "audio_mask": cond_mask})[0]
            u = self.backbone.run(None, {"input_ids": uncond_ids, "audio_mask": uncond_mask})[0]
            c_lp = _log_softmax(c[0, :, cond_len - target_len:cond_len, :].astype(np.float32))
            u_lp = _log_softmax(u[0, :, :target_len, :].astype(np.float32))
            log_probs = _log_softmax(c_lp + GUIDANCE_SCALE * (c_lp - u_lp))
            log_probs[..., MASK_ID] = -np.inf
            predicted = log_probs.argmax(-1)   # class temperature 0: greedy

            scores = log_probs.max(-1) - layer_ids * LAYER_PENALTY
            scores = _gumbel(scores, POSITION_TEMPERATURE, rng)
            scores = np.where(tokens != MASK_ID, -np.inf, scores)
            flat = scores.ravel()
            chosen = np.argpartition(-flat, take - 1)[:take] if take < flat.size else np.arange(flat.size)
            flat_tokens = tokens.ravel().copy()
            flat_tokens[chosen] = predicted.ravel()[chosen]
            tokens = flat_tokens.reshape(CODEBOOKS, target_len)
            cond_ids[0, :, cond_len - target_len:cond_len] = tokens
            uncond_ids[0, :, :target_len] = tokens
        return tokens

    def _decode_audio(self, codes):
        import numpy as np

        return np.asarray(self.decoder.run(["waveform_24k"], {"codes": codes[:, None, :]})[0], np.float32).reshape(1, -1)

    def generate(self, req, report):
        import numpy as np
        from onnx_port.audio import cross_fade_chunks, fade_and_pad_audio, remove_silence
        from onnx_port.prompt import _ZH_RE, _resolve_instruct
        from onnx_port.text import add_punctuation, chunk_text_punctuation

        o = read_options(req)
        clip = (req.voice or "").strip()
        ref_codes = ref_rms = None
        ref_text = None
        if clip:
            if not os.path.isfile(clip):
                raise FileNotFoundError(f"voice reference clip not found: {clip}")
            ref_codes, ref_rms = self._reference(clip, report)
            if o.ref_text:
                ref_text = add_punctuation(o.ref_text)
            else:
                log("No reference transcript: cloning from the clip's sound only (the ONNX runtime has no Whisper).")
        instruct = _resolve_instruct(o.instruct, use_zh=bool(_ZH_RE.search(o.text))) if o.instruct else None

        # Upstream's long-form path (_generate_chunked): above ~30 s split at punctuation into ~15 s chunks. Without
        # a clip, the first chunk becomes the reference of the others so the voice stays the same.
        target_len = self._target_len(o.text, ref_text, ref_codes, o.speed)
        if target_len / FRAME_RATE > CHUNK_THRESHOLD_SEC:
            chunk_len = int(CHUNK_SEC * FRAME_RATE / (target_len / len(o.text)))
            chunks = chunk_text_punctuation(text=o.text, chunk_len=chunk_len, min_chunk_len=3)
            log(f"Long text (~{target_len / FRAME_RATE:.0f} s): generating {len(chunks)} chunks")
        else:
            chunks = [o.text]

        expected = o.steps * len(chunks)
        done = [0]

        def on_pass():
            done[0] += 1
            report(0.1 + 0.85 * min(1.0, done[0] / expected), "Synthesizing")  # raises when cancelled

        rng = np.random.default_rng(None if req.seed is None else int(req.seed) % (2**32))
        report(0.1, "Synthesizing")
        codes = []
        for i, chunk in enumerate(chunks):
            if ref_codes is None and i > 0:
                c_ref_text, c_ref_codes = chunks[0], codes[0]
            else:
                c_ref_text, c_ref_codes = ref_text, ref_codes
            length = self._target_len(chunk, c_ref_text, c_ref_codes, o.speed) if len(chunks) > 1 else target_len
            codes.append(self._decode_tokens(chunk, length, c_ref_text, c_ref_codes, o.language, instruct,
                                             o.steps, rng, on_pass))

        audio = cross_fade_chunks([self._decode_audio(c) for c in codes], SAMPLE_RATE)
        # Upstream _post_process_audio: drop long pauses, match the clip's loudness, fade and pad the edges.
        trimmed = remove_silence(audio, SAMPLE_RATE, mid_sil=500, lead_sil=100, trail_sil=100)
        audio = trimmed if trimmed.shape[-1] else audio
        if ref_rms is not None and ref_rms < TARGET_RMS:
            audio = audio * ref_rms / TARGET_RMS
        elif ref_rms is None:
            peak = float(np.abs(audio).max())
            if peak > 1e-6:
                audio = audio / peak * 0.5
        audio = fade_and_pad_audio(audio, pad_duration=0.1, fade_duration=0.1, sample_rate=SAMPLE_RATE).reshape(-1)
        if audio.size == 0:
            raise RuntimeError("OmniVoice produced no audio")
        duration_ms = write_wav(req.out_path, audio, SAMPLE_RATE)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": SAMPLE_RATE}


if __name__ == "__main__":
    run_worker(OmniVoiceOnnxWorker(), name="omnivoice", model_version="c5fdb5cc")
