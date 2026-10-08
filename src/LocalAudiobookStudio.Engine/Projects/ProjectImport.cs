using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine.Hosting;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>Creating projects and importing text without the import dialog (defaults instead of choices).</summary>
public static class ProjectImport
{
    /// <summary>Creates a project folder with a narrator on the default worker, plus the given speakers and chapters.</summary>
    public static Project Create(StudioHost host, string folder, string title, IEnumerable<Chapter>? chapters = null,
        IEnumerable<Speaker>? speakers = null)
    {
        var defaultWorker = host.DefaultTtsWorker;
        var p = ProjectStore.Create(folder, title, defaultWorker);
        if (host.Catalog.Get(defaultWorker) is { } m) p.Speakers["narrator"].Settings = m.DefaultSettings();
        if (speakers != null)
            foreach (var sp in speakers) p.Speakers[sp.Id] = sp;
        if (chapters != null) p.Chapters.AddRange(chapters);
        ProjectStore.SaveAll(p);
        return p;
    }

    /// <summary>
    /// Import options for a project (or a new one): speakers' names and aliases are known labels, and a
    /// segment is never longer than its speaker's worker takes.
    /// </summary>
    public static ImportOptions Options(StudioHost host, Project? project, int maxSegmentChars = 1500)
    {
        var known = new List<string>();
        if (project != null)
            foreach (var sp in project.Speakers.Values)
            {
                if (!string.IsNullOrWhiteSpace(sp.DisplayName)) known.Add(sp.DisplayName);
                known.AddRange(sp.MarkerAliases);
            }
        return new ImportOptions
        {
            MaxSegmentChars = maxSegmentChars,
            KnownSpeakerNames = known.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            MaxCharsForSpeaker = label => Math.Min(maxSegmentChars, MaxCharsFor(host, project, label)),
        };
    }

    /// <summary>The character limit of the worker a label's speaker uses (the default speaker for unlabelled text).</summary>
    public static int MaxCharsFor(StudioHost host, Project? project, string? label)
    {
        string? worker = null;
        if (project != null)
        {
            var sp = label == null ? project.FindSpeaker(project.Defaults.Speaker) : project.FindSpeakerByLabel(label);
            worker = sp?.Worker;
        }
        worker ??= host.DefaultTtsWorker;
        return host.Catalog.Get(worker)?.MaxChars ?? 1500;
    }

    /// <summary>Speakers for a new project: one per label (narrator excluded), each with a different built-in voice.</summary>
    public static List<Speaker> SpeakersForLabels(StudioHost host, IEnumerable<string> labels)
    {
        var worker = host.DefaultTtsWorker;
        var manifest = host.Catalog.Get(worker);
        return labels.Select((label, i) => new Speaker
        {
            Id = Speaker.MakeId(label),
            DisplayName = label,
            Color = ProjectStore.SpeakerPalette[(i + 1) % ProjectStore.SpeakerPalette.Length],
            Worker = worker,
            Settings = manifest?.DefaultSettings(i + 1) ?? new(),
            MarkerAliases = [label],
        }).GroupBy(s => s.Id).Select(g => g.First()).Where(s => s.Id != "narrator").ToList();
    }

    /// <summary>
    /// Maps speaker labels to an open project's speakers: a speaker whose id, name or alias matches, else a
    /// new speaker on the default speaker's worker. Unlabelled text goes to the default speaker.
    /// </summary>
    public static Func<string?, string> MapLabels(ProjectEngine engine, IEnumerable<string> labels)
    {
        var project = engine.Project;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var worker = project.FindSpeaker(project.Defaults.Speaker)?.Worker ?? engine.Host.DefaultTtsWorker;
        foreach (var label in labels)
        {
            var sp = project.FindSpeakerByLabel(label) ?? engine.AddSpeaker(label, worker, project.Speakers.Count);
            map[label] = sp.Id;
        }
        return label => label != null && map.TryGetValue(label, out var id) ? id : project.Defaults.Speaker;
    }
}
