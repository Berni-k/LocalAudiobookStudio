# Fish Audio Drama 3 worker

Backend: [fishaudio/fish-speech](https://github.com/fishaudio/fish-speech) v3, pinned to commit `1ff3c3e` (tag
`v3.0.0-beta`); model [fishaudio/drama-3](https://huggingface.co/fishaudio/drama-3): a 35B mixture-of-experts text
backbone (3B active per token, 256 experts in each of 40 layers, FP8), a 10-codebook audio decoder and the S2 Pro DAC
codec, about 39 GB of weights.

- **License:** Fish Audio Community License: free for research, and for commercial use under US$500,000 annual
  revenue (with your affiliates); above that, or to offer it as a voice service, a license from Fish Audio is needed.
  It forbids cloning a real person's voice without their consent.
- **GPU memory:** set the worker's **GPU count** (Workers window) to split it over several GPUs. The layers are
  divided by each GPU's free memory; the last GPU also holds the audio decoder, the embeddings and the codec. GPU only.

  | Experts | Needs about | Runs on (tested) |
  |---|---|---|
  | FP8 | 43 GB | 2 × 24 GB (1.1× real time) |
  | 4-bit | 29 GB | 2 × 24 GB; 2 × 16 GB with *Codec on the CPU*; 3 × 12 GB (0.45× real time) |

  One 24 GB GPU isn't enough even with 4-bit experts. The 16 GB and 12 GB cards were simulated on RTX 3090s with
  `DRAMA3_SIMULATE_GPU_GB` (caps PyTorch's memory per GPU and what the split plans with).
- **Why it fits (`drama3_patches.py`):** fish-speech v3 targets one big SM89+ GPU with Triton, where its FP8 kernel runs
  the experts. Elsewhere it would dequantize all experts to bf16 at load (about 70 GB). Here they stay FP8 and only the
  8 of 256 a step routes to are dequantized, straight to bf16. With one SM89+ GPU and Triton (Linux), fish-speech's own
  kernel is kept.
- **4-bit experts** (*Expert precision* worker setting, or `las tts --set expertPrecision=int4`): each FP8 expert is
  dequantized as it loads and re-quantized to 4 bits with a scale and offset per 64 weights (about 0.56 bytes per
  weight against 1), so the model needs about 29 GB instead of 43. It never holds the FP8 copy as a whole, so it
  fits where FP8 doesn't. Loading takes about 10 s longer. In the voice tests (3 clips × 3 seeds × English,
  German, Slovenian; `samples/Test`) the voices were as close to their clips as with FP8 (similarity 0.78 / 0.81 /
  0.77 against 0.79 / 0.82 / 0.77), but takes that stop reading part-way were more frequent: 4 of 54 against 1
  (Whisper word errors 5.2 / 8.3 / 0.7 % against 2.1 / 0.7 / 1.1 %), and generation was about 25 % slower. Use it
  when FP8 doesn't fit; the app's transcript check flags those takes for regenerating.
- **Speed:** fish-speech only captures its decode step as a CUDA graph on a single GPU; split over several it decodes
  eagerly at about 2 frames/s on Windows (launch-bound). The patches capture a graph per GPU and copy between them
  (GeForce cards on Windows have no peer access, so one graph can't span them), and prefill the whole prompt at once
  with the experts in batched matmuls. On 2 × RTX 3090: about 1.0× real time.
- **Reference clips** are encoded once: their codes are cached in `<DataRoot>/cache/drama3-references/` by the clip's
  content. When encoding (or decoding) runs out of GPU memory, that step runs on the CPU; *Codec on the CPU* (worker
  setting) puts it there for good, freeing about 2 GB on the last GPU.
- **Cloning needs the transcript:** fish-speech ignores a reference without its text, so the worker refuses that.
  Without a clip the model invents a voice, a different one for every segment.
- **Inline control:** free-form `[tags]` steer delivery where they stand: `[sighs]`, `[laughs]`, `[whispering]`,
  `[slow, dangerously quiet]`. In an imported text file a tag at the start of a line needs a backslash (`\[sighs]`).
- **Languages:** 80+, detected from the text (no language setting). In the voice tests it read Slovenian better than
  any other model (Whisper WER 0.2 %).
- **Runaway takes:** in very emotional text it sometimes acts the scene out (sobbing, breathing) and drops the rest of
  the words; now and then it also just stops after a sentence or two (1 take in 54 in the voice tests, 4 with 4-bit
  experts). Each text chunk is capped at about twice the frames its text needs (21.5 frames/s), and the app's
  transcript check flags what's missing.
- **Shared GPU memory:** the NVIDIA driver can spill allocations into system RAM (NVIDIA Control Panel, *CUDA – Sysmem
  Fallback Policy*). It turns an out-of-memory error into a generation many times slower; prefer a higher GPU count.
- Long text is split by fish-speech into chunks of about 300 bytes, each generated with the first and the previous
  chunk as context. Segment limit: 1000 characters. Output: 44.1 kHz mono.
