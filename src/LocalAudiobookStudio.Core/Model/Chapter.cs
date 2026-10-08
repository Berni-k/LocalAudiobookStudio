using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Core.Model;

/// <summary>Contents of <c>chapters/&lt;id&gt;.json</c>. The item list defines reading order.</summary>
public sealed class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public List<TimelineItem> Items { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    [JsonStringEnumMemberName("tts")] Tts,
    [JsonStringEnumMemberName("clip")] Clip,
    [JsonStringEnumMemberName("pause")] Pause,
    /// <summary>A note in the book (its text is in <see cref="TimelineItem.Text"/>): never spoken, takes no time.</summary>
    [JsonStringEnumMemberName("comment")] Comment,
    /// <summary>
    /// A segment of a kind this version doesn't know (made by a newer version). It keeps its JSON
    /// (<see cref="TimelineItem.Raw"/>), shows it, plays nothing and takes no time; saving writes it back unchanged.
    /// </summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>How long a background item plays (it starts together with the next section).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackgroundLength>))]
public enum BackgroundLength
{
    /// <summary>Its own natural length.</summary>
    [JsonStringEnumMemberName("full")] Full,
    /// <summary>At most <see cref="TimelineItem.BackgroundLimit"/> seconds.</summary>
    [JsonStringEnumMemberName("seconds")] Seconds,
    /// <summary>Until the end of the next <see cref="TimelineItem.BackgroundLimit"/> sections.</summary>
    [JsonStringEnumMemberName("sections")] Sections,
    /// <summary>Until the end of the chapter's last section.</summary>
    [JsonStringEnumMemberName("chapter")] ChapterEnd,
}

/// <summary>
/// One entry in a chapter's timeline. A single class covers all four kinds; fields that
/// don't apply to a kind stay null and are omitted from JSON.
/// </summary>
public sealed class TimelineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemKind Kind { get; set; } = ItemKind.Tts;

    // --- tts ---
    public string? Text { get; set; }
    public string? Speaker { get; set; }
    public JsonObject? SettingsOverride { get; set; }

    /// <summary>The length asked of the worker, for workers that take one (<c>[length: 4s]</c> in imported text).</summary>
    public double? DurationSec { get; set; }

    // --- clip ---
    public string? ClipFile { get; set; }
    public string? Label { get; set; }

    // --- any kind with audio ---
    /// <summary>
    /// The file name this section gets in a per-segment export (<c>[id: name]</c> in imported text). Null: it is
    /// numbered by its place in the chapter.
    /// </summary>
    public string? ExportId { get; set; }

    // --- pause ---
    /// <summary>Replaces the default gap at this point. Negative values make the neighbours overlap.</summary>
    public int? DurationMs { get; set; }

    // --- unknown ---
    /// <summary>For <see cref="ItemKind.Unknown"/>: the segment's JSON as it was read (written back by <c>TimelineItemConverter</c>).</summary>
    [JsonIgnore]
    public JsonObject? Raw { get; set; }

    // --- generated (speaker segments) ---
    public int? SelectedTake { get; set; }
    public string? InputHash { get; set; }

    /// <summary>Last generation error; null when the last attempt succeeded.</summary>
    public string? LastError { get; set; }

    // --- shared by all kinds ---
    public double GainDb { get; set; }

    /// <summary>
    /// Deprecated and ignored by the timeline: gaps always come from the project defaults, and a
    /// pause item (possibly negative) changes the spacing. Kept so older project files still load.
    /// </summary>
    public int? GapAfterMs { get; set; }

    /// <summary>
    /// Background: starts together with the next section and takes no time of its own on the
    /// timeline (ambience, music beds). Not available for pauses.
    /// </summary>
    public bool? Background { get; set; }

    /// <summary>How long a background item plays; null = <see cref="Model.BackgroundLength.Full"/>.</summary>
    public BackgroundLength? BackgroundLength { get; set; }

    /// <summary>Seconds or section count for <see cref="BackgroundLength"/>.</summary>
    public double? BackgroundLimit { get; set; }

