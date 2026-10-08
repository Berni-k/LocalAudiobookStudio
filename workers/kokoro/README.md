# Kokoro worker

Backend: [hexgrad/kokoro](https://github.com/hexgrad/kokoro), model `hexgrad/Kokoro-82M` (v1.0).

- Built-in voices only (no voice cloning). The speaker's **voice** is a voice id such as `af_heart`;
  the list is in `manifest.json` and returned by `listVoices`.
- English only in this worker (`a` = American, `b` = British). Other Kokoro languages need extra
  `misaki` extras and `espeak-ng`, so they are not enabled.
- Output: 24 kHz mono.
- Output is deterministic, so the seed has no effect (it is still recorded).
- `misaki` downloads the spaCy model with pip at runtime if it's missing. uv venvs don't contain pip,
  so the model wheel is listed in `requirements.txt`, and `pip` is installed as a fallback.
- Out-of-vocabulary words fall back to espeak-ng if it is installed on the system; otherwise misaki
  guesses the pronunciation.
- **CPU:** supported (`devices: cuda, cpu`); the 82M model runs well on the CPU.
