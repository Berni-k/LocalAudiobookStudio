# Stable Audio 3 worker

Backend: [Stability-AI/stable-audio-3](https://github.com/Stability-AI/stable-audio-3), pinned to
commit `3a82c80` and installed from its archive. Models (the **Model** setting):

| Setting | Repo | Size | Max length | Notes |
| --- | --- | --- | --- | --- |
| Small SFX (default) | `stabilityai/stable-audio-3-small-sfx` | 433M | 120 s | Sound effects; runs on the CPU too |
| Small Music | `stabilityai/stable-audio-3-small-music` | 433M | 120 s | Music beds (put a BPM in the prompt) |
| Medium | `stabilityai/stable-audio-3-medium` | 1.4B | 380 s | Needs flash-attention: Linux + CUDA only |

The Length field's maximum follows the segment's model (manifest `length.maxBy`).

- **Gated models:** open each model's page on Hugging Face once and accept the terms (approval is
  automatic), then create a read-only token at huggingface.co/settings/tokens and enter it as
  **Hugging Face token** in Settings. The app downloads the model the first time it is used; without
  access, that download fails with a message saying exactly this.
- **License:** Stability AI Community License: free, including commercial use, below $1M annual
  revenue; above that an enterprise license is needed. Trained on licensed data.
- **Output:** stereo, 44.1 kHz, cut to the segment's length.
- **Steps:** 8 by default (upstream's default; more doesn't necessarily sound better). Progress is
  reported per step and a cancel takes effect after the current step.
- **Device:** GPU or CPU (the Small models are made to run without a GPU; recommended for the CPU).
  Half precision on the GPU only.
- **Environment** `torch2_11-cu128-tx5` (shared, see `workers/environments.json`): upstream pins
  torch 2.7.1 (overridden with the tested 2.11) and needs transformers >= 5.8.
- One model is kept loaded; switching the Model setting reloads.
- Not used yet: audio-to-audio, inpainting/continuation and LoRAs (all supported upstream).
