# VibeVoice worker

Backend: the community fork [vibevoice-community/VibeVoice](https://github.com/vibevoice-community/VibeVoice),
pinned to commit `952326d`. Microsoft removed the TTS code from `microsoft/VibeVoice`, so this fork is
the upstream to check when updating. Models: `vibevoice/VibeVoice-1.5B` (default) and
`vibevoice/VibeVoice-7B`, chosen by the **Model** setting.

- **Voice cloning:** the speaker's reference clip (any format librosa reads) is resampled to 24 kHz
  mono and used as the voice sample. 10–30 s of clean speech works best.
- **Preset voices:** without a clip, the **Preset voice** setting picks one of the fork's
  `demo/voices` clips. They are downloaded once into `<DataRoot>/models/vibevoice-voices`. The
  `*_bgm` presets (with background music) are left out.
- A single segment is rendered as `Speaker 1: <text>`. Batches and context (`generate_parts`) become a
  multi-speaker script, one `Speaker N:` line per part with one voice sample per speaker (at most 4). Context
  lines are rendered too, also those after the segments: the model reads the whole script. The output is one
  stream of speech without marks between lines, so the app cuts it by transcription. Not on the Hartsy runtime.
- **Environment** `torch2_11-cu128-tx4` (shared with Qwen3-TTS, see `workers/environments.json`):
  the fork pins `transformers==4.51.3`; the environment uses 4.57.3 and `worker.py` patches the two
  transformers changes the fork trips over (same audio for a seed as on 4.51.3). Not on transformers 5:
  it ships its own VibeVoice, which clashes with the fork's.
- **Attention:** uses flash-attention 2 when it is installed (Linux), otherwise SDPA. Upstream
  says flash-attention is the tested path and SDPA may sound slightly worse.
- **Seed:** generation is greedy for tokens but the diffusion head samples noise, so the seed
  changes the result.
- **Switching models:** one model is loaded per worker. A request for the other model unloads the
  current one and loads the new one, so mixing 1.5B and 7B speakers in one chapter is slow.
- Output: 24 kHz mono.
- **CPU (not recommended):** supported on the CPU via upstream's CPU path (float32, SDPA), but very slow. Use 1.5B; 7B needs about 30 GB of RAM. Marked `"cpuRecommended": false`: the worker's Device setting offers it as "CPU (not recommended)".
