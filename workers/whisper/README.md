# Whisper worker

OpenAI's [Whisper](https://github.com/openai/whisper) (MIT) for transcription, on three runtimes:

| Runtime | What runs it | Models (worker setting **Model**) |
|---|---|---|
| `internal` (default; "Internal (whisper.cpp CPU)") | The bundled `las-internal-worker`'s `whisper` engine ([Whisper.net](https://github.com/sandrohanea/whisper.net) on [whisper.cpp](https://github.com/ggml-org/whisper.cpp)), CPU builds only. Comes with the app. | ggml files from `ggerganov/whisper.cpp`, listed in the manifest's `engineConfig`: small (8-bit, default here), large-v3 turbo (8-bit), large-v3 (5-bit) |
| `internal-gpu` ("Internal (whisper.cpp GPU)") | The same engine with whisper.cpp's Vulkan build and the CUDA build for the installed CUDA 12 or 13 toolkit, downloaded on install (`InternalGpuRuntime.cs`: 37 MB for Vulkan, plus 143 MB for CUDA 13 or 250 MB for CUDA 12). cudart and cuBLAS come from the toolkit on the PATH. | the same files; large-v3 turbo by default |
| `python` | [transformers](https://github.com/huggingface/transformers)' speech recognition pipeline (`worker.py`), PyTorch environment | the original checkpoints |

- Tagged `Transcription` and `Alignment`: it answers `transcribeAlign` (the words heard with their times) and
  `transcribe` (text only), not `generate`.
- About 100 languages. The speaker setting **Language** (`las transcribe --set language=de`) is used when set;
  with Auto, Whisper detects it and reports it.
- Word timings come from dynamic time warping over the model's alignment heads (both runtimes). whisper.cpp's DTW time
  for a token is where it has been said (its end), so in the internal runtime a word ends at its last token's time and
  starts where the word before it ended; a segment's first word starts where its sound does (found in the audio).
  Measured against the audio, that is closer than Parakeet: within a few tens of ms, a final full stop running on a
  little.
- Takes longer than 20 s are cut at their quietest points and recognised in pieces: with word timings, Whisper
  drops the last words of a window packed with speech, and the pipeline's overlapping windows can repeat words.
- Whisper writes what it thinks was meant: it tends to smooth over stutters and repeats, and can invent text over
  silence. Words far quieter than the take's speech are dropped (and logged). The take's text is never passed as a prompt,
  since that would make Whisper hear the right words and hide the mistakes the check looks for.
- **GPU runtime backends:** *GPU backend* Automatic uses CUDA on an NVIDIA GPU, else Vulkan, else the CPU (Whisper.net
  tries the builds in that order and keeps the first that loads). Forcing Vulkan also uses a GPU the app doesn't list
  as a device (AMD, Intel). Vulkan's first transcription in a process is slow (it compiles its shaders).
- Speed measured with Kokoro clips, large-v3 turbo, per clip after loading (RTX 3090, 12-core Threadripper):
  CUDA 0.6–1.6 s for 6–55 s of speech; Vulkan 4–15 s; CPU 10–49 s. Small on the CPU: 2–12 s.
- **CPU:** whisper.cpp encodes only the piece's length instead of Whisper's full 30 s window there, several times
  faster for short takes. Its Windows builds need the Visual C++ Redistributable (the self-test says so if it's missing).
