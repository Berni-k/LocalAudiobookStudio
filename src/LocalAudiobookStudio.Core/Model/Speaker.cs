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

    public List<string> MarkerAliases { get; set; } = new();

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
