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
    /// windows, then the diarizer): which step runs, how far it is, and about how long it still takes. The bar moves
    /// through the steps in equal shares.
    /// </summary>
    private sealed class StepProgress(Listeners listeners)
    {
        private readonly string[] _names = new[]
        {
            listeners.Hearer == null ? null : listeners.Hearer.CanDiarize && listeners.Diarizer == null ? "Hearing the words and voices" : "Hearing the words",
            listeners.Aligner == null ? null : "Timing the words",
            listeners.Diarizer == null ? null : "Telling the voices apart",
        }.OfType<string>().ToArray();

        private int _step = -1;
        private DateTime _stepStartedUtc;

        public (double Fraction, string Message) Of(GenerationJob first)
        {
            var job = first;
            while (job.ContinuedBy is { } next) job = next;
            var mode = job.Transcribe?.Mode;
            var name = mode == ListenMode.Diarize ? "Telling the voices apart" : mode == ListenMode.Align ? "Timing the words" : _names.FirstOrDefault() ?? "Listening";
            var step = Math.Max(0, Array.IndexOf(_names, name));
            // A step's time counts from when its first job starts running (not while it waits or its worker loads).
            if (step != _step || _stepStartedUtc == default)
            {
                _step = step;
                _stepStartedUtc = job.State == JobState.Running && job.Progress > 0 ? DateTime.UtcNow : default;
            }

            var within = job.Part is { } part ? (part.Number - 1 + job.Progress) / part.Of : job.Progress;
            var fraction = (step + Math.Clamp(within, 0, 1)) / Math.Max(1, _names.Length);
            var detail = job.State == JobState.Queued
                ? "waiting for a worker…"
                : (job.Part is { } p ? $"part {p.Number} of {p.Of}" + (job.ProgressMessage is { } m ? $", {m}" : "") : job.ProgressMessage) ?? "working…";
            var header = _names.Length > 1 ? $"Step {step + 1} of {_names.Length}: {name}" : name;
            return (fraction, $"{header} — {detail}{Left(within)}");
        }

        /// <summary>" · about 3 min left" once the step is far enough along to tell; empty before.</summary>
        private string Left(double within)
        {
            if (_stepStartedUtc == default || within < 0.03 || within >= 1) return "";
            var spent = DateTime.UtcNow - _stepStartedUtc;
            if (spent < TimeSpan.FromSeconds(10)) return "";
            var left = spent * ((1 - within) / within);
            return left < TimeSpan.FromMinutes(1) ? " · less than a minute left" : $" · about {Math.Ceiling(left.TotalMinutes):0} min left";
        }
    }
}
