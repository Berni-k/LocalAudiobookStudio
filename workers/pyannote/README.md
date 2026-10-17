# pyannote community-1 worker (speaker diarization)

Runs [pyannote speaker-diarization-community-1](https://huggingface.co/pyannote/speaker-diarization-community-1)
(CC-BY-4.0) in the internal .NET worker, on ONNX Runtime, CPU: no Python, no Hugging Face token. The engine is
`src/LocalAudiobookStudio.InternalWorker/PyannoteEngine.cs`; the arithmetic around the networks, ported from
pyannote.audio 4.0.7 (MIT), is `DiarizationMath.cs`.

- **Models:** converted copies made by [Sidekick](https://github.com/royce-mathew/sidekick-models/releases/tag/diarization-community-1)
  (opset 17, unchanged weights): the powerset segmentation network (5.9 MB), the WeSpeaker ResNet34 embedding
  network (26.5 MB; its Kaldi filterbank features are computed in `Fbank.cs` with a Hamming window) and one JSON file
  with the PLDA transform and the clustering settings (threshold 0.6, Fa 0.07, Fb 0.8). Pinned by hash in
  `workers/models.json`. The original is gated on Hugging Face (accept its terms there to use it from Python).
- **How it works:** 10 s windows every second go through the segmentation network (which of up to 3 local speakers
  talk in each frame); each active local speaker of each window is embedded (weighted by when they talk alone);
  the embeddings are grouped (centroid-linkage agglomerative clustering, PLDA, VBx); the windows are put back
  together into a discrete diarization, and turns are read off it.
- **Output:** `diarize` answers with turns (no text), voices named S1, S2 … in the order they first speak. By default
  one voice at a time (the one that talks most when several do); *Overlapping speech* keeps all of them.
  The number of voices is found; *Number of voices*, *At least* and *At most* set it (k-means, as in pyannote, when VBx
  finds another number), and so does the app's `speakerCount` hint.
- **Checked on:** a 56 s file of two voices (three turns, found at 17.7 s and 32.9 s, where they are) and a 200 s mix of
  three voices in ten turns (all three found; 0.9% of the speech given to the wrong voice, frame by frame against the
  known turns), about 6× real time on the CPU.
- **Against pyannote itself** (the Python pipeline, 4.0.7, CPU, same files): the segmentation network gives the same
  class in all 2356 frames checked (largest difference in the log-probabilities 9e-5). Given the same 16 kHz audio, the
  two-voice file comes out identical (100% of the speech frames, no voice differences) and the 200 s mix agrees on 98.8%
  of the speech; the rest is 2.2 s of voice handover at five of the nine joins, 0.1 to 0.5 s each. Against the known
  turns the Python pipeline scores 10.6% (frame errors, no collar) on the mix and this one 10.2%. The app resamples to
  16 kHz with its own resampler (WDL), not torchaudio's, and the segmentation is sensitive to that at the quietest
  pauses: on the files' original 24 kHz audio, 95.6% (two voices) and 91.2% (mix) of the speech frames agree, with the
  same voices found. Speed: the Python pipeline took 12.4 s and 48 s for the 56 s and 200 s files on the CPU, this one
  about 9 s and 34 s (worker start and model loading included).
- **Runtimes:** *internal* (.NET, CPU) and *onnx* / *onnx-cuda* (`worker.py`: the same pipeline in numpy and scipy around
  the same ONNX files, GPU with onnxruntime-gpu). They agree to within float noise (99.96% of the speech frames on a
  32-minute, five-voice podcast; the same turns on the 56 s file). On that podcast: GPU (RTX 3090) 43 s including
  decoding and starting the worker, internal CPU 115 s (3.7 s clustering). The CPU runtime runs the two networks with
  one thread per call and many calls at once, which keeps all cores busy; one call with many threads didn't.
- **Length:** no limit. Segmentation and embeddings are linear in the length; the agglomerative step keeps every pair's
  distance, so beyond 6000 embeddings (about 1.5 hours) it groups every other one (every third, …) and the rest join the
  nearest cluster. Voices are still grouped over the whole recording, so labels stay consistent. A 105-minute file (the
  podcasts joined) took 107 s on the GPU and 351 s on the CPU; at 90% overlap between windows the skipped embeddings
  add little (a podcast grouped from every other embedding kept all five voices).
