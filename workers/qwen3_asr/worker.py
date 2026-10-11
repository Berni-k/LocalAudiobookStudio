"""Qwen3-ASR 0.6B and Qwen3-ForcedAligner 0.6B through transformers: speech recognition with word timings.

Two models, one worker. The ASR model (an audio encoder and a Qwen3 language model) writes the text and names the
language. The aligner has the same build with a classification head instead of the language head: given the audio and
the words, it predicts for each word's start and end the 80 ms step it falls on, in one pass without decoding. The
aligner takes any text, so a ``transcribe`` request loads only the ASR model and a ``transcribeAlign`` request both.

Both are built into transformers 5.13 and newer, from the ``-hf`` checkpoints. The ``qwen-asr`` package is not used: it
pins transformers 4.57, and its own copy of the model code doesn't run on 5.

The aligner supports 11 languages and up to 5 minutes of audio; takes are cut at their quietest points into pieces of
60 to 120 s. It drops punctuation and splits words its own way (a hyphenated word is one word, Chinese is one word per
character, Japanese and Korean are split by nagisa and soynlp); words are given back as the text wrote them where the
two split alike. Its times come in 80 ms steps, and some words get none (start equals end): those are widened.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, quiet_pieces, read_wav  # noqa: E402
from common.protocol import ensure_model, log, run_worker, torch_device  # noqa: E402

ASR_MODEL = "qwen3-asr-0.6b"                # workers/models.json
ALIGNER_MODEL = "qwen3-forced-aligner-0.6b"
PIECE_SEC = (60.0, 120.0)
# Tokens per second of speech to allow, with room for dense languages (Chinese is about 4 per second, English 3).
TOKENS_PER_SEC = 12
MIN_WORD_MS = 40
ALIGN_MAX_SEC = 300          # the aligner takes 5 minutes at most
LANGUAGE_PROBE_SEC = 30      # align with language Auto: the language is heard from this much of the start


class Qwen3AsrWorker:
    def __init__(self):
        self.asr_dir = None
        self.aligner_dir = None
        self.asr_processor = self.asr = None
        self.aligner_processor = self.aligner = None
        self.device = "cpu"
        self.dtype = None

    def selftest(self):
        import transformers

        major, minor = (int(p) for p in transformers.__version__.split(".")[:2])
        if (major, minor) < (5, 13):
            raise RuntimeError(f"Qwen3-ASR needs transformers 5.13 or newer, this is {transformers.__version__}.")

    def prepare(self, report):
        self.asr_dir = ensure_model(ASR_MODEL)
        self.aligner_dir = ensure_model(ALIGNER_MODEL)

    def load(self, report):
        # The models load when first needed: text-only requests never touch the aligner.
        import torch

        if self.asr_dir is None:
            self.prepare(report)
        self.device = torch_device()
        self.dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        self._load_asr(report)
        report(1.0, "Ready")

    def _load_asr(self, report):
        from transformers import AutoModelForMultimodalLM, AutoProcessor

        if self.asr is not None:
            return
        report(0.3, "Loading Qwen3-ASR 0.6B")
        self.asr_processor = AutoProcessor.from_pretrained(self.asr_dir)
        self.asr = AutoModelForMultimodalLM.from_pretrained(self.asr_dir, dtype=self.dtype).to(self.device).eval()
        log(f"Qwen3-ASR 0.6B loaded on {self.device}")

    def _load_aligner(self, report):
        from transformers import AutoModelForTokenClassification, AutoProcessor

        if self.aligner is not None:
            return
        report(0.0, "Loading the Qwen3 forced aligner")
        self.aligner_processor = AutoProcessor.from_pretrained(self.aligner_dir)
        self.aligner = AutoModelForTokenClassification.from_pretrained(self.aligner_dir, dtype=self.dtype).to(self.device).eval()
        log(f"Qwen3 forced aligner loaded on {self.device}")

    def transcribe(self, req, report):
        audio = read_wav(req.audio_path)
        pieces = self._listen(audio, req, report, 1.0)
        report(1.0)
        return {"text": _join(text for _, _, text, _ in pieces), "language": pieces[0][3] if pieces else None}

    def transcribe_align(self, req, report):
        audio = read_wav(req.audio_path)
        pieces = self._listen(audio, req, report, 0.8)
        words = []
        todo = [p for p in pieces if p[2]]
        for i, (begin, end, text, language) in enumerate(todo):
            report(0.8 + 0.2 * i / len(todo), "Timing the words")
            words.extend(self._align(audio[begin:end], text, language, begin / SAMPLE_RATE, report))
        report(1.0)
        return {"text": " ".join(w["text"] for w in words), "words": _tidy(words), "language": pieces[0][3] if pieces else None}

    def align(self, req, report):
        """Times the words of ``req.text`` in the audio as they are written (forced alignment): the aligner alone, without
        hearing what was said. Used as another transcription worker's aligner."""
        audio = read_wav(req.audio_path)
        text = " ".join((req.text or "").split())
        if len(audio) > ALIGN_MAX_SEC * SAMPLE_RATE:
            raise ValueError(f"the Qwen3 forced aligner takes at most {ALIGN_MAX_SEC // 60} minutes of audio")
        language = str(req.settings.get("language") or "Auto")
        if language == "Auto":
            # The aligner needs the language: hear it from the start of the audio.
            heard = self._listen(audio[:LANGUAGE_PROBE_SEC * SAMPLE_RATE], req, report, 0.4)
            language = heard[0][3] if heard else None
        words = _tidy(self._align(audio, text, language, 0.0, report)) if text else []
        report(1.0)
        return {"text": text, "words": words, "language": language}

    def _listen(self, audio, req, report, share):
        """[(start sample, end sample, text, language code)] for each piece of the audio, with the progress bar up to
        ``share`` of its length."""
        import torch
        from transformers import StoppingCriteria, StoppingCriteriaList

        if len(audio) < SAMPLE_RATE // 10:
            return []
        self._load_asr(report)
        setting = str(req.settings.get("language") or "Auto")
        pieces = quiet_pieces(audio, *PIECE_SEC)
        heard = []
        for i, (begin, end) in enumerate(pieces):
            label = "Listening" if len(pieces) == 1 else f"Listening (part {i + 1} of {len(pieces)})"
            inputs = self.asr_processor.apply_transcription_request(
                audio=audio[begin:end], language=None if setting == "Auto" else setting).to(self.device, self.dtype)
            start = inputs["input_ids"].shape[1]
            expected = max(4 * (end - begin) / SAMPLE_RATE, 1)

            def progress(fraction, i=i, label=label):
                report(share * (i + fraction) / len(pieces), label)

            class Progress(StoppingCriteria):
                """Called after every token: moves the progress bar, and report() raises when the job is cancelled."""

                def __call__(self, input_ids, scores, **kwargs):
                    progress(min((input_ids.shape[1] - start) / expected, 0.95))
                    return torch.zeros(input_ids.shape[0], dtype=torch.bool, device=input_ids.device)

            with torch.inference_mode():
                ids = self.asr.generate(**inputs, max_new_tokens=int(TOKENS_PER_SEC * (end - begin) / SAMPLE_RATE) + 128,
                                        do_sample=False, stopping_criteria=StoppingCriteriaList([Progress()]))
            parsed = self.asr_processor.decode(ids[:, start:], return_format="parsed")[0]
            text = (parsed.get("transcription") or "").strip()
            heard.append((begin, end, text, _language_code(parsed.get("language")) if setting == "Auto" else setting))
        return heard

    def _align(self, audio, text, language, offset_sec, report):
        import torch

        name = _language_name(language)
        if name not in _aligner_languages():
            raise RuntimeError(f"The Qwen3 forced aligner has no times for {name or 'this language'} "
                               "(it knows Chinese, English, Cantonese, French, German, Italian, Japanese, Korean, "
                               "Portuguese, Russian and Spanish). Pick a language in the settings, or another worker.")
        self._load_aligner(report)
        inputs, word_lists = self.aligner_processor.prepare_forced_aligner_inputs(audio=audio, transcript=text, language=name)
        inputs = inputs.to(self.device, self.dtype)
        with torch.inference_mode():
            logits = self.aligner(**inputs).logits
        timed = self.aligner_processor.decode_forced_alignment(
            logits=logits, input_ids=inputs["input_ids"], word_lists=word_lists,
            timestamp_token_id=self.aligner.config.timestamp_token_id)[0]
        shown = _as_written(text, [t["text"] for t in timed])
        return [{"text": shown[i], "startMs": int(round((offset_sec + t["start_time"]) * 1000)),
                 "endMs": int(round((offset_sec + t["end_time"]) * 1000))} for i, t in enumerate(timed)]


