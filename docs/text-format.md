# Text format

Plain prose works as-is: blank lines separate paragraphs, and lines like `Chapter 3` or `# Prologue` start chapters. Markers add more control:

```text
# Chapter 1

[comment: Check how "Aoife" is pronounced before generating.]

The wind howled across the moor.

[Alice] Who's there?
[Bob] Only me. Open the door.

[length: 3s] [Sounds] heavy wooden door creaking open
[pause 1.5s]

[speaker: Alice]
Everything after a block tag is spoken by Alice,
until the next tag.
[speaker: default]
```

| Marker | Meaning |
|---|---|
| `# Title` or `Chapter …` | Starts a new chapter (the pattern can be changed on import) |
| `[Name] text` | This paragraph is spoken by *Name* |
| `Speaker 1: text` | Same, in VibeVoice style; `Name: text` works for names you list on import |
| `[speaker: Name]` … `[speaker: default]` | Everything in between is spoken by *Name* |
| `[pause 1.5s]` / `[pause 500ms]` | Silence instead of the normal gap |
| `[clip: assets/file.wav]` | An audio file in the project (path relative to the project folder) |
| `[length: 4s]` | The length of the next segment, for speakers whose worker takes one (`500ms`, `4s`, `2 min`). On its own line, or at the start of one: `[length: 4s] [Sounds] distant thunder` |
| `[id: name]` | Names the next segment in a per-segment export (*Export → WAV/MP3 per segment*). On its own line, or at the start of one: `[id: intro] [Alice] Hello` |
| `[comment: text]` / `[comment] text` | A note for yourself: never spoken, takes no time. The first form can span several lines. |
| `\[`, `\]`, `\#`, `\:`, `\\` | The character as plain text: `\[whisper] Hello` is spoken text with a voice tag, not a speaker called *whisper*; `Alice\: hi` isn't a speaker marker. A backslash before any other character stays as it is (`assets\door.wav`). |
| `\` at the start of a line | The line is plain text, not a marker or a heading: `\Chapter 3 was the hardest.` |

Sound effects and music are segments like any other: give them a speaker (here *Sounds*) whose worker makes sounds (Stable Audio, AudioGen, ACE-Step, …), and the text is what it should sound like. A worker that needs a length and gets none uses a length named in the text ("distant thunder, 4s"), else 5 seconds.

## Round trip

*File → Export script* writes a project back to this format, so you can edit it in any text editor. It adds the backslashes where a segment's text, a speaker's name or a comment would otherwise read as a marker. To bring a chapter's changes back, import its text with *This project: replace a chapter's text*; for the whole book use `las project MyBook import book.txt --keep-takes`. Either way, unchanged segments keep their takes.
