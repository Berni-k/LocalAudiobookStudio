"""MMS forced aligner on ONNX Runtime: finds when each word of a known text is spoken.

MMS-300M-FA (the model of torchaudio's MMS_FA) exported to ONNX by tatsu020
(https://huggingface.co/tatsu020/mms-300m-fa-onnx). The model gives, for every 20 ms of audio, the probability of each
letter a-z, the apostrophe, or "blank" (CTC). The Viterbi path that spells the text through those frames is the
alignment; the built-in runtime (src/LocalAudiobookStudio.InternalWorker/CtcAlignEngine.cs) does the same.

One script for both ONNX runtimes: the onnx1_30-cpu environment has the CPU build of onnxruntime, the onnx1_30-cu13
environment the CUDA build (NVIDIA's libraries come as packages and are loaded with onnxruntime.preload_dlls()).
"""

import json
import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, read_wav  # noqa: E402
from common.protocol import Cancelled, ensure_model, log, run_worker  # noqa: E402

MODEL = "mms-300m-fa-onnx"  # workers/models.json
FRAME_SAMPLES = 320      # one output frame: 20 ms
FRAME_SEC = 0.02
RECEPTIVE_FIELD = 400    # samples one frame looks at; n samples give (n - 400) // 320 + 1 frames
WINDOW = 1500 * FRAME_SAMPLES   # 30 s, a whole number of frames
CONTEXT = 100 * FRAME_SAMPLES   # 2 s on each side that is run but not kept: a window's edges are the model's worst frames
MAX_TRELLIS = 1_500_000_000     # bytes of Viterbi back-pointers (about 8 minutes of speech)

# Letters with accents and the few that are spelled with two, as the a-z the model knows. A table rather than Unicode
# normalisation so that this and the built-in runtime (CtcAlignEngine.cs, the same table) spell alike.
_FOLDED = {}
for _letters, _accented in [
    ("a", "àáâãäåāăą"), ("c", "çćĉċč"), ("d", "ďđð"), ("e", "èéêëēĕėęě"), ("g", "ĝğġģ"), ("h", "ĥ"),
    ("i", "ìíîïĩīĭįı"), ("j", "ĵ"), ("k", "ķ"), ("l", "ĺļľł"), ("n", "ñńņň"), ("o", "òóôõöōŏőø"),
    ("r", "ŕŗř"), ("s", "śŝşš"), ("t", "ţť"), ("u", "ùúûüũūŭůűų"), ("w", "ŵ"), ("y", "ýÿŷ"), ("z", "źżž"),
    ("ss", "ß"), ("ae", "æ"), ("oe", "œ"), ("th", "þ"),
]:
    for _c in _accented:
        _FOLDED[_c] = _letters


def spell(word):
    """A word as the model's letters: lower case, accents dropped, and the few letters that are two sounds spelled out;
    anything else (digits, punctuation, other scripts) has no letter. Apostrophes stay inside a word only. Kept in step
    with Spell in CtcAlignEngine.cs."""
    out = []
    for ch in word:
        c = ch.lower() if len(ch.lower()) == 1 else ch
        if "a" <= c <= "z":
            out.append(c)
        elif c in "'’‘ʼ":
            out.append("'")
        elif c in _FOLDED:
            out.append(_FOLDED[c])
    return "".join(out).strip("'")


def _cuda_build():
    """True in the CUDA environment (onnxruntime-gpu). Loads NVIDIA's pip-installed libraries first."""
    import onnxruntime as ort

    if "CUDAExecutionProvider" not in ort.get_available_providers():
        return False
    if hasattr(ort, "preload_dlls"):
        ort.preload_dlls()
    return True


