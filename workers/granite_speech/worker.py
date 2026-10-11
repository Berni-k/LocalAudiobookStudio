"""IBM Granite Speech 4.1 2B Plus through transformers: speech recognition with word timings.

The model is a Conformer encoder, a Q-Former and a Granite 1B language model; what it does is chosen by the prompt.
Two prompts are used here:

- ``transcribe``: plain recognition. One pass of text, several times faster than real time.
- ``transcribe_align``: "Timestamps". After every word the model writes ``[T:n]``, n being the word's end in
  centiseconds modulo 1000 (so the count wraps every 10 s), and ``_`` for a stretch of silence. A word starts where the
  word or silence before it ended. The model writes every token itself, so this runs about as long as the audio does.

Takes longer than a minute are cut at their quietest points and recognised in pieces (the model was trained on up to
5 minutes with timestamps, 10 without, and is slow to decode).

The model speaks out numbers, dates and money ("three forty five", "forty two dollars") in timestamp mode and writes no
punctuation; the plain mode writes numerals and some punctuation. The take's text is never passed to the model.
"""

import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, quiet_pieces, read_wav  # noqa: E402
from common.protocol import ensure_model, log, run_worker, torch_device  # noqa: E402

MODEL = "granite-speech-4.1-2b-plus"  # workers/models.json

TEXT_PROMPT = "can you transcribe the speech into a written format?"
TIMESTAMP_PROMPT = ("Timestamps: Transcribe the speech. After each word, add a timestamp tag showing the end time "
                    "in centiseconds, e.g. hello [T:45] world [T:82]")

# Piece lengths in seconds. Timestamp mode decodes about 13 tokens per second of speech, text mode a quarter of that.
# The model's times lag further behind the audio the longer a piece goes on (+0.25 s after 20 s in a 44 s take, against
# a forced aligner), and pieces of 10 to 20 s keep the lag near 0.05 s; shorter ones were no better.
TEXT_PIECE_SEC = (60.0, 120.0)
WORDS_PIECE_SEC = (10.0, 20.0)
TOKENS_PER_SEC = 13
_TAG = re.compile(r"([^\s\[]+)\s*\[T:(\d+)\]")


def parse_words(raw: str, offset_sec: float):
    """The words of a timestamp-mode answer, with times in the whole file: ``_ [T:11] the [T:22]`` -> the 0.11-0.22."""
    words, wraps, previous = [], 0, 0.0
    for text, count in _TAG.findall(raw):
        end = int(count) / 100
        while end + wraps < previous:  # the count wrapped past 9.99 s
            wraps += 10
        end += wraps
        if text != "_":
            words.append({"text": text, "startMs": int(round((offset_sec + previous) * 1000)),
                          "endMs": int(round((offset_sec + end) * 1000))})
        previous = end
    return words


class GraniteSpeechWorker:
    def __init__(self):
        self.model_dir = None
        self.processor = None
        self.model = None
        self.device = "cpu"

    def selftest(self):
        import transformers

        major, minor = (int(p) for p in transformers.__version__.split(".")[:2])
        if (major, minor) < (5, 8):
            raise RuntimeError(f"Granite Speech 4.1 Plus needs transformers 5.8 or newer, this is {transformers.__version__}.")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import torch
        from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor

        if self.model_dir is None:
            self.prepare(report)
        self.device = torch_device()
        report(0.3, "Loading Granite Speech 4.1 2B Plus")
        self.processor = AutoProcessor.from_pretrained(self.model_dir)
        self.model = AutoModelForSpeechSeq2Seq.from_pretrained(
            self.model_dir, dtype=torch.bfloat16 if self.device == "cuda" else torch.float32).to(self.device).eval()
        report(1.0, "Ready")
        log(f"Granite Speech 4.1 2B Plus loaded on {self.device}")

    def transcribe(self, req, report):
        audio = read_wav(req.audio_path)
        texts = self._pieces(audio, TEXT_PROMPT, TEXT_PIECE_SEC, TOKENS_PER_SEC // 4, req, report)
        report(1.0)
        return {"text": " ".join(t.strip() for _, t in texts if t.strip()), "language": None}

    def transcribe_align(self, req, report):
        audio = read_wav(req.audio_path)
        words = []
        for begin, raw in self._pieces(audio, TIMESTAMP_PROMPT, WORDS_PIECE_SEC, TOKENS_PER_SEC, req, report):
            words.extend(parse_words(raw, begin / SAMPLE_RATE))
        report(1.0)
        return {"text": " ".join(w["text"] for w in words), "words": words, "language": None}

    def _pieces(self, audio, prompt, piece_sec, tokens_per_sec, req, report):
        """[(start sample, the model's answer)] for each piece of the audio."""
        if len(audio) < SAMPLE_RATE // 10:
            return []
        pieces = quiet_pieces(audio, *piece_sec)
        answers = []
        for i, (begin, end) in enumerate(pieces):
            label = "Listening" if len(pieces) == 1 else f"Listening (part {i + 1} of {len(pieces)})"

            def progress(fraction, i=i, label=label):
                report((i + fraction) / len(pieces), label)

            answers.append((begin, self._ask(audio[begin:end], prompt, tokens_per_sec, progress)))
        return answers

    def _ask(self, audio, prompt, tokens_per_sec, progress):
        import torch
        from transformers import StoppingCriteria, StoppingCriteriaList

        seconds = len(audio) / SAMPLE_RATE
        chat = [{"role": "user", "content": "<|audio|>" + prompt}]
        text = self.processor.tokenizer.apply_chat_template(chat, tokenize=False, add_generation_prompt=True)
        inputs = self.processor(text, torch.from_numpy(audio)[None, :], device=self.device, return_tensors="pt").to(self.device)
        start = inputs["input_ids"].shape[1]
        expected = max(tokens_per_sec * seconds, 1)

        class Progress(StoppingCriteria):
            """Called after every token: moves the progress bar, and report() raises when the job is cancelled."""

            def __call__(self, input_ids, scores, **kwargs):
                progress(min((input_ids.shape[1] - start) / expected, 0.95))
                return torch.zeros(input_ids.shape[0], dtype=torch.bool, device=input_ids.device)

        with torch.inference_mode():
            output = self.model.generate(**inputs, max_new_tokens=int(expected * 4) + 200, do_sample=False, num_beams=1,
                                         stopping_criteria=StoppingCriteriaList([Progress()]))
        return self.processor.tokenizer.decode(output[0, start:], skip_special_tokens=True)


if __name__ == "__main__":
    run_worker(GraniteSpeechWorker(), name="granite_speech", model_version="v1")
