# Plan: Import audio (an audiobook, or any recording) into a project

**Status: built** (CLI `import-audio` and File → Import audio…). Done: structure (`AudioStructurer`), prepare (`AudioSource`),
analyze (`AudioAnalyzer`), alignment windows (`AlignWindows`), take cutting and locking (`AudioImporter`,
`ProjectEngine.SaveImportedTake`), the dialog (`ImportAudioViewModel`, `ImportAudioDialog`), tests for the pure parts. Not done:
several input files at once, a reference clip imported as the voice of new speakers, the pre-analysis length check (the
warning appears once the file is read), hands-on testing of the dialog by the maintainer. The text below is the design.

Goal: **File → Import audio…**, next to Import text…, turns a recording into chapters and segments of an **existing
project**, optionally with the original audio as each segment's first take. Nothing is built yet.

The shape, simplified:

> Pick a file → **Analyze** (the whole file goes to the transcription models, in memory) → adjust options until the
> **preview** looks right → **Import**. Cancelling while it works aborts everything and closes the window.

## Decisions

- **No new project.** It imports into the open project, as new chapters **at its end** (like Import text's "append").
- **Quoted-dialogue detection is out of scope.** Speakers are one narrator, or told apart by a model.
- **The whole file goes to the models.** No cutting into pieces, no stitching, no overlap, no resume. Very long
  recordings (hours) are the user's to cut down. A worker's manifest can declare the longest audio it takes
  (`maxAudioMinutes`, absent = no known limit; MOSS: 90). From **90 % of it** the dialog warns, naming the limit, but
  still lets the user run it.
- **Everything stays in memory** until Import. No workspace on disk, no per-stage files. Options that only affect the
  structure (below) re-run instantly on the words already heard.
- **Long pauses become Pause items** with the measured length, to mimic the recording.
- **Keep audio (take 1 = the original) is on by default**, and so is locking those takes.
- **No text editing** in the dialog; that is what the main window is for. The dialog only decides how the recording is
  split and who the speakers are.
- **Cancel aborts the whole thing and closes the window.** No partial results.

## What exists now

| Piece | State |
|---|---|
| Transcribers | Whisper, Parakeet, Granite, Qwen3-ASR, MOSS-Transcribe-Diarize. Each cuts long audio at quiet points itself, so the engine doesn't have to. |
| Diarization | `Diarization` tag; **pyannote** (CPU and GPU, any length) and **MOSS** (up to 90 min). Words carry `Speaker` and `Block`. pyannote takes the voice count as the request's `speakerCount` hint, with At least / At most settings as bounds. |
| Listener chain | `Transcription.Listen`: hearer, optional forced aligner, optional diarizer that gives each word a voice. One job, progress and cancel included. |
| ffmpeg | A managed tool (Tools → System check). Already used to import FLAC/M4A clips and in export. m4b, aac and flac go through it. |
| Text import | `TextImporter` → `ImportResult` → project; the dialog's label mapping (new speaker / existing / merge) is reusable. |
| Locking | `IsLocked` on items. **To check:** that it also protects against clearing takes and cleaning up. |

## Pipeline

```
file
 1 Prepare    ffmpeg → (a) 16 kHz mono WAV for the models, (b) the original as 16-bit PCM WAV for cutting takes
              (both temporary files; no whole-file float decode); chapter metadata read
 2 Analyze    ONE job through Transcription.Listen: words with times (+ voices) for the whole file
 3 Structure  chapters, then segments, pauses and speakers from the words   ← re-runs live as options change
 4 Preview    chapters, segments, speakers; listen to the original
 5 Import     segments into the project; slice the takes out of (b)
```

### 1 Prepare
- One ffmpeg run writes both files (the models' 16 kHz mono WAV, and the original's PCM for slicing). Files WAV, Opus
  and MP3 work without ffmpeg (`AudioDecoder`), for modest lengths. The temp files are deleted when the dialog closes.
- Several input files in one go are fine (file boundaries are chapter boundaries): they are joined in order.
- Chapter atoms (m4b), ID3 CHAP and cue sheets are read here (`ffmpeg -f ffmetadata`).

### 2 Analyze
- One `Transcription.Listen` job: the user's transcriber (default Whisper, Parakeet when speed or CPU matters), an
  optional aligner for sharper word times, an optional diarizer. Language and the transcriber's settings come from the
  same editors the Speakers dialog uses.
- Speakers: **None** (one `narrator`, default) or **told apart by a model** (MOSS alone, or a transcriber + pyannote),
  with a *Voices* field feeding the `speakerCount` hint. No cross-piece label reconciliation is needed: one pass, one
  set of labels.
- Progress is the job's own; **Cancel** aborts it, deletes the temp files and closes the dialog.
- Re-run only when the Listen options change (worker, language, voices, diarizer): an **Analyze** button, with the
  structure options greyed out until there are words.

### 3 Structure (live, in memory, from the word list)
- **Chapters** (each source optional, combined in this order): container metadata; spoken headings (the importer's
  `ChapterRegex`, short, after a long pause: "chapter twelve"); and, if the user ticks it, **a new chapter at every
  pause longer than a threshold** (*Start a chapter at pauses longer than* N s, user-set, default 3 s), read from the
  gaps between words and named `Chapter N`. With none ticked or found, one chapter. Credits can't be edited here:
  they are deleted in the main window.
- **Segments**: sentence ends from punctuation (and from pauses ≥ ~0.7 s when there is none); a speaker change always
  ends a segment; sentences are joined up to *Max segment length* (the speaker's worker limit also applies) unless a
  pause ≥ ~1 s intervenes.
- **Pauses**: a gap between two segments of at least *Insert pauses longer than* (default ~600 ms, adjustable) becomes
  a `Pause` item of the measured length; shorter gaps ride on the project's default spacing. (A negative-pause overlap
  is never inferred.)
- Output: an `ImportResult` plus, per segment, `(startMs, endMs)` and its words and speaker, so the text import's
  mapping into the project and speaker label handling apply unchanged.
- Word times are the model's; "adjust until the preview looks good" means these options (and the aligner), nothing
  more.

### 4 Preview
Left: **chapters** (read-only list with segment counts and durations). Right: the selected chapter's **segments** and
pauses in order: time, speaker chip, text, a play button per segment. Playback is the original, from the prepared
audio, with `PlayFileAsync(path, fromMs, toMs)`, which already takes a range. Below: the **speakers** panel (the text
import's label mapping — new speaker / existing / merge — plus an editable name and colour, a *listen* button
(reference clip: the speaker's longest clean turn, 5–15 s) and the minutes they speak). Summary line: chapters,
segments, pauses, speakers, minutes, characters.

### 5 Import
- Adds the chapters and items to the open project through the same path as text import (undoable, one step).
- New speakers use the project's default worker (Kokoro) and, if it clones, the reference clip as their voice, so a
  regenerated segment sounds like the original.
- **Keep audio:** take 1 of every segment is a slice of the original `(b)` from the segment's first word − 40 ms to its
  last word + 40 ms (never past the middle of the neighbouring gap), stored through `StoreAudio` (the project's
  format) and analysed like any take. Slicing reads the PCM file by offset, so memory stays flat.
- **Not stale by construction:** the take is saved with the segment's *current* input hash, so it counts as up to date
  exactly like a generated take, and editing the text or the speaker's voice later makes it stale as usual. `TakeInfo`
  gets `Imported = true` (worker "imported") for display ("original recording") and so the generation inspector
  works; its transcript is stored (`StoredTranscript`), so it starts at 0 errors. No special stale rule.
- **Lock** (default on): `IsLocked` on the imported segments, so "Generate stale" after changing a speaker's worker
  doesn't replace the recording. Unlocked per segment or selection.
- Progress while importing (slicing is the slow part) with its own Cancel, which rolls the project back to before the
  import (the undo step is only recorded once it all succeeded).

## The dialog

One window (`ImportAudioDialog`), like Import text:
- **Left**: file(s); *Listen* (transcriber, aligner, language, speakers: none / told apart by a model, voices);
  *Structure* (chapters from: metadata, spoken headings, pauses longer than N s; max segment length; insert pauses longer than;
  speak headings); *Audio* (keep the original as takes, lock them); **Analyze**.
- **Right**: while analyzing, the progress bar and message with Cancel; afterwards the preview above.
- **Bottom**: summary and **Import** / **Cancel**.

Changing a Structure option updates the preview immediately; changing a Listen option marks the preview out of date
until Analyze runs again. A long file (thousands of segments) is shown in a virtualized list.

## CLI

`las project <dir> import-audio <file...> [--transcriber whisper] [--aligner mms_fa] [--diarizer pyannote] [--voices N]
[--chapters meta,headings,pauses] [--chapter-pause 3] [--pause-ms 600] [--no-keep-audio] [--no-lock] [--language xx]`,
usable as a step before `generate`/`export`. `--json` summary: chapters, speakers, segments, pauses, minutes. The
structure code (3) is shared with the dialog, in the Engine, and tested without a GPU.

## Milestones

1. **Experiment (no repo changes).** 30–60 min of a real multi-voice audiobook through `las transcribe`: Whisper and
   Parakeet, MOSS, and a transcriber + pyannote. Listen: do the word gaps give good pauses and chapter breaks, and do
   character voices by one narrator confuse the diarizers? Decides defaults.
2. **Structure** in the Engine (chapters, segments, pauses from a word list → `ImportResult`) with tests on fabricated
   transcripts. Shippable as `las project import-audio` for a transcript file first.
3. **Prepare + Analyze**: ffmpeg → the two files, one Listen job, progress and cancel; `las project import-audio` end to
   end for one narrator, text only.
4. **Speakers**: the model-told-apart path, labels → speakers, reference clips.
5. **Takes**: slicing, `Imported`, input hash, lock. Tests for slice math and staleness.
6. **Dialog**: the preview and the live options (compile-checked here; the maintainer tests the GUI).

## Settled

- New chapters go at the end of the project.
- Length: `maxAudioMinutes` in a worker's manifest (a number; absent: unknown). The dialog compares the file's duration
  with the limit of every listener it will use (the strictest wins) and warns at ≥ 90 %, in the Listen section and in
  the summary; the Analyze button stays enabled. Set it for MOSS-Transcribe-Diarize (90) now, others when a limit is
  known. Shown in the Workers window like other manifest facts.
- Chapters by pauses is an option with a user-set threshold, off by default.
- Quoted dialogue is out of scope.

## Experiment so far (33-min, five-voice podcast: `D:\Temp\pya\podcasts\hpr4744.mp3`)

- **Whisper + pyannote, whole file:** 192 s (about 10× real time), 5,457 words, punctuation present (519 sentence ends),
  five voices found, 111 speaker runs (a few short interjections). Works as planned.
- **Pauses from word gaps work.** 4,870 of 5,456 gaps are exactly 0, but that is fast continuous speech, not hidden
  pauses: checked against the audio's loudness, a word's start is within 30 ms of its first sound and silence inside a
  word's end is about 0. Gaps ≥ 600 ms: 103 from the raw times, about 148 when words are tightened against the audio,
  so reading the gaps is enough; tightening is a possible refinement. Music or noise in a gap counts as speech (the
  21.7 s opening jingle is one "word"), which no timing can fix.
- **The MMS aligner can't take the whole file by itself** ("too long to align in one go": about 8 minutes of speech).
  Solved in the alignment path (`AlignWindows`, built): when the transcriber gave rough word times (or blocks), the
  recording is cut at its pauses into windows of about 60 % of the aligner's `maxAudioMinutes`, each aligned on its
  own slice and joined on the recording's timeline. Whisper + MMS + pyannote on the whole 33-minute file: 204 s, the
  same 5,457 words in order with no overlaps, five voices kept, no discontinuity at the window seams. So the dialog's
  length warning applies to the *transcriber* (and MOSS's 90 min), not to the aligner.
- **The aligner exposes far more pauses:** gaps ≥ 600 ms went from 103 (Whisper alone) to 303, ≥ 1 s from 44 to 95;
  median word length 240 → 160 ms. Start times move by a median 70 ms (p90 300 ms) against Whisper's. For Pause items
  and the chapter-by-pause option, the aligner is worth offering as a default for modest extra time (about 10 s more here).
- Not run: MOSS-Transcribe-Diarize on the same file (stopped).

## Still open

Nothing blocking. The defaults for the thresholds come from more files; the chapter-by-pause option stays opt-in with
a user-set length.
