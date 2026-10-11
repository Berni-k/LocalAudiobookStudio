# MMS forced aligner worker

[MMS-300M-FA](https://pytorch.org/audio/stable/generated/torchaudio.pipelines.MMS_FA.html) (Meta's multilingual
wav2vec 2.0 CTC model, as in torchaudio's `MMS_FA`), exported to ONNX by
[tatsu020/mms-300m-fa-onnx](https://huggingface.co/tatsu020/mms-300m-fa-onnx) (16-bit weights, 0.6 GB). License
CC-BY-NC 4.0: non-commercial. Three runtimes, as for Parakeet:

| Runtime | What runs it |
|---|---|
| `internal` (default; "Internal (ONNX CPU)") | The bundled `las-internal-worker`'s `ctc-align` engine: ONNX Runtime for .NET on the CPU (`CtcAlignEngine.cs`), files from the manifest's `engineConfig`. Comes with the app; nothing to install but the model. |
| `onnx` | ONNX Runtime in the `onnx1_30-cpu` Python environment (`worker.py`) |
| `onnx-cuda` | The same script in the `onnx1_30-cu13` environment, on the GPU |

Both implementations use the same model and algorithm and, on the same audio, give the same times.

- An alignment worker (tag `Alignment`): it is given a text with the audio and finds when each word is spoken; it
  doesn't hear what is said. Use it where the text is known (a take of a segment) and the words don't need checking.
  On the command line: `las transcribe take.wav -t "the text" -w mms_fa`.
- Per 20 ms frame the model gives the probability of each letter `a`–`z`, the apostrophe, or "blank"; the Viterbi
  path that spells the text through the frames is the alignment (CTC). A word runs from its first letter's first frame
  to its last letter's last frame, so words are close-cut and the pauses between them are left out.
- **Which text it can align:** letters a–z. Accents are dropped (é → e, ß → ss; one table, in `worker.py` and
  `CtcAlignEngine.cs`), a quote or apostrophe inside a word is kept, and everything else (digits, symbols, other
  scripts such as Cyrillic or Chinese) has no sound for the model. A word without letters (`1984`, `—`) gets no time of its own: it is
  placed, with no length, where the word before it ends. Spell numbers out in the text for them to be timed.
  A text without any letter is an error.
- Takes of any length: the model runs on 30 s windows (2 s of audio around each is run but not kept), then one Viterbi
  pass covers the whole take. The pass needs a byte per frame and state; takes of more than about 8 minutes of speech
  are refused.
- Confidence is the geometric mean of the probabilities of the word's letters at their best frame: low when the
  audio doesn't contain the text there (a word that wasn't spoken, or spoken differently). Words that are missing from the audio still get
  a time, squeezed in between the words around them.
- No settings. The input is always 16 kHz mono 16-bit WAV; the app converts takes before sending them.
- Measured on the CPU with a 7.6 s clip: about 1.3 s for the built-in runtime and 0.6 s for `onnx`; a 38 s clip
  took about 7 s either way, including start-up.
- **CPU:** recommended; a GPU gains little for takes this short.
