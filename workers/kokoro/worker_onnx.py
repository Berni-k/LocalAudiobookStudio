"""Kokoro-82M on ONNX Runtime: the same model and voices as worker.py, without torch.

Runs the onnx-community export (https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX) through
kokoro-onnx. Text becomes phonemes with misaki, as in kokoro's own pipeline, so the audio matches the
torch worker (correlation 0.99 with its output for a test sentence). The full-precision model is used:
the fp16 and int8 exports drift from it (0.79 and about 0), and int8 is slower on the CPU.

One script for both ONNX runtimes: the onnx1_30-cpu environment has the CPU build of onnxruntime, the
onnx1_30-cu13 environment the CUDA build (NVIDIA's libraries come as packages and are loaded with
onnxruntime.preload_dlls()).
"""

import glob
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, fix_espeak_data_path, log, model_file, run_worker, write_wav  # noqa: E402

MODEL = "kokoro-82m-onnx"  # workers/models.json; voices are extra files of it
VOCAB_MODEL = "kokoro-82m-vocab"  # hexgrad's config.json (the vocabulary); the export's tokenizer.json differs
MODEL_FILE = "onnx/model.onnx"
SAMPLE_RATE = 24000
DEFAULT_VOICE = "af_heart"


def _cuda_build():
    """True in the CUDA environment (onnxruntime-gpu). Loads NVIDIA's pip-installed libraries first."""
    import onnxruntime as ort

    if "CUDAExecutionProvider" not in ort.get_available_providers():
        return False
    if hasattr(ort, "preload_dlls"):
        ort.preload_dlls()
    return True


class KokoroOnnxWorker:
    # The protocol's default self-test would import torch to check CUDA; this environment has none.
    requires_cuda = False

    def __init__(self):
        self.model_dir = None
        self.vocab = None
        self.kokoro = None
        self.g2p = {}
        self.device = "cpu"

    def selftest(self):
        import kokoro_onnx  # noqa: F401
        import onnxruntime as ort
        from misaki import en  # noqa: F401

        cuda = _cuda_build()
        log(f"onnxruntime {ort.__version__}, providers {ort.get_available_providers()}")
        if "cuda" in os.environ.get("LAS_DEVICES", "") and not cuda:
            raise RuntimeError("This is the CUDA runtime, but onnxruntime has no CUDA provider (the CPU build got installed?).")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)
        self.vocab = os.path.join(ensure_model(VOCAB_MODEL), "config.json")

    def load(self, report):
        import onnxruntime as ort
        from kokoro_onnx import Kokoro

        if self.model_dir is None:
            self.prepare(report)
        report(0.1, "Loading Kokoro-82M (ONNX)")
        cuda = _cuda_build() and os.environ.get("LAS_DEVICE") != "cpu"
        options = ort.SessionOptions()
        threads = os.environ.get("LAS_CPU_THREADS")
        if threads and threads.isdigit() and int(threads) > 0:
            options.intra_op_num_threads = int(threads)
        # CUDA_VISIBLE_DEVICES already pins the app's chosen GPU, so it is device 0 here.
        providers = ([("CUDAExecutionProvider", {"device_id": 0})] if cuda else []) + ["CPUExecutionProvider"]
        session = ort.InferenceSession(os.path.join(self.model_dir, MODEL_FILE), options, providers=providers)
        if cuda and "CUDAExecutionProvider" not in session.get_providers():
            raise RuntimeError("CUDA could not be used (ONNX Runtime 1.30 needs an NVIDIA driver with CUDA 13, 580 or newer).")
        self.device = "cuda" if cuda else "cpu"
        self.kokoro = Kokoro.from_session(session, self._voices_archive(report), vocab_config=self.vocab)
        report(0.8, "Preparing English pipeline")
        self._g2p("a")
        report(1.0, "Ready")
        log(f"Kokoro (ONNX) loaded on {self.device}, providers {session.get_providers()}")

    def _voices_archive(self, report):
        """kokoro-onnx reads its voices from one .npz; the export has a .bin per voice ([510, 1, 256] floats).
        The manifest's voices are fetched from the app once and packed into the model folder (an extra file,
        deleted with the model); a changed voice list packs a new archive."""
        import hashlib

        import numpy as np

        names = sorted(self._voice_names())
        key = hashlib.sha1(",".join(names).encode("utf-8")).hexdigest()[:10]
        path = os.path.join(self.model_dir, f"las-voices-{key}.npz")
        if not os.path.isfile(path):
            voices = {}
            for i, name in enumerate(names):
                report(0.1, f"Fetching Kokoro voices ({i + 1}/{len(names)})")
                voices[name] = np.fromfile(model_file(MODEL, f"voices/{name}.bin"), dtype=np.float32).reshape(-1, 1, 256)
            np.savez(path + ".partial.npz", **voices)
            os.replace(path + ".partial.npz", path)
            for old in glob.glob(os.path.join(self.model_dir, "las-voices-*.npz")):
                if old != path:
                    os.remove(old)
        return path

    def _voice_names(self):
        """The "voice" setting's options in the manifest."""
        import json

        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "manifest.json"), encoding="utf-8") as f:
            settings = json.load(f).get("settings", [])
        return next((s.get("options", []) for s in settings if s.get("key") == "voice"), [])

    def _g2p(self, lang_code):
        """misaki's English G2P with espeak for unknown words, as kokoro's KPipeline sets it up."""
        g = self.g2p.get(lang_code)
        if g is None:
            from misaki import en, espeak

            fix_espeak_data_path()
            british = lang_code == "b"
            g = en.G2P(trf=False, british=british, fallback=espeak.EspeakFallback(british=british))
            self.g2p[lang_code] = g
        return g

    def list_voices(self):
        import json

        with open(os.path.join(os.path.dirname(__file__), "manifest.json"), encoding="utf-8") as f:
            return json.load(f).get("voices", [])

    def generate(self, req, report):
        text = (req.text or "").strip()
        if not text:
            raise ValueError("empty text")
        voice = (req.voice or req.settings.get("voice") or DEFAULT_VOICE).strip()
        if voice not in self.kokoro.get_voices():
            raise ValueError(f"unknown Kokoro voice: {voice}")
        speed = float(req.settings.get("speed", 1.0))

        report(0.05, "Synthesizing")
        phonemes, _ = self._g2p(voice[0] if voice[0] in "ab" else "a")(text)
        if not phonemes.strip():
            raise RuntimeError("Kokoro produced no audio (text may contain no pronounceable words)")
        # trim=False: keep the model's own lead-in and tail, like the torch worker (the app trims silence itself).
        audio, sample_rate = self.kokoro.create(phonemes, voice=voice, speed=speed, is_phonemes=True, trim=False)
        duration_ms = write_wav(req.out_path, audio, sample_rate)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sample_rate}


if __name__ == "__main__":
    run_worker(KokoroOnnxWorker(), name="kokoro", model_version="v1.0")
