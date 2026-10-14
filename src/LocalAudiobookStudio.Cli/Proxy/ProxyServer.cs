using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using LocalAudiobookStudio.Engine.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Cli.Proxy;

/// <summary>
/// The las proxy: one process per user (and copy of las) that runs other las processes' commands on one shared host,
/// so workers stay loaded between them and the GPUs are shared out like in the app. Each connection is one command;
/// the client's console gets its output. It exits after <c>idle</c> without clients or jobs, or on las proxy stop.
/// </summary>
public sealed class ProxyServer
{
    /// <summary>The commands it runs: the ones that generate or listen, which are slow to start and worth keeping loaded.</summary>
    public static readonly string[] Commands = ["tts", "transcribe", "compare"];

    private readonly string _pipeName;
    private readonly Func<Task<StudioHost>> _startHost;
    private readonly TimeSpan _idle;
    private readonly ILogger? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private Task<StudioHost> _host = null!;
    private int _clients;
    private int _connectionIds;
    private long _requests;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public ProxyServer(string pipeName, Func<Task<StudioHost>> startHost, TimeSpan idle, ILogger? log = null)
    {
        _pipeName = pipeName;
        _startHost = startHost;
        _idle = idle;
        _log = log;
    }

    /// <summary>
    /// Takes the pipe and serves until stopped or idle; then cancels what still runs and waits for it (the caller shuts
    /// the host down). False when another proxy already has the pipe.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        NamedPipeServerStream first;
        try
        {
            first = NewPipe(PipeOptions.FirstPipeInstance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogInformation("Another las proxy has the pipe {Pipe}; exiting", _pipeName);
            return false;
        }
        await using var reg = ct.Register(_stop.Cancel);
        _log?.LogInformation("las proxy listening on {Pipe} (exits after {Idle} min idle)", _pipeName, _idle.TotalMinutes);
        // The host starts while the first client connects; its requests wait for it.
        _host = Task.Run(_startHost);
        var idle = WatchIdleAsync();
        await AcceptAsync(first);
        await idle;

        var running = _connections.Values.ToList();
        if (running.Count > 0)
        {
            _log?.LogInformation("Stopping: waiting for {Count} connection(s)", running.Count);
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(20)); } catch { /* exiting anyway */ }
        }
        _log?.LogInformation("las proxy stopped");
        return true;
    }

    /// <summary>The host, once started (null while starting or when it failed).</summary>
    public StudioHost? Host => _host is { IsCompletedSuccessfully: true } t ? t.Result : null;

    public void Stop() => _stop.Cancel();

    private NamedPipeServerStream NewPipe(PipeOptions extra = PipeOptions.None) =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, ProxyPipe.Options | extra);

    private async Task AcceptAsync(NamedPipeServerStream pipe)
    {
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException ex)
            {
                // A client that went away while connecting; listen again.
                _log?.LogDebug(ex, "Connection failed");
                await pipe.DisposeAsync();
                pipe = NewPipe();
                continue;
            }
            var id = Interlocked.Increment(ref _connectionIds);
            var connected = pipe;
            _connections[id] = Task.Run(async () =>
            {
                try { await ServeAsync(connected); }
                finally { _connections.TryRemove(id, out _); }
            });
            try { pipe = NewPipe(); }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Can't listen on {Pipe} any more; stopping", _pipeName);
                _stop.Cancel();
                return;
            }
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private TimeSpan IdleFor => DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>Busy while a client is connected or jobs are queued or running (a client that went away cancels its own).</summary>
    private async Task WatchIdleAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token); }
            catch (OperationCanceledException) { return; }
            var busy = Volatile.Read(ref _clients) > 0 || Host is { } h && (h.Scheduler.QueuedCount > 0 || h.Scheduler.RunningCount > 0);
            if (busy) Touch();
            else if (IdleFor >= _idle)
            {
                _log?.LogInformation("Idle for {Minutes:0.#} min; stopping", IdleFor.TotalMinutes);
                _stop.Cancel();
            }
        }
    }

    // ------------------------------------------------------------------ one connection

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        Interlocked.Increment(ref _clients);
        Touch();
        var frames = new FrameStream(pipe);
        try
        {
            frames.WriteJson(FrameType.Hello, new ProxyHello(Environment.ProcessId, AppVersion.Display));
            if (await frames.ReadAsync(_stop.Token) is not { } first) return;
            switch (first.Type)
            {
                case FrameType.Request:
                    await RunRequestAsync(frames, FrameStream.Json<ProxyRequest>(first.Payload));
                    break;
                case FrameType.Status:
                    frames.WriteJson(FrameType.StatusReply, Status());
                    await ClientClosedAsync(frames);
                    break;
                case FrameType.Stop:
                    _log?.LogInformation("Stop requested");
                    frames.WriteExit(Program.ExitOk);
                    await ClientClosedAsync(frames);
                    _stop.Cancel();
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            _log?.LogDebug(ex, "Connection ended");
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Connection failed");
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            Touch();
            await pipe.DisposeAsync();
        }
    }

    /// <summary>Waits (a little) for the client to close its end: closing first could cut off what it hasn't read yet.</summary>
    private static async Task ClientClosedAsync(FrameStream frames)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { while (await frames.ReadAsync(timeout.Token) != null) { } }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException) { }
    }

    private ProxyStatus Status()
    {
        var host = Host;
        var devices = host?.Scheduler.DeviceSnapshots().ToDictionary(d => d.Index, d => d.Name) ?? [];
        var workers = host?.Scheduler.WorkerSnapshots()
            // Its runtime can only change while it isn't loaded, so the chosen one is the one it runs on.
            .Select(w => new ProxyWorker(w.WorkerId, host.Catalog.Get(w.WorkerId)?.Runtime ?? "?",
                devices.GetValueOrDefault(w.DeviceIndex) ?? "?", w.State.ToString(), w.JobLabel))
            .ToList() ?? [];
        return new ProxyStatus(Environment.ProcessId, AppVersion.Display, _startedUtc, Math.Max(0, Volatile.Read(ref _clients) - 1),
            host?.Scheduler.QueuedCount ?? 0, host?.Scheduler.RunningCount ?? 0, Math.Round(IdleFor.TotalMinutes, 1), workers);
    }

    private async Task RunRequestAsync(FrameStream frames, ProxyRequest request)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var stdin = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new CliIo
        {
            Out = new FrameTextWriter(frames, FrameType.Out),
            Error = new FrameTextWriter(frames, FrameType.Error),
            OpenStdout = () => new FrameOutputStream(frames),
            IsInputRedirected = request.StdinRedirected,
            ReadStdin = () =>
            {
                frames.Write(FrameType.NeedStdin);
                return stdin.Task.WaitAsync(cts.Token).GetAwaiter().GetResult();
            },
        };

        // Ctrl+C and stdin from the client; when it goes away its jobs are cancelled.
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await frames.ReadAsync() is { } f)
                {
                    if (f.Type == FrameType.Cancel) cts.Cancel();
                    else if (f.Type == FrameType.Stdin) stdin.TrySetResult(FrameStream.Text(f.Payload));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException) { }
            stdin.TrySetCanceled();
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        });

        GlobalOptions g;
        try { g = GlobalOptions.Parse(request.Args); }
        catch (UsageException ex)
        {
            io.Error.WriteLine(ex.Message);
            await FinishAsync(Program.ExitUsage);
            return;
        }
        if (g.Command is not { } command || !(Commands.Contains(command) || command == ProxyCommand.UnloadCommand))
        {
            io.Error.WriteLine($"'las {g.Command}' doesn't run in the las proxy; only {string.Join(", ", Commands)} do.");
            await FinishAsync(Program.ExitUsage);
            return;
        }

        var ctx = new CliContext(g.Verbose, g.Quiet, g.Json, cts.Token, io)
        {
            OnlyGpu = g.Gpu,
            Cwd = request.Cwd,
            SharedHost = () => _host,
            // Requests are served in the order they came: each one's jobs after the earlier ones'.
            OrderBase = Interlocked.Increment(ref _requests) * 1_000_000_000_000L,
        };
        _log?.LogInformation("Request {Number}: {Args} (in {Cwd})", ctx.OrderBase / 1_000_000_000_000L, string.Join(' ', request.Args), request.Cwd);
        using var done = new CancellationTokenSource();
        var queue = ReportQueueAsync(ctx, frames, done.Token);
        int code;
        try
        {
            code = await CommandRunner.RunAsync(ctx, g.Rest);
        }
        finally
        {
            ctx.DetachFromSharedHost();
            done.Cancel();
            await queue;
        }
        await FinishAsync(code);

        async Task FinishAsync(int exitCode)
        {
            frames.WriteExit(exitCode);
            // The client closes the pipe once it has read everything; closing first could cut off the end.
            try { await reader.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        }
    }

    /// <summary>
    /// Tells the client how many jobs are ahead of its first one, while none of its jobs has started. The client shows
    /// it (not with --json or --quiet), so a waiting command doesn't look stuck.
    /// </summary>
    private static async Task ReportQueueAsync(CliContext ctx, FrameStream frames, CancellationToken done)
    {
        var last = 0;
        try
        {
            while (true)
            {
                await Task.Delay(250, done);
                var ids = ctx.JobIds;
                if (ids.Count == 0 || ctx.Host is not { } host) continue;
                var (started, ahead) = host.Scheduler.QueueStatus(ids);
                if (started)
                {
                    if (last > 0) frames.WriteJson(FrameType.Queue, new QueueInfo(true, 0));
                    return;
                }
                if (ahead != last && (ahead > 0 || last > 0))
                {
                    frames.WriteJson(FrameType.Queue, new QueueInfo(false, ahead));
                    last = ahead;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    /// <summary>Text for the client's stdout or stderr. Whole lines go as one frame; nothing is thrown once the client is gone.</summary>
    private sealed class FrameTextWriter(FrameStream frames, FrameType type) : TextWriter
    {
        private volatile bool _gone;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Send(value.ToString());
        public override void Write(string? value) => Send(value);
        public override void Write(char[] buffer, int index, int count) => Send(new string(buffer, index, count));
        public override void WriteLine(string? value) => Send(value + NewLine);
        public override void WriteLine() => Send(NewLine);

        private void Send(string? text)
        {
            if (_gone || string.IsNullOrEmpty(text)) return;
            try { frames.WriteText(type, text); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { _gone = true; }
        }
    }

    /// <summary>Bytes for the client's stdout (tts -o -).</summary>
    private sealed class FrameOutputStream(FrameStream frames) : Stream
    {
        private const int Chunk = 1024 * 1024;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            for (var i = 0; i < buffer.Length; i += Chunk)
                frames.Write(FrameType.Binary, buffer.Slice(i, Math.Min(Chunk, buffer.Length - i)));
        }
    }
}
