# Whisper worker

Backend: [transformers](https://github.com/huggingface/transformers)' speech recognition pipeline running OpenAI's
[Whisper](https://github.com/openai/whisper) (MIT): large-v3 turbo by default, large-v3 or small in the worker
setting **Model**.

- A transcription worker (tag `Transcription`): it answers `transcribe` requests, not `generate`.
- About 100 languages. The request's language (`las transcribe -l de`) is used when given; otherwise Whisper
  detects it and reports it.
- Word timings come from the decoder's cross-attention (dynamic time warping over the model's alignment heads). They
  are rougher than Parakeet's, typically within a word or so.
- Takes longer than 20 s are cut at their quietest points and recognised in pieces: with word timings, Whisper
  drops the last words of a window packed with speech, and the pipeline's overlapping windows can repeat words.
- Whisper writes what it thinks was meant: it tends to smooth over stutters and repeats, and can invent text over
  silence. Words far quieter than the take's speech are dropped (and logged). The take's text is never passed as a prompt,
  since that would make Whisper hear the right words and hide the mistakes the check looks for.
- No per-word confidence.
- **CPU:** works, slowly with the large models.
