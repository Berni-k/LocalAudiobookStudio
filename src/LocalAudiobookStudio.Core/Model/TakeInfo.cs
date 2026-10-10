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

    /// <summary>Set when the take was cut out of a fused generation (several segments, or context, in one go).</summary>
    public TakeFusion? Fusion { get; set; }

    public static string FileStem(int take) => $"take-{take:0000}";

    /// <summary>The audio file's name: take-NNNN.wav or take-NNNN.opus.</summary>
    public string FileName => FileStem(Take) + AudioFormats.ExtensionOf(Format);
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

    public const string Worker = "worker", Transcript = "transcript";
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
