"""Fish Audio Drama 3 worker (https://github.com/fishaudio/fish-speech v3, model fishaudio/drama-3).

fish-speech is not installable as a package (``pyrootutils`` needs the repository's ``.project-root``, and the codec
config is loaded by relative path), so the worker downloads the source at a pinned commit and imports it from there,
as the Fish S2 Pro worker does. ``drama3_patches.py`` makes the 35B model fit and run on consumer GPUs: FP8 experts
dequantized on use (or re-quantized to 4 bits: the "Expert precision" setting), the layers split over the GPUs the
app gives the process (its "GPU count" setting), and decoding as CUDA graphs per GPU.

Reference clips are encoded once: their codes are cached on disk by the clip's content. The codec runs on the last
GPU, or on the CPU (worker setting); when the GPU runs out of memory encoding or decoding, that step is done on the CPU.
"""

import hashlib
import io
import os
import shutil
import sys
import urllib.request
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, run_worker, worker_settings, write_wav  # noqa: E402

MODEL = "drama-3"  # fishaudio/drama-3 (workers/models.json)
MODEL_VERSION = "drama-3"
FISH_COMMIT = "1ff3c3ed2f80d3106a83909dd25b83129c3d816f"  # tag v3.0.0-beta
SOURCE_URL = f"https://codeload.github.com/fishaudio/fish-speech/zip/{FISH_COMMIT}"
# The codec makes 44100 / 2048 = 21.5 frames per second and speech is about 14 characters per second.
FRAMES_PER_CHAR = 1.5
# Frames one text chunk may take (fish-speech splits long text into chunks of about 300 bytes): about twice what the
# text needs, so a generation that runs on (acting out a scene instead of reading it) stops.
MAX_CHUNK_FRAMES = 1024


def models_dir():
    return os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface")


def cache_dir():
    return os.environ.get("XDG_CACHE_HOME") or os.path.join(os.path.expanduser("~"), ".cache")


def ensure_source():
    """Downloads fish-speech at FISH_COMMIT once and puts it on sys.path."""
    root = os.path.join(models_dir(), "fish-speech-src")
    target = os.path.join(root, FISH_COMMIT[:12])
    if not os.path.isfile(os.path.join(target, ".project-root")):
        os.makedirs(root, exist_ok=True)
        log(f"Downloading fish-speech source {SOURCE_URL}")
        data = urllib.request.urlopen(SOURCE_URL, timeout=120).read()
        tmp = target + ".partial"
        shutil.rmtree(tmp, ignore_errors=True)
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            z.extractall(tmp)
        inner = os.path.join(tmp, os.listdir(tmp)[0])  # the archive's single top folder
        shutil.rmtree(target, ignore_errors=True)
        os.replace(inner, target)
        shutil.rmtree(tmp, ignore_errors=True)
    if target not in sys.path:
        sys.path.insert(0, target)
    os.environ.setdefault("EINX_FILTER_TRACEBACK", "false")  # as fish-speech's launchers do
    return target


class Codec:
    """The DAC codec on a GPU, with the CPU as the fallback when the GPU runs out of memory (or as its only place)."""

    def __init__(self, path, device, dtype, load):
        self.path, self.device, self.dtype, self.load = path, device, dtype, load
        self.gpu = load(path, device, dtype) if device else None
        self.cpu = None

    def _cpu(self):
        if self.cpu is None:
            import torch

            log("Loading the codec on the CPU")
            self.cpu = self.load(self.path, "cpu", torch.float32)
        return self.cpu

    @property
    def sample_rate(self):
        return int((self.gpu if self.gpu is not None else self._cpu()).sample_rate)

    def _run(self, fn, what):
        import torch

        if self.gpu is not None:
            try:
                return fn(self.gpu, self.device)
            except torch.OutOfMemoryError:
                torch.cuda.empty_cache()
                log(f"{what}: the GPU is out of memory, using the CPU")
        return fn(self._cpu(), "cpu")

    def encode_file(self, path):
        """VQ codes [codebooks, frames] of an audio file, on the CPU."""
        import numpy as np
        import soundfile as sf
        import torch
        import torchaudio

        # soundfile, not torchaudio.load: since torchaudio 2.9 that needs torchcodec (and FFmpeg DLLs).
        data, sr = sf.read(path, dtype="float32", always_2d=True)
        wav = torch.from_numpy(np.ascontiguousarray(data.mean(axis=1)))

        def encode(codec, device):
            with torch.inference_mode():
                audio = torchaudio.functional.resample(wav.to(device), sr, codec.sample_rate)
                dtype = next(codec.parameters()).dtype
                indices, lengths = codec.encode(audio[None, None].to(dtype),
                                                torch.tensor([len(audio)], device=device, dtype=torch.long))
                return indices[0, :, : lengths[0]].cpu()

        return self._run(encode, "Encoding the reference clip")

    def decode(self, codes):
        """Mono float32 audio of VQ codes [codebooks, frames]."""
        import torch

        def decode(codec, device):
            with torch.inference_mode():
                return codec.from_indices(codes[None].to(device))[0, 0].float().cpu().numpy()

        return self._run(decode, "Decoding")


