namespace LocalAudiobookStudio.Core.Model;

/// <summary>Metadata stored next to each take in <c>audio/&lt;segmentId&gt;/take-NNNN.json</c>.</summary>
public sealed class TakeInfo
{
    public int Take { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string InputHash { get; set; } = "";
    public string Worker { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    /// <summary>What ran the model: "python" or "hartsy" (null in takes from before runtimes).</summary>
    public string? Runtime { get; set; }
    public long? Seed { get; set; }
    public string? Gpu { get; set; }
    public int DurationMs { get; set; }

    /// <summary>The rate the model made it at (an Opus file decodes at 48 kHz regardless).</summary>
    public int SampleRate { get; set; }

    /// <summary>How the audio file is stored: <see cref="AudioFormats.Wav"/> or <see cref="AudioFormats.Opus"/>.</summary>
    public string Format { get; set; } = AudioFormats.Wav;
    public double? LoudnessLufs { get; set; }
    public int GenerationMs { get; set; }

    /// <summary>Detected leading silence, used when the item has no explicit trim.</summary>
    public int TrimStartMs { get; set; }

    /// <summary>Detected trailing silence, used when the item has no explicit trim.</summary>
    public int TrimEndMs { get; set; }

    /// <summary>Number of characters of the source text (used for speaking-rate estimates).</summary>
    public int? TextLength { get; set; }

    /// <summary>
    /// How the transcript compared with the text the take was made from, worked out once when it was transcribed: the
    /// differences found (null: not transcribed) and the words of that text. Kept here so counting a chapter or listing
    /// takes needs no transcript file, and an older take keeps the verdict it had.
    /// </summary>
    public int? TranscriptErrors { get; set; }

    public int? TranscriptWordCount { get; set; }

    /// <summary>
    /// How much the take sounds like its speaker's reference clip, from the speaker's SIM scorer: a cosine similarity, worked
    /// out once when the take was made (null: not scored). Only meaningful beside scores of the same <see cref="SimCheck"/>;
    /// the speaker's thresholds say when it is low.
    /// </summary>
    public double? SimScore { get; set; }

    /// <summary>How much speech <see cref="SimScore"/> rests on, in ms: short takes score unreliably.</summary>
    public int? SimSpeechMs { get; set; }

    /// <summary>Who scored the take and against what.</summary>
    public SimScoreInfo? SimCheck { get; set; }

    /// <summary>Set when the take was cut out of a fused generation (several segments, or context, in one go).</summary>
    public TakeFusion? Fusion { get; set; }

    public static string FileStem(int take) => $"take-{take:0000}";

    /// <summary>The audio file's name: take-NNNN.wav or take-NNNN.opus.</summary>
    public string FileName => FileStem(Take) + AudioFormats.ExtensionOf(Format);
}

/// <summary>What a take's voice score was worked out with. Scores with different <see cref="Key"/>s are never mixed.</summary>
public sealed class SimScoreInfo
{
    public string Worker { get; set; } = "";

    /// <summary>The worker's model that scored (scores of different models aren't comparable).</summary>
    public string Model { get; set; } = "";

    /// <summary>What the take was compared with, for people: "voice clip kate.wav" or "3 other takes of the speaker".</summary>
    public string Against { get; set; } = "";

    /// <summary>
    /// Worker, model and reference together. When the speaker's current key differs (another checker, model or voice
    /// clip), the score is out of date.
    /// </summary>
    public string Key { get; set; } = "";
}

/// <summary>How a take came out of a fused generation.</summary>
public sealed class TakeFusion
{
    /// <summary>Segments generated together (this one included).</summary>
    public int Segments { get; set; }

    /// <summary>Lines of context around them.</summary>
    public int Context { get; set; }

    /// <summary>How it was cut out: <see cref="Worker"/> (the worker said where) or <see cref="Transcript"/>.</summary>
    public string Split { get; set; } = Worker;

    /// <summary>The kept record of the generation (<see cref="FusionRecord"/>), when the fused generation inspector was on.</summary>
    public string? Record { get; set; }

    public const string Worker = "worker", Transcript = "transcript";
}

/// <summary>
/// What a fused generation was sent and gave back, kept for the fused generation inspector: <c>fused/&lt;id&gt;/record.json</c>
/// next to the worker's whole output (<c>audio.wav</c>). For seeing how its segments were cut.
/// </summary>
public sealed class FusionRecord
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Worker { get; set; } = "";
    public string? Runtime { get; set; }
    public string ModelVersion { get; set; } = "";
    public long? Seed { get; set; }
    public string? Device { get; set; }
    public int GenerationMs { get; set; }

    /// <summary>The settings all parts shared (for a worker that doesn't fuse: the segment's settings).</summary>
    public System.Text.Json.Nodes.JsonObject Settings { get; set; } = new();

    /// <summary>For a worker that doesn't fuse: the one text it was sent, context and segment as paragraphs.</summary>
    public string? RequestText { get; set; }

    public List<FusionRecordPart> Parts { get; set; } = [];

    /// <summary>How the takes were cut out: <see cref="TakeFusion.Worker"/> or <see cref="TakeFusion.Transcript"/>.</summary>
    public string Split { get; set; } = TakeFusion.Worker;

    /// <summary>The transcription worker that found the parts, and what it heard (times in <c>audio.wav</c>).</summary>
    public string? Transcriber { get; set; }
    public List<Transcripts.HeardWord>? Transcript { get; set; }

    /// <summary>The blocks of speech by one voice the transcript's words came from, when the transcriber tells voices apart.</summary>
    public List<Transcripts.HeardBlock>? TranscriptBlocks { get; set; }

    /// <summary>Why cutting it went wrong as a whole (the transcription failed), if it did.</summary>
    public string? Error { get; set; }

    public const string RecordFile = "record.json", AudioFile = "audio.wav";
}

/// <summary>One part of a <see cref="FusionRecord"/>, as the worker was sent it, and what became of it.</summary>
public sealed class FusionRecordPart
{
    /// <summary>"generate" (a segment) or "context".</summary>
    public string Role { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Voice { get; set; }
    public System.Text.Json.Nodes.JsonObject? Settings { get; set; }

    /// <summary>Context heard from a take instead of generated: that take's audio.</summary>
    public string? Audio { get; set; }

    /// <summary>A segment: its item and the take cut out for it (null when it wasn't cut out).</summary>
    public Guid? ItemId { get; set; }
    public int? Take { get; set; }

    /// <summary>Where the worker said the part is in <c>audio.wav</c>, if it did.</summary>
    public int? SpanStartMs { get; set; }
    public int? SpanEndMs { get; set; }

    /// <summary>Where the segment's take was cut out of <c>audio.wav</c>.</summary>
    public int? CutStartMs { get; set; }
    public int? CutEndMs { get; set; }

    /// <summary>What happened to it, when it wasn't the plain case ("not cut out: …").</summary>
    public string? Note { get; set; }
}

/// <summary>How a project stores its audio (takes, recordings, imported WAVs).</summary>
public static class AudioFormats
{
    /// <summary>Uncompressed, as the worker wrote it.</summary>
    public const string Wav = "wav";

    /// <summary>Ogg Opus, about 6 to 10 times smaller (see Audio/Decoding/OggOpus.cs).</summary>
    public const string Opus = "opus";

    public static string ExtensionOf(string format) => format == Opus ? ".opus" : ".wav";
}
