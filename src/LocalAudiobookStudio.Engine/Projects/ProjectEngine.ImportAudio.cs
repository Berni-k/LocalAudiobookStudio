using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Engine.Projects;

public sealed partial class ProjectEngine
{
    /// <summary>The "worker" of a take that is a piece of a recording that was imported.</summary>
    public const string ImportedWorker = "imported";

    /// <summary>
    /// Saves <paramref name="wavPath"/>, a piece of an imported recording, as take 1 of <paramref name="item"/>, selects it, and
    /// stores what was heard in it. The take gets the segment's <em>current</em> input hash, so it is as up to date as a
    /// generated one: any later change of the text or the speaker's voice makes it stale as usual. The item's speaker must
    /// already exist. The caller adds the item to the project.
    /// </summary>
    /// <param name="words">What was heard in the piece, timed from its start.</param>
    public TakeInfo SaveImportedTake(TimelineItem item, string wavPath, IReadOnlyList<HeardWord> words, string? language, string listenedBy, bool locked)
    {
        var dir = Paths.SegmentAudioDir(item.Id);
        Directory.CreateDirectory(dir);
        var take = TakeStore.HighestTakeNumber(dir) + 1;
        TakeStore.RecordTakeNumber(dir, take);
        var target = Paths.TakeWav(item.Id, take);
        if (!string.Equals(Path.GetFullPath(wavPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Move(wavPath, target, overwrite: true);

        var analysis = TakeAnalyzer.Analyze(target, Paths.TakePeaks(item.Id, take), Host.SilenceTrim);
        var info = new TakeInfo
        {
            Take = take,
            CreatedUtc = DateTime.UtcNow,
            InputHash = ComputeHash(item) ?? "",
            Worker = ImportedWorker,
            DurationMs = analysis.DurationMs,
            SampleRate = analysis.SampleRate,
            LoudnessLufs = analysis.LoudnessLufs is double l ? Math.Round(l, 2) : null,
            TrimStartMs = analysis.TrimStartMs,
            TrimEndMs = analysis.TrimEndMs,
            TextLength = item.Text?.Length,
            // What was said is what the text was made from: no differences.
            TranscriptErrors = 0,
            TranscriptWordCount = words.Count,
            Format = StoreAudio(target),
        };
        TakeStore.SaveTakeInfo(Paths, item.Id, info);
        Takes.Add(item.Id, info);
        new StoredTranscript { Worker = listenedBy, Language = language, Words = words.ToList() }.Save(Paths.TakeTranscript(item.Id, take));

        item.SelectedTake = take;
        item.InputHash = info.InputHash;
        item.Locked = locked;
        return info;
    }

    /// <summary>Removes the audio folders of segments that were not added to the project after all (a cancelled import).</summary>
    public void DiscardAudioOf(IEnumerable<TimelineItem> items)
    {
        foreach (var item in items)
        {
            Takes.Remove(item.Id);
            try { Directory.Delete(Paths.SegmentAudioDir(item.Id), true); } catch (IOException) { /* cache-like */ } catch (UnauthorizedAccessException) { }
        }
    }
}
