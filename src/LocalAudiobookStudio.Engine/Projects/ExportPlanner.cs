using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Engine.Export;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>What an export covers: chapter ranges on the timeline and the output path.</summary>
public static class ExportPlanner
{
    /// <summary>
    /// The chapters to render, in order, as ranges of the timeline. The last one ends a second after its
    /// last item rather than after the chapter gap.
    /// </summary>
    public static List<ExportChapter> Chapters(IReadOnlyList<Chapter> chapters, TimelineLayout layout, IEnumerable<int> chapterIndices)
    {
        var result = new List<ExportChapter>();
        var lastIdx = -1;
        foreach (var idx in chapterIndices)
        {
            var start = layout.ChapterStarts[idx];
            var end = idx + 1 < layout.ChapterStarts.Length ? layout.ChapterStarts[idx + 1] : layout.TotalMs;
            result.Add(new ExportChapter(chapters[idx].Title, start, end));
            lastIdx = idx;
        }
        if (result.Count > 0)
        {
            var last = result[^1];
            var lastItemEnd = layout.Items.Where(t => t.ChapterIndex == lastIdx).Select(t => t.EndMs).DefaultIfEmpty(last.EndMs).Max();
            if (lastItemEnd < last.EndMs && lastItemEnd > last.StartMs) result[^1] = last with { EndMs = lastItemEnd + 1000 };
        }
        return result;
    }

    /// <summary>
    /// File names for a per-segment export of these chapters, for every section with audio (generated or not, so
    /// numbers don't shift as generation goes on). A section's export id names it; others get their number in the
    /// chapter (1, 2, 3, counting sections with audio), after the chapter title when the project has more than one
    /// chapter. A name already taken gets "-2", "-3", … until it is unique.
    /// </summary>
    public static List<(TimelineItem Item, string Name)> SegmentNames(IReadOnlyList<Chapter> chapters, IEnumerable<int> chapterIndices)
    {
        var result = new List<(TimelineItem, string)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idx in chapterIndices)
        {
            var chapter = chapters[idx];
            var number = 0;
            foreach (var item in chapter.Items.Where(i => i.HasAudio))
            {
                number++;
                var name = !string.IsNullOrWhiteSpace(item.ExportId) ? AudiobookExporter.SafeFileName(item.ExportId.Trim())
                    : chapters.Count > 1 ? $"{AudiobookExporter.SafeFileName(chapter.Title)} - {number}"
                    : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var unique = name;
                for (var n = 2; !used.Add(unique); n++) unique = $"{name}-{n}";
                result.Add((item, unique));
            }
        }
        return result;
    }

    /// <summary>
    /// The sections of these chapters that have audio, each alone from 0 with its trims, gain and fades (a
    /// background plays its own length, not the stretch it covers in the book). Sections without audio yet are
    /// left out and counted in <c>Skipped</c>.
    /// </summary>
    public static (List<ExportSegment> Segments, int Skipped) Segments(ProjectEngine engine, IEnumerable<int> chapterIndices)
    {
        var segments = new List<ExportSegment>();
        var skipped = 0;
        foreach (var (item, name) in SegmentNames(engine.Project.Chapters, chapterIndices))
        {
            if (engine.Takes.SourceOf(item) is not { } src)
            {
                skipped++;
                continue;
            }
            var (trimStart, trimEnd) = ProjectMixPlanBuilder.Trims(item, src);
            var length = TimelineCalculator.TrimmedMs(src.RawDurationMs, trimStart, trimEnd);
            var gain = ProjectMixPlanBuilder.EffectiveGainDb(item, src, engine.Project.Defaults.TargetLoudnessLufs);
            var mix = new MixItem(item.Id, 0, length, src.Path, trimStart, gain, item.FadeInMs ?? 0, item.FadeOutMs ?? 0);
            segments.Add(new ExportSegment(name, new MixPlan([mix], length)));
        }
        return (segments, skipped);
    }

    /// <summary>
    /// True when a chapter has nothing estimated, stale or failed: all its audio is generated and current.
    /// </summary>
    public static bool IsComplete(ProjectEngine engine, TimelineLayout layout, int chapterIndex) =>
        engine.Project.Chapters[chapterIndex].Items.All(i =>
            (!i.HasAudio || layout.Find(i.Id) is not { IsEstimate: true })
            && engine.StateOf(i) is not (SegmentAudioState.Stale or SegmentAudioState.Failed));

    /// <summary>The output file (single-file formats) or base path (per-chapter formats) in a folder.</summary>
    public static string OutputPath(ExportFormat format, string folder, string title, string? chapterTitle = null)
    {
        var baseName = AudiobookExporter.SafeFileName(string.IsNullOrWhiteSpace(title) ? "audiobook" : title);
        if (chapterTitle != null) baseName += " - " + AudiobookExporter.SafeFileName(chapterTitle);
        return format switch
        {
            ExportFormat.M4b => Path.Combine(folder, baseName + ".m4b"),
            ExportFormat.Mp3Single => Path.Combine(folder, baseName + ".mp3"),
            _ => Path.Combine(folder, baseName),
        };
    }
}
