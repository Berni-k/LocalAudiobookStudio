# MOSS-VoiceGenerator worker

Backend: transformers remote code from [OpenMOSS/MOSS-TTS](https://github.com/OpenMOSS/MOSS-TTS); model
[MOSS-VoiceGenerator](https://huggingface.co/OpenMOSS-Team/MOSS-VoiceGenerator) (1.7B, Apache 2.0, 4 GB) with the 24 kHz
codec ([MOSS-Audio-Tokenizer](https://huggingface.co/OpenMOSS-Team/MOSS-Audio-Tokenizer), 6.6 GB, shared with MOSS-TTS).
Shared code: `workers/common/moss.py`.

- **Voice design, not cloning:** the speaker's *Voice description* is the model's `instruction`; the segment's text is
  what is said. There is no reference clip, so a speaker's takes are different voices that fit the description. A fixed
  seed with the same description and text repeats a take; it does not carry the voice to other text.
- **Languages:** English and Chinese.
- **Sampling:** the model is sensitive to it; defaults are upstream's (temperature 1.5, top-p 0.6, top-k 50, repetition
  penalty 1.1).
- **Stop temperature:** at each step the model chooses to continue or end the audio, and upstream samples that choice
  at temperature 1.5. That ended some takes after a few seconds (2 of 6 seeds on a 20 s text). At 1.0 and 0.7 none of
  12 did, so the default is 1.0.
- **Input cleanup:** the processor runs with `normalize_inputs=True`, as upstream's demo does: it removes `[]` and `{}`
  tags and decorative symbols from the text and the description.
- **CPU:** works (float32, eager attention) but slowly.
