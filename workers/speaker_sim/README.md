# Speaker SIM Score worker

Scores how much a recording sounds like a voice, with a **speaker-embedding model** (speaker verification): the network
turns a recording into one vector for the voice, and the cosine of two vectors is their similarity (-1 to 1; the same
voice is high, a different one clearly lower). Runs on the bundled `las-internal-worker`'s `speaker-embed` engine
(`SpeakerEmbedEngine.cs`): ONNX Runtime for .NET on the CPU, no Python. Tag `Speaker Verification`: it listens, so it is
never offered as a speaker's voice.

| `model` setting | Network | Params | Embedding | Trained on | License |
|---|---|---|---|---|---|
| `eres2netv2` (default) | 3D-Speaker ERes2NetV2 | 17.8 M | 192 | 200k speakers, mostly Mandarin | Apache-2.0 |
| `campplus` | 3D-Speaker CAM++ | 7.2 M | 192 | the same | Apache-2.0 |
| `wespeaker` | WeSpeaker ResNet34-LM | 6.6 M | 256 | VoxCeleb (English) | CC-BY-4.0 |

ERes2Net is what the CosyVoice 2 and Qwen TTS papers report speaker similarity with, so numbers are roughly comparable
to theirs. WeSpeaker has other training data: use it as a cross-check. The 3D-Speaker files are the
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models) exports (GitHub release assets, pinned
by hash in `workers/models.json`); WeSpeaker's is the original ONNX of
[Wespeaker/wespeaker-voxceleb-resnet34-LM](https://huggingface.co/Wespeaker/wespeaker-voxceleb-resnet34-LM).

## The request

`compareVoices` with `files`: a list of `{ "path", "role": "reference" | "candidate" }` (at least two; 16 kHz mono WAV, made
like a transcription's) and `settings`. (The list is `files`, not `audio`: that name already carries base64 WAV bytes
for remote transports.) The answer, `voiceScores`:

- `scores`: one per candidate, in order: its cosine to the references. With several references, to the **average of
  their embeddings** (each scaled to length 1 first). Empty when there are no references.
- `matrix`: the cosine of every pair of files, in request order.
- `speechMs`: per file, how much speech there is (frames within 30 dB of its speech level). Short clips are unreliable.
- `model`: which model scored. Scores of different models must never be compared.

## How it works

- Features are Kaldi-style filterbanks computed in C# (`Fbank.cs`): 80 mel bins, 25 ms Povey windows every 10 ms, pre-emphasis
  0.97, DC removal, no dither, log energies, then the mean over the clip subtracted. Checked against
  `torchaudio.compliance.kaldi.fbank` (unit test) and against sherpa-onnx's own features.
- Samples are scaled as each model was trained, read from the ONNX metadata (`normalize_samples`, `feature_normalize_type`);
  WeSpeaker's own file has none, so its `engineConfig` entry says (`int16Samples`, `meanNormalize`).
- A clip is embedded whole. Past 30 s it is cut at quiet points into pieces of 10–20 s whose embeddings are averaged.
- `LAS_DEBUG_EMBEDDINGS=1` in the worker's environment logs every embedding to the worker log.

## What a score means

- Embeddings capture **timbre and pitch range**, not rhythm. A voice whose delivery changes between seeds while its timbre
  stays the same scores as the same voice.
- Emotion, whispering, loudness and short clips all lower the score. The same human recordings score about 0.73–0.78
  depending on the model, so **a threshold never transfers**. The app's warn and fail thresholds (default 0.9 and 0.75)
  belong to a speaker, model and reference clip: tune them by listening, and expect a new model to need new ones.
- Reported correlations between such scores and listener ratings are about 0.5–0.77.
