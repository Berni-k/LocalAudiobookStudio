# MOSS-TTS v1.5 worker

Backend: Hugging Face transformers with the models' own remote code (`trust_remote_code`), from
[OpenMOSS/MOSS-TTS](https://github.com/OpenMOSS/MOSS-TTS). Models, all Apache 2.0:
- [MOSS-TTS-v1.5](https://huggingface.co/OpenMOSS-Team/MOSS-TTS-v1.5): 8B (Qwen3 backbone, delay pattern over 32
  codebooks), 24 kHz mono, 15.8 GB, plus the 24 kHz codec ([MOSS-Audio-Tokenizer](https://huggingface.co/OpenMOSS-Team/MOSS-Audio-Tokenizer), 6.6 GB).
- [MOSS-TTS-Local-Transformer-v1.5](https://huggingface.co/OpenMOSS-Team/MOSS-TTS-Local-Transformer-v1.5): 4B (a
  backbone plus a local transformer, 12 codebooks), 48 kHz stereo, 8.5 GB, plus the v2 codec
  ([MOSS-Audio-Tokenizer-v2](https://huggingface.co/OpenMOSS-Team/MOSS-Audio-Tokenizer-v2), 7.9 GB).

The *Model* setting picks one; only one is in memory at a time, and the first segment after a switch pays for loading.

- **Shared code:** loading, reference encoding and sampling are in `workers/common/moss.py`, used by `moss_ttsd` and
  `moss_voicegen` too. `moss_ttsd` (dialogue) and `moss_voicegen` (voice design) are separate workers because their
  inputs differ.
- **Cloning:** the speaker's reference clip, no transcript needed. v1.5 copes with a long reference and a short text.
  Without a clip the model invents a voice per segment.
- **Language:** set it whenever you know it (upstream: v1.5 is stronger with the tag, mixed without).
- **Control:** `[pause 1.5s]` inserts an explicit pause (the worker lists it as an inline tag, so transcript checks
  ignore it). Pinyin with tone numbers (`nin2 hao3`) and IPA between slashes are read as pronunciation. A segment's
  length (Length field) is passed as a duration target: 12.5 tokens per second.
- **Codec precision:** the 24 kHz codec's weights are float32; on the GPU it is held in bfloat16 and run under autocast
  so the 8B model and the codec fit 24 GB. Compare a take against its transcript if it ever sounds off.
- **Attention:** SDPA (`flash_attention_2` isn't installed).
- **Stop temperature:** the temperature of the decision to keep going or end the audio. Upstream's 8B default of 1.5
  ended some MOSS-VoiceGenerator takes early (same model code); the 8B showed none in 45 takes, but the default here is
  1.0 for both models.
- **Sampling:** upstream's values, audio temperature 1.7, top-p 0.8, top-k 25, no repetition penalty. The text
  tokens use the model's own defaults.
- **Progress / cancel:** the 8B model's per-step tqdm and the Local model's global transformer forward are hooked to
  report progress; a cancel raises there.