class MmsFaWorker:
    # The protocol's default self-test would import torch to check CUDA; this environment has none.
    requires_cuda = False

    def __init__(self):
        self.model_dir = None
        self.session = None
        self.vocab = {}
        self.blank = 0
        self.tokens = 0
        self.device = "cpu"

    def selftest(self):
        import onnxruntime as ort

        cuda = _cuda_build()
        log(f"onnxruntime {ort.__version__}, providers {ort.get_available_providers()}")
        if "cuda" in os.environ.get("LAS_DEVICES", "") and not cuda:
            raise RuntimeError("This is the CUDA runtime, but onnxruntime has no CUDA provider (the CPU build got installed?).")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import onnxruntime as ort

        if self.model_dir is None:
            self.prepare(report)
        report(0.1, "Loading the MMS forced aligner (ONNX)")
        cuda = _cuda_build() and os.environ.get("LAS_DEVICE") != "cpu"
        options = ort.SessionOptions()
        threads = os.environ.get("LAS_CPU_THREADS")
        if threads and threads.isdigit() and int(threads) > 0:
            options.intra_op_num_threads = int(threads)
        # CUDA_VISIBLE_DEVICES already pins the app's chosen GPU, so it is device 0 here.
        providers = ([("CUDAExecutionProvider", {"device_id": 0})] if cuda else []) + ["CPUExecutionProvider"]
        self.session = ort.InferenceSession(os.path.join(self.model_dir, "mms_300m_fa_fp16.onnx"), options, providers=providers)
        if cuda and "CUDAExecutionProvider" not in self.session.get_providers():
            raise RuntimeError("CUDA could not be used (ONNX Runtime 1.30 needs an NVIDIA driver with CUDA 13, 580 or newer).")
        with open(os.path.join(self.model_dir, "vocab.json"), encoding="utf-8") as f:
            data = json.load(f)
        # Only the single letters are spelled with.
        self.vocab = {k: v for k, v in data["vocab"].items() if len(k) == 1}
        self.blank = data.get("blankTokenId", 0)
        self.tokens = max(data["vocab"].values()) + 1
        self.device = "cuda" if cuda else "cpu"
        report(1.0, "Ready")
        log(f"MMS forced aligner loaded on {self.device}, providers {self.session.get_providers()}")

    def align(self, req, report):
        if not (req.text or "").strip():
            raise ValueError("this worker aligns a known text: the request has none")
        audio = read_wav(req.audio_path)
        words = req.text.split()
        spelled = [spell(w) for w in words]
        if not any(spelled):
            raise ValueError("The text has no letters this model can align (it knows a to z; numbers and other scripts have no sound for it).")
        heard = []
        if len(audio) >= SAMPLE_RATE // 10:
            emissions = self._emissions(audio, req, report)
            report(0.95, "Aligning")
            heard = self._align(words, spelled, emissions, req)
        words_out = self._finish(heard)
        report(1.0)
        return {"text": " ".join(w["text"] for w in words_out), "words": words_out, "language": None}

    def _emissions(self, audio, req, report):
        """Log-probabilities of every frame of the audio: [frames, tokens]. The model runs on windows of 30 s with 2 s
        of audio around each (not kept), because it attends over its whole input."""
        import numpy as np

        frames = 0 if len(audio) < RECEPTIVE_FIELD else (len(audio) - RECEPTIVE_FIELD) // FRAME_SAMPLES + 1
        result = np.zeros((frames, self.tokens), dtype=np.float32)
        windows = (len(audio) + WINDOW - 1) // WINDOW
        input_name = self.session.get_inputs()[0].name
        for w in range(windows):
            if req.cancelled:
                raise Cancelled()
            report(0.05 + 0.9 * w / windows, "Listening" if windows == 1 else f"Listening (part {w + 1} of {windows})")
            keep_start = w * WINDOW
            run_start = max(0, keep_start - CONTEXT)
            run_end = min(len(audio), keep_start + WINDOW + CONTEXT)
            if run_end - run_start < RECEPTIVE_FIELD:
                continue
            # Zero mean and unit variance per run: what the model was trained with.
            run = audio[run_start:run_end]
            run = ((run - run.mean()) / np.sqrt(run.var() + 1e-7)).astype(np.float32)
            logits = self.session.run(None, {input_name: run[None, :]})[0][0].astype(np.float32)
            logits -= logits.max(axis=1, keepdims=True)
            logits -= np.log(np.exp(logits).sum(axis=1, keepdims=True))
            first_global = run_start // FRAME_SAMPLES
            keep_from = keep_start // FRAME_SAMPLES
            keep_to = frames if w == windows - 1 else min(frames, (keep_start + WINDOW) // FRAME_SAMPLES)
            lo, hi = keep_from - first_global, min(keep_to - first_global, len(logits))
            if hi > lo:
                result[keep_from:keep_from + (hi - lo)] = logits[lo:hi]
        return result

    def _align(self, words, spelled, lp, req):
        """The Viterbi path through the frames that spells the text (CTC: letters and blanks, a letter may last several
        frames, a blank must separate a letter from the same letter after it). A word starts at its first letter's first
        frame and ends after its last letter's last frame. A word without letters (a number, a dash) is placed where the
        word before it ends."""
        import numpy as np

        tokens, word_of = [], []
        for i, s in enumerate(spelled):
            for c in s:
                if c in self.vocab:
                    tokens.append(self.vocab[c])
                    word_of.append(i)
        n = len(tokens)
        frames = len(lp)
        states = 2 * n + 1  # blank, letter, blank, letter, ... blank
        if frames * states > MAX_TRELLIS:
            raise ValueError("This take and its text are too long to align in one go.")
        if frames < n:
            raise ValueError("The audio is too short for the text.")

        ext = np.full(states, self.blank, dtype=np.int64)
        ext[1::2] = tokens
        # A letter can follow the letter before the blank between them, unless it's the same letter.
        skip_ok = np.zeros(states, dtype=bool)
        skip_ok[2:] = (ext[2:] != self.blank) & (ext[2:] != ext[:-2])
        neg = -np.inf
        back = np.zeros((frames, states), dtype=np.uint8)
        score = np.full(states, neg, dtype=np.float32)
        score[0] = lp[0, self.blank]
        score[1] = lp[0, ext[1]]
        for t in range(1, frames):
            if req.cancelled:
                raise Cancelled()
            stay = score
            step = np.concatenate(([neg], score[:-1]))
            skip = np.where(skip_ok, np.concatenate(([neg, neg], score[:-2])), neg)
            choices = np.stack((stay, step, skip))
            pick = choices.argmax(axis=0)
            back[t] = pick
            score = choices[pick, np.arange(states)] + lp[t, ext]
        state = states - 1
        if states > 1 and score[states - 2] > score[states - 1]:
            state = states - 2
        if not np.isfinite(score[state]):
            raise ValueError("The audio is too short for the text.")

        first = [None] * len(words)
        last = [None] * len(words)
        letter_logprob = [neg] * n
        for t in range(frames - 1, -1, -1):
            if state & 1:
                letter = state // 2
                w = word_of[letter]
                first[w] = t if first[w] is None else min(first[w], t)
                last[w] = t if last[w] is None else max(last[w], t)
                letter_logprob[letter] = max(letter_logprob[letter], float(lp[t, ext[state]]))
            state -= int(back[t, state])

        counts = [0] * len(words)
        for w in word_of:
            counts[w] += 1
        heard, previous_end, letter0 = [], 0, 0
        for i, word in enumerate(words):
            if counts[i] == 0:
                heard.append({"text": word, "start": previous_end, "end": previous_end, "confidence": 0.0})
                continue
            mean = sum(letter_logprob[letter0:letter0 + counts[i]]) / counts[i]
            letter0 += counts[i]
            start = int(round(first[i] * FRAME_SEC * 1000))
            end = int(round((last[i] + 1) * FRAME_SEC * 1000))
            heard.append({"text": word, "start": start, "end": end, "confidence": math.exp(mean)})
            previous_end = end
        return heard

    @staticmethod
    def _finish(heard):
        """Words in order that don't overlap."""
        words = []
        for i, w in enumerate(heard):
            following = heard[i + 1]["start"] if i + 1 < len(heard) else None
            end = w["end"] if following is None else min(w["end"], following)
            words.append({"text": w["text"], "startMs": w["start"], "endMs": max(w["start"], end),
                          "confidence": round(w["confidence"], 4)})
        return words


if __name__ == "__main__":
    run_worker(MmsFaWorker(), name="mms_fa", model_version="v1")
