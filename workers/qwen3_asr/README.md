# Qwen3-ASR worker

Alibaba's [Qwen3-ASR 0.6B](https://huggingface.co/Qwen/Qwen3-ASR-0.6B-hf) and
[Qwen3-ForcedAligner 0.6B](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B-hf) (both Apache 2.0) through
[transformers](https://github.com/huggingface/transformers) (`worker.py`, PyTorch environment `torch2_11-cu128-tx5`,
bf16 on the GPU, about 3.5 GB of VRAM with both loaded). One runtime; there is no ONNX export that does what this does.

- Tagged `Transcription` and `Alignment Forced` (the full set: its forced aligner times its own transcript too): it
  answers `transcribeAlign` (the words heard with their
  times), `transcribe` (text only, without loading the aligner) and `align` (the forced aligner alone on a given text,
  so it can be another transcription worker's aligner; with language Auto, the language is heard from the first 30 s,
  and the audio can be 5 minutes at most).
- **transformers' own implementation, from the `-hf` checkpoints** (5.13 or newer). The `qwen-asr` package is not
  used: it pins transformers 4.57, and its own copy of the model code breaks on 5. The original `Qwen/Qwen3-ASR-0.6B`
  repos have different weight names and don't load here.
- **Language:** the ASR model hears 30 languages and 22 Chinese dialects and names the language it heard; the
  `language` setting forces one instead. The aligner knows only Chinese, English, Cantonese, French, German, Italian,
  Japanese, Korean, Portuguese, Russian and Spanish: with another language `transcribeAlign` fails (say so, rather than
  guess times), and `transcribe` still works. Japanese and Korean words are split with nagisa and soynlp.
- **The aligner** is a pass without decoding: given the audio and the words, it predicts each word's start and end in
  80 ms steps. It accepts any text, not only Qwen's own. Against the MMS aligner on four minutes of Kokoro speech the
  start times differ by 40 ms at the median (140 ms at the 95th percentile), without drift. It drops punctuation and
  joins hyphenated words; the worker gives words back as the text wrote them where it splits like the aligner. Some
  words come out with no length: `_tidy` gives every word 40 ms and keeps them in order.
- Takes are cut at their quietest points into pieces of 60 to 120 s (the aligner takes 5 minutes at most).
- **Quirk:** on text that repeats itself closely (the same three sentences over and over for minutes) the ASR model
  stops early and skips whole stretches. Real speech didn't do it.
- Speed (RTX 3090, a 12 s take): about 2 s to listen, 0.1 s to time the words; models load in 5 s. On the CPU the
  same take takes about 24 s in all.
