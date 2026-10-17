# MOSS-Transcribe-Diarize worker

Backend: transformers remote code shipped in the model repo, [MOSS-Transcribe-Diarize](https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize)
(0.9B, Apache 2.0, 1.8 GB). Upstream's GitHub package ([OpenMOSS/MOSS-Transcribe-Diarize](https://github.com/OpenMOSS/MOSS-Transcribe-Diarize))
is not installed: it drags in a web server, PyAV and numba, and what the worker needs from it (the prompt, building the
inputs, generating, parsing the answer) is a few lines.

- **Output:** one pass gives `[start][S01]words[end]` per segment: seconds, anonymous speaker labels. Recordings up to
  90 minutes; 50+ languages. The prompt is upstream's default (Chinese, as trained); *Hotwords* are appended the way its
  recipes do.
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
- **Speed:** 6× real time on an RTX 3090 (56 s in 9.2 s, model loaded). Greedy decoding, bfloat16 on the GPU, float32
  on the CPU.
- **Not done:** long recordings aren't cut into pieces (the model takes up to 90 minutes), and the `speakers` and
  `speakerCount` hints are ignored.
