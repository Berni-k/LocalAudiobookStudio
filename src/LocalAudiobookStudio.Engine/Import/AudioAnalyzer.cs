using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Engine;

/// <summary>Listens to a whole prepared recording with the chosen workers: one job, the words (and voices) of all of it.</summary>
public static class AudioAnalyzer
{
    /// <summary>
    /// Runs <see cref="Transcription.Listen"/> on the recording (a transcriber, optionally an aligner and a diarizer) and returns
    /// what it heard. Cancelling stops the job and what continues it.
    /// </summary>
    /// <param name="voices">How many voices there are, when known (a hint for the diarizer).</param>
    public static async Task<TranscriptResult> AnalyzeAsync(GenerationScheduler scheduler, AudioSource source, Listeners listeners, int? voices,
        IProgress<(double Fraction, string Message)>? progress, CancellationToken ct)
    {
        var tag = new object();
        var job = Transcription.Listen(listeners, source.AnalysisWav, null, "Import audio", JobPriority.Normal, tag, 0, null,
                      speakerCount: voices)
                  ?? throw new InvalidOperationException("Choose a transcription worker first.");
        var done = new TaskCompletionSource<GenerationJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(GenerationJob j)
        {
            if (ReferenceEquals(j.Tag, tag)) done.TrySetResult(j);
        }
        scheduler.JobCompleted += Finished;
        scheduler.JobFailed += Finished;
        using var cancel = ct.Register(() => scheduler.CancelMany([job.Id]));
        try
        {
            scheduler.Enqueue(job);
            while (!done.Task.IsCompleted)
            {
                await Task.WhenAny(done.Task, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
                if (job.State == JobState.Running) progress?.Report((job.Progress, job.ProgressMessage ?? "Listening…"));
                else if (job.State == JobState.Completed && !done.Task.IsCompleted) progress?.Report((1, "Timing the words…"));
                else if (job.State == JobState.Queued) progress?.Report((0, "Waiting for a worker…"));
            }
            var finished = await done.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished.State == JobState.Cancelled) throw new OperationCanceledException();
            return finished.State == JobState.Completed && finished.Transcript is { } transcript
                ? transcript
                : throw new InvalidOperationException(finished.LastError ?? "The audio could not be transcribed.");
        }
        finally
        {
            scheduler.JobCompleted -= Finished;
            scheduler.JobFailed -= Finished;
        }
    }
}
