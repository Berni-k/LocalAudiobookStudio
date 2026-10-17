# MOSS-TTSD worker

Backend: transformers remote code from [OpenMOSS/MOSS-TTS](https://github.com/OpenMOSS/MOSS-TTS); model
[MOSS-TTSD-v1.0](https://huggingface.co/OpenMOSS-Team/MOSS-TTSD-v1.0) (8B, Apache 2.0, 15.6 GB, 16 codebooks) with the
24 kHz codec ([MOSS-Audio-Tokenizer](https://huggingface.co/OpenMOSS-Team/MOSS-Audio-Tokenizer), 6.6 GB, shared with
MOSS-TTS). Shared code: `workers/common/moss.py`.

- **Continuation cloning:** each speaker's clip and transcript form the prompt, in speaker order: the user message holds
  `[S1] words [S2] words` plus the dialogue, the assistant message the clips' audio tokens joined. A clip needs its
  transcript (the worker refuses one without it). Upstream suggests 3–10 s clips. A speaker without a clip gets an
  invented voice; that path (no clip for anybody, plain generation mode) is untested.
- **Conversations** (`generate_parts`): up to 5 voices, numbered `[S1]`… by who speaks first. Consecutive lines of one
  speaker are joined into one turn. The model returns one audio for the whole conversation and no spans, so the app
  transcribes it to cut the segments out. Context lines are text only (they are part of the dialogue, then cut away).
- **One segment** (`generate`) is a dialogue of one turn.
- **Replayed reference:** with one voice, the prompt ends with the same speaker who starts the dialogue (`[S1] ref
  … [S1] text`), and the model sometimes replays a short reference instead of speaking the text (seen once in 45 takes:
  the clip's sentence three times). Upstream's own example ends the prompt with a different speaker. So when the voice
  that ends the prompt also starts the dialogue and its clip is 12 s or shorter, the worker gives the clip a second
  speaker number (`[S1] ref [S2] ref`, dialogue still `[S1]`). Tested on 3 clips × 5 texts × 3 seeds: no replays (1
  before), the 5.6 s clip's similarity 0.68 → 0.73; a 25 s clip got worse when doubled (0.80 → 0.69), hence the limit.
- **Stop temperature:** the temperature of the decision to keep going or end; a lower value makes early stops rarer.
- **Sampling:** upstream's values, audio temperature 1.1, top-p 0.9, top-k 50, repetition penalty 1.1.
- **Languages:** 20 (Chinese, English, German, Spanish, French, Japanese, Italian, Hebrew, Korean, Russian, Persian,
  Arabic, Polish, Portuguese, Czech, Danish, Swedish, Hungarian, Greek, Turkish).
- **Codec precision:** as in `moss_tts`, the codec runs in bfloat16 under autocast on the GPU.
