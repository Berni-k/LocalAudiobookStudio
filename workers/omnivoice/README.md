# OmniVoice worker

Backend: [k2-fsa/OmniVoice](https://github.com/k2-fsa/OmniVoice), model
[`k2-fsa/OmniVoice`](https://huggingface.co/k2-fsa/OmniVoice) (Qwen3 0.6B backbone, Higgs Audio v2 codec).
**License:** the code is Apache 2.0, but the weights are **CC-BY-NC 4.0 (non-commercial)** because of their
training data (Emilia). One model does every mode:

| When | Mode |
| --- | --- |
| Speaker has a reference clip | Voice cloning (the voice attributes, if set, can steady it) |
| Gender / age / pitch / accent / whisper set | Voice design |
| Neither | The model picks a voice |

## Runtimes

| Runtime | Entry | Environment | Model |
| --- | --- | --- | --- |
| `python` (default) | `worker.py`, PyPI `omnivoice` 0.2.1 | `torch2_11-cu128-tx5` | `omnivoice` (+ `whisper-large-v3-turbo` when needed) |
| `onnx-cuda` | `worker_onnx.py` | `onnx1_30-cu13` | `omnivoice-onnx` |
| `onnx` (CPU) | `worker_onnx.py` | `onnx1_30-cpu` | `omnivoice-onnx` |

- **ONNX** runs OpenVoiceOS's export [`OpenVoiceOS/phoonnx-omnivoice`](https://huggingface.co/OpenVoiceOS/phoonnx-omnivoice),
  whose backbone and codec match upstream PyTorch token for token (the older `onnx-community/OmniVoice-Onnx`
  export has causal attention and is wrong). The decoding loop in `worker_onnx.py` follows upstream's
  `_generate_iterative` and phoonnx's numpy port of it; `onnx_port/` vendors upstream's torch-free helpers
  (prompt text, voice design, duration estimate, text chunking) and phoonnx's numpy port of the audio
  post-processing (all Apache 2.0, headers kept).
- **ONNX differences:** no Whisper (a clip without a **Reference transcript** clones from its sound only, which
  is noticeably worse), and the seed drives NumPy's generator, so the same seed gives a different take than
  PyTorch.
- **Measured** on an RTX 3090 (load included): a 4.6 s line takes ~7 s on onnx-cuda, ~23 s on the CPU runtime
  (8 threads), and ~16 s on PyTorch (mostly importing torch); 35 s of text takes ~16 s on onnx-cuda and ~19 s
  on PyTorch. All test outputs transcribed back correctly with Whisper.

## Notes

- **Languages:** 646 (the Language list, `docs/languages.md` upstream), as OmniVoice language ids (mostly
  ISO 639-1/3). *Auto* lets the model work it out; naming the language is slightly better. Voice design was
  trained on Chinese and English only and can be unstable in other languages; cloning is the most stable mode.
- **Cloning:** a 3–10 s clean clip works best; longer clips slow it down. Use a clip in the same language as
  the text, otherwise the accent carries over. With a **Reference transcript** the clip is used as is;
  without one (PyTorch) it is transcribed with Whisper large-v3 turbo (downloaded the first time it's needed,
  1.6 GB, then kept loaded) and the transcript is written to the worker log. Prompts are cached per clip.
- **Voice design** builds upstream's `instruct` string from the settings. Accents only apply to English
  text, Chinese dialects only to Chinese text. A designed or picked voice is re-imagined for every segment;
  for a consistent character, render one line, add it as an asset and clone it.
- **Inline tags** in the text: `[laughter]`, `[sigh]`, `[confirmation-en]`, `[question-en]`, `[surprise-ah]`,
  `[dissatisfaction-hnn]` and others; pronunciation overrides as CMU phonemes (`[B EY1 S]`) or pinyin with
  tone numbers.
- **Numbers:** upstream's text normalization needs WeTextProcessing (pynini, no Windows wheels), so it's off.
  Write numbers as words for the best results.
- **Long text:** above ~30 s of estimated audio the text is split at punctuation into ~15 s chunks (upstream's
  logic on both runtimes); without a clip, the first chunk is the voice reference for the rest.
- **Progress / cancel:** progress counts decoding passes (`Steps` per chunk), and a cancel stops at the next pass.
- **Sampling:** position selection is sampled (temperature 5), so the seed changes the result.
- **Environment (PyTorch):** float16 on the GPU (about 3–4 GB VRAM with Whisper), SDPA attention. The tx5
  environment drops `tensorboardX` and `webdataset`, which `omnivoice` lists for training only (tensorboardX
  would lift protobuf over descript-audiotools' pin).
- **HartsyInference:** no OmniVoice model there (checked in 2.0.0-alpha.249).
- Output: 24 kHz mono.
