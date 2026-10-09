# Breeze TTS 2 worker

Backend: [breezeblue-ai/breeze-tts](https://github.com/breezeblue-ai/breeze-tts) at commit `58ec70c`, model
[`BreezeBlue/Breeze-TTS-2`](https://huggingface.co/BreezeBlue/Breeze-TTS-2) (~3B: Qwen3 backbone, depth decoder,
Qwen3-TTS 12 Hz audio tokenizer). **License:** the code is Apache 2.0, but the weights and everything generated
with them self-hosted fall under the **BreezeBlue Research and Non-Commercial License**.

| When | Upstream template |
| --- | --- |
| Reference clip | Voice clone (`ref_clone_tata`) |
| Reference clip + Voice description / direction | Voice direction (`ref_edit_tata`): the clip's voice, directed |
| Voice description / direction only | Voice design (`tts_instruction`) |
| Neither | The model picks a voice (`tts_plain`) |

- **Languages:** English and Chinese (Mandarin), detected from the text. Write the instruction in the text's language.
- **Source code:** upstream's inference code isn't a package, so the worker downloads it at the pinned commit
  once (into `HF_HOME/breeze-tts-src`) and puts it on `sys.path`, like `fish_s2`. Its top-level package is
  named `models`.
- **Cloning:** the clip should be clean, non-looping speech. Upstream needs the clip's exact transcript
  (repetitions included); leave **Reference transcript** empty and the worker transcribes it with Whisper
  large-v3 turbo (downloaded the first time, 1.6 GB; the transcript goes to the worker log). The clip's codes
  are cached per clip.
- **Instruction strength** is classifier-free guidance, only used with an instruction (4 by upstream's advice;
  1, no guidance, otherwise).
- **Vocal events** inline in the text: `(laugh)`, `(cough)`, `(clears throat)`, `(sigh)` in English; `[笑]`,
  `[咳嗽]`, `[清嗓子]`, `[叹气]` in Chinese.
- **Length:** upstream generates at most 1500 frames (2 minutes) within 2048 tokens of context; `maxChars`
  800 keeps segments well inside that. The log warns if a segment hit the limit.
- **Speed:** upstream's fast path captures a CUDA graph of each stage, after `torch.compile` for the depth
  decoder and the codec. `torch.compile` needs Triton, which Windows doesn't have, so the worker makes it a
  no-op and captures the graphs from the eager modules. Measured on an RTX 3090 (41 s of English):

  | Stages with CUDA graphs | RTF | VRAM | Extra load time |
  | --- | --- | --- | --- |
  | none (eager) | 3.2 | 7.6 GiB | – |
  | backbone only | 2.9 | 8.7 GiB | 15 s |
  | depth decoder only | 1.0 | 8.0 GiB | 7 s |
  | **all but the codec (the worker's fast path)** | **0.62** | **8.7 GiB** | **19 s** |
  | all (upstream's `--fast-all`) | 0.55 | 13.1 GiB | 42 s |

  The depth decoder (16 codebooks per frame) is what eager mode spends its time on. The **Fast path** worker
  setting (Workers window) turns it off. bfloat16, GPU only.
- **Progress / cancel:** progress counts generated frames against an estimate from the text length; a
  cancel stops at the next frame.
- **Environment** `torch2_11-cu128-tx4` (shared with Qwen3-TTS and VibeVoice): it needs `qwen-tts` 0.1.1 and
  transformers 4.57.3.
- Output: 24 kHz mono.
