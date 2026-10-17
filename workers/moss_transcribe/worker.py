"""MOSS-Transcribe-Diarize 0.9B (https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize).

One pass of speech recognition and speaker diarization: the model writes ``[start][S01]words[end]`` per segment
(seconds, anonymous speaker labels), for recordings up to 90 minutes. The model's own code is Hugging Face remote
code; the GitHub package's helpers (message building, input preparation, generation, the output parser) are a few
lines each and are redone here, so none of its dependencies (a web server, PyAV, numba …) are installed.

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

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, read_wav  # noqa: E402
from common.protocol import ensure_model, log, run_worker, torch_device  # noqa: E402

MODEL = "moss-transcribe-diarize"  # workers/models.json

# The model's default prompt (Chinese, as trained); hotwords are appended the way upstream's recipes do.
PROMPT = ("请将音频转写为文本，每一段需以起始时间戳和说话人编号（[S01]、[S02]、[S03]…）开头，正文为对应的语音内容，"
          "并在段末标注结束时间戳，以清晰标明该段语音范围。")
HOTWORD_HINT = "热词提示："

_SEGMENT = re.compile(r"\[(\d+(?:\.\d+)?)\]\s*\[(S\d+)\](.*?)\[(\d+(?:\.\d+)?)\]", re.DOTALL)
TOKENS_PER_SEC = 10  # speech is a few words a second, plus the tags; the cap below is generous


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

        if self.model_dir is None:
            self.prepare(report)
        self.device = torch_device()
        dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        report(0.3, "Loading MOSS-Transcribe-Diarize")
        self.processor = AutoProcessor.from_pretrained(self.model_dir, trust_remote_code=True)
        self.model = AutoModelForCausalLM.from_pretrained(
            self.model_dir, trust_remote_code=True, dtype=dtype, attn_implementation="sdpa").to(self.device).eval()
        report(1.0, "Ready")
        log(f"MOSS-Transcribe-Diarize loaded on {self.device}")

    def _hear(self, req, report):
        """(the model's answer, the audio's length in ms)."""
        import torch
        from transformers.generation.streamers import BaseStreamer

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
        dtype = next(self.model.parameters()).dtype
        with torch.inference_mode(), (torch.autocast("cuda", dtype=dtype) if self.device == "cuda" else torch.no_grad()):
            inputs = self.processor(text=text, audio=[audio], max_length=131072, audio_kwargs=audio_kwargs,
                                    return_tensors="pt").to(self.device)
        prompt_len = int(inputs["attention_mask"][0].sum().item())
        seconds = len(audio) / SAMPLE_RATE
        expected = max(TOKENS_PER_SEC * seconds, 16.0)

        class Progress(BaseStreamer):
            """Called with every token: moves the progress bar, and report() raises when the job is cancelled."""

            def __init__(self):
                self.count, self.seen_prompt = 0, False

            def put(self, value):
                if not self.seen_prompt:
                    self.seen_prompt = True
                    return
                self.count += int(value.numel()) if hasattr(value, "numel") else 1
                report(min(0.95, 0.05 + 0.9 * self.count / expected), "Listening")

            def end(self):
                return None

        report(0.05, "Listening")
        generation_config = type(self.model.generation_config)(**self.model.generation_config.to_dict())
        generation_config.max_new_tokens = int(expected * 4) + 256
        generation_config.do_sample = False
        with torch.inference_mode(), (torch.autocast("cuda", dtype=dtype) if self.device == "cuda" else torch.no_grad()):
            output = self.model.generate(
                input_ids=inputs["input_ids"], attention_mask=inputs["attention_mask"],
                input_features=inputs["input_features"], audio_feature_lengths=inputs["audio_feature_lengths"],
                audio_chunk_mapping=inputs["audio_chunk_mapping"], generation_config=generation_config,
                streamer=Progress())
        return self.processor.tokenizer.decode(output[0][prompt_len:], skip_special_tokens=True).strip(), total_ms

    def transcribe_diarize(self, req, report):
        """The segments as the model timed them. An answer without tags (the model doesn't always follow the
        format) is still the words it heard: one block over the whole recording."""
        raw, total_ms = self._hear(req, report)
        segments = parse_segments(raw, total_ms)
        if not segments and raw.strip():
            segments = [{"text": raw.strip(), "startMs": 0, "endMs": total_ms, "speaker": "S1"}]
        report(1.0)
        return {"text": " ".join(s["text"] for s in segments), "segments": segments, "language": None}


if __name__ == "__main__":
    run_worker(MossTranscribeWorker(), name="moss_transcribe", model_version="v1")
