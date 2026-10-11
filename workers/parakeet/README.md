# Parakeet worker

[istupakov/parakeet-tdt-0.6b-v3-onnx](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx), the ONNX export of
[nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) (CC-BY-4.0), on three runtimes:

| Runtime | What runs it |
|---|---|
| `internal` (default; "Internal (ONNX CPU)") | The bundled `las-internal-worker`'s `tdt` engine: ONNX Runtime for .NET on the CPU (`TdtEngine.cs`), files from the manifest's `engineConfig`. Comes with the app; nothing to install but the model. |
| `onnx` | [onnx-asr](https://github.com/istupakov/onnx-asr) in the `onnx1_30-cpu` Python environment (`worker.py`) |
| `onnx-cuda` | The same script in the `onnx1_30-cu13` environment, on the GPU |

The built-in runtime uses the same graphs (plus the export's `nemo128.onnx` mel preprocessor) and the same decoding as
`worker.py`; on the same audio the words agree, and timings differ at most by a frame now and then. Measured with
Kokoro clips (5.8, 22 and 64 s), 8-bit model, 12-core CPU: built-in 1.1 s for 22 s of speech and 3.0 s for 64 s, all
three clips in 7.8 s including start-up; `onnx-cuda` on an RTX 3090 took 11.6 s (the 8-bit model gains little on the GPU).

- A transcription worker (tag `Transcription`): it answers `transcribe` requests, not `generate`. It hears the words
  in a take and when each starts and ends; the app compares them with the segment's text.
- 25 European languages; the model detects the language itself (it doesn't report which), so it has no
  language setting. Punctuation and capitals come out as the model writes them.
- Word timings: a TDT model predicts each token's start frame and how many frames it lasts (80 ms frames). onnx-asr's
  own decoder drops the duration, so `worker.py` (and the built-in runtime) run the decoding loop themselves and keep
  it. `worker.py` uses onnx-asr internals for that, hence the pinned version. Against the audio they are rougher
  than Whisper's: a word's end often runs on to the next word's start (over the pause between lines), a line's first
  word can start early in the pause before it, and now and then a word is off by half a second.
- Takes longer than 30 s are cut at their quietest point between 20 and 30 s and recognised in parts; the model
  attends over the whole input and was trained on short utterances.
- Worker setting **Model**: the 8-bit export (0.7 GB, default) or full precision (2.5 GB).
- The input is always 16 kHz mono 16-bit WAV; the app converts takes before sending them.
- **CPU:** recommended; the built-in runtime is the fastest of the three on a modern CPU.
