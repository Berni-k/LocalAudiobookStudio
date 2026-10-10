"""Fish Audio S2 Pro worker (https://github.com/fishaudio/fish-speech, model fishaudio/s2-pro).

fish-speech is not installable as a package: its modules call ``pyrootutils.setup_root`` (which
needs the repository's ``.project-root`` marker) and load Hydra configs by relative path. So the
worker downloads the source at a pinned commit into the model cache and imports it from there;
``requirements.txt`` holds the package's dependencies.

Instead of fish-speech's thread-and-queue wrapper (which hangs forever if loading fails), this
calls the same pieces directly: ``init_model`` + ``generate_long`` for the semantic tokens and the
DAC codec for encoding the reference and decoding audio, as ``TTSInferenceEngine`` does.
"""

import io
import os
import shutil
import sys
import urllib.request
import zipfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import Unfusable, ensure_model, log, run_worker, write_wav  # noqa: E402

MODEL = "fish-s2-pro"  # fishaudio/s2-pro (workers/models.json)
FISH_COMMIT = "214da3cd841bda85da2496b96cd3c4d7edb1337e"
SOURCE_URL = f"https://codeload.github.com/fishaudio/fish-speech/zip/{FISH_COMMIT}"
SAMPLE_RATE = 44100
# The codec runs at 44100 / 2048 = 21.5 tokens per second and speech is about 14 characters per
# second, so a segment needs roughly 1.5 tokens per character (progress bar and length cap).
TOKENS_PER_CHAR = 1.5
# Voices in one conversation (generate_parts); generate_long groups at most 5 speakers per batch.
MAX_SPEAKERS = 5


def models_dir():
    return os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface")


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


def _load_audio(reference_audio, sr):
    """Replacement for fish-speech's ReferenceLoader.load_audio: mono float32 at ``sr``."""
    import numpy as np
    import soundfile as sf
    import torch
    import torchaudio

    if isinstance(reference_audio, bytes):
        reference_audio = io.BytesIO(reference_audio)
    data, original_sr = sf.read(reference_audio, dtype="float32", always_2d=True)
    waveform = torch.from_numpy(np.ascontiguousarray(data.T.mean(axis=0, keepdims=True)))
    if original_sr != sr:
        waveform = torchaudio.functional.resample(waveform, original_sr, sr)
    return waveform.squeeze().numpy()


