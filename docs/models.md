# Supported models

A worker is a model the app runs in its own process, on one of the [supported runtimes](../README.md#supported-runtimes). Install workers from *Workers → Manage workers*; the app downloads a worker's model the first time the worker needs it, into its own models folder, so workers that use the same model share one copy. The Workers window lists each worker's models with their sizes; delete them there, or point one at a folder of your own (a finetune).

| Worker | Kind | Runs on | Voice cloning | Model license | Status | Links |
|---|---|---|---|---|---|---|
| Kokoro 82M | Speech (English) | CPU, GPU (HartsyInference by default; ONNX and PyTorch too) | No (built-in voices) | Apache 2.0 | Tested | [Model](https://huggingface.co/hexgrad/Kokoro-82M) · [ONNX](https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX) · [Code](https://github.com/hexgrad/kokoro) |
| Qwen3-TTS | Speech (10 languages) | GPU | Yes (3 s clip), voice design | Apache 2.0 | Tested | [CustomVoice](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice) · [Base](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base) · [VoiceDesign](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign) · [Code](https://github.com/QwenLM/Qwen3-TTS) |
| OmniVoice | Speech (646 languages) | GPU (PyTorch by default; ONNX with CUDA too), CPU (ONNX, slow) | Yes (3–10 s clip), voice design | **CC-BY-NC 4.0 (non-commercial)** | Experimental | [Model](https://huggingface.co/k2-fsa/OmniVoice) · [ONNX](https://huggingface.co/OpenVoiceOS/phoonnx-omnivoice) · [Code](https://github.com/k2-fsa/OmniVoice) |
| Breeze TTS 2 | Speech (English, Chinese) | GPU (12 GB) | Yes (clip + transcript), voice design and direction | **BreezeBlue Research and Non-Commercial License** | Experimental | [Model](https://huggingface.co/BreezeBlue/Breeze-TTS-2) · [Code](https://github.com/breezeblue-ai/breeze-tts) |
| VibeVoice | Speech, long-form | GPU | Yes | MIT | Tested | [1.5B](https://huggingface.co/vibevoice/VibeVoice-1.5B) · [7B](https://huggingface.co/vibevoice/VibeVoice-7B) · [Code](https://github.com/vibevoice-community/VibeVoice) |
| Fish Audio S2 Pro | Speech (80+ languages) | GPU (24 GB) | Yes (clip + transcript) | **Fish Audio Research License (non-commercial)** | Experimental | [Model](https://huggingface.co/fishaudio/s2-pro) · [Code](https://github.com/fishaudio/fish-speech) |
| Chatterbox | Speech (English) | GPU (HartsyInference) | Yes | MIT | Experimental | [Model](https://huggingface.co/ResembleAI/chatterbox) · [Code](https://github.com/resemble-ai/chatterbox) |
| CosyVoice 2 | Speech | GPU (HartsyInference) | Yes (clip required) | Apache 2.0 | Experimental | [Model](https://huggingface.co/FunAudioLLM/CosyVoice2-0.5B) · [Code](https://github.com/FunAudioLLM/CosyVoice) |
| Stable Audio 3 | Sound effects, music beds | GPU, CPU | – | Stability AI Community License (gated) | Experimental | [Small SFX](https://huggingface.co/stabilityai/stable-audio-3-small-sfx) · [Small Music](https://huggingface.co/stabilityai/stable-audio-3-small-music) · [Medium](https://huggingface.co/stabilityai/stable-audio-3-medium) · [Code](https://github.com/Stability-AI/stable-audio-3) |
| Stable Audio Open Small | Sound effects, short music | GPU (HartsyInference) | – | Stability AI Community License | Experimental | [Model](https://huggingface.co/stabilityai/stable-audio-open-small) |
| MOSS-SoundEffect v2.0 | Sound effects | GPU | – | Apache 2.0 | Experimental | [Model](https://huggingface.co/OpenMOSS-Team/MOSS-SoundEffect-v2.0) · [Code](https://github.com/OpenMOSS/MOSS-TTS) |
| AudioGen | Sound effects | GPU (HartsyInference) | – | **CC-BY-NC 4.0 (non-commercial)** | Experimental | [Model](https://huggingface.co/facebook/audiogen-medium) · [Code](https://github.com/facebookresearch/audiocraft) |
| ACE-Step 1.5 | Music | GPU | – | MIT | Experimental | [Turbo](https://huggingface.co/ACE-Step/acestep-v15-xl-turbo-diffusers) · [SFT](https://huggingface.co/ACE-Step/acestep-v15-xl-sft-diffusers) · [Code](https://github.com/ace-step/ACE-Step-1.5) |
| Parakeet TDT 0.6B v3 | Transcription with word timings (25 European languages) | CPU, GPU (ONNX) | – | CC-BY-4.0 | New | [Model](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) · [ONNX](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx) · [Code](https://github.com/istupakov/onnx-asr) |
| Test tones | Debug | CPU | – | – | For testing the pipeline | – |

**The app's MIT license does not cover the models.** Each model has its own license, and some don't allow commercial use. Check the model's page before publishing audio made with it. Gated models (Stable Audio 3) need you to accept their terms on Hugging Face and put a read-only access token in *Settings → Hugging Face token*.

Each worker folder under [`workers/`](../workers) has a README with details. The ComfyUI bridge runs your own ComfyUI workflows instead of a built-in model; see [ComfyUI bridge](comfyui.md).
