using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>Identifies the job that scores a take's voice; <see cref="WavPaths"/> are the 16 kHz copies it listens to.</summary>
public sealed record SimJobTag(ProjectEngine Engine, Guid ItemId, int Take, IReadOnlyList<string> WavPaths, SimScoreInfo Check);

/// <summary>Where a speaker's reference clip comes from.</summary>
/// <param name="Path">The clip's file.</param>
/// <param name="FromGeneration">It is the reference clip of Generation, not one chosen for speaker verification.</param>
public sealed record SimReference(string Path, bool FromGeneration);

public sealed partial class ProjectEngine
{
    // ================================================================== SIM scores

    /// <summary>
    /// The speaker verification worker that scores a speaker's takes: its chosen one, else the first in the catalog. Null
    /// when verification is off or there is no such worker.
    /// </summary>
    public WorkerManifest? SimScorerOf(Speaker speaker)
    {
        if (!speaker.SimEnabled) return null;
        return speaker.SimScorer.Length > 0
            ? Catalog.Get(speaker.SimScorer) is { VerifiesSpeaker: true } chosen ? chosen : null
            : Catalog.SimScorers.FirstOrDefault();
    }

    /// <summary>
    /// The clip a speaker's takes are compared with: the one chosen for speaker verification, else the reference clip of
    /// Generation when the speaker's worker clones from one. Null when there is neither (nothing is scored).
    /// </summary>
    public SimReference? SimReferenceOf(Speaker speaker)
    {
        if (ResolveVoice(speaker.SimReference) is { } own && File.Exists(own)) return new SimReference(own, false);
        if (Catalog.Get(speaker.Worker) is { VoiceCloning: true } && ResolveVoice(speaker.Voice) is { } voice && File.Exists(voice))
            return new SimReference(voice, true);
        return null;
    }

    /// <summary>
    /// What a speaker's scores are worth: the scorer, its model and the reference clip (its name, size and time). A take
    /// scored with another key is out of date. Null when the speaker can't be scored.
    /// </summary>
    public string? SimScoreKey(Speaker speaker)
    {
        if (SimScorerOf(speaker) is not { } scorer || SimReferenceOf(speaker) is not { } reference) return null;
        var model = MergeSettings(speaker.SimScorerSettings, null, scorer)["model"]?.ToString() ?? "";
        var info = new FileInfo(reference.Path);
        return $"{scorer.Id}|{model}|{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>Whether a take's score was worked out the way the speaker is scored now.</summary>
    public bool IsSimScoreCurrent(Speaker speaker, TakeInfo info) =>
        info.SimScore != null && info.SimCheck?.Key is { } key && key == SimScoreKey(speaker);

    /// <summary>
    /// The job that scores a take against its speaker's reference clip, or null when verification is off, the scorer can't
    /// run here, or the speaker has no reference clip. Decodes the clip and the take into the 16 kHz copies the worker reads.
    /// </summary>
    public GenerationJob? BuildSimScoreJob(TimelineItem item, int take, int chapterIndex, int itemIndex)
    {
        if (item.Kind != ItemKind.Tts || Project.FindSpeaker(item.Speaker) is not { } speaker
            || SimScorerOf(speaker) is not { } scorer || SimReferenceOf(speaker) is not { } reference
            || SimScoreKey(speaker) is not { } key)
            return null;
        if (Host.WhyWorkerCannotRun(scorer.Id) is { } why)
        {
            Host.Log.LogInformation("Not scoring the voice of {Item}: {Why}", item.Id, why);
            return null;
        }
        var audio = Takes.TakeAudio(item.Id, take);
        if (!File.Exists(audio)) return null;

        var dir = Path.Combine(Paths.CacheDir, "sim");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var refWav = Path.Combine(dir, $"{id}-ref.wav");
        var takeWav = Path.Combine(dir, $"{id}-take.wav");
        try
        {
            Transcription.PrepareAudio(reference.Path, refWav);
            Transcription.PrepareAudio(audio, takeWav);
        }
        catch
        {
            foreach (var w in new[] { refWav, takeWav }) try { File.Delete(w); } catch (IOException) { /* cache */ }
            throw;
        }

        var settings = MergeSettings(speaker.SimScorerSettings, null, scorer);
        var label = item.Text!.Length > 50 ? item.Text[..47] + "…" : item.Text;
        var check = new SimScoreInfo
        {
            Worker = scorer.Id,
            Model = settings["model"]?.ToString() ?? "",
            Against = reference.FromGeneration ? $"the reference clip {Path.GetFileName(reference.Path)} (Generation)" : $"the reference clip {Path.GetFileName(reference.Path)}",
            Key = key,
        };
        VoiceFile[] files =
        [
            new() { Path = refWav, Role = VoiceRoles.Reference },
            new() { Path = takeWav, Role = VoiceRoles.Candidate },
        ];
        return SpeakerVerification.Job(scorer, files, settings, $"{chapterIndex + 1}.{itemIndex + 1}  SIM score: {label}", JobPriority.Low,
            new SimJobTag(this, item.Id, take, [refWav, takeWav], check), chapterIndex * 1_000_000L + itemIndex);
    }

    public bool OwnsSimScore(GenerationJob job) => job.Tag is SimJobTag tag && ReferenceEquals(tag.Engine, this);

    /// <summary>
    /// Saves a finished score in the take's own file and removes the job's 16 kHz copies. Returns the item and take it
    /// belongs to, or null when the job failed or the take is gone.
    /// </summary>
    public (Guid ItemId, int Take)? FinishSimScore(GenerationJob job)
    {
        if (job.Tag is not SimJobTag tag || !ReferenceEquals(tag.Engine, this)) return null;
        foreach (var wav in tag.WavPaths) try { File.Delete(wav); } catch (IOException) { /* cache */ }
        if (job.State != JobState.Completed || job.VoiceScores is not { Scores.Count: 1 } scores) return null;
        if (Takes.Find(tag.ItemId, tag.Take) is not { } info) return null;
        info.SimScore = Math.Round(scores.Scores[0], 4);
        info.SimSpeechMs = scores.SpeechMs[^1];
        info.SimCheck = tag.Check;
        TakeStore.SaveTakeInfo(Paths, tag.ItemId, info);
        return (tag.ItemId, tag.Take);
    }

    /// <summary>
    /// How a take's score stands against its speaker's thresholds, or null when it has none or the speaker is scored
    /// another way now (<see cref="IsSimScoreCurrent"/>): an old score says nothing about the voice as it is set up.
    /// </summary>
    public SimLevel? SimLevelOf(Speaker? speaker, TakeInfo? info) =>
        speaker != null && info is { SimScore: { } score } && IsSimScoreCurrent(speaker, info)
            ? SpeakerVerification.LevelOf(score, speaker.SimWarn, speaker.SimFail)
            : null;
}
