# MOSS-Transcribe-Diarize worker

Backend: transformers remote code shipped in the model repo, [MOSS-Transcribe-Diarize](https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize)
(0.9B, Apache 2.0, 1.8 GB). Upstream's GitHub package ([OpenMOSS/MOSS-Transcribe-Diarize](https://github.com/OpenMOSS/MOSS-Transcribe-Diarize))
is not installed: it drags in a web server, PyAV and numba, and what the worker needs from it (the prompt, building the
inputs, generating, parsing the answer) is a few lines.

- **Output:** one pass gives `[start][S01]words[end]` per segment: seconds, anonymous speaker labels. 50+ languages.
  The prompt is upstream's default (Chinese, as trained); *Hotwords* are appended the way its recipes do.
- **Up to 30 minutes.** Upstream says 90, but the model ends its answer after 16,384 tokens wherever it is (upstream
  issue #26): dense prose takes about 8 tokens a second, so 45 minutes of it stopped at 34:40. The worker checks that
  the blocks reach the end of the speech and fails otherwise (error code `final`: the app doesn't retry, as the answer
  would be the same), so a long recording is never returned with its end silently missing. Measured on Kokoro reading
  the repository's docs: 20 and 30 minutes came back whole, 45 minutes stopped. Cutting recordings into parts was tried
  and left: separate parts number their voices afresh, and earlier parts kept in the context made the model copy the
  previous part's answer.
- **Blocks, not words.** The model times segments. The worker is tagged Transcription and Diarization (no
  Alignment) and answers `transcribeDiarize` with those segments as they are: `{text, startMs, endMs, speaker}`
  (voices labelled S1, S2 …; the model's S01 is renumbered). It also answers `diarize` with the same blocks, which
  the app reads as turns, so it can be a speaker's diarizer for another transcriber (`las transcribe -w parakeet
  --diarizer moss_transcribe`). The app
  shares a block's time among its words when it compares the transcript with a text (so words are highlighted in
  proportion to their length), and cuts fused generations at the blocks' edges when each part is whole blocks. Spreading
  words over a segment was tried first (tagged Alignment): against Whisper's word times on a 56 s two-voice file the
  starts differed by 0.54 s at the median, 1.76 s at most. For close times give the speaker an aligner (Speakers window,
  Alignment) and use this worker as the transcriber: the app uses the blocks when they fit and the aligner when not.
  Either way the timed words keep the voice and block they came from: on the 56 s file, `las transcribe -w moss_transcribe
  --aligner mms_fa` gave 149 words, each with its speaker and block, changing at 17.72 s and 32.94 s (the blocks start at
  17.68 s and 32.91 s). The take's transcript stores the words and the blocks.
- **Speakers:** on a file of one voice, a second voice and the first again, it labelled S01, S02, S01 and put the
  changes within a word of the joins. The labels only mean "the same voice as before".
- **Speed and memory** (`moss_decoding.py`): the worker does its own greedy decoding instead of `generate()`, which on
  an RTX 3090 made 30 minutes take 20 minutes and spill out of 24 GB:
  - the audio is encoded eight 30 s chunks at a time and the prompt fed to the language model in 2,048-token slices,
    so memory grows with the length instead of its square (10 minutes: 13.3 GB → 4.6 GB), and the progress moves
    while the model takes the recording in;
  - attention for each new token is two matrix products: PyTorch's attention kernels split one query's work only by
    head, which over a long cache leaves most of the GPU idle (64,000 cached tokens: 4.8 ms a layer → 0.74 ms);
  - each step is replayed as a CUDA graph over a static cache, since launching a 0.6B model's kernels from Python is
    most of a step (26 → 150 tokens a second with a short context, 70–85 at 20–30 minutes). The graph attends over the
    part of the cache written so far, in steps of 4,096 tokens. The cache writes where its own counter says, which the
    capture's warm-up moves on: it is put back before capturing.

  20 minutes of prose: 115 s (was 679 s); 30 minutes: 188 s (was 1,211 s), the same words (20 of 3,227 differ, as
  rounding moves near ties). The progress is the latest time the model wrote ("12:30 of 30:00"). On the CPU the same
  loop runs without graphs.
- **Not done:** the `speakers` and `speakerCount` hints are ignored.
