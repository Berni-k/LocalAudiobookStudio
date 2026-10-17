# Supported models

A worker is a model the app runs in its own process, on one of the [supported runtimes](../README.md#supported-runtimes). Install workers from *Workers → Manage workers*; the app downloads a worker's model the first time the worker needs it, into its own models folder, so workers that use the same model share one copy. The Workers window lists each worker's models with their sizes; delete them there, or point one at a folder of your own (a finetune).

**The app's MIT license does not cover the models.** Each model has its own license, and some don't allow commercial use (⚠️). Check the model's page before publishing audio made with it. Gated models (Stable Audio 3) need you to accept their terms on Hugging Face and put a read-only access token in *Settings → Hugging Face token*.

**Speed** is how many seconds of audio a model makes (or hears) per second on an RTX 3090, on its fastest runtime: 2× is twice as fast as real time, 0.5× half as fast. Measured with `las` with the model already loaded. Sound effects were timed at 10 s and music at 30 s: these models take about the same time for any length, so longer clips are faster per second. **RAM/VRAM** is roughly how much memory the model needs.

## Voice generation

| Model | Languages | Preset voices | Voice cloning | Voice design | Direction | Inline tags | Conversations | Speed | RAM/VRAM | License | Links |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Kokoro 82M | English | ✅ | – | – | – | – | – | 73× | 2 GB | Apache 2.0 | [Model](https://huggingface.co/hexgrad/Kokoro-82M) · [ONNX](https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX) · [Code](https://github.com/hexgrad/kokoro) |
| Qwen3-TTS | 10 | ✅ | ✅ 3 s clip | ✅ | ✅ | – | – | 3.1× | 6 GB | Apache 2.0 | [CustomVoice](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice) · [Base](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base) · [VoiceDesign](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign) · [Code](https://github.com/QwenLM/Qwen3-TTS) |
| OmniVoice | 646 | – | ✅ 3–10 s clip | ✅ | – | ✅ `[laughter]` | – | 6.5× | 4 GB | CC-BY-NC 4.0 ⚠️ | [Model](https://huggingface.co/k2-fsa/OmniVoice) · [ONNX](https://huggingface.co/OpenVoiceOS/phoonnx-omnivoice) · [Code](https://github.com/k2-fsa/OmniVoice) |
| Breeze TTS 2 | English, Chinese | – | ✅ Clip + transcript | ✅ | ✅ | ✅ `(laugh)` `[…]` | – | 1.5× | 9 GB | BreezeBlue Research and Non-Commercial License ⚠️ | [Model](https://huggingface.co/BreezeBlue/Breeze-TTS-2) · [Code](https://github.com/breezeblue-ai/breeze-tts) |
| VibeVoice | English, Chinese | ✅ | ✅ 10–30 s clip | – | – | – | ✅ [4 voices](#conversations-and-context) | 2.7× | 8 GB (7B: 19 GB) | MIT | [1.5B](https://huggingface.co/vibevoice/VibeVoice-1.5B) · [7B](https://huggingface.co/vibevoice/VibeVoice-7B) · [Code](https://github.com/vibevoice-community/VibeVoice) |
| Fish Audio S2 Pro | 80+ | – | ✅ Clip + transcript | – | – | ✅ `[…]` | ✅ [5 voices](#conversations-and-context) | 0.4× | 20 GB | Fish Audio Research License ⚠️ | [Model](https://huggingface.co/fishaudio/s2-pro) · [Code](https://github.com/fishaudio/fish-speech) |
| Fish Audio Drama 3 | 80+ | – | ✅ Clip + transcript | – | – | ✅ `[…]` | – | 1.1× to 0.5× | 43 GB ([down to 8 GB with offload](#big-models-on-small-gpus)) | Fish Audio Community License ⚠️ (free under US$500k revenue) | [Model](https://huggingface.co/fishaudio/drama-3) · [Code](https://github.com/fishaudio/fish-speech) |
| Chatterbox | English | ✅ | ✅ Clip | – | – | – | – | 1.2× | 5 GB | MIT | [Model](https://huggingface.co/ResembleAI/chatterbox) · [Code](https://github.com/resemble-ai/chatterbox) |
| CosyVoice 2 | Chinese, English, Japanese, Korean, Cantonese | – | ✅ Clip max 17 s | – | ✅ | – | – | 1.5× | 4 GB | Apache 2.0 | [Model](https://huggingface.co/FunAudioLLM/CosyVoice2-0.5B) · [Code](https://github.com/FunAudioLLM/CosyVoice) |
| MOSS-TTS v1.5 (8B, Local 4B) | 31 | – | ✅ Clip | – | – | ✅ `[pause 1.5s]` | – | ? | 22 GB (Local: 14 GB) | Apache 2.0 | [8B](https://huggingface.co/OpenMOSS-Team/MOSS-TTS-v1.5) · [Local](https://huggingface.co/OpenMOSS-Team/MOSS-TTS-Local-Transformer-v1.5) · [Code](https://github.com/OpenMOSS/MOSS-TTS) |
| MOSS-TTSD | 20 | – | ✅ 3–10 s clip + transcript | – | – | – | ✅ [5 voices](#conversations-and-context) | ? | 22 GB | Apache 2.0 | [Model](https://huggingface.co/OpenMOSS-Team/MOSS-TTSD-v1.0) · [Code](https://github.com/OpenMOSS/MOSS-TTS) |
| MOSS-VoiceGenerator | English, Chinese | – | – | ✅ | – | – | – | ? | 8 GB | Apache 2.0 | [Model](https://huggingface.co/OpenMOSS-Team/MOSS-VoiceGenerator) · [Code](https://github.com/OpenMOSS/MOSS-TTS) |

- **Voice cloning**: what the model needs to copy a voice. "Clip + transcript" means it also needs the clip's words.
- **Voice design**: makes a new voice from a description or attributes, without a clip. Designed voices usually differ from seed to seed and from text to text, even with the same description. For a consistent voice, design it once, keep the take you like as a reference clip, and let a voice cloning model speak the rest with that clip.
- **Direction**: an instruction for how to say a line (calm, whispering, faster).
- **Inline tags**: directions typed into the text that the model acts out but doesn't say.
- Some features exist only on some runtimes (Qwen3-TTS's design and direction, CosyVoice 2's direction, VibeVoice's conversations); see [Runtimes](#runtimes).

## Sound effects

| Model | Max length | Sample rate | Negative prompt | Speed | RAM/VRAM | License | Links |
|---|---|---|---|---|---|---|---|
| Stable Audio 3 (Small SFX, Medium) | 120 s (Medium: 380 s) | 44.1 kHz | – | 20× | 3 GB | Stability AI Community License (gated) | [Small SFX](https://huggingface.co/stabilityai/stable-audio-3-small-sfx) · [Medium](https://huggingface.co/stabilityai/stable-audio-3-medium) · [Code](https://github.com/Stability-AI/stable-audio-3) |
| Stable Audio Open Small | 11 s | 44.1 kHz | – | ? | 3 GB | Stability AI Community License | [Model](https://huggingface.co/stabilityai/stable-audio-open-small) |
| MOSS-SoundEffect v2.0 | 30 s | 48 kHz | ✅ | 0.4× | 10 GB | Apache 2.0 | [Model](https://huggingface.co/OpenMOSS-Team/MOSS-SoundEffect-v2.0) · [Code](https://github.com/OpenMOSS/MOSS-TTS) |
| AudioGen | 30 s | 16 kHz | – | 1.7× | 6 GB | CC-BY-NC 4.0 ⚠️ | [Model](https://huggingface.co/facebook/audiogen-medium) · [Code](https://github.com/facebookresearch/audiocraft) |

Stable Audio Open Small is broken for now: on HartsyInference its audio comes out as screeching noise. Use Stable Audio 3.

## Music generation

| Model | Max length | Vocals | BPM and key | Speed | RAM/VRAM | License | Links |
|---|---|---|---|---|---|---|---|
| ACE-Step 1.5 | 10 min | ✅ Lyrics in 10 languages | ✅ | 25× (SFT: 3×) | 16 GB | MIT | [Turbo](https://huggingface.co/ACE-Step/acestep-v15-xl-turbo-diffusers) · [SFT](https://huggingface.co/ACE-Step/acestep-v15-xl-sft-diffusers) · [Code](https://github.com/ace-step/ACE-Step-1.5) |
| Stable Audio 3 (Small Music, Medium) | 120 s (Medium: 380 s) | – | – | 50× | 3 GB | Stability AI Community License (gated) | [Small Music](https://huggingface.co/stabilityai/stable-audio-3-small-music) · [Medium](https://huggingface.co/stabilityai/stable-audio-3-medium) · [Code](https://github.com/Stability-AI/stable-audio-3) |
| Stable Audio Open Small | 11 s | – | – | ? | 3 GB | Stability AI Community License | [Model](https://huggingface.co/stabilityai/stable-audio-open-small) |

## Transcription

| Model | Languages | Transcribe | Align | Word times | Speed | RAM/VRAM | License | Links |
|---|---|---|---|---|---|---|---|---|
| Whisper | ~100 | ✅ ⏱️ | – | Close (within tens of ms) | 49× | 4 GB | MIT | [large-v3 turbo](https://huggingface.co/openai/whisper-large-v3-turbo) · [large-v3](https://huggingface.co/openai/whisper-large-v3) · [ggml](https://huggingface.co/ggerganov/whisper.cpp) · [Code](https://github.com/openai/whisper) · [whisper.cpp](https://github.com/ggml-org/whisper.cpp) |
| Parakeet TDT 0.6B v3 | 25 European | ✅ ⏱️ | – | Loose (ends run on into pauses) | 21× (CPU) | 2 GB | CC-BY-4.0 | [Model](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) · [ONNX](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx) · [Code](https://github.com/istupakov/onnx-asr) |
| Granite Speech 4.1 2B Plus | English, French, German, Spanish, Portuguese | ✅ ⏱️ | – | Close (about 0.05 s late) | 7× (with times: 1.1×) | 5 GB | Apache 2.0 | [Model](https://huggingface.co/ibm-granite/granite-speech-4.1-2b-plus) |
| Qwen3-ASR 0.6B + Forced Aligner | 30 (times: 11) | ✅ ⏱️ | ✅ 11 languages | Very accurate | 6.7× | 4 GB | Apache 2.0 | [ASR](https://huggingface.co/Qwen/Qwen3-ASR-0.6B-hf) · [Aligner](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B-hf) · [Code](https://github.com/QwenLM/Qwen3-ASR) |
| MOSS-Transcribe-Diarize 0.9B | 50+ | ✅ 🗣️ | – | Blocks (a sentence or a turn; see 🗣️) | 6× | 4 GB | Apache 2.0 | [Model](https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize) · [Code](https://github.com/OpenMOSS/MOSS-Transcribe-Diarize) |
| MMS forced aligner | Letters a–z (accents are dropped) | – | ✅ | Very accurate | 170× (CPU: 11×) | 1 GB | CC-BY-NC 4.0 ⚠️ | [Model](https://huggingface.co/MahmoudAshraf/mms-300m-1130-forced-aligner) · [ONNX](https://huggingface.co/tatsu020/mms-300m-fa-onnx) · [Code](https://docs.pytorch.org/audio/stable/tutorials/ctc_forced_alignment_api_tutorial.html) |

- **Transcribe**: hears the words; ⏱️ means it also says when each word is.
- 🗣️ **Tells voices apart**: labels who said each block of text (S1, S2 …; the labels are anonymous and only mean "the same voice as before"), and times the blocks, not the words. `las transcribe --words` shows them. When a speaker with such a transcriber has its segments generated together, the app cuts them out at the blocks' edges if each segment is whole blocks; otherwise it uses the speaker's aligner (*Speakers → Alignment*) to time the words, and without one the segments are generated one by one.
- **Align**: times a text you already have, without changing its words. A speaker can pair a transcription worker
  with an aligner (*Speakers → Alignment*): the first hears the words, the aligner times them.
- Use Whisper to judge *where* words are and Parakeet to check *what* was said (fast and strict about words).

## Speaker diarization

Says who speaks when, as turns without words, and finds the number of voices itself. `las diarize file.wav` prints the
turns; `las transcribe -w parakeet --diarizer pyannote` gives each heard word the voice of its turn (and keeps the turns
as the transcript's blocks). Voice labels (S1, S2 …) are anonymous: they only mean "the same voice as before". A
transcriber that tells voices apart (MOSS-Transcribe-Diarize) can be the diarizer too: its blocks are read as turns.

| Model | Speed | RAM | Notes | License | Links |
|---|---|---|---|---|---|
| pyannote community-1 | 45× (CPU: 16×) | 1 GB | Overlapping speech (*Overlapping speech* setting); up to 3 voices at once and any number in all. Built in (ONNX Runtime, CPU); the ONNX runtime installs a Python environment for the GPU. Any length (105 minutes tried); the voices are grouped over the whole recording, so labels stay consistent | CC-BY-4.0 | [Model](https://huggingface.co/pyannote/speaker-diarization-community-1) · [ONNX files](https://github.com/royce-mathew/sidekick-models/releases/tag/diarization-community-1) · [Code](https://github.com/pyannote/pyannote-audio) |

## Speaker verification

Scores how much a take sounds like a voice, against a reference clip, with yellow and red thresholds (*Speakers →
Speaker Verification*); `las compare` for seed and model tests. Scores from different models can't be compared.

| Model | Worker | RAM/VRAM | Notes | License | Links |
|---|---|---|---|---|---|
| ERes2NetV2 | Speaker SIM Score | <1 GB | The default | Apache 2.0 | [3D-Speaker](https://github.com/modelscope/3D-Speaker) · [ONNX files](https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models) |
| CAM++ | Speaker SIM Score | <1 GB | | Apache 2.0 | [3D-Speaker](https://github.com/modelscope/3D-Speaker) · [ONNX files](https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models) |
| WeSpeaker ResNet34-LM | Speaker SIM Score | <1 GB | | CC-BY-4.0 | [Model](https://huggingface.co/Wespeaker/wespeaker-voxceleb-resnet34-LM) · [Code](https://github.com/wenet-e2e/wespeaker) |
| WavLM Base Plus | Speaker SIM Score (WavLM) | 1 GB | Larger; the model speech synthesis papers' similarity scores are based on. Use it as a second opinion | UniSpeech license (CC BY-SA 3.0, see the model card) | [Model](https://huggingface.co/microsoft/wavlm-base-plus-sv) · [WavLM](https://github.com/microsoft/unilm/tree/master/wavlm) |

## Runtimes

Which runtimes each worker runs on: ⭐ the default, ✅ also supported. *Built in* runtimes come with the app;
*Built in GPU* is downloaded when the worker is installed. The CPU column says whether the worker can run without a
GPU: ✅ yes, 🐢 slowly (several times slower than real time), ❌ no.

| Worker | PyTorch | ONNX (CPU) | ONNX (CUDA) | HartsyInference | Built in (CPU) | Built in GPU | CPU |
|---|---|---|---|---|---|---|---|
| Kokoro 82M | ✅ | ✅ | ✅ | ⭐ | | | ✅ |
| Qwen3-TTS | ⭐ | | | ✅ ¹ | | | 🐢 |
| OmniVoice | ⭐ | ✅ | ✅ | | | | 🐢 |
| Breeze TTS 2 | ⭐ | | | | | | ❌ |
| VibeVoice | ⭐ | | | ✅ ² | | | 🐢 |
| Fish Audio S2 Pro | ⭐ | | | | | | ❌ |
| Fish Audio Drama 3 | ⭐ | | | | | | ❌ |
| Chatterbox | | | | ⭐ | | | 🐢 |
| CosyVoice 2 | ✅ | | | ⭐ ³ | | | 🐢 |
| MOSS-TTS v1.5 | ⭐ | | | | | | ❌ |
| MOSS-TTSD | ⭐ | | | | | | ❌ |
| MOSS-VoiceGenerator | ⭐ | | | | | | 🐢 |
| | | | | | | | |
| Stable Audio 3 | ⭐ | | | | | | ✅ |
| Stable Audio Open Small | | | | ⭐ | | | ✅ |
| MOSS-SoundEffect v2.0 | ⭐ | | | | | | 🐢 |
| AudioGen | | | | ⭐ | | | 🐢 |
| | | | | | | | |
| ACE-Step 1.5 | ✅ | | | ✅ | | | ❌ |
| | | | | | | | |
| Whisper | ✅ | | | | ⭐ | ✅ | ✅ |
| Parakeet TDT 0.6B v3 | | ✅ | ✅ | | ⭐ | | ✅ |
| Granite Speech 4.1 2B Plus | ⭐ | | | | | | 🐢 |
| Qwen3-ASR 0.6B + Forced Aligner | ⭐ | | | | | | 🐢 |
| MOSS-Transcribe-Diarize | ⭐ | | | | | | 🐢 |
| MMS forced aligner | | ✅ | ✅ | | ⭐ | | ✅ |
| | | | | | | | |
| pyannote community-1 | | | | | ⭐ | | ✅ |
| Speaker SIM Score | | | | | ⭐ | | ✅ |
| Speaker SIM Score (WavLM) | ⭐ | | | | | | ✅ |

1. No voice design, direction, language setting or reference transcript; some preset voices are missing.
2. 1.5B only, and no conversations (batching).
3. No direction or speed.

## Conversations and context

Some workers can render several lines in one go, so a conversation sounds like one: **VibeVoice** (up to 4 voices,
PyTorch runtime) and **Fish Audio S2 Pro** (up to 5). Turn it on per speaker under *Speakers → Context*:

- **Include context**: the segments before and after a segment that the model also gets, then cut away. Works with
  every worker: one that renders a single voice reads them in the segment's voice. Fish S2 hears lines that already
  have a take instead of generating them again.
- **Batching** (VibeVoice and Fish S2 only): the longest run of neighbouring segments whose speakers also batch is generated together, within each
  of their speakers' maximum segments and characters (any, by default). They need the same worker, the same settings
  (voice settings such as the preset voice may differ) and the same transcription worker. With context included, it
  goes around the batch, not between its segments.

Context and batches only take in segments whose speakers allow it: context stops at a speaker without *Include
context* (check it and set 0 before and 0 after to let a speaker's lines be context without getting any), a batch at
one without *Batching*. Both look past pauses and background items, and stop at a clip.

Each segment still gets its own take. Fish S2 says where each segment is in the result; for VibeVoice the speaker's
transcription worker finds them. A segment that can't be found cleanly (a word missing at its start or end) is
generated again on its own. Changing the context settings doesn't make takes stale.

## Big models on small GPUs

Some models can be split over several GPUs, for more speed or more memory: set their **GPU count** in the worker
settings (*Workers → Manage workers*; `las tts --gpu-count <n>` for one run). A job then waits until it has that many
GPUs, taking each one as it frees up.

Some can also run on a smaller GPU by offloading part of the model to system RAM, at the cost of speed. Their worker
settings have a *GPU memory target*; the memory settings left on Auto are switched on until the model fits.

## Other workers

- **ComfyUI bridge** runs your own ComfyUI workflows instead of a built-in model; see [ComfyUI bridge](comfyui.md).
- **Test tones** makes beeps instead of speech, for testing the pipeline without a model.

Each worker folder under [`workers/`](../workers) has a README with details.
