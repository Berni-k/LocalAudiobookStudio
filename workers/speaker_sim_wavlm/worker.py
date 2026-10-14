"""WavLM Base Plus speaker verification (microsoft/wavlm-base-plus-sv) through transformers: how alike voices are.

The model is WavLM Base Plus (self-supervised on 94k hours) with an x-vector head fine-tuned for speaker verification on
VoxCeleb1. The head gives one 512-number embedding per clip; the cosine of two embeddings is their similarity. Same
request and answer as the ``speaker_sim`` worker (``compare_voices``), so a score is read the same way, but the scores
of the two are never comparable.

A clip is embedded whole. Past 30 s it is cut at quiet points into pieces of 10-20 s whose embeddings (each scaled to
length 1) are averaged.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, frame_levels, quiet_pieces, read_wav  # noqa: E402
from common.protocol import ensure_model, log, run_worker, torch_device  # noqa: E402

MODEL = "wavlm-base-plus-sv"  # workers/models.json
PIECE_SEC = (10.0, 20.0)
WHOLE_UP_TO_SEC = 30.0


class SpeakerSimWavlmWorker:
    def __init__(self):
        self.model_dir = None
        self.extractor = None
        self.model = None
        self.device = "cpu"

    def selftest(self):
        from transformers import WavLMForXVector  # noqa: F401  (an import error is the answer)

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        from transformers import Wav2Vec2FeatureExtractor, WavLMForXVector

        if self.model_dir is None:
            self.prepare(report)
        self.device = torch_device()
        report(0.3, "Loading WavLM speaker verification")
        self.extractor = Wav2Vec2FeatureExtractor.from_pretrained(self.model_dir)
        self.model = WavLMForXVector.from_pretrained(self.model_dir).to(self.device).eval()
        report(1.0, "Ready")
        log(f"WavLM Base Plus speaker verification loaded on {self.device}")

    def _embed_piece(self, samples):
        """The unit-length embedding of one piece of audio."""
        import torch

        inputs = self.extractor(samples, sampling_rate=SAMPLE_RATE, return_tensors="pt").to(self.device)
        with torch.inference_mode():
            embedding = self.model(**inputs).embeddings[0].float()
        return torch.nn.functional.normalize(embedding, dim=-1).cpu()

    def _embed(self, audio):
        """(the voice of a clip, how many ms of it are speech): frames within 30 dB of the clip's speech level."""
        import numpy as np
        import torch

        levels = frame_levels(audio)
        speech = float(np.sort(levels)[int(round(0.95 * (len(levels) - 1)))])
        speech_ms = 0 if speech <= 0 else int((levels >= 0.03 * speech).sum()) * 20
        if len(audio) > WHOLE_UP_TO_SEC * SAMPLE_RATE:
            pieces = [self._embed_piece(audio[a:b]) for a, b in quiet_pieces(audio, *PIECE_SEC)]
            voice = torch.nn.functional.normalize(torch.stack(pieces).mean(dim=0), dim=-1)
        else:
            voice = self._embed_piece(audio)
        return voice, speech_ms

    def compare_voices(self, req, report):
        import torch

        paths = list(dict.fromkeys(f["path"] for f in req.files))  # each distinct file is embedded once
        embedded = {}
        for i, path in enumerate(paths):
            report(0.1 + 0.9 * i / len(paths), "Listening" if len(paths) == 1 else f"Listening (file {i + 1} of {len(paths)})")
            embedded[path] = self._embed(read_wav(path))

        vectors = torch.stack([embedded[f["path"]][0] for f in req.files])
        matrix = (vectors @ vectors.T).clamp(-1, 1)
        references = [embedded[f["path"]][0] for f in req.files if f["role"] == "reference"]
        scores = []
        if references:
            # Several references: compared with the average of their embeddings.
            average = torch.nn.functional.normalize(torch.stack(references).mean(dim=0), dim=-1)
            scores = [float(embedded[f["path"]][0] @ average) for f in req.files if f["role"] != "reference"]
        report(1.0)
        return {"scores": [round(s, 4) for s in scores],
                "matrix": [[round(float(v), 4) for v in row] for row in matrix],
                "speechMs": [embedded[f["path"]][1] for f in req.files],
                "model": MODEL}


if __name__ == "__main__":
    run_worker(SpeakerSimWavlmWorker(), name="speaker_sim_wavlm", model_version="v1")
