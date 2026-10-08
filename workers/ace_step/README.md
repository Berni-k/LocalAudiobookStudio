# ACE-Step 1.5 music worker

Backend: [ACE-Step 1.5](https://github.com/ace-step/ACE-Step-1.5) XL through diffusers'
`AceStepPipeline` (diffusers 0.40), with the official diffusers-format weights (MIT license, not gated):

| Setting | Repo | Steps | Notes |
| --- | --- | --- | --- |
| XL Turbo (default) | `ACE-Step/acestep-v15-xl-turbo-diffusers` | 8 | Guidance distilled in; fast |
| XL SFT | `ACE-Step/acestep-v15-xl-sft-diffusers` | ~50 | More detail; uses the Guidance setting |

- **Why diffusers:** the upstream `ace-step` package depends on a bundled nano-vllm referenced through
  a uv-only path source, which doesn't install from an archive. The diffusers pipeline is the 4B DiT
  alone, without upstream's optional 5 Hz "planner" LM (which drafts structure and metadata).
- **Instrumental by default:** without lyrics the worker sends upstream's `[Instrumental]`. For a song,
  set **Lyrics** (structured: `[verse]`, `[chorus]` …) and **Lyrics language**, usually as a
  per-segment override.
- **Prompt:** style, mood, instruments, e.g. "calm solo piano, melancholic, sparse, cinematic". BPM
  and Key can be fixed in the settings (0 / empty = the model chooses).
- **Length:** required, 10 s to 10 min.
- **Output:** stereo, 48 kHz.
- **VRAM:** the XL DiT is ~9 GB in bf16; upstream recommends 20 GB or more without offloading (a 24 GB
  GPU is fine). GPU only.
- **Environment** `torch2_11-cu128-tx5` (shared, see `workers/environments.json`): diffusers 0.40
  needs transformers 5 and huggingface-hub 1.x.
- Progress per step; cancel takes effect after the current step. One model is kept loaded; switching
  between Turbo and SFT reloads.
- Not used yet: cover/repaint/continuation, reference audio and LoRAs (all in the pipeline).
