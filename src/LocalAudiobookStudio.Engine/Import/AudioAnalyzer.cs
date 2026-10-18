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
            var steps = new StepProgress(listeners);
            while (!done.Task.IsCompleted)
            {
                await Task.WhenAny(done.Task, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
                if (!done.Task.IsCompleted) progress?.Report(steps.Of(job));
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

    /// <summary>
    /// Progress over the whole chain of jobs <see cref="Transcription.Listen"/> makes (the hearer, then the aligner's
    /// windows, then the diarizer): the message is two lines, which step runs ("Step 2 of 3: Timing the words"), then
    /// what its worker says. The bar moves through the steps in equal shares.
    /// </summary>
    private sealed class StepProgress(Listeners listeners)
    {
        private readonly string[] _names = new[]
        {
            listeners.Hearer == null ? null : listeners.Hearer.CanDiarize && listeners.Diarizer == null ? "Hearing the words and voices" : "Hearing the words",
            listeners.Aligner == null ? null : "Timing the words",
            listeners.Diarizer == null ? null : "Telling the voices apart",
        }.OfType<string>().ToArray();

        public (double Fraction, string Message) Of(GenerationJob first)
        {
            var job = first;
            while (job.ContinuedBy is { } next) job = next;
            var mode = job.Transcribe?.Mode;
            var name = mode == ListenMode.Diarize ? "Telling the voices apart" : mode == ListenMode.Align ? "Timing the words" : _names.FirstOrDefault() ?? "Listening";
            var step = Math.Max(0, Array.IndexOf(_names, name));
            var within = Math.Clamp(job.Part is { } part ? (part.Number - 1 + job.Progress) / part.Of : job.Progress, 0, 1);
            var fraction = (step + within) / Math.Max(1, _names.Length);
            var detail = job.State == JobState.Queued
                ? "Waiting for a worker…"
                : (job.Part is { } p ? $"Part {p.Number} of {p.Of}" + (job.ProgressMessage is { } m ? $": {m}" : "") : job.ProgressMessage) ?? "Working…";
            var header = _names.Length > 1 ? $"Step {step + 1} of {_names.Length}: {name}" : name;
            return (fraction, $"{header}\n{detail}");
        }
    }
}