class FishS2Worker:
    def __init__(self):
        self.model = None
        self.decode_one_token = None
        self.engine = None
        self.device = "cpu"
        self.refs = {}  # (clip path, mtime) -> VQ prompt tokens
        self.model_dir = None

    def selftest(self):
        ensure_source()
        from fish_speech.inference_engine import TTSInferenceEngine  # noqa: F401
        from fish_speech.models.text2semantic.inference import generate_long  # noqa: F401

        log(f"fish-speech {FISH_COMMIT[:12]} imported")

    def prepare(self, report):
        ensure_source()
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import torch

        ensure_source()
        from fish_speech.inference_engine import TTSInferenceEngine
        from fish_speech.models.dac.inference import load_model as load_codec
        from fish_speech.models.text2semantic.inference import init_model

        path = self.model_dir or ensure_model(MODEL)

        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        precision = torch.bfloat16
        report(0.4, "Loading S2 Pro")
        model, decode_one_token = init_model(path, self.device, precision, compile=False)
        with torch.device(self.device):
            model.setup_caches(max_batch_size=1, max_seq_len=model.config.max_seq_len,
                               dtype=next(model.parameters()).dtype)
        model._cache_setup_done = True
        report(0.85, "Loading codec")
        codec = load_codec(config_name="modded_dac_vq", checkpoint_path=os.path.join(path, "codec.pth"),
                           device=self.device)
        # Used only for its reference encoding and decoding helpers; its queue isn't needed.
        self.engine = TTSInferenceEngine(llama_queue=None, decoder_model=codec, precision=precision, compile=False)
        # fish-speech reads the reference with torchaudio.load, which needs torchcodec (and FFmpeg DLLs) since
        # torchaudio 2.9; soundfile reads the same WAV/FLAC/OGG files without them.
        self.engine.load_audio = _load_audio
        self.model = model
        self.decode_one_token = decode_one_token
        report(1.0, "Ready")
        log(f"S2 Pro loaded on {self.device}")

    def _reference(self, clip):
        import torch

        key = (clip, os.path.getmtime(clip))
        tokens = self.refs.get(key)
        if tokens is None:
            with open(clip, "rb") as f, torch.inference_mode():
                tokens = self.engine.encode_reference(reference_audio=f.read(), enable_reference_audio=True)
            self.refs[key] = tokens
        return tokens

    def generate(self, req, report):
        import numpy as np
        import torch
        import fish_speech.models.text2semantic.inference as t2s
        from fish_speech.utils import set_seed

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
                # fish-speech silently ignores a reference without its transcript.
                raise ValueError("S2 Pro needs the reference clip's transcript: fill in 'Reference transcript' "
                                 "in the speaker's settings.")
            report(0.02, "Encoding reference clip")
            prompt_tokens, prompt_text = [self._reference(clip)], [ref_text]

        expected = max(16.0, len(text) * TOKENS_PER_CHAR)

        def progress_tqdm(iterable, *args, **kwargs):
            """Replaces fish-speech's per-token tqdm: reports progress and lets a cancel stop it."""
            for i, x in enumerate(iterable):
                report(min(0.9, 0.05 + 0.85 * i / expected), "Synthesizing")
                yield x

        set_seed(req.seed)
        segments = []
        original_tqdm = t2s.tqdm
        t2s.tqdm = progress_tqdm
        try:
            with torch.inference_mode():
                for result in t2s.generate_long(
                    model=self.model,
                    decode_one_token=self.decode_one_token,
                    device=self.device,
                    text=text,
                    max_new_tokens=int(expected * 2.5) + 256,
                    top_p=float(s.get("topP", 0.8)),
                    repetition_penalty=float(s.get("repetitionPenalty", 1.1)),
                    temperature=float(s.get("temperature", 0.8)),
                    compile=False,
                    iterative_prompt=True,
                    chunk_length=200,
                    prompt_text=prompt_text,
                    prompt_tokens=prompt_tokens,
                ):
                    if result.action == "next":
                        break
                    report(0.92, "Decoding")
                    segments.append(self.engine.get_audio_segment(result))
        finally:
            t2s.tqdm = original_tqdm
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        if not segments:
            raise RuntimeError("S2 Pro produced no audio (check the text)")
        audio = np.concatenate([np.asarray(a, dtype=np.float32).reshape(-1) for a in segments])
        codec = self.engine.decoder_model
        sr = int(getattr(getattr(codec, "spec_transform", None), "sample_rate", 0) or codec.sample_rate or SAMPLE_RATE)
        duration_ms = write_wav(req.out_path, audio, sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sr}

    def _encode_audio(self, path):
        """VQ codes of a context take (not cached: each take is heard once or twice)."""
        import torch

        with open(path, "rb") as f, torch.inference_mode():
            return self.engine.encode_reference(reference_audio=f.read(), enable_reference_audio=True)

    def generate_parts(self, req, report):
        """A conversation in one go: every part is its own turn, generated after the turns before it, as
        fish-speech's generate_long does with its batches, so each segment hears what came before. One turn per
        part keeps each part's codes apart, so the spans are exact. Context with a take is heard (its codes go into
        the history) rather than generated; context after the last segment can't change it and is skipped. Only the
        segments are written to the output, one after the other."""
        import numpy as np
        import torch
        import fish_speech.models.text2semantic.inference as t2s
        from types import SimpleNamespace
        from fish_speech.content_sequence import TextPart, VQPart
        from fish_speech.conversation import Conversation, Message
        from fish_speech.utils import set_seed

        s = req.settings
        parts = req.parts
        kept = [i for i, p in enumerate(parts) if not p.is_context]
        if not kept:
            raise ValueError("no segment to generate")
        last = kept[-1]

        # Each speaker's reference: a clip with its transcript. fish-speech pairs the i-th reference with
        # <|speaker:i|>, so the speakers that have one get the first numbers.
        refs = {}
        for p in parts[:last + 1]:
            clip = (p.voice or "").strip()
            ref_text = " ".join(str(p.settings.get("refText") or "").split())
            if clip and not os.path.isfile(clip):
                raise FileNotFoundError(f"voice reference clip not found: {clip}")
            if clip and not ref_text and not p.is_context:
                raise ValueError("S2 Pro needs the reference clip's transcript: fill in 'Reference transcript' "
                                 "in the speaker's settings.")
            if p.speaker not in refs or (clip and ref_text and refs[p.speaker] is None):
                refs[p.speaker] = (clip, ref_text) if clip and ref_text else None
        if len(refs) > MAX_SPEAKERS:
            raise Unfusable(f"S2 Pro takes at most {MAX_SPEAKERS} voices in one conversation")
        order = [k for k, r in refs.items() if r] + [k for k, r in refs.items() if not r]
        number = {k: i for i, k in enumerate(order)}

        report(0.02, "Encoding reference clips")
        referenced = [refs[k] for k in order if refs[k]]
        if referenced:
            system_parts = [
                TextPart(text="convert the provided text to speech reference to the following:\n\nText:\n", cal_loss=False),
                TextPart(text="\n".join(f"<|speaker:{i}|>{text}" for i, (_, text) in enumerate(referenced)), cal_loss=False),
                TextPart(text="\n\nSpeech:\n", cal_loss=False),
                VQPart(codes=torch.cat([self._reference(clip).cpu() for clip, _ in referenced], dim=1), cal_loss=False),
            ]
        else:
            system_parts = [TextPart(text="convert the provided text to speech", cal_loss=False)]
        system = Message(role="system", parts=system_parts, cal_loss=False, add_im_start=True, add_im_end=True)

        def user(p):
            text = " ".join((p.text or "").split())
            return Message(role="user", parts=[TextPart(text=f"<|speaker:{number[p.speaker]}|>{text}", cal_loss=False)],
                           cal_loss=False, add_im_start=True, add_im_end=True)

        def assistant(codes):
            return Message(role="assistant", parts=[VQPart(codes=codes.cpu(), cal_loss=False)], cal_loss=False,
                           modality="voice", add_im_start=True, add_im_end=True)

        rendered = [i for i in range(last + 1) if not (parts[i].is_context and parts[i].audio_path)]
        expected_total = max(16.0, sum(len(parts[i].text or "") for i in rendered) * TOKENS_PER_CHAR)
        done_tokens = [0]

        def progress_tqdm(iterable, *args, **kwargs):
            for x in iterable:
                done_tokens[0] += 1
                report(min(0.9, 0.05 + 0.85 * done_tokens[0] / expected_total), "Synthesizing")
                yield x

        set_seed(req.seed)
        max_length = self.model.config.max_seq_len
        history = []  # (user, assistant) message pairs, oldest first
        audio = {}  # part index -> decoded audio of a kept part
        original_tqdm = t2s.tqdm
        t2s.tqdm = progress_tqdm
        try:
            with torch.inference_mode():
                for i, p in enumerate(parts[:last + 1]):
                    if p.is_context and p.audio_path:
                        report(min(0.9, 0.05 + 0.85 * done_tokens[0] / expected_total), "Hearing the context")
                        history.append((user(p), assistant(self._encode_audio(p.audio_path))))
                        continue
                    # The oldest turns go when the conversation outgrows the model's context (as generate_long
                    # would fail instead); the references stay.
                    while True:
                        conversation = Conversation([system] + [m for pair in history for m in pair] + [user(p)])
                        conversation.append(Message(role="assistant", parts=[], cal_loss=False, modality="voice",
                                                    add_im_start=True, add_im_end=False))
                        encoded, audio_masks, audio_parts = conversation.encode_for_inference(
                            self.model.tokenizer, num_codebooks=self.model.config.num_codebooks)
                        if encoded.size(1) <= max_length - 2048 or not history:
                            break
                        history.pop(0)
                    if encoded.size(1) > max_length - 2048:
                        raise ValueError("the reference clips and this segment are too long for S2 Pro")
                    prompt_length = encoded.size(1)
                    y = t2s.generate(
                        model=self.model,
                        prompt=encoded.to(device=self.device),
                        max_new_tokens=int(len(p.text or "") * TOKENS_PER_CHAR * 2.5) + 256,
                        audio_masks=audio_masks,
                        audio_parts=audio_parts,
                        decode_one_token=self.decode_one_token,
                        temperature=float(s.get("temperature", 0.8)),
                        top_p=float(s.get("topP", 0.8)),
                        top_k=30,
                    )
                    codes = y[1:, prompt_length:-1].clone()
                    del y, encoded
                    history.append((user(p), assistant(codes)))
                    if not p.is_context:
                        report(min(0.9, 0.05 + 0.85 * done_tokens[0] / expected_total), "Decoding")
                        audio[i] = np.asarray(self.engine.get_audio_segment(SimpleNamespace(codes=codes)),
                                              dtype=np.float32).reshape(-1)
        finally:
            t2s.tqdm = original_tqdm
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        codec = self.engine.decoder_model
        sr = int(getattr(getattr(codec, "spec_transform", None), "sample_rate", 0) or codec.sample_rate or SAMPLE_RATE)
        spans = [None] * len(parts)
        offset = 0
        for i in kept:
            n = len(audio[i])
            spans[i] = (round(offset * 1000 / sr), round((offset + n) * 1000 / sr))
            offset += n
        if offset == 0:
            raise RuntimeError("S2 Pro produced no audio (check the text)")
        duration_ms = write_wav(req.out_path, np.concatenate([audio[i] for i in kept]), sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sr, "spans": spans}


if __name__ == "__main__":
    run_worker(FishS2Worker(), name="fish_s2", model_version="s2-pro")
