# MOSS-SoundEffect v2.0 worker

Backend: [OpenMOSS/MOSS-TTS `moss_soundeffect_v2`](https://github.com/OpenMOSS/MOSS-TTS/tree/main/moss_soundeffect_v2),
pinned to commit `4cf2dab` and installed from the repository archive's subfolder. Weights:
[OpenMOSS-Team/MOSS-SoundEffect-v2.0](https://huggingface.co/OpenMOSS-Team/MOSS-SoundEffect-v2.0)
(1.3B DiT + Qwen3 text encoder + DAC VAE). License: Apache 2.0.

- A speaker using it turns each segment's text into a prompt; the length is the segment's length in the
  inspector (or `[length: …]` in imported text).
- **Maximum 30 s:** the model always denoises a 30 s latent and crops it, so every effect costs the
  same time regardless of length. Longer requests fail with an explanation; loop a shorter effect
  as a background instead.
- **Prompts:** English or Chinese, descriptive works best, e.g. "Heavy rain on a tin roof, distant
  thunder rolling." Upstream appends " duration: Xs" to the prompt, matching training.
- **Environment** `torch2_11-cu128-tx5` (shared, see `workers/environments.json`): upstream pins
  torch 2.9, numpy 1.26, transformers 4.57.1 and diffusers 0.37.1; the environment overrides them
  with torch 2.11, numpy 2.3, transformers 5.18 and diffusers 0.40, which were tested.
- **No torch.compile on Windows:** the DiT step is compiled with Triton CUDA graphs, which Windows
  lacks, so the worker sets `TORCHDYNAMO_DISABLE=1` there (upstream's scripts do the same). On
  Linux compilation stays on (the first generation then takes a few minutes); set
  `LAS_MOSS_NO_COMPILE=1` to disable it.
- **Progress / cancel:** the pipeline's step iterator is replaced, so progress is per diffusion
  step and cancel takes effect after the current step.
- Output: 48 kHz mono.
- **CPU (not recommended):** allowed on the CPU (float32 weights, TorchDynamo off). Each effect still denoises a full 30 s latent, so expect minutes per effect. Marked `"cpuRecommended": false`: the worker's Device setting offers it as "CPU (not recommended)".
