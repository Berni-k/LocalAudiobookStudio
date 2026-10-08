using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.Import;

public sealed class ImportOptions
{
    public const string DefaultChapterRegex = @"^(chapter|part|prologue|epilogue)\b.*$";

    /// <summary>Case-insensitive regex for chapter headings. Lines starting with '#' are always headings.</summary>
    public string ChapterRegex { get; set; } = DefaultChapterRegex;

    /// <summary>Segments longer than this are split at sentence boundaries.</summary>
    public int MaxSegmentChars { get; set; } = 1500;

    /// <summary>Optional per-speaker limit (from the speaker's worker manifest). Receives the speaker label (null = default speaker).</summary>
    public Func<string?, int?>? MaxCharsForSpeaker { get; set; }

    /// <summary>Treat every line as its own paragraph (for files without blank lines).</summary>
    public bool EveryLineIsParagraph { get; set; }

    /// <summary>Also create a spoken segment for each chapter heading.</summary>
    public bool SpeakHeadings { get; set; } = true;

    /// <summary>Headings longer than this are treated as normal text.</summary>
    public int MaxHeadingChars { get; set; } = 120;

    /// <summary>Names allowed in the "Name: text" prefix format (case-insensitive).</summary>
    public IReadOnlyCollection<string> KnownSpeakerNames { get; set; } = Array.Empty<string>();

    /// <summary>Title for text that comes before the first heading (or for a file with no headings).</summary>
    public string DefaultChapterTitle { get; set; } = "Chapter 1";
}

/// <summary>One parsed item before speakers are mapped to project speakers.</summary>
public sealed class ImportedItem
{
    public ItemKind Kind { get; init; } = ItemKind.Tts;
    public string? Text { get; set; }

    /// <summary>The speaker label as written in the file; null means the default speaker.</summary>
    public string? SpeakerLabel { get; set; }

    /// <summary>From <c>[length: 4s]</c>: the length asked of the worker.</summary>
    public double? DurationSec { get; set; }
    public int? DurationMs { get; init; }
    public string? ClipFile { get; init; }
    public bool IsHeading { get; init; }

    /// <summary>From <c>[id: name]</c>: the file name in a per-segment export.</summary>
    public string? ExportId { get; set; }
}

public sealed class ImportedChapter
{
    public string Title { get; set; } = "";
    public List<ImportedItem> Items { get; } = new();
}

public sealed class ImportResult
{
    public List<ImportedChapter> Chapters { get; } = new();

    /// <summary>Distinct speaker labels in order of first appearance (excludes the default speaker).</summary>
    public List<string> SpeakerLabels { get; } = new();

    public int SegmentCount => Chapters.Sum(c => c.Items.Count);
    /// <summary>Characters of spoken text (comments are not spoken).</summary>
    public int TextLength => Chapters.Sum(c => c.Items.Where(i => i.Kind != ItemKind.Comment).Sum(i => i.Text?.Length ?? 0));
}
