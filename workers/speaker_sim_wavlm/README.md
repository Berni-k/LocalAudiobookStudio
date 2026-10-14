# Speaker SIM Score (WavLM) worker

Scores how much a recording sounds like a voice, like [`speaker_sim`](../speaker_sim/README.md) does, with a larger model:
Microsoft's [`wavlm-base-plus-sv`](https://huggingface.co/microsoft/wavlm-base-plus-sv), WavLM Base Plus (94M parameters,
self-supervised on 94k hours of speech) with an x-vector head fine-tuned for speaker verification on VoxCeleb1. Runs on
transformers in the `torch2_11-cu128-tx5` environment (`worker.py`), on the GPU or the CPU. Tag `Speaker Verification`.

The request and the answer are the same as `speaker_sim`'s (`compareVoices` / `voiceScores`, see `workers/common/protocol.py`),
so `las compare -w speaker_sim_wavlm ...` and the Speakers window use it like the other scorer. **Scores of different
models are never comparable**: this one gives other numbers than ERes2NetV2 for the same clips, so thresholds
don't carry over.

- The embedding is 512 numbers; the score is the cosine of two embeddings (each scaled to length 1). With several
  references a take is compared with the average of their embeddings.
- A clip is embedded whole; past 30 s it is cut at quiet points into pieces of 10-20 s whose embeddings are averaged.
- `speechMs` is the speech in each clip (frames within 30 dB of its speech level), as in `speaker_sim`.
- The model is a `pytorch_model.bin` (no safetensors upstream), pinned by hash in `workers/models.json`.
- **License:** the model card points to the [UniSpeech license](https://github.com/microsoft/UniSpeech/blob/main/LICENSE)
  (Creative Commons Attribution-ShareAlike 3.0); check it for your use.
- Why this model: speech synthesis papers compute speaker similarity with WavLM-based verification models, and a
  comparison of speech models with human similarity ratings
  ([Kishi et al., 2026](https://arxiv.org/abs/2606.05739)) found WavLM-family embeddings among the closest to listeners.
  Even so, no model matches listeners closely; use it as a second opinion next to `speaker_sim`.
