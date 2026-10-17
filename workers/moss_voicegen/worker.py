"""MOSS-VoiceGenerator worker (https://huggingface.co/OpenMOSS-Team/MOSS-VoiceGenerator).

A 1.7B voice-design model: the speaker's ``instruction`` describes the voice (emotion, speed, pitch, character),
the segment's text is what it says. No reference clip; each take is a new voice drawn from the description, so a
fixed seed (and the same description) is what keeps a voice recognisable. English and Chinese only.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common import moss  # noqa: E402
from common.protocol import ensure_model, log, run_worker, seed_everything, torch_device, write_wav  # noqa: E402

MODEL = "moss-voicegenerator"
CODEC = "moss-audio-tokenizer"


class MossVoiceGenWorker:
    def __init__(self):
        self.device = "cpu"
        self.processor = None
        self.model = None

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
        report(0.05, "Loading MOSS-VoiceGenerator")
        # normalize_inputs strips [] {} tags and decorative symbols from the text and instruction, as upstream's demo does.
        self.processor, self.model = moss.load(model_dir, codec_dir, self.device, normalize_inputs=True)
        report(1.0, "Ready")

    def generate(self, req, report):
        text = (req.text or "").strip()
        if not text:
            raise ValueError("empty text")
        s = req.settings
        instruction = " ".join(str(s.get("instruction") or "").split())
        if not instruction:
            raise ValueError("MOSS-VoiceGenerator needs a voice description: fill in 'Voice description' in the "
                             "speaker's settings.")

        message = self.processor.build_user_message(text=text, instruction=instruction)
        sampling = dict(
            audio_temperature=float(s.get("temperature", 1.5)),
            audio_top_p=float(s.get("topP", 0.6)),
            audio_top_k=int(s.get("topK", 50)),
            audio_repetition_penalty=float(s.get("repetitionPenalty", 1.1)),
            text_temperature=float(s.get("textTemperature", 1.0)),
        )
        max_new, expected = moss.frame_budget(text)
        seed_everything(int(req.seed))
        audio, sr = moss.generate(self.processor, self.model, self.device, [message], "generation",
                                  max_new, expected, report, **sampling)
        duration_ms = write_wav(req.out_path, audio.numpy(), sr)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": sr}


if __name__ == "__main__":
    run_worker(MossVoiceGenWorker(), name="moss_voicegen", model_version="v1")
