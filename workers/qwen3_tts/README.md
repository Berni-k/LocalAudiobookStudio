# Qwen3-TTS worker

Backend: [QwenLM/Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS), PyPI package `qwen-tts` 0.1.1.
Models (`Qwen/Qwen3-TTS-12Hz-<size>-<type>`), picked per request:

| When | Model | Call |
| --- | --- | --- |
| Speaker has a reference clip | `Base` (1.7B / 0.6B) | `generate_voice_clone` |
| Voice = "Designed from the instruction" | `VoiceDesign` (1.7B only) | `generate_voice_design` |
| Otherwise (preset voice) | `CustomVoice` (1.7B / 0.6B) | `generate_custom_voice` |

- **Cloning:** with a **Reference transcript**, the clip is used as an in-context prompt (closest
  match). Without a transcript, only the speaker embedding (x-vector) is used. The prompt is
  cached per clip and transcript. The clip should be clean speech; a 3 s clip already works.
- **Instruction:** speaking style for preset voices (ignored by the 0.6B model, upstream), or the
  voice description for designed voices. A designed voice is re-imagined for every segment, so it
  can drift between segments; for a consistent character, render one line, add it as an asset and
  clone it.
- **Languages:** Auto, Chinese, English, Japanese, Korean, German, French, Russian, Portuguese,
  Spanish, Italian. Each preset voice speaks all of them, best in its native one.
- **Environment** `torch2_11-cu128-tx4` (shared with VibeVoice, see `workers/environments.json`):
  `qwen-tts` 0.1.1 needs transformers 4.57 (it fails on 5.x and below 4.57).
- **Attention:** flash-attention 2 if installed (Linux), otherwise SDPA.
- **Sampling:** generation samples (temperature, top-k 50), so the seed changes the result.
- **Progress / cancel:** the upstream API has no per-step hook, so progress jumps from start to
  done and a cancel takes effect only when the segment finishes.
- Up to 3 models stay loaded (about 4–5 GB VRAM each at 1.7B); the least recently used is unloaded.
- Output: 24 kHz mono.
- **CPU (not recommended):** allowed on the CPU (float32, SDPA). Upstream only documents GPU use, so this is untested and slow; prefer the 0.6B models. Marked `"cpuRecommended": false`: the worker's Device setting offers it as "CPU (not recommended)".
