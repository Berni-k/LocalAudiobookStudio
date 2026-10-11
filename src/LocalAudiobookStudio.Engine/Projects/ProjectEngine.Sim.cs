using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>Identifies the job that scores a take's voice; <see cref="WavPaths"/> are the 16 kHz copies it listens to.</summary>
public sealed record SimJobTag(ProjectEngine Engine, Guid ItemId, int Take, IReadOnlyList<string> WavPaths, SimScoreInfo Check);

public sealed partial class ProjectEngine
{
    // ================================================================== SIM scores

    /// <summary>The speaker verification worker of a segment's speaker, or null when it has none.</summary>
    public WorkerManifest? SimScorerFor(TimelineItem item) =>
        item.Kind == ItemKind.Tts && Project.FindSpeaker(item.Speaker) is { } sp ? SimScorerOf(sp) : null;

    private WorkerManifest? SimScorerOf(Speaker speaker) =>
        speaker.SimScorer.Length > 0 && Catalog.Get(speaker.SimScorer) is { VerifiesSpeaker: true } m ? m : null;

    /// <summary>
    /// The speaker's voice clip when its worker clones voices from one (the clip a take should sound like), else null.
    /// </summary>
    public string? VoiceClipOf(Speaker speaker) =>
        Catalog.Get(speaker.Worker) is { VoiceCloning: true } && ResolveVoice(speaker.Voice) is { } voice && File.Exists(voice) ? voice : null;

    /// <summary>
    /// What a speaker's voice scores are worth: the checker, its model and what takes are compared with. A take scored
    /// with another key is out of date. Null when the speaker has no usable checker.
    /// </summary>
    public string? SimScoreKey(Speaker speaker)
    {
        if (SimScorerOf(speaker) is not { } checker) return null;
        var model = MergeSettings(speaker.SimScorerSettings, null, checker)["model"]?.ToString() ?? "";
        var clip = VoiceClipOf(speaker);
        var reference = clip == null ? "takes" : $"clip:{Path.GetFileName(clip)}:{new FileInfo(clip).Length}:{File.GetLastWriteTimeUtc(clip).Ticks}";
        return $"{checker.Id}|{model}|{reference}";
    }

    /// <summary>Whether a take's voice score was worked out the way the speaker is checked now.</summary>
    public bool IsSimScoreCurrent(Speaker speaker, TakeInfo info) =>
        info.SimScore != null && info.SimCheck?.Key is { } key && key == SimScoreKey(speaker);

