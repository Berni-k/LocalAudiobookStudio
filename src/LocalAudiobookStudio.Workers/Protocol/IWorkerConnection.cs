namespace LocalAudiobookStudio.Workers.Protocol;

/// <summary>
/// A connection to one worker process. The scheduler only talks to workers through this interface,
/// so a remote transport (WebSocket) can be added later without touching it.
/// </summary>
public interface IWorkerConnection : IAsyncDisposable
{
    string WorkerId { get; }

    /// <summary>Set after the worker's <c>hello</c> message.</summary>
    HelloInfo? Hello { get; }

    bool IsAlive { get; }

    /// <summary>Raised once when the process exits or the connection drops (argument: exit code if known).</summary>
    event Action<int?>? Exited;

    /// <summary>Diagnostic output (stderr), one line at a time.</summary>
    event Action<string>? LogLine;

    /// <summary>Starts the worker and waits for <c>hello</c>.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Loads the model; progress may include weight downloads. <paramref name="prepared"/> is called when
    /// the worker reports <c>prepared</c> (files downloaded, only the heavy load left); workers without a
    /// prepare stage never call it.
    /// </summary>
    Task LoadAsync(IProgress<WorkerProgress>? progress, CancellationToken ct, Action? prepared = null);

    /// <summary>
    /// Renders one request. The result WAV is at <see cref="GenerateResult.Path"/> (== request.OutPath).
    /// Cancelling the token sends <c>cancel</c>; the task then ends with <see cref="OperationCanceledException"/>
    /// once the worker acknowledges, or keeps running until the caller kills the process.
    /// </summary>
    Task<GenerateResult> GenerateAsync(GenerateRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Transcribes (or aligns) one audio file, for workers tagged Transcription or Alignment. Cancelling works as
    /// for <see cref="GenerateAsync"/>.
    /// </summary>
    Task<TranscriptResult> TranscribeAsync(TranscribeRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct);

    Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(CancellationToken ct);

    /// <summary>Asks the worker to exit; kills it if it hasn't after <paramref name="grace"/>.</summary>
    Task ShutdownAsync(TimeSpan grace);

    /// <summary>Terminates the worker immediately.</summary>
    void Kill();
}
