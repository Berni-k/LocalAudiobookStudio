"""Parakeet TDT 0.6B v3 on ONNX Runtime: speech recognition with word timings, 25 European languages.

Runs istupakov's ONNX export (https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx) with onnx-asr for the
features and the model sessions. The decoding loop is our own: onnx-asr's keeps only the frame where each token
starts, and a TDT model also predicts how many frames a token lasts, which gives each word its end.

One script for both ONNX runtimes: the onnx1_30-cpu environment has the CPU build of onnxruntime, the onnx1_30-cu13
environment the CUDA build (NVIDIA's libraries come as packages and are loaded with onnxruntime.preload_dlls()).
"""

import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, quiet_pieces, read_wav  # noqa: E402
from common.protocol import Cancelled, ensure_model, log, run_worker, worker_settings  # noqa: E402

MODELS = {"int8": "parakeet-tdt-0.6b-v3-onnx-int8", "full": "parakeet-tdt-0.6b-v3-onnx"}  # workers/models.json
FRAME_SEC = 0.08  # one encoder frame: 10 ms features, subsampled 8x

# The model was trained on short utterances and attends over the whole input, so long takes are cut at their
# quietest point between MIN_CHUNK_SEC and MAX_CHUNK_SEC and each part is recognised on its own.
MAX_CHUNK_SEC = 30.0
MIN_CHUNK_SEC = 20.0


def _cuda_build():
    """True in the CUDA environment (onnxruntime-gpu). Loads NVIDIA's pip-installed libraries first."""
    import onnxruntime as ort

    if "CUDAExecutionProvider" not in ort.get_available_providers():
        return False
    if hasattr(ort, "preload_dlls"):
        ort.preload_dlls()
    return True


class ParakeetWorker:
    # The protocol's default self-test would import torch to check CUDA; this environment has none.
    requires_cuda = False

    def __init__(self):
        self.model_dir = None
        self.precision = "int8"
        self.asr = None
        self.device = "cpu"

    def selftest(self):
        import onnx_asr  # noqa: F401
        import onnxruntime as ort

        cuda = _cuda_build()
        log(f"onnxruntime {ort.__version__}, providers {ort.get_available_providers()}")
        if "cuda" in os.environ.get("LAS_DEVICES", "") and not cuda:
            raise RuntimeError("This is the CUDA runtime, but onnxruntime has no CUDA provider (the CPU build got installed?).")

    def prepare(self, report):
        self.precision = str(worker_settings().get("precision") or "int8")
        if self.precision not in MODELS:
            raise ValueError(f"unknown precision: {self.precision}")
        self.model_dir = ensure_model(MODELS[self.precision])

    def load(self, report):
        import onnx_asr
        import onnxruntime as ort

        if self.model_dir is None:
            self.prepare(report)
        report(0.1, "Loading Parakeet TDT 0.6B v3 (ONNX)")
        cuda = _cuda_build() and os.environ.get("LAS_DEVICE") != "cpu"
        options = ort.SessionOptions()
        threads = os.environ.get("LAS_CPU_THREADS")
        if threads and threads.isdigit() and int(threads) > 0:
            options.intra_op_num_threads = int(threads)
        # CUDA_VISIBLE_DEVICES already pins the app's chosen GPU, so it is device 0 here.
        providers = ([("CUDAExecutionProvider", {"device_id": 0})] if cuda else []) + ["CPUExecutionProvider"]
        # The model type rather than its Hub name: onnx-asr then only reads the folder and never goes online.
        adapter = onnx_asr.load_model("nemo-conformer-tdt", self.model_dir,
                                      quantization="int8" if self.precision == "int8" else None,
                                      sess_options=options, providers=providers)
        self.asr = adapter.asr
        if cuda and "CUDAExecutionProvider" not in self.asr._encoder.get_providers():
            raise RuntimeError("CUDA could not be used (ONNX Runtime 1.30 needs an NVIDIA driver with CUDA 13, 580 or newer).")
        self.device = "cuda" if cuda else "cpu"
        report(1.0, "Ready")
        log(f"Parakeet ({self.precision}) loaded on {self.device}, providers {self.asr._encoder.get_providers()}")

    def transcribe_align(self, req, report):
        audio = read_wav(req.audio_path)
        pieces = quiet_pieces(audio, MIN_CHUNK_SEC, MAX_CHUNK_SEC)
        words = []
        for i, (start, end) in enumerate(pieces):
            report(i / len(pieces), "Listening" if len(pieces) == 1 else f"Listening (part {i + 1} of {len(pieces)})")
            words.extend(self._recognize(audio[start:end], start / SAMPLE_RATE, req))
        report(1.0)
        return {"text": " ".join(w["text"] for w in words), "words": words, "language": None}

    def _recognize(self, audio, offset_sec, req):
        """Words of one piece, with times in the whole file."""
        import numpy as np

        if len(audio) < SAMPLE_RATE // 10:
            return []
        asr = self.asr
        features, lens = asr._preprocessor(audio[None, :], np.array([len(audio)], dtype=np.int64))
        encoded, encoded_lens = asr._encode(features, lens)
        tokens = self._decode(encoded[0], int(encoded_lens[0]), req)
        return self._words(tokens, offset_sec, len(audio) / SAMPLE_RATE)

    def _decode(self, frames, frame_count, req):
        """Greedy TDT decoding as onnx-asr does it, keeping each token's duration: [(token, frame, frames, logprob)]."""
        import numpy as np

        asr = self.asr
        state = asr._create_state()
        emitted = []
        t, per_step = 0, 0
        while t < frame_count:
            if req.cancelled:
                raise Cancelled()
            logits, step, new_state = asr._decode([e[0] for e in emitted], state, frames[t])
            token = int(logits.argmax())
            if token != asr._blank_idx:
                state = new_state
                shifted = logits - logits.max()
                logprob = float(shifted[token] - np.log(np.exp(shifted).sum()))
                emitted.append((token, t, step, logprob))
                per_step += 1
            if step > 0:
                t += step
                per_step = 0
            elif token == asr._blank_idx or per_step == asr._max_tokens_per_step:
                t += 1
                per_step = 0
        return emitted

    def _words(self, tokens, offset_sec, piece_sec):
        """Tokens joined into words: a token starting with a space begins a word; punctuation stays on its word."""
        vocab = self.asr._vocab
        words, current = [], None
        for token, frame, frames, logprob in tokens:
            piece = vocab[token]
            start = frame * FRAME_SEC
            end = min((frame + max(frames, 1)) * FRAME_SEC, piece_sec)
            if current is None or piece.startswith(" "):
                current = {"text": "", "start": start, "end": end, "logprobs": []}
                words.append(current)
            current["text"] += piece
            current["end"] = max(current["end"], end)
            current["logprobs"].append(logprob)
        words = [w for w in words if w["text"].strip()]
        # A token's predicted duration can run past the next word's first frame; words don't overlap.
        for w, following in zip(words, words[1:]):
            w["end"] = max(w["start"], min(w["end"], following["start"]))
        result = []
        for w in words:
            text = w["text"].strip()
            result.append({
                "text": text,
                "startMs": int(round((offset_sec + w["start"]) * 1000)),
                "endMs": int(round((offset_sec + w["end"]) * 1000)),
                # The geometric mean of the tokens' probabilities: low when the model was unsure of any part.
                "confidence": round(math.exp(sum(w["logprobs"]) / len(w["logprobs"])), 4),
            })
        return result


if __name__ == "__main__":
    run_worker(ParakeetWorker(), name="parakeet", model_version="v3")
