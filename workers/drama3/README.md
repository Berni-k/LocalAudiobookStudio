# Fish Audio Drama 3 worker

Backend: [fishaudio/fish-speech](https://github.com/fishaudio/fish-speech) v3, pinned to commit `1ff3c3e` (tag
`v3.0.0-beta`); model [fishaudio/drama-3](https://huggingface.co/fishaudio/drama-3): a 35B mixture-of-experts text
backbone (3B active per token, 256 experts in each of 40 layers, FP8), a 10-codebook audio decoder and the S2 Pro DAC
codec, about 39 GB of weights.

- **License:** Fish Audio Community License: free for research, and for commercial use under US$500,000 annual
  revenue (with your affiliates); above that, or to offer it as a voice service, a license from Fish Audio is needed.
  It forbids cloning a real person's voice without their consent.
- **GPU memory:** the worker fits itself to the memory it has (`drama3_plan.py`). Its worker settings (Workers window):
  - *GPU memory target (GB)*: how much it may use per GPU; 0 = the whole GPU (less half a GB for the CUDA context).
    Other workers loaded on the same GPU aren't counted: with them there, set a lower target.
  - *Expert precision*, *Expert layers in RAM*, *Codec*, *8-bit attention and audio decoder*: a fixed choice, or Auto
    (the default). The Auto ones are switched on in that order until the plan fits the target: 4-bit experts (Auto
    precision is FP8 only when that fits without anything else), then more layers' experts in RAM, then the codec on
    the CPU, then 8-bit weights. When even all of them don't fit, the worker log says so and it runs anyway.
  - *GPU count* splits it over several GPUs instead (fastest). There, only precision and codec are chosen; experts in
    RAM and 8-bit weights are for one GPU.

  The plan is in the worker log (`Memory plan: …`), with each segment's peak (`GPU memory peak: …`). Tested on RTX
  3090s, the smaller cards simulated with `DRAMA3_SIMULATE_GPU_GB` (holds PyTorch to that much per GPU):

  | GPU memory | Auto chooses | Speed (real time ≈ 21.5 frames/s) |
  |---|---|---|
  | 2 × 24 GB (GPU count 2) | FP8 | about 20 frames/s |
  | 2 × 16 GB (GPU count 2) | 4-bit, codec on the CPU | about 16 frames/s |
  | 32 GB | 4-bit | (not tested) |
  | 24 GB | 4-bit, 14 layers' experts in RAM | about 14.5 frames/s |
  | 16 GB | 4-bit, 34 layers in RAM | about 12 frames/s |
  | 12 GB | 4-bit, 31 layers in RAM, codec on the CPU | about 12 frames/s |
  | 8 GB | 4-bit, all 40 layers in RAM, codec on the CPU | about 11 frames/s |
  | 6 GB | the same with 8-bit attention and audio decoder | about 9 frames/s |

  "The whole GPU" is its memory less 1.2 GB (what an idle 24 GB RTX 3090 keeps on Windows); the simulated cards
  are counted the same way. In the voice tests the 8 GB plan sounded like 4-bit on two GPUs (it computes the same
  4-bit weights): similarity 0.77 / 0.81 / 0.77 (English / German / Slovenian), 5 of 54 takes stopping part-way,
  about 0.42× real time over the whole suite.

  Experts in RAM need that much free RAM, pinned (about 0.42 GB per layer with 4-bit experts, 17 GB for 40), plus
  about 2 GB for the embedding and output layer and 4 GB for the codec when it runs on the CPU.
- **Experts in RAM** (`drama3_offload.py`, one GPU): the embedding and output layer stay in RAM and only their rows for
  the end token and the 4096 audio codes go to the GPU (decoding never produces anything else); one CUDA-graph KV
  bucket, KV caches for 4096 rows. A layer whose experts are in RAM is decoded as a graph up to its router, one sync
  for the host to read the 8 experts it chose, copies of the ones missing from its GPU cache slots (spare memory goes
  to more slots), and one graph shared by all such layers for the experts themselves. Routing isn't sticky (a frame
  shares about 2 of its 8 experts with the one before), so it is the per-layer sync, not PCIe (about 20 GiB/s per
  expert on 4.0 ×16), that sets the speed. 8-bit weights: one scale per output row, quantized in RAM before moving to
  the GPU, all dequantized into one shared buffer; small matrices (routers, gates) stay bf16, quantizing those
  derails the audio.
- **Why it fits (`drama3_patches.py`):** fish-speech v3 targets one big SM89+ GPU with Triton, where its FP8 kernel runs
  the experts. Elsewhere it would dequantize all experts to bf16 at load (about 70 GB). Here they stay FP8 and only the
  8 of 256 a step routes to are dequantized, straight to bf16. With one SM89+ GPU and Triton (Linux), fish-speech's own
  kernel is kept.
- **4-bit experts** (*Expert precision* worker setting, or `las tts --set expertPrecision=int4`): runs on Fish
  Audio's own int4 build, [fishaudio/drama-3-int4](https://huggingface.co/fishaudio/drama-3-int4) (22 GB download,
  the `drama-3-int4` model; GPTQ symmetric, one scale per 128 weights, experts and attention in 4 bits). The experts'
  values are used as they are (`gptq_to_packed`, `gptq_to_groups`: the same numbers, with the scale repeated for our
  groups of 64 and an offset of −8 × scale), the attention layers are expanded to bf16 while loading. About 29 GB of
  weights on the GPU(s) with the shared expert and tower in bf16, instead of 43 with FP8, so it fits where FP8
  doesn't. The earlier voice tests (3 clips × 3 seeds × English, German, Slovenian; `samples/Test`) used 4-bit
  made by re-quantizing the FP8 experts on loading; the numbers in this README come from those: the voices were as close
  to their clips as with FP8 (similarity 0.78 / 0.81 / 0.77 against 0.79 / 0.82 / 0.77), but takes that stop reading
  part-way were more frequent: 4 of 54 against 1 (Whisper word errors 5.2 / 8.3 / 0.7 % against 2.1 / 0.7 / 1.1 %),
  and generation was about 25 % slower. The app's transcript check flags such takes for regenerating. Not retested
  with Fish's build yet.
- **Speed:** fish-speech only captures its decode step as a CUDA graph on a single GPU; split over several it decodes
  eagerly at about 2 frames/s on Windows (launch-bound). The patches capture a graph per GPU and copy between them
  (GeForce cards on Windows have no peer access, so one graph can't span them), and prefill the whole prompt at once
  with the experts in batched matmuls. On 2 × RTX 3090: about 1.0× real time.
- **Reference clips** are encoded once: their codes are cached in `<DataRoot>/cache/drama3-references/` by the clip's
  content. When encoding (or decoding) runs out of GPU memory, that step runs on the CPU; *Codec: CPU* (worker
  setting) puts it there for good, freeing about 3.8 GB on the last GPU plus about 1.6 GB while decoding.
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
- **Shared GPU memory:** on Windows a GPU that runs over its memory doesn't fail: the NVIDIA driver spills into system
  RAM (NVIDIA Control Panel, *CUDA – Sysmem Fallback Policy*) and generation gets many times slower (seen: 1.3 frames/s
  instead of 15). If a plan runs that slowly, lower the GPU memory target by a GB or two.
- Long text is split by fish-speech into chunks of about 300 bytes, each generated with the first and the previous
  chunk as context. Segment limit: 1000 characters. Output: 44.1 kHz mono.
