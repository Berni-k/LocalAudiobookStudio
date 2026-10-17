"""MOSS-TTSD v1.0 dialogue worker (https://huggingface.co/OpenMOSS-Team/MOSS-TTSD-v1.0).

An 8B model that speaks a whole conversation of up to five voices in one go. The text carries ``[S1]`` … ``[S5]``
speaker tags. It clones in *continuation* mode: each speaker's reference clip and its transcript form a prompt (their
tags, texts and audio, in speaker order), and the model carries on in those voices with the dialogue. A speaker
without a clip gets an invented voice.

The app's fused generation (``generate_parts``) is the natural fit: each part is a turn, numbered by the order its
speaker first speaks. TTSD returns one audio for the whole dialogue and no spans, so the app transcribes it to cut it
into segments. A single segment (``generate``) is a dialogue of one turn.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common import moss  # noqa: E402
from common.protocol import (  # noqa: E402
    Unfusable, ensure_model, log, run_worker, seed_everything, torch_device, write_wav)

MODEL = "moss-ttsd-1.0"
CODEC = "moss-audio-tokenizer"
MAX_SPEAKERS = 5
DOUBLE_MAX_SECONDS = 12.0  # see _speak


class MossTtsdWorker:
    def __init__(self):
        self.device = "cpu"
        self.processor = None
        self.model = None
        self.refs = moss.ReferenceCache()
        self.joined = {}  # tuple of (path, mtime) -> codes of those clips joined

    def selftest(self):
        import torch
        import transformers

        log(f"torch {torch.__version__}, transformers {transformers.__version__}")

    def prepare(self, report):
        ensure_model(MODEL)
        ensure_model(CODEC)

    def load(self, report):
        self.device = torch_device()
        report(0.02, "Fetching the model")
        model_dir = ensure_model(MODEL)
        codec_dir = ensure_model(CODEC)
        report(0.05, "Loading MOSS-TTSD")
        self.processor, self.model = moss.load(model_dir, codec_dir, self.device)
        report(1.0, "Ready")

    def _joined_codes(self, clips):
        """Codec tokens of the speakers' clips one after the other (the assistant's prompt audio)."""
        import torch
        import torchaudio

        key = tuple((c, os.path.getmtime(c)) for c in clips)
        codes = self.joined.get(key)
        if codes is None:
            sr = int(self.processor.model_config.sampling_rate)
            wavs = []
            for clip in clips:
                wav, clip_sr = moss.read_clip(clip)
                wav = wav.mean(dim=0, keepdim=True)
                if clip_sr != sr:
                    wav = torchaudio.functional.resample(wav, clip_sr, sr)
                wavs.append(wav)
            codes = self.refs.encode_wav(self.processor, self.device, torch.cat(wavs, dim=-1), sr)
            if len(self.joined) > 16:
                self.joined.clear()
            self.joined[key] = codes
        return codes

    def _speak(self, turns, voices, req, report):
        """``turns``: [(speaker key, text)] in order. ``voices``: speaker key -> (clip path or "", transcript)."""
        order = []
        for key, _ in turns:
            if key not in order:
                order.append(key)
        if len(order) > MAX_SPEAKERS:
            raise Unfusable(f"MOSS-TTSD takes at most {MAX_SPEAKERS} voices in one conversation")
        number = {key: i + 1 for i, key in enumerate(order)}

        # Consecutive turns of one speaker are one turn.
        merged = []
        for key, text in turns:
            text = " ".join((text or "").split())
            if not text:
                continue
            if merged and merged[-1][0] == key:
                merged[-1] = (key, merged[-1][1] + " " + text)
            else:
                merged.append((key, text))
        if not merged:
            raise ValueError("empty text")
        dialogue = " ".join(f"[S{number[key]}] {text}" for key, text in merged)

        referenced = []  # (speaker number, clip, transcript), in speaker order
        for key in order:
            clip, transcript = voices.get(key, ("", ""))
            if not clip:
                continue
            if not transcript:
                raise ValueError("MOSS-TTSD needs the reference clip's transcript: fill in 'Reference transcript' "
                                 "in the speaker's settings.")
            referenced.append((number[key], clip, transcript))

        report(0.02, "Encoding reference clips")
        if referenced:
            prompt = list(referenced)
            # Upstream's prompt ends with a different speaker than the one who talks first. When the same voice
            # ends the prompt and starts the dialogue, a short reference can get replayed instead of followed
            # (seen: the clip's sentence three times). Giving the clip a second speaker number, as the voice that
            # ends the prompt, restores that layout. Clips over ~12 s are left alone: doubling them (50 s of prompt)
            # lowered the voice similarity.
            if prompt[-1][0] == number[merged[0][0]] and _clip_seconds(prompt[-1][1]) <= DOUBLE_MAX_SECONDS:
                prompt.append((len(order) + 1,) + prompt[-1][1:])
            codes = {n: self.refs.get(self.processor, self.device, clip) for n, clip, _ in prompt}
            reference = [codes.get(n) for n in range(1, max(n for n, _, _ in prompt) + 1)]
            prompt_audio = self._joined_codes([clip for _, clip, _ in prompt])
            prompt_text = " ".join(f"[S{n}] {_strip_tag(text)}" for n, _, text in prompt)
            conversation = [
                self.processor.build_user_message(text=f"{prompt_text} {dialogue}", reference=reference),
                self.processor.build_assistant_message(audio_codes_list=[prompt_audio]),
            ]
            mode = "continuation"
        else:
            conversation = [self.processor.build_user_message(text=dialogue)]
            mode = "generation"

        s = req.settings
        sampling = dict(
            audio_temperature=float(s.get("temperature", 1.1)),
            audio_top_p=float(s.get("topP", 0.9)),
            audio_top_k=int(s.get("topK", 50)),
            audio_repetition_penalty=float(s.get("repetitionPenalty", 1.1)),
            text_temperature=float(s.get("textTemperature", 1.1)),
        )
        chars = len(dialogue)
        max_new = min(16384, chars * 3 + 200)
        seed_everything(int(req.seed))
        audio, sr = moss.generate(self.processor, self.model, self.device, conversation, mode,
                                  max_new, max(40.0, float(chars)), report, **sampling)
        duration_ms = write_wav(req.out_path, audio.numpy(), sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sr}

    def generate(self, req, report):
        s = req.settings
        voices = {"s1": ((req.voice or "").strip(), _clean(s.get("refText")))}
        return self._speak([("s1", req.text)], voices, req, report)

    def generate_parts(self, req, report):
        """A conversation in one go: every part is a turn (context lines too: they are part of the conversation and
        cut away afterwards). No spans: the app transcribes the result to cut it."""
        voices = {}
        for p in req.parts:
            clip = (p.voice or "").strip()
            transcript = _clean(p.settings.get("refText"))
            if p.speaker not in voices or (clip and not voices[p.speaker][0]):
                voices[p.speaker] = (clip, transcript)
        return self._speak([(p.speaker, p.text) for p in req.parts], voices, req, report)


def _clip_seconds(path):
    import soundfile as sf

    info = sf.info(path)
    return info.frames / info.samplerate


def _clean(value):
    return " ".join(str(value or "").split())


def _strip_tag(text):
    """A transcript pasted with its own [S1] tag keeps just the words (the worker adds the tag)."""
    text = text.strip()
    if text.startswith("[S") and "]" in text[:6]:
        text = text[text.index("]") + 1:].strip()
    return text


if __name__ == "__main__":
    run_worker(MossTtsdWorker(), name="moss_ttsd", model_version="v1.0")
