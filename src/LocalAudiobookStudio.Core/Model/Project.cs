using LocalAudiobookStudio.Core.IO;

namespace LocalAudiobookStudio.Core.Model;

/// <summary>An open project: the project file plus its chapters and speakers, in memory.</summary>
public sealed class Project
{
    public Project(ProjectPaths paths, ProjectFile file)
    {
        Paths = paths;
        File = file;
    }

    public ProjectPaths Paths { get; }
    public ProjectFile File { get; }

    /// <summary>Nothing is saved into the project folder (see <see cref="ProjectFile.ReadOnly"/>).</summary>
    public bool IsReadOnly => File.ReadOnly;

    /// <summary>Chapters in reading order. Keep <see cref="ProjectFile.ChapterOrder"/> in sync via <see cref="SyncChapterOrder"/>.</summary>
    public List<Chapter> Chapters { get; } = new();

    public Dictionary<string, Speaker> Speakers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProjectDefaults Defaults => File.Defaults;

    public void SyncChapterOrder() => File.ChapterOrder = Chapters.Select(c => c.Id).ToList();

    public Speaker? FindSpeaker(string? id) =>
        id != null && Speakers.TryGetValue(id, out var s) ? s : null;

    /// <summary>Finds a speaker by id, display name or marker alias (case-insensitive).</summary>
    public Speaker? FindSpeakerByLabel(string label)
    {
        if (Speakers.TryGetValue(label, out var s)) return s;
        foreach (var sp in Speakers.Values)
        {
            if (string.Equals(sp.DisplayName, label, StringComparison.OrdinalIgnoreCase)) return sp;
            if (sp.MarkerAliases.Any(a => string.Equals(a, label, StringComparison.OrdinalIgnoreCase))) return sp;
        }
        return null;
    }

    public (Chapter chapter, int index)? FindItem(Guid itemId)
    {
        foreach (var c in Chapters)
        {
            var i = c.Items.FindIndex(x => x.Id == itemId);
            if (i >= 0) return (c, i);
        }
        return null;
    }

    public IEnumerable<TimelineItem> AllItems() => Chapters.SelectMany(c => c.Items);

    /// <summary>Returns a speaker id not yet in use, based on the given label.</summary>
    public string UniqueSpeakerId(string label)
    {
        var baseId = Speaker.MakeId(label);
        var id = baseId;
        for (int n = 2; Speakers.ContainsKey(id); n++) id = $"{baseId}-{n}";
        return id;
    }
}
