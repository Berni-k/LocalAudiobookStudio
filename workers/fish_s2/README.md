# Fish Audio S2 Pro worker

Backend: [fishaudio/fish-speech](https://github.com/fishaudio/fish-speech), pinned to commit
`214da3c`; model [fishaudio/s2-pro](https://huggingface.co/fishaudio/s2-pro) (4B "slow AR" + 400M
"fast AR", plus the DAC codec, about 11 GB of weights).

- **License:** the weights are under the Fish Audio Research License: research and non-commercial
  use only. Commercial use needs a license from Fish Audio.
- **VRAM:** upstream recommends a 24 GB GPU.
- **Source, not package:** fish-speech only works from a source checkout (`pyrootutils` needs its
  `.project-root` marker, and Hydra loads the codec config by relative path). The worker downloads
  the pinned commit into `<DataRoot>/models/fish-speech-src/` (during the environment self-test) and
  imports it from there. `requirements.txt` lists the package's own dependencies.
- **Windows:** upstream documents Linux/WSL only. Imports, codec loading and reference encoding
  were checked on Windows; `--compile` (Triton) is not used.
- **Cloning needs the transcript:** fish-speech ignores a reference clip without its text, so the
  worker refuses that combination with an error. Without any clip the model picks a random voice,
  which changes from segment to segment.
- **Inline control:** free-form `[tags]` in the text steer delivery, e.g. `[whisper]`, `[laughing]`,
  `[pause]`, `[professional broadcast tone]`. Type them in the segment text; note that a tag at the
  start of a line in an *imported* text file is read as a speaker marker by the import.
- **Languages:** 80+, detected from the text.
- **Progress / cancel:** fish-speech's per-token `tqdm` loop is swapped for a reporter, so progress
  is live and cancel stops within one token.
- Length cap: about 2.5× the expected token count for the text (21.5 tokens/s), so runaway
  generations stop. Segment limit is 1000 characters.
- Output: 44.1 kHz mono (the app resamples when mixing).
- **GPU only:** not offered on the CPU. The 4.4B model needs about 20 GB of memory and upstream targets 24 GB GPUs; CPU generation would be impractically slow.