class Drama3Worker:
    def __init__(self):
        self.model = None
        self.codec = None
        self.devices = ""
        self.model_dir = None
        self.refs = {}  # content hash -> VQ codes of a reference clip

    def selftest(self):
        ensure_source()
        from fish_speech.models.text2semantic.inference import generate_long  # noqa: F401

        log(f"fish-speech {FISH_COMMIT[:12]} imported")

    def prepare(self, report):
        ensure_source()
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import torch

        if not torch.cuda.is_available():
            raise RuntimeError("Drama 3 needs an NVIDIA GPU.")
        ensure_source()
        import drama3_patches
        from fish_speech.models.text2semantic.inference import init_model, load_codec_model

        path = self.model_dir or ensure_model(MODEL)
        settings = worker_settings()
        codec_on_gpu = not settings.get("codecOnCpu", False)
        int4 = str(settings.get("expertPrecision") or "fp8").lower() == "int4"
        native = drama3_patches.apply(codec_on_gpu, int4, log)
        count = torch.cuda.device_count()
        self.devices = ",".join(f"cuda:{i}" for i in range(count))
        report(0.1, f"Loading Drama 3 on {count} GPU{'s' if count > 1 else ''}")
        log("Experts: " + ("4-bit, re-quantized while loading" if int4
                           else "FP8 with fish-speech's kernel" if native else "FP8, dequantized when used"))
        try:
            self.model, _ = init_model(path, self.devices, torch.bfloat16)
        except torch.OutOfMemoryError as e:
            raise RuntimeError("Drama 3 doesn't fit in the GPUs' memory. Raise its GPU count in the worker settings, "
                               f"or put the codec on the CPU. ({e})") from e
        report(0.9, "Loading the codec")
        device = str(self.model.device) if codec_on_gpu else None
        self.codec = Codec(os.path.join(path, "codec.pth"), device, self.model.dtype, load_codec_model)
        report(1.0, "Ready")
        log(f"Drama 3 loaded on {self.devices}, codec on {device or 'the CPU'}")

    def _reference(self, clip):
        """The clip's VQ codes, encoded once: kept in memory and on disk by the clip's content."""
        import torch

        with open(clip, "rb") as f:
            key = hashlib.sha256(f.read()).hexdigest()[:32]
        codes = self.refs.get(key)
        if codes is not None:
            return codes
        folder = os.path.join(cache_dir(), "drama3-references")
        file = os.path.join(folder, key + ".pt")
        if os.path.isfile(file):
            try:
                codes = torch.load(file, weights_only=True)
            except Exception as e:  # a broken cache file is encoded again
                log(f"ignoring cached reference {file}: {e}")
        if codes is None:
            codes = self.codec.encode_file(clip)
            os.makedirs(folder, exist_ok=True)
            torch.save(codes, file + ".partial")
            os.replace(file + ".partial", file)
        self.refs[key] = codes
        return codes

    def generate(self, req, report):
        import numpy as np
        import torch
        import fish_speech.models.text2semantic.fish_omni.generate as omni_generate
        from fish_speech.models.text2semantic.inference import generate_long

        text = " ".join((req.text or "").split())
        if not text:
            raise ValueError("empty text")
        s = req.settings

        prompt_tokens, prompt_text = [], []
        clip = (req.voice or "").strip()
        if clip:
            if not os.path.isfile(clip):
                raise FileNotFoundError(f"voice reference clip not found: {clip}")
            ref_text = " ".join(str(s.get("refText") or "").split())
            if not ref_text:
                raise ValueError("Drama 3 needs the reference clip's transcript: fill in 'Reference transcript' "
                                 "in the speaker's settings.")
            report(0.02, "Encoding the reference clip")
            prompt_tokens, prompt_text = [self._reference(clip)], [ref_text]

        expected = max(16.0, len(text) * FRAMES_PER_CHAR)
        frames = [0]

        def progress_tqdm(iterable, *args, **kwargs):
            """Replaces fish-speech's per-frame tqdm: reports progress, and lets a cancel stop it."""
            for x in iterable:
                frames[0] += 1
                report(min(0.9, 0.05 + 0.85 * frames[0] / expected), "Synthesizing")
                yield x

        torch.manual_seed(req.seed)
        torch.cuda.manual_seed_all(req.seed)
        codes = []
        original_tqdm = omni_generate.tqdm
        omni_generate.tqdm = progress_tqdm
        try:
            with torch.inference_mode():
                for result in generate_long(
                    model=self.model,
                    device=self.devices,
                    decode_one_token=None,
                    text=text,
                    max_new_tokens=min(MAX_CHUNK_FRAMES, int(min(len(text), 320) * FRAMES_PER_CHAR * 2) + 64),
                    temperature=float(s.get("temperature", 0.7)),
                    top_p=float(s.get("topP", 0.7)),
                    top_k=int(s.get("topK", 30)),
                    prompt_text=prompt_text,
                    prompt_tokens=prompt_tokens,
                ):
                    if result.action == "sample":
                        codes.append(result.codes)
                    elif result.action == "next":
                        break
        finally:
            omni_generate.tqdm = original_tqdm

        if not codes:
            raise RuntimeError("Drama 3 produced no audio (check the text)")
        report(0.93, "Decoding")
        audio = np.asarray(self.codec.decode(torch.cat(codes, dim=1)), dtype=np.float32)
        torch.cuda.empty_cache()
        sr = self.codec.sample_rate
        duration_ms = write_wav(req.out_path, audio, sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sr}


if __name__ == "__main__":
    run_worker(Drama3Worker(), name="drama3", model_version=MODEL_VERSION)
