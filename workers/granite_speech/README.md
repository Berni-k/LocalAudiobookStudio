# Granite Speech worker

IBM's [Granite Speech 4.1 2B Plus](https://huggingface.co/ibm-granite/granite-speech-4.1-2b-plus) (Apache 2.0) through
[transformers](https://github.com/huggingface/transformers) 5.8 or newer (`worker.py`, PyTorch environment, bf16 on the
GPU, about 4.2 GB of VRAM). One runtime; there is no ONNX or GGUF export of the Plus model.

- Tagged `Transcription` and `Alignment`: it answers `transcribeAlign` (the words heard with their times) and
  `transcribe` (text only), not `generate`. Which prompt runs depends on the request: the app asks for the timed words
  only when it needs them.
- English, French, German, Spanish and Portuguese. No language setting: the model recognises the language itself.
  (The plain Granite Speech 4.1 2B adds Japanese, punctuation and translation, but has no word times.)
- `transcribe`: the plain prompt. Fast (a 44 s take in 8 s on an RTX 3090). Numerals and some punctuation come out as
  the model writes them.
- `transcribeAlign`: the "Timestamps" prompt. The model writes `word [T:n]` for every word, n being the word's end in
  centiseconds modulo 1000 (it wraps every 10 s), and `_` for silence. A word starts where the one before it (or the
  silence) ended. It writes numbers as spoken ("three forty five") and no punctuation or capitals. Decoding takes about
  as long as the audio: a 44 s take in 42 s, a 7 s take in 8.5 s. Measured against Kokoro clips the times are close and
  gaps between sentences show as gaps.
- Takes are cut at their quietest points into pieces of 10 to 20 s (timestamps) or 60 to 120 s (text). The model was
  trained on up to 5 minutes with timestamps, but its times lag further behind the audio as a piece goes on (a 44 s
  take of repeated words: +0.25 s late after 20 s, against the MMS forced aligner); 10 to 20 s pieces keep the lag
  around 0.05 s, with the same total time.
- The model also has a speaker-attribution prompt and keyword biasing; neither is used here.
- **CPU:** works, but several times slower than real time.
