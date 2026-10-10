# Parakeet worker

Backend: [onnx-asr](https://github.com/istupakov/onnx-asr) running
[istupakov/parakeet-tdt-0.6b-v3-onnx](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx), the ONNX export of
[nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) (CC-BY-4.0).

- A transcription worker (tag `Transcription`): it answers `transcribe` requests, not `generate`. It hears the words
  in a take and when each starts and ends; the app compares them with the segment's text.
- 25 European languages; the model detects the language itself (it doesn't report which), so the request's language
  is ignored. Punctuation and capitals come out as the model writes them.
- Word timings: a TDT model predicts each token's start frame and how many frames it lasts (80 ms frames). onnx-asr's
  own decoder drops the duration, so `worker.py` runs the decoding loop itself and keeps it. That uses onnx-asr
  internals, hence the pinned version.
- Takes longer than 30 s are cut at their quietest point between 20 and 30 s and recognised in parts; the model
  attends over the whole input and was trained on short utterances.
- Worker setting **Model**: the 8-bit export (0.7 GB, default) or full precision (2.5 GB).
- The input is always 16 kHz mono 16-bit WAV; the app converts takes before sending them.
- **CPU:** recommended. The CUDA runtime works too.
