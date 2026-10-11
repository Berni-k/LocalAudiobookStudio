using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Workers.Protocol;

/// <summary>
/// Protocol v2 over a child process's stdin/stdout. Stdout carries protocol lines only; stderr is
/// forwarded to <see cref="LogLine"/>. A dedicated task reads stdout, and a watchdog kills the
/// process if a request sees no stdout activity for longer than the generation timeout. While the worker
/// waits for one of its own requests (a model download, see <see cref="RequestHandler"/>) the watchdog waits too.
/// </summary>
public sealed class StdioWorkerConnection : IWorkerConnection
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _arguments;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly string _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<HelloInfo> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private StreamWriter? _stdin;
    private long _lastActivityTicks;
    private int _exitedRaised;
    private CancellationTokenSource? _watchdogCts;
    /// <summary>The worker's open needModel / needFile requests, by their ref.</summary>
    private readonly ConcurrentDictionary<string, WorkerRequest> _workerRequests = new();

    private sealed record WorkerRequest(string? RequestId, CancellationTokenSource Cancellation)
    {
        public void Cancel()
        {
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } // finished meanwhile
        }
    }

    private sealed class Pending(string type, IProgress<WorkerProgress>? progress, Action? prepared = null)
    {
        public string Type { get; } = type;
        public IProgress<WorkerProgress>? Progress { get; } = progress;
        public Action? Prepared { get; } = prepared;
        public TaskCompletionSource<WorkerMessage> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A Python worker script run unbuffered (<c>python -u script</c>).</summary>
    public StdioWorkerConnection(
        string workerId,
        string pythonPath,
        string scriptPath,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan generationTimeout,
        ILogger? logger = null)
        : this(workerId, pythonPath, ["-u", scriptPath], Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            environment, generationTimeout, logger)
    {
    }

    /// <summary>Any worker program (a Python interpreter with its script, or the HartsyInference worker).</summary>
    public StdioWorkerConnection(
        string workerId,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan generationTimeout,
        ILogger? logger = null)
    {
        WorkerId = workerId;
        _executable = executable;
        _arguments = arguments;
        _environment = environment;
        _workingDirectory = workingDirectory;
        _timeout = generationTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(10) : generationTimeout;
        _logger = logger;
    }

    public string WorkerId { get; }
    public HelloInfo? Hello { get; private set; }
    public bool IsAlive => _process is { HasExited: false };
    public int? ProcessId => _process?.Id;

    public event Action<int?>? Exited;
    public event Action<string>? LogLine;

    /// <summary>
    /// Answers the worker's own requests (<c>needModel</c>, <c>needFile</c>): gets the request, the progress of the
    /// app request it belongs to, and a token cancelled with that request. Without a handler they fail.
    /// </summary>
    public Func<WorkerMessage, IProgress<WorkerProgress>?, CancellationToken, Task<WorkerMessage>>? RequestHandler { get; set; }

    public async Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = _workingDirectory,
        };
        foreach (var a in _arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _environment) psi.Environment[k] = v;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => OnExited();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Touch();
            LogLine?.Invoke(e.Data);
        };
        if (!p.Start()) throw new WorkerCrashedException($"Could not start worker {WorkerId}.");
        _process = p;
        // Our own writer guarantees UTF-8 without a byte-order mark: a BOM before the first
        // message makes it unparseable for the worker.
        _stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        p.BeginErrorReadLine();
        Touch();

        _ = Task.Run(ReadStdoutAsync);
        _watchdogCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchdogAsync(_watchdogCts.Token));

        // Wait for hello (model imports can be slow, but hello comes before any heavy work).
        using var reg = ct.Register(() => _hello.TrySetCanceled(ct));
        var helloTask = _hello.Task;
        var winner = await Task.WhenAny(helloTask, Task.Delay(TimeSpan.FromMinutes(2), ct)).ConfigureAwait(false);
        if (winner != helloTask)
        {
            ct.ThrowIfCancellationRequested();
            Kill();
            throw new WorkerCrashedException($"Worker {WorkerId} did not say hello within 2 minutes.");
        }
        Hello = await helloTask.ConfigureAwait(false);
    }

    public async Task LoadAsync(IProgress<WorkerProgress>? progress, CancellationToken ct, Action? prepared = null)
    {
        var msg = new WorkerMessage { Type = MessageTypes.Load };
        await RequestAsync(msg, progress, ct, sendCancelOnAbort: false, prepared).ConfigureAwait(false);
    }

    public async Task<GenerateResult> GenerateAsync(GenerateRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var msg = new WorkerMessage
        {
            Type = MessageTypes.Generate,
            Text = request.Text,
            DurationSec = request.DurationSec,
            Voice = request.Voice,
            Settings = request.Settings,
            Seed = request.Seed,
            OutPath = request.OutPath,
            Parts = request.Parts,
        };
        var done = await RequestAsync(msg, progress, ct, sendCancelOnAbort: true).ConfigureAwait(false);
        var path = done.Path ?? request.OutPath;
        if (done.Audio != null)
        {
            // Remote-style response: the audio travels in the message.
            await File.WriteAllBytesAsync(request.OutPath, Convert.FromBase64String(done.Audio), CancellationToken.None).ConfigureAwait(false);
            path = request.OutPath;
        }
        // Spans that don't match the parts can't be trusted for any part: the app transcribes instead.
        var spans = request.Parts != null && done.Spans?.Count == request.Parts.Count ? done.Spans : null;
        return new GenerateResult(path, done.DurationMs ?? 0, done.Seed, done.SampleRate ?? 0, spans);
    }

    public async Task<TranscriptResult> TranscribeAsync(TranscribeRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var msg = new WorkerMessage
        {
            Type = request.Mode switch
            {
                ListenMode.Transcribe => MessageTypes.Transcribe,
                ListenMode.Align => MessageTypes.Align,
                _ => MessageTypes.TranscribeAlign,
            },
            AudioPath = request.AudioPath,
            Text = request.Text,
            Settings = request.Settings,
            Speakers = request.Speakers?.ToList(),
            SpeakerCount = request.SpeakerCount,
        };
        var reply = await RequestAsync(msg, progress, ct, sendCancelOnAbort: true).ConfigureAwait(false);
        var text = reply.Text ?? "";
        // Text only: its words without times (all at 0), so it can still be compared with the text it should be.
        var words = request.Mode == ListenMode.Transcribe || reply.Words == null
            ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => new TranscriptWord(w, 0, 0)).ToList()
            : reply.Words;
        return new TranscriptResult(text, words, reply.Language);
    }

    public async Task<VoiceScores> CompareVoicesAsync(VoiceCompareRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var msg = new WorkerMessage
        {
            Type = MessageTypes.CompareVoices,
            Files = request.Files.ToList(),
            Settings = request.Settings,
        };
        var reply = await RequestAsync(msg, progress, ct, sendCancelOnAbort: true).ConfigureAwait(false);
        if (reply.Matrix == null || reply.SpeechMs == null)
            throw new WorkerErrorException("The worker's answer to compareVoices has no matrix or speechMs.", false, null);
        return new VoiceScores(reply.Scores ?? [], reply.Matrix.Select(r => (IReadOnlyList<double>)r).ToList(), reply.SpeechMs, reply.Model ?? "");
    }

    public async Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(new WorkerMessage { Type = MessageTypes.ListVoices }, null, ct, false).ConfigureAwait(false);
        return reply.Voices ?? new List<VoiceEntry>();
    }

    private async Task<WorkerMessage> RequestAsync(WorkerMessage msg, IProgress<WorkerProgress>? progress, CancellationToken ct,
        bool sendCancelOnAbort, Action? prepared = null)
    {
        if (!IsAlive) throw new WorkerCrashedException($"Worker {WorkerId} is not running.");
        var id = Guid.NewGuid().ToString("N")[..12];
        msg.Id = id;
        var pending = new Pending(msg.Type, progress, prepared);
        _pending[id] = pending;
        try
        {
            Touch();
            await SendAsync(msg).ConfigureAwait(false);
            await using var reg = ct.Register(() =>
            {
                // A download the worker waits for stops too; the worker hears "cancelled" in its reply.
                foreach (var r in _workerRequests.Values.Where(r => r.RequestId == id)) r.Cancel();
                if (sendCancelOnAbort)
                    _ = SendAsync(new WorkerMessage { Type = MessageTypes.Cancel, Id = id });
                else
                    pending.Tcs.TrySetCanceled(ct);
            });
            return await pending.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(WorkerMessage msg)
    {
        var p = _process;
        var writer = _stdin;
        if (p == null || writer == null || p.HasExited) return;
        var line = WorkersJson.Serialize(msg);
        _logger?.LogTrace("Worker {Worker} -> {Line}", WorkerId, line);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process is gone; OnExited fails the pending requests.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutAsync()
    {
        var p = _process!;
        try
        {
            while (true)
            {
                var line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                Touch();
                if (line.Length == 0) continue;
                _logger?.LogTrace("Worker {Worker} <- {Line}", WorkerId, line);
                WorkerMessage? msg = null;
                try { msg = WorkersJson.Deserialize<WorkerMessage>(line); }
                catch (Exception)
                {
                    // Unparseable stdout is logged and ignored.
                    _logger?.LogWarning("Worker {Worker}: ignoring unparseable stdout line: {Line}", WorkerId, line);
                    LogLine?.Invoke("[stdout] " + line);
                    continue;
                }
                Dispatch(msg);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Worker {Worker}: stdout reader stopped", WorkerId);
        }
        OnExited();
    }

    private void Dispatch(WorkerMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.Hello:
                _hello.TrySetResult(new HelloInfo(msg.Protocol ?? 1, msg.Worker ?? WorkerId, msg.ModelVersion ?? "", msg.Device ?? ""));
                return;
            case MessageTypes.Progress:
                if (msg.Id != null && _pending.TryGetValue(msg.Id, out var pp))
                    pp.Progress?.Report(new WorkerProgress(Math.Clamp(msg.Fraction ?? 0, 0, 1), msg.Message));
                return;
            case MessageTypes.Prepared:
                if (msg.Id != null && _pending.TryGetValue(msg.Id, out var pr)) pr.Prepared?.Invoke();
                return;
            case MessageTypes.Ready:
            case MessageTypes.Done:
            case MessageTypes.Voices:
            case MessageTypes.Transcript:
            case MessageTypes.VoiceScores:
                if (msg.Id != null && _pending.TryGetValue(msg.Id, out var pd)) pd.Tcs.TrySetResult(msg);
                return;
            case MessageTypes.NeedModel:
            case MessageTypes.NeedFile:
                _ = ServeWorkerRequestAsync(msg);
                return;
            case MessageTypes.Error:
                var error = new WorkerErrorException(msg.Message ?? "Unknown worker error", msg.Fatal ?? false, msg.Trace, msg.Code);
                if (msg.Trace != null) LogLine?.Invoke(msg.Trace);
                if (msg.Id != null && _pending.TryGetValue(msg.Id, out var pe))
                {
                    if (msg.Message == "cancelled") pe.Tcs.TrySetCanceled();
                    else pe.Tcs.TrySetException(error);
                }
                else
                {
                    // An error without a matching request (e.g. during startup) fails everything if fatal.
                    if (error.Fatal) FailAll(error);
                    _hello.TrySetException(error);
                }
                return;
            default:
                _logger?.LogWarning("Worker {Worker}: unknown message type {Type}", WorkerId, msg.Type);
                return;
        }
    }

    private async Task ServeWorkerRequestAsync(WorkerMessage msg)
    {
        var key = msg.Ref ?? Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        _workerRequests[key] = new WorkerRequest(msg.Id, cts);
        var progress = msg.Id != null && _pending.TryGetValue(msg.Id, out var p) ? p.Progress : null;
        var failedType = msg.Type == MessageTypes.NeedFile ? MessageTypes.FileFailed : MessageTypes.ModelFailed;
        WorkerMessage reply;
        try
        {
            reply = RequestHandler == null
                ? new WorkerMessage { Type = failedType, Message = "This host can't provide models." }
                : await RequestHandler(msg, progress, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reply = new WorkerMessage { Type = failedType, Message = ex.Message, Cancelled = ex is OperationCanceledException ? true : null };
        }
        finally
        {
            _workerRequests.TryRemove(key, out _);
            Touch(); // the watchdog's clock starts again from the reply
        }
        reply.Ref = msg.Ref;
        reply.Id = msg.Id;
        await SendAsync(reply).ConfigureAwait(false);
    }

    private void FailAll(Exception ex)
    {
        foreach (var p in _pending.Values) p.Tcs.TrySetException(ex);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (_pending.IsEmpty || !_workerRequests.IsEmpty) continue;
                var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
                if (idle > _timeout)
                {
                    _logger?.LogWarning("Worker {Worker}: no output for {Idle}; killing it as hung", WorkerId, idle);
                    LogLine?.Invoke($"No output for {idle.TotalMinutes:0.#} minutes; the worker is treated as hung and killed.");
                    FailAll(new WorkerCrashedException($"Worker {WorkerId} timed out (no output for {idle.TotalMinutes:0.#} min)."));
                    Kill();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnExited()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) == 1) return;
        _watchdogCts?.Cancel();
        foreach (var r in _workerRequests.Values) r.Cancel();
        int? code = null;
        try { if (_process is { HasExited: true }) code = _process.ExitCode; } catch { }
        var ex = new WorkerCrashedException($"Worker {WorkerId} exited unexpectedly" + (code is int c ? $" (exit code {c})." : "."));
        FailAll(ex);
        _hello.TrySetException(ex);
        Exited?.Invoke(code);
    }

    public async Task ShutdownAsync(TimeSpan grace)
    {
        var p = _process;
        if (p == null || p.HasExited) return;
        await SendAsync(new WorkerMessage { Type = MessageTypes.Shutdown }).ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(grace);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _watchdogCts?.Cancel();
        _process?.Dispose();
    }
}
