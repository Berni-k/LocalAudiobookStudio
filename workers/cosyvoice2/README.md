# CosyVoice 2 worker

Model: [FunAudioLLM/CosyVoice2-0.5B](https://huggingface.co/FunAudioLLM/CosyVoice2-0.5B), a Qwen2 0.5B LLM that
writes speech tokens, a flow-matching decoder that turns them into a mel spectrogram, and a HiFT vocoder (24 kHz).
Every voice is cloned: the speaker needs a reference clip.

Two runtimes:

| Runtime | Backend | Notes |
| --- | --- | --- |
| HartsyInference (default) | HartsyInference's own port (.NET) | No Python. Uses Chatterbox's S3Gen speech tokenizer. Reference transcript and seed only. |
| PyTorch | Upstream [FunAudioLLM/CosyVoice](https://github.com/FunAudioLLM/CosyVoice) | The reference implementation: CAM++ speaker embedding, CosyVoice's own speech tokenizer, instructions, speed, inline tags. |

## PyTorch runtime

- **Code:** upstream isn't a package, so the inference files are entries in `workers/models.json`
  (`cosyvoice-code` at a pinned commit, and `matcha-tts-code` for the Matcha-TTS blocks it imports), downloaded and
  checked like a model and put on `sys.path`. Training code and its libraries (Lightning, Hydra, DeepSpeed) are left
  out; `matcha/utils/__init__.py` isn't downloaded and its logger is stubbed.
- **Model building:** `worker.py` builds the LLM, flow and HiFT with `cosyvoice2.yaml`'s values instead of loading
  the yaml (hyperpyyaml would build the training stack too). The Qwen2 LLM is built from CosyVoice-BlankEN's config
  only: upstream loads its 1 GB of weights and overwrites all of them with `llm.pt`.
- **Models:** `cosyvoice2-0.5b` (`llm.pt`, `flow.pt`, `hift.pt`, shared with HartsyInference) and
  `cosyvoice2-0.5b-frontend` (`campplus.onnx`, `speech_tokenizer_v2.onnx`, the Qwen tokenizer files).
- **Reference clip:** up to 30 s (10–20 s works best). With a **Reference transcript**, the clip's words and speech
  tokens prompt the LLM (zero-shot, closest match). Without one, only the speaker embedding and the flow prompt are
  used (upstream's cross-lingual mode: the timbre, not the clip's pacing). The clip's features are cached.
- **Instruction:** upstream's `instruct2` mode, e.g. "Speak slowly and sadly." or "用四川话说这句话" (Chinese
  instructions are what it was trained on most). The instruction replaces the transcript as the LLM prompt; the clip
  still gives the voice.
- **Inline tags:** `[breath]`, `[quick_breath]`, `[laughter]`, `[sigh]`, `[cough]`, `[noise]`, `[lipsmack]`,
  `<strong>word</strong>`, `<laughter>words</laughter>` (special tokens of its tokenizer).
- **Text:** split into sentences of 60–80 tokens by upstream's frontend and generated one by one, then joined.
  Numbers are spelled out (inflect); no wetext or ttsfrd normalization (wetext downloads its grammars at run time,
  ttsfrd is Linux only).
- **Speed:** stretches the mel spectrogram before the vocoder (upstream's `speed`), so the pitch stays.
- **Sampling:** the LLM samples (top-p 0.8, top-k 25, repetition-aware), so the seed changes the result; the flow
  uses fixed noise.
- **Progress / cancel:** per speech token (the LLM runs in the worker's thread instead of upstream's `tts()` thread).
- **Environment** `torch2_11-cu128-tx4`: upstream pins transformers 4.51 / 4.57. `onnxruntime` is the CPU build (the
  speech tokenizer runs once per clip). Reference clips are read with soundfile: `torchaudio.load` needs torchcodec
  since torchaudio 2.9.
- fp32 on the GPU (about 4 GB VRAM); no TensorRT, JIT or vLLM (Linux only upstream).
- **CPU (not recommended):** works, slowly.
