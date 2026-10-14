using System.IO.Pipes;
using System.Text;
using LocalAudiobookStudio.Cli.Proxy;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Tests;

/// <summary>A tone worker that waits for the test before generating some texts.</summary>
internal sealed class GatedToneConnection(string workerId, Func<string, CancellationToken, Task> gate) : IWorkerConnection
{
    public string WorkerId { get; } = workerId;
    public HelloInfo? Hello { get; private set; }
    public bool IsAlive { get; private set; }
    public event Action<int?>? Exited;
    public event Action<string>? LogLine { add { } remove { } }

    public Task StartAsync(CancellationToken ct)
    {
        IsAlive = true;
        Hello = new HelloInfo(1, WorkerId, "v1", "cpu");
        return Task.CompletedTask;
    }

    public Task LoadAsync(IProgress<WorkerProgress>? progress, CancellationToken ct, Action? prepared = null)
    {
        prepared?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<GenerateResult> GenerateAsync(GenerateRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        await gate(request.Text ?? "", ct);
        EngineTests.WriteTone(request.OutPath, 200);
        return new GenerateResult(request.OutPath, 200, request.Seed ?? 1, 24000);
    }

    public Task<TranscriptResult> TranscribeAsync(TranscribeRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<VoiceScores> CompareVoicesAsync(VoiceCompareRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VoiceEntry>>([]);
    public Task ShutdownAsync(TimeSpan grace) { Kill(); return Task.CompletedTask; }

    public void Kill()
    {
        if (!IsAlive) return;
        IsAlive = false;
        Exited?.Invoke(0);
    }

    public ValueTask DisposeAsync() { IsAlive = false; return ValueTask.CompletedTask; }
}

/// <summary>The las proxy in process: requests over its pipe, run on a host with a fake worker.</summary>
public sealed class ProxyServerTests : IAsyncDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _pipe = "las-test-" + Guid.NewGuid().ToString("N")[..12];
    private readonly TaskCompletionSource _firstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GenerationScheduler _scheduler;
    private readonly ProxyServer _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<bool> _run;

    public ProxyServerTests()
    {
        var workers = Path.Combine(_tmp.Path, "workers", "tone");
        Directory.CreateDirectory(workers);
        File.WriteAllText(Path.Combine(workers, "manifest.json"), """
            { "id": "tone", "displayName": "Tone", "modelVersion": "v1", "maxChars": 200 }
            """);
        // "first" waits until the test lets it go, so the other requests queue behind it.
        _scheduler = new GenerationScheduler((w, _) => new GatedToneConnection(w,
            (text, ct) => text.Contains("first") ? _firstGate.Task.WaitAsync(ct) : Task.CompletedTask), _ => WorkerHosting.AnyLocal);
        _scheduler.ConfigureDevices([new DeviceConfig { Kind = DeviceKind.Gpu, GpuIndex = 0, Name = "GPU 0" }]);
        var host = StudioHost.CreateForTests(new WorkerCatalog(Path.Combine(_tmp.Path, "workers")), _scheduler,
            dataRoot: Path.Combine(_tmp.Path, "data"));
        _server = new ProxyServer(_pipe, () => Task.FromResult(host), TimeSpan.FromHours(1));
        _run = _server.RunAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _firstGate.TrySetResult();
        _stop.Cancel();
        await _run.WaitAsync(TimeSpan.FromSeconds(30));
        await _scheduler.DisposeAsync();
        _tmp.Dispose();
    }

    private sealed record Result(int Code, string Out, string Error, List<QueueInfo> Queue);

    /// <summary>Runs a command like las --proxy does. <paramref name="onQueue"/> sees each queue report (it may send frames).</summary>
    private async Task<Result> RunAsync(string[] args, Action<FrameStream, QueueInfo>? onQueue = null)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, ProxyPipe.Options);
        await pipe.ConnectAsync(10_000);
        var frames = new FrameStream(pipe);
        Assert.Equal(FrameType.Hello, (await frames.ReadAsync())?.Type);
        frames.WriteJson(FrameType.Request, new ProxyRequest(args, _tmp.Path, false));
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var queue = new List<QueueInfo>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await frames.ReadAsync(timeout.Token) is { } f)
        {
            switch (f.Type)
            {
                case FrameType.Out: stdout.Append(FrameStream.Text(f.Payload)); break;
                case FrameType.Error: stderr.Append(FrameStream.Text(f.Payload)); break;
                case FrameType.Queue:
                    var q = FrameStream.Json<QueueInfo>(f.Payload);
                    queue.Add(q);
                    onQueue?.Invoke(frames, q);
                    break;
                case FrameType.Exit: return new Result(FrameStream.ExitCode(f.Payload), stdout.ToString(), stderr.ToString(), queue);
            }
        }
        throw new InvalidOperationException("The proxy closed the pipe without an exit code.");
    }

    [Fact]
    public async Task RunsTtsWithPathsFromTheClientsFolder()
    {
        _firstGate.SetResult();
        var r = await RunAsync(["tts", "Hello there.", "-o", "out.wav", "-w", "tone"]);
        Assert.True(r.Code == 0, r.Error);
        var expected = Path.Combine(_tmp.Path, "out.wav");
        Assert.True(File.Exists(expected));
        Assert.Contains(expected, r.Out);                       // the result line, on the client's stdout
        Assert.Contains("done", r.Error);                       // progress, on its stderr
        Assert.Empty(r.Queue);                                  // nothing was ahead of it
    }

    [Fact]
    public async Task ShowsTheQueueAndCancelsOnlyItsOwnJobs()
    {
        var first = RunAsync(["tts", "The first one.", "-o", "first.wav", "-w", "tone"]);
        await WaitUntil(() => _scheduler.RunningCount == 1);
        // The second waits behind the first; its Ctrl+C cancels it and leaves the first alone.
        var second = await RunAsync(["tts", "The second one.", "-o", "second.wav", "-w", "tone"],
            (frames, q) => { if (!q.Started) frames.Write(FrameType.Cancel); });
        Assert.Equal(130, second.Code);
        Assert.Equal(new QueueInfo(false, 1), second.Queue[0]);
        Assert.False(File.Exists(Path.Combine(_tmp.Path, "second.wav")));

        Assert.Equal(1, _scheduler.RunningCount);
        _firstGate.SetResult();
        var r = await first;
        Assert.True(r.Code == 0, r.Error);
        Assert.True(File.Exists(Path.Combine(_tmp.Path, "first.wav")));
    }

    [Fact]
    public async Task UnloadStopsTheWorkerOnceItsJobIsDone()
    {
        Assert.Equal(0, (await RunAsync(["tts", "Load it.", "-o", "a.wav", "-w", "tone"])).Code);
        Assert.Single(_scheduler.WorkerSnapshots());

        // Busy: unload waits for the job, then the worker is gone.
        var busy = RunAsync(["tts", "The first one.", "-o", "b.wav", "-w", "tone"]);
        await WaitUntil(() => _scheduler.RunningCount == 1);
        var unload = RunAsync(["unload", "tone"]);
        await Task.Delay(300);
        Assert.False(unload.IsCompleted);
        _firstGate.SetResult();
        var r = await unload;
        Assert.True(r.Code == 0, r.Error);
        Assert.Contains("tone GPU 0", r.Out);
        Assert.Empty(_scheduler.WorkerSnapshots());
        Assert.Equal(0, (await busy).Code);

        Assert.Equal(2, (await RunAsync(["unload", "nothing-like-this"])).Code);
        Assert.Contains("No workers are loaded", (await RunAsync(["unload"])).Error);
    }

    [Fact]
    public async Task RefusesCommandsThatDontGenerate()
    {
        var r = await RunAsync(["devices"]);
        Assert.Equal(2, r.Code);
        Assert.Contains("doesn't run in the las proxy", r.Error);
    }

    [Fact]
    public async Task StopEndsTheProxy()
    {
        await using (var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, ProxyPipe.Options))
        {
            await pipe.ConnectAsync(10_000);
            var frames = new FrameStream(pipe);
            Assert.Equal(FrameType.Hello, (await frames.ReadAsync())?.Type);
            frames.Write(FrameType.Stop);
            Assert.Equal(FrameType.Exit, (await frames.ReadAsync())?.Type);
        }
        Assert.True(await _run.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ASecondProxyOnThePipeExits()
    {
        var other = new ProxyServer(_pipe, () => throw new InvalidOperationException("never started"), TimeSpan.FromHours(1));
        Assert.False(await other.RunAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