def _join(texts):
    return " ".join(t for t in texts if t)


def _names():
    from transformers.models.qwen3_asr.processing_qwen3_asr import LANGUAGE_CODE_TO_NAME

    return LANGUAGE_CODE_TO_NAME


def _aligner_languages():
    from transformers.models.qwen3_asr.processing_qwen3_asr import FORCED_ALIGNER_LANGUAGES

    return FORCED_ALIGNER_LANGUAGES


def _language_name(language):
    """"en" or "English" -> "English"; None when it isn't one the model knows."""
    if not language:
        return None
    names = _names()
    if language in names:
        return names[language]
    return language if language in names.values() else None


def _language_code(language):
    """The model names the language it heard ("English"); the app uses codes."""
    for code, name in _names().items():
        if name.lower() == str(language or "").lower():
            return code
    return None


def _as_written(text, tokens):
    """The aligner's words as the transcript wrote them (with punctuation and hyphens) when splitting the text at
    spaces gives the same words as the aligner's, else the aligner's own."""
    written = [w for w in text.split() if any(ch.isalnum() or ch == "'" for ch in w)]
    if len(written) == len(tokens) and all(_squash(w) == t for w, t in zip(written, tokens)):
        return written
    return tokens


def _squash(word):
    from transformers.models.qwen3_asr.processing_qwen3_asr import _is_kept_char

    return "".join(ch for ch in word if _is_kept_char(ch))


def _tidy(words):
    """Words in order without overlaps, none shorter than MIN_WORD_MS: the aligner gives some words no time at all."""
    previous_end = 0
    for i, w in enumerate(words):
        w["startMs"] = max(w["startMs"], previous_end)
        if w["endMs"] < w["startMs"] + MIN_WORD_MS:
            # Room before the word first (the word before it ended earlier), else after.
            w["startMs"] = max(previous_end, min(w["startMs"], w["endMs"] - MIN_WORD_MS))
            w["endMs"] = max(w["endMs"], w["startMs"] + MIN_WORD_MS)
        previous_end = w["endMs"]
    return words


if __name__ == "__main__":
    run_worker(Qwen3AsrWorker(), name="qwen3_asr", model_version="v1")
