"""MOSS-Transcribe-Diarize 0.9B (https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize).

One pass of speech recognition and speaker diarization: the model writes ``[start][S01]words[end]`` per segment
(seconds, anonymous speaker labels), for recordings up to about 30 minutes (see check_whole). The model's own code is
Hugging Face remote code; the GitHub package's helpers (message building, input preparation, the output parser) are a
few lines each and are redone here, so none of its dependencies (a web server, PyAV, numba …) are installed. Decoding
is our own, for speed and memory on long recordings (moss_decoding.py).

The model times segments, not words, so the worker is tagged Transcription and Diarization (no Alignment) and answers
``transcribeDiarize`` with the segments as they are: timed blocks of text, each with its voice (labelled S1, S2 …; the
model's own S01 is renumbered). ``diarize`` gets the same blocks, which the app reads as turns. The app compares their
words with the expected text itself, sharing a block's time among its words. For word times, give the speaker an
aligner (Speakers window, Alignment) and use this worker as the transcriber; the app tries the blocks first and
brings the aligner in when they don't line up with what it is looking for.
"""

import os
import re
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, frame_levels, read_wav  # noqa: E402
from common.protocol import Final, busy, ensure_model, log, run_worker, torch_device  # noqa: E402

MODEL = "moss-transcribe-diarize"  # workers/models.json

# The model's default prompt (Chinese, as trained); hotwords are appended the way upstream's recipes do.
PROMPT = ("请将音频转写为文本，每一段需以起始时间戳和说话人编号（[S01]、[S02]、[S03]…）开头，正文为对应的语音内容，"
          "并在段末标注结束时间戳，以清晰标明该段语音范围。")
HOTWORD_HINT = "热词提示："

_SEGMENT = re.compile(r"\[(\d+(?:\.\d+)?)\]\s*\[(S\d+)\](.*?)\[(\d+(?:\.\d+)?)\]", re.DOTALL)
_TIME = re.compile(r"\[(\d+(?:\.\d+)?)\]")
TOKENS_PER_SEC = 12  # most tokens the answer may take per second of audio (dense prose: about 8, with the tags)
# Upstream's own cap. The model itself ends its answer after 16,384 tokens wherever it is (upstream issue #26; 45
# minutes of prose stopped at 34:40 with exactly that many, the cap then 32,656); check_whole turns that into an error.
MAX_ANSWER_TOKENS = 65536
ENCODER_BATCH = 8    # 30 s chunks the Whisper encoder hears at once
PREFILL_SLICE = 2048  # prompt tokens fed to the language model at once
UNHEARD_LIMIT_SEC = 10  # more speech than this after the last block: the model stopped early


def check_whole(segments, audio) -> None:
    """Raises Final when the model stopped writing well before the speech ends. It does that on long recordings (it
    ends its answer after MAX_ANSWER_TOKENS, wherever it is), and a transcript that silently leaves out the rest is
    worse than none. The model answers the same way every time, so the app doesn't retry."""
    import numpy as np

    total = len(audio) / SAMPLE_RATE
    last = max((s["endMs"] for s in segments), default=0) / 1000
    levels = frame_levels(audio)
    after = levels[int(last * 50) + 50:]  # from a second after the last block
    speech = float(np.percentile(levels, 95)) if len(levels) else 0.0
    if speech <= 0 or len(after) == 0:
        return
    unheard = int((after > 0.1 * speech).sum()) / 50  # seconds of sound after the last block
    if unheard > UNHEARD_LIMIT_SEC:
        raise Final(f"MOSS-Transcribe-Diarize stopped writing at {clock_text(last)} of {clock_text(total)}; "
                    f"there are {clock_text(unheard)} of speech after that. It writes down about 30 minutes of speech "
                    "at most: cut the recording into parts of 30 minutes or less, or choose another transcriber.")


def clock_text(seconds: float) -> str:
    """A time in the recording for progress messages: "4:05", or "1:02:05" past an hour."""
    s = int(round(seconds))
    return f"{s // 3600}:{s // 60 % 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60}:{s % 60:02d}"


def parse_segments(raw: str, total_ms: int):
    """[{"text", "startMs", "endMs", "speaker"}] from the model's answer, within the audio."""
    out = []
    for m in _SEGMENT.finditer(raw):
        text = m.group(3).strip()
        if not text:
            continue
        start = max(0, int(round(float(m.group(1)) * 1000)))
        end = max(start, int(round(float(m.group(4)) * 1000)))
        if total_ms:
            start, end = min(start, total_ms), min(end, total_ms)
        out.append({"text": text, "startMs": start, "endMs": end, "speaker": "S" + str(int(m.group(2)[1:]))})  # S01 -> S1
    return out