    /// <summary>
    /// The job that scores a take's voice with its speaker's SIM scorer, or null when the speaker has none, it can't run
    /// here, or there is nothing to compare the take with yet (no voice clip, and no other take of the speaker). Decodes the
    /// take and its references into the 16 kHz copies the worker reads.
    /// </summary>
    public GenerationJob? BuildSimScoreJob(TimelineItem item, int take, int chapterIndex, int itemIndex)
    {
        if (SimScorerFor(item) is not { } checker || Project.FindSpeaker(item.Speaker) is not { } speaker) return null;
        if (Host.WhyWorkerCannotRun(checker.Id) is { } why)
        {
            Host.Log.LogInformation("Not checking the voice of {Item}: {Why}", item.Id, why);
            return null;
        }
        var audio = Takes.TakeAudio(item.Id, take);
        if (!File.Exists(audio) || SimScoreKey(speaker) is not { } key) return null;

        string against;
        List<string> referenceAudio;
        if (VoiceClipOf(speaker) is { } clip)
        {
            referenceAudio = [clip];
            against = $"voice clip {Path.GetFileName(clip)}";
        }
        else
        {
            var candidates = Project.AllItems()
                .Where(i => i.Kind == ItemKind.Tts && string.Equals(i.Speaker, item.Speaker, StringComparison.OrdinalIgnoreCase)
                            && i.SelectedTake != null && Takes.Find(i.Id, i.SelectedTake) != null)
                .Select(i => new ReferenceCandidate(i.Id, i.SelectedTake!.Value, Takes.Find(i.Id, i.SelectedTake)!.CreatedUtc));
            var chosen = SpeakerVerification.ChooseReferences(candidates, item.Id)
                .Select(c => Takes.TakeAudio(c.ItemId, c.Take)).Where(File.Exists).ToList();
            if (chosen.Count == 0) return null;
            referenceAudio = chosen;
            against = chosen.Count == 1 ? "1 other take of the speaker" : $"{chosen.Count} other takes of the speaker";
        }

        var dir = Path.Combine(Paths.CacheDir, "voice");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var files = new List<VoiceFile>();
        var wavs = new List<string>();
        try
        {
            for (var i = 0; i < referenceAudio.Count; i++)
            {
                var wav = Path.Combine(dir, $"{id}-ref{i}.wav");
                wavs.Add(wav);
                Transcription.PrepareAudio(referenceAudio[i], wav);
                files.Add(new VoiceFile { Path = wav, Role = VoiceRoles.Reference });
            }
            var own = Path.Combine(dir, $"{id}-take.wav");
            wavs.Add(own);
            Transcription.PrepareAudio(audio, own);
            files.Add(new VoiceFile { Path = own, Role = VoiceRoles.Candidate });
        }
        catch
        {
            foreach (var w in wavs) try { File.Delete(w); } catch (IOException) { /* cache */ }
            throw;
        }

        var settings = MergeSettings(speaker.SimScorerSettings, null, checker);
        var label = item.Text!.Length > 50 ? item.Text[..47] + "…" : item.Text;
        var check = new SimScoreInfo
        {
            Worker = checker.Id,
            Model = settings["model"]?.ToString() ?? "",
            Against = against,
            Key = key,
        };
        return SpeakerVerification.Job(checker, files, settings, $"{chapterIndex + 1}.{itemIndex + 1}  Speaker SIM Score: {label}", JobPriority.Low,
            new SimJobTag(this, item.Id, take, wavs, check), chapterIndex * 1_000_000L + itemIndex);
    }

    public bool OwnsSimScore(GenerationJob job) => job.Tag is SimJobTag tag && ReferenceEquals(tag.Engine, this);

    /// <summary>
    /// Saves a finished SIM score in the take's own file and removes the job's 16 kHz copies. Returns the item and take
    /// it belongs to, or null when the job failed or the take is gone.
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
    /// The segments whose selected take sounds well below its speaker's other scored takes
    /// (<see cref="SpeakerVerification.LowerBound"/>). Only scores worked out the way the speaker is checked now count, so
    /// scores of different models or voice clips are never mixed. Takes with little speech aren't flagged.
    /// </summary>
    public HashSet<Guid> FlaggedSims()
    {
        var flagged = new HashSet<Guid>();
        var bySpeaker = Project.AllItems()
            .Where(i => i.Kind == ItemKind.Tts && i.Speaker != null)
            .GroupBy(i => i.Speaker!, StringComparer.OrdinalIgnoreCase);
        foreach (var group in bySpeaker)
        {
            if (Project.FindSpeaker(group.Key) is not { } speaker || SimScoreKey(speaker) is not { } key) continue;
            var scored = group.Select(i => (Item: i, Info: Takes.Selected(i)))
                .Where(x => x.Info is { SimScore: not null } info && info.SimCheck?.Key == key)
                .ToList();
            // One baseline for the speaker: the take's own score is in it, which makes flagging a little more cautious.
            if (scored.Count <= SpeakerVerification.MinBaseline) continue;
            if (SpeakerVerification.LowerBound(scored.Select(x => x.Info!.SimScore!.Value).ToList()) is not { } bound) continue;
            foreach (var (item, info) in scored)
                if (info!.SimScore < bound && (info.SimSpeechMs ?? 0) >= SpeakerVerification.MinSpeechMs) flagged.Add(item.Id);
        }
        return flagged;
    }
}
