using System.Text.Json.Nodes;

namespace LocalAudiobookStudio.Core.Model;

/// <summary>A voice definition stored in <c>speakers/&lt;id&gt;.json</c>.</summary>
public sealed class Speaker
{
    /// <summary>Stable identifier; also the file name. Lowercase letters, digits, '-' and '_'.</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Color { get; set; } = "#6FA8DC";

    /// <summary>Worker id from the worker manifest (for example "kokoro").</summary>
    public string Worker { get; set; } = "";

    /// <summary>Voice reference: a file in the speakers folder, or a worker-specific voice id.</summary>
    public string? Voice { get; set; }

    /// <summary>Opaque settings passed to the worker; editors are generated from the worker manifest.</summary>
    public JsonObject Settings { get; set; } = new();

    /// <summary>
    /// Worker id of the transcription worker that listens to this speaker's audio (empty: none). Independent of
    /// <see cref="Worker"/>, so a recorded speaker can have one too.
    /// </summary>
    public string Transcriber { get; set; } = "";

    /// <summary>Settings for <see cref="Transcriber"/>, edited from its manifest like <see cref="Settings"/>.</summary>
    public JsonObject TranscriberSettings { get; set; } = new();

    /// <summary>
    /// Word times come from <see cref="Aligner"/> rather than <see cref="Transcriber"/>: a worker tagged Alignment that
    /// times words more accurately (cutting fused generations). Off: the transcriber's own times, if it gives any.
    /// </summary>
    public bool UseAligner { get; set; }

    /// <summary>Worker id of the aligner (kept while <see cref="UseAligner"/> is off, so turning it back on keeps the choice).</summary>
    public string Aligner { get; set; } = "";

    /// <summary>Settings for <see cref="Aligner"/>.</summary>
    public JsonObject AlignerSettings { get; set; } = new();

    public List<string> MarkerAliases { get; set; } = new();

    /// <summary>
    /// What the speaker's segments are generated with, for workers that render several lines in one go. Not part of
    /// the input hash: other context doesn't make a take stale.
    /// </summary>
    public SpeakerContext Context { get; set; } = new();

    public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    /// <summary>Turns a label like "Speaker 1" into a valid id like "speaker-1".</summary>
    public static string MakeId(string label)
    {
        var chars = label.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var id = new string(chars);
        while (id.Contains("--")) id = id.Replace("--", "-");
        id = id.Trim('-');
        return id.Length == 0 ? "speaker" : id;
    }
}

/// <summary>
/// A speaker's context settings (<see cref="Speaker.Context"/>). Only used with workers that take fused generations;
/// a segment of a group is cut out of the result by the worker's own marks or by transcription.
/// </summary>
public sealed class SpeakerContext
{
    /// <summary>
    /// The model also gets lines around the segment (<see cref="Before"/>, <see cref="After"/>), cut away again. Also
    /// what lets this speaker's lines be context for others: context ends at a speaker without it (with 0 before and
    /// 0 after, a speaker's lines are context for others but its own segments get none).
    /// </summary>
    public bool Include { get; set; }

    /// <summary>Lines before the segment the model also gets, for a delivery that follows on from them.</summary>
    public int Before { get; set; } = 2;

    /// <summary>Lines after the segment the model also gets.</summary>
    public int After { get; set; }

    /// <summary>The most characters of context on each side; lines that would go past it are left out.</summary>
    public int MaxChars { get; set; } = 600;

    /// <summary>
    /// Neighbouring segments whose speakers also allow it are generated together, as one conversation. With
    /// <see cref="Include"/>, the context goes around the whole batch (before its first and after its last segment).
    /// </summary>
    public bool Batch { get; set; }

    /// <summary>The most segments in a batch this speaker is in; null: any number.</summary>
    public int? BatchMaxSegments { get; set; }

    /// <summary>The most characters of all the batch's segments together (context not counted); null: any.</summary>
    public int? BatchMaxChars { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasContext => Include && (Before > 0 || After > 0);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Any => Batch || HasContext;

    public SpeakerContext Clone() => (SpeakerContext)MemberwiseClone();
}
