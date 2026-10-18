using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Engine;

/// <summary>An imported recording as project content: chapters of new segments, and where each spoken one is in the recording.</summary>
public sealed record ImportedChapters(List<Chapter> Chapters, List<(TimelineItem Item, SegmentSpan Span)> Segments);

/// <summary>Turns an analysed recording (<see cref="AudioStructure"/>) into project content, with the original audio as take 1.</summary>
public static class AudioImporter
{
    /// <summary>Audio kept before a segment's first word and after its last (never past the middle of the gap to the next one).</summary>
    public const int PaddingMs = 40;

    /// <summary>
    /// The chapters the structure describes, as new items (not yet in a project); speakers are the project's ids for the
    /// structure's labels (null label: <paramref name="speakerIdForLabel"/> decides, normally the default speaker).
    /// </summary>
    public static ImportedChapters ToChapters(AudioStructure structure, Func<string?, string> speakerIdForLabel)
    {
        var chapters = TextImporter.ToChapters(structure.Result, speakerIdForLabel);
        var segments = new List<(TimelineItem, SegmentSpan)>();
        // (One new item per imported item, in order.)
        for (var c = 0; c < chapters.Count; c++)
            for (var i = 0; i < chapters[c].Items.Count; i++)
                if (structure.Spans.TryGetValue(structure.Result.Chapters[c].Items[i], out var span))
                    segments.Add((chapters[c].Items[i], span));
        return new ImportedChapters(chapters, segments);
    }

    /// <summary>
    /// Cuts every segment's piece out of the original audio and saves it as the segment's first take
    /// (<see cref="ProjectEngine.SaveImportedTake"/>). The items' speakers must exist in the project. A failure or cancel removes
    /// what was written. Runs for a while: call it off the UI thread.
    /// </summary>
    public static async Task WriteTakesAsync(ProjectEngine engine, AudioSource source, IReadOnlyList<(TimelineItem Item, SegmentSpan Span)> segments,
        string listenedBy, string? language, bool lockTakes, IProgress<(double Fraction, string Message)>? progress, CancellationToken ct)
    {
        if (source.OriginalWav is not { } original) throw new InvalidOperationException("The original audio was not kept.");
        var ordered = segments.OrderBy(s => s.Span.StartMs).ToList();
        var written = new List<TimelineItem>();
        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < ordered.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (item, span) = ordered[i];
                    var from = Math.Max(0, span.StartMs - PaddingMs);
                    var to = span.EndMs + PaddingMs;
                    if (i > 0) from = Math.Max(from, (ordered[i - 1].Span.EndMs + span.StartMs) / 2);
                    if (i + 1 < ordered.Count) to = Math.Min(to, (span.EndMs + ordered[i + 1].Span.StartMs) / 2);
                    to = Math.Max(to, from + 1);

                    var slice = Path.Combine(source.Folder, "slice.wav");
                    AlignWindows.Slice(original, from, to, slice);
                    // The words are timed from the start of the piece.
                    var words = span.Words.Select(w => new HeardWord(w.Text, Math.Max(0, w.StartMs - from), Math.Max(0, w.EndMs - from), w.Confidence, w.Speaker, w.Block)).ToList();
                    engine.SaveImportedTake(item, slice, words, language, listenedBy, lockTakes);
                    written.Add(item);
                    progress?.Report(((i + 1d) / ordered.Count, $"Cutting the audio: {i + 1} of {ordered.Count} segments"));
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            engine.DiscardAudioOf(written);
            throw;
        }
    }

    /// <summary>The listener names for a take's transcript: "whisper + mms_fa + pyannote".</summary>
    public static string ListenedBy(Listeners listeners) => listeners.Names;
}