    /// <summary>Loop the audio to fill the background's length (not used with the full length).</summary>
    public bool? BackgroundLoop { get; set; }

    /// <summary>Null uses the selected take's detected silence trim.</summary>
    public int? TrimStartMs { get; set; }

    /// <summary>Null uses the selected take's detected silence trim.</summary>
    public int? TrimEndMs { get; set; }

    public int? FadeInMs { get; set; }
    public int? FadeOutMs { get; set; }

    /// <summary>Loudness normalization; null means on.</summary>
    public bool? Normalize { get; set; }

    public bool Locked { get; set; }

    [JsonIgnore]
    public bool IsGenerated => Kind == ItemKind.Tts;

    [JsonIgnore]
    public bool IsBackground => Background == true && Kind is not (ItemKind.Pause or ItemKind.Comment or ItemKind.Unknown);

    /// <summary>False for pauses, comments and unknown segments: they play nothing.</summary>
    [JsonIgnore]
    public bool HasAudio => Kind is not (ItemKind.Pause or ItemKind.Comment or ItemKind.Unknown);

    /// <summary>Comments and unknown segments: no time on the timeline, and no gap around them.</summary>
    [JsonIgnore]
    public bool TakesNoTime => Kind is ItemKind.Comment or ItemKind.Unknown;

    /// <summary>Background length mode, with the defaults applied.</summary>
    [JsonIgnore]
    public BackgroundLength EffectiveBackgroundLength => BackgroundLength ?? Model.BackgroundLength.Full;

    /// <summary>Text shown in the row: the segment's text, clip label, pause length or comment.</summary>
    [JsonIgnore]
    public string DisplayText => Kind switch
    {
        ItemKind.Tts => Text ?? "",
        ItemKind.Clip => Label ?? Path.GetFileName(ClipFile ?? ""),
        ItemKind.Pause when DurationMs < 0 => $"Overlap {-(DurationMs ?? 0) / 1000.0:0.##} s",
        ItemKind.Pause => $"Pause {(DurationMs ?? 0) / 1000.0:0.##} s",
        ItemKind.Comment => Text ?? "",
        ItemKind.Unknown => Raw?.ToJsonString() ?? "",
        _ => ""
    };

    /// <summary>Deep copy with the same id (used for undo snapshots).</summary>
    public TimelineItem Clone() => Serialization.CoreJson.Clone(this);

    /// <summary>Copies every field except <see cref="Id"/> from a snapshot, keeping this object's identity.</summary>
    public void CopyFrom(TimelineItem o)
    {
        Kind = o.Kind;
        Raw = o.Raw?.DeepClone() as JsonObject;
        Text = o.Text;
        Speaker = o.Speaker;
        SettingsOverride = o.SettingsOverride?.DeepClone() as JsonObject;
        DurationSec = o.DurationSec;
        ClipFile = o.ClipFile;
        Label = o.Label;
        ExportId = o.ExportId;
        DurationMs = o.DurationMs;
        SelectedTake = o.SelectedTake;
        InputHash = o.InputHash;
        LastError = o.LastError;
        GainDb = o.GainDb;
        GapAfterMs = o.GapAfterMs;
        Background = o.Background;
        BackgroundLength = o.BackgroundLength;
        BackgroundLimit = o.BackgroundLimit;
        BackgroundLoop = o.BackgroundLoop;
        TrimStartMs = o.TrimStartMs;
        TrimEndMs = o.TrimEndMs;
        FadeInMs = o.FadeInMs;
        FadeOutMs = o.FadeOutMs;
        Normalize = o.Normalize;
        Locked = o.Locked;
    }

    /// <summary>Deep copy with a fresh id and no generated audio (used for paste/duplicate).</summary>
    public TimelineItem CloneAsNew()
    {
        var c = Clone();
        c.Id = Guid.NewGuid();
        c.SelectedTake = null;
        c.InputHash = null;
        c.LastError = null;
        return c;
    }
}