class MossTranscribeWorker:
    def __init__(self):
        self.model_dir = None
        self.processor = None
        self.model = None
        self.device = "cpu"

    def selftest(self):
        import transformers

        major, minor = (int(p) for p in transformers.__version__.split(".")[:2])
        if (major, minor) < (5, 6):
            raise RuntimeError(f"MOSS-Transcribe-Diarize needs transformers 5.6 or newer, this is {transformers.__version__}.")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import torch
        from transformers import AutoModelForCausalLM, AutoProcessor

        import moss_decoding

        if self.model_dir is None:
            self.prepare(report)
        self.device = torch_device()
        dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        report(0.3, "Loading MOSS-Transcribe-Diarize")
        self.processor = AutoProcessor.from_pretrained(self.model_dir, trust_remote_code=True)
        self.model = AutoModelForCausalLM.from_pretrained(
            self.model_dir, trust_remote_code=True, dtype=dtype,
            attn_implementation=moss_decoding.ATTENTION).to(self.device).eval()
        eos = self.model.generation_config.eos_token_id
        self.eos = set(eos if isinstance(eos, (list, tuple)) else [eos])
        report(1.0, "Ready")
        log(f"MOSS-Transcribe-Diarize loaded on {self.device}")

    def _hear(self, req, report):
        """(the model's answer, the audio's length in ms)."""
        import torch

        audio = read_wav(req.audio_path)
        total_ms = int(len(audio) * 1000 / SAMPLE_RATE)
        if len(audio) < SAMPLE_RATE // 10:
            return "", total_ms
        hotwords = ", ".join(w.strip() for w in str(req.settings.get("hotwords") or "").split(",") if w.strip())
        prompt = PROMPT + (HOTWORD_HINT + hotwords if hotwords else "")
        messages = [{"role": "user", "content": [{"type": "audio", "audio": req.audio_path},
                                                 {"type": "text", "text": prompt}]}]
        text = self.processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
        audio_kwargs = {"device": "cuda"} if self.device == "cuda" else {}
        seconds = len(audio) / SAMPLE_RATE
        length = clock_text(seconds)
        clock = time.monotonic()
        with busy(report, 0.01, "Reading the audio"), torch.inference_mode():
            inputs = self.processor(text=text, audio=[audio], max_length=131072, audio_kwargs=audio_kwargs,
                                    return_tensors="pt").to(self.device)
        ids = inputs["input_ids"]
        log(f"{seconds:.0f} s of audio: features in {time.monotonic() - clock:.1f} s, {ids.shape[1]} prompt tokens")
        import moss_decoding

        # The model stops by itself; this only bounds the cache (dense prose takes about 8 tokens a second of audio).
        max_new = min(int(TOKENS_PER_SEC * seconds) + 256, MAX_ANSWER_TOKENS)
        decoder = moss_decoding.Decoder(self.model, ids.shape[1] + max_new, self.device)
        try:
            clock = time.monotonic()
            with torch.inference_mode():
                self._take_in(inputs, decoder, report, seconds)
            log(f"recording taken in: {time.monotonic() - clock:.1f} s")
            clock = time.monotonic()

            # Greedy, one token at a time. The progress is the latest time the model wrote, and report() raises when
            # the job is cancelled.
            tokenizer = self.processor.tokenizer
            written, tail, reached = [], "", 0.0
            token, position = int(ids[0, -1]), ids.shape[1] - 1
            with torch.inference_mode():
                while len(written) < max_new:
                    token = decoder.step(token, position)
                    position += 1
                    if token in self.eos:
                        break
                    written.append(token)
                    tail = (tail + tokenizer.decode([token], skip_special_tokens=True))[-48:]
                    times = _TIME.findall(tail)
                    if times:
                        reached = max(reached, min(seconds, float(times[-1])))
                    report(0.1 + 0.85 * reached / max(seconds, 1.0), f"Writing down what was said, {clock_text(reached)} of {length}")
            spent = time.monotonic() - clock
            log(f"{len(written)} tokens in {spent:.1f} s ({len(written) / max(spent, 1e-3):.0f} a second)")
        finally:
            decoder.close()
        return tokenizer.decode(written, skip_special_tokens=True).strip(), total_ms

    def _take_in(self, inputs, decoder, report, seconds):
        """Fills the decoder's cache with the whole prompt but its last token: the audio encoded a few 30 s chunks at
        a time, then the prompt fed in slices. Done in one go (as generate() does it) the attention of a long recording
        takes far more memory than the card has (15 minutes: 24 GB and spilling into shared memory, crawling); this way
        it grows with the length (10 minutes: 3.4 GB), and the progress can move while it happens."""
        import torch

        m = self.model.model
        length = clock_text(seconds)
        features, lengths = inputs["input_features"], inputs["audio_feature_lengths"].tolist()
        dtype = next(m.whisper_encoder.parameters()).dtype
        chunks = features.shape[0]
        heard = []
        for first in range(0, chunks, ENCODER_BATCH):
            report(0.02 + 0.04 * first / chunks, f"Hearing the audio, {clock_text(min(seconds, first * 30))} of {length}")
            hidden = m.whisper_encoder(features[first:first + ENCODER_BATCH].to(dtype), return_dict=True).last_hidden_state
            for j, n in enumerate(lengths[first:first + ENCODER_BATCH]):
                heard.append(hidden[j:j + 1, : int(n) * 4])
        audio_embeds = m.vq_adaptor(m.time_merge(torch.cat(heard, dim=1).to(m.dtype))).squeeze(0)

        ids = inputs["input_ids"][:, :-1]
        embeds = m.get_input_embeddings()(ids)
        mask = (ids == self.model.config.audio_token_id).unsqueeze(-1).expand_as(embeds)
        embeds = embeds.masked_scatter(mask, audio_embeds.to(embeds.dtype))
        decoder.prefill(embeds, PREFILL_SLICE, lambda done, total: report(
            0.06 + 0.04 * done / total, f"Taking in the recording, {clock_text(seconds * done / total)} of {length}"))

    def transcribe_diarize(self, req, report):
        """The segments as the model timed them. An answer without tags (the model doesn't always follow the
        format) is still the words it heard: one block over the whole recording."""
        raw, total_ms = self._hear(req, report)
        segments = parse_segments(raw, total_ms)
        if not segments and raw.strip():
            segments = [{"text": raw.strip(), "startMs": 0, "endMs": total_ms, "speaker": "S1"}]
        check_whole(segments, read_wav(req.audio_path))
        report(1.0)
        return {"text": " ".join(s["text"] for s in segments), "segments": segments, "language": None}


if __name__ == "__main__":
    run_worker(MossTranscribeWorker(), name="moss_transcribe", model_version="v1")
