using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Tests;

public class SchedulingPolicyTests
{
    private static ComputeDevice Gpu(int i, int maxActive = 1, int? maxTotal = null) =>
        new(i, new DeviceConfig { Kind = DeviceKind.Gpu, GpuIndex = i, Name = $"GPU {i}", MaxActive = maxActive, MaxTotal = maxTotal });

    private static ComputeDevice Cpu() => new(9, new DeviceConfig { Kind = DeviceKind.Cpu, Name = "CPU" });

    private static int _pid;

    private static WorkerProcess Proc(ComputeDevice d, string worker, WorkerState state = WorkerState.Idle, int lane = 0, TimeSpan? idleFor = null) =>
        new(++_pid, d, worker, lane) { State = state, IdleSinceUtc = DateTime.UtcNow - (idleFor ?? TimeSpan.Zero) };

    private static long _seq;

    private static GenerationJob Job(string worker, JobPriority p = JobPriority.Normal, long order = 0, TimeSpan? age = null)
    {
        return new GenerationJob
        {
            WorkerId = worker, Priority = p, ReadingOrder = order, ItemId = Guid.NewGuid(),
            Sequence = ++_seq, EnqueuedUtc = DateTime.UtcNow - (age ?? TimeSpan.Zero),
        };
    }

    private static WorkerHosting Hosting(string w) => w switch
    {
        "comfy" => WorkerHosting.External,
        "wide" => new WorkerHosting(WorkerPlacement.Gpu, 2),
        _ => WorkerHosting.AnyLocal,
    };

    private static Assignment? Pick(ComputeDevice d, List<GenerationJob> q, List<WorkerProcess>? processes = null,
        IReadOnlyList<ComputeDevice>? devices = null) =>
        SchedulingPolicy.PickNext(d, q, processes ?? [], devices ?? [d], Hosting, DateTime.UtcNow);

    private static readonly WorkerHosting OnGpu = new(WorkerPlacement.Gpu);
    private static readonly WorkerHosting OnCpu = new(WorkerPlacement.Cpu);

    [Fact]
    public void DevicesRunOnlyWorkersSetToThem()
    {
        var gpu = Gpu(0);
        var cpu = Cpu();
        Assert.True(gpu.Allows("w", OnGpu));
        Assert.False(cpu.Allows("w", OnGpu));
        Assert.True(cpu.Allows("w", OnCpu));
        Assert.False(gpu.Allows("w", OnCpu));
        Assert.False(cpu.Allows("w", WorkerHosting.External));
        var ext = new ComputeDevice(5, new DeviceConfig { Kind = DeviceKind.External, ExternalWorker = "comfy" });
        Assert.True(ext.Allows("comfy", WorkerHosting.External));
        Assert.False(ext.Allows("other", WorkerHosting.AnyLocal));
    }

    [Fact]
    public void GpuIdsLimitTheGpus()
    {
        var only1 = new WorkerHosting(WorkerPlacement.Gpu, 1, new HashSet<int> { 1 });
        Assert.False(Gpu(0).Allows("w", only1));
        Assert.True(Gpu(1).Allows("w", only1));
        Assert.True(Gpu(0).Allows("w", new WorkerHosting(WorkerPlacement.Gpu, 1, new HashSet<int>())));
    }

    [Fact]
    public void HostingComesFromWorkerSettings()
    {
        var both = new WorkerManifest { Devices = ["cuda", "cpu"] };
        Assert.Equal(WorkerPlacement.Gpu, both.Hosting(null).Device);
        Assert.Equal(WorkerPlacement.Cpu, both.Hosting(null, machineHasGpu: false).Device);
        var h = both.Hosting(new JsonObject { ["device"] = "cpu", ["parallel"] = 3, ["gpus"] = "0" });
        Assert.Equal(WorkerPlacement.Cpu, h.Device);
        Assert.Equal(3, h.Lanes);
        Assert.Null(h.Gpus);                                                    // GPU ids don't apply on the CPU
        var g = both.Hosting(new JsonObject { ["device"] = "gpu", ["gpus"] = " 0, 2 ,x" });
        Assert.Equal(new HashSet<int> { 0, 2 }, g.Gpus);

        var gpuOnly = new WorkerManifest();
        Assert.Equal(WorkerPlacement.Gpu, gpuOnly.Hosting(new JsonObject { ["device"] = "cpu" }).Device);
        Assert.Equal(CpuSupport.Unsupported, gpuOnly.CpuSupport);
        Assert.Equal(["gpu"], gpuOnly.HostingSettings[0].Options);

        var slow = new WorkerManifest { Devices = ["cuda", "cpu"], CpuRecommended = false };
        Assert.Equal(CpuSupport.NotRecommended, slow.CpuSupport);
        Assert.Equal(WorkerPlacement.Gpu, slow.Hosting(null, machineHasGpu: false).Device); // never chosen by default
        Assert.Equal("CPU (not recommended)", slow.HostingSettings[0].OptionLabels[1]);

        // External workers only have "parallel": how many requests they send at once.
        var ext = new WorkerManifest { External = true };
        Assert.Equal(["parallel"], ext.HostingSettings.Select(s => s.Key));
        var eh = ext.Hosting(new JsonObject { ["parallel"] = 3 });
        Assert.Equal(WorkerPlacement.External, eh.Device);
        Assert.Equal(3, eh.Lanes);
    }

    [Fact]
    public void HighestPriorityThenReadingOrder()
    {
        var q = new List<GenerationJob> { Job("a", order: 5), Job("a", order: 1), Job("a", JobPriority.High, order: 9) };
        Assert.Same(q[2], Pick(Gpu(0), q)!.Job);
        q.RemoveAt(2);
        Assert.Same(q[1], Pick(Gpu(0), q)!.Job);
    }

    [Fact]
    public void ReusesAnIdleProcessOfTheWorker()
    {
        var d = Gpu(0);
        var idle = Proc(d, "b");
        var q = new List<GenerationJob> { Job("a", order: 1), Job("b", order: 2) };
        var a = Pick(d, q, [idle])!;
        Assert.Same(q[1], a.Job);   // affinity: no model load
        Assert.Same(idle, a.Reuse);
    }

    [Fact]
    public void AffinityBeatsFreshHigherPriorityButNotStaleOne()
    {
        var d = Gpu(0);
        var procs = new List<WorkerProcess> { Proc(d, "b") };
        var q = new List<GenerationJob> { Job("a", JobPriority.High, order: 1), Job("b", order: 2) };
        Assert.Same(q[1], Pick(d, q, procs)!.Job);

        q[0] = Job("a", JobPriority.High, order: 1, age: SchedulingPolicy.AffinityMaxWait + TimeSpan.FromSeconds(1));
        Assert.Same(q[0], Pick(d, q, procs)!.Job);
    }

    [Fact]
    public void MaxActiveLimitsGeneratingWorkers()
    {
        var d = Gpu(0, maxActive: 1);
        var busy = Proc(d, "a", WorkerState.Busy);
        Assert.Null(Pick(d, [Job("b")], [busy]));                       // default: one at a time

        var d2 = Gpu(1, maxActive: 2);
        var a = Pick(d2, [Job("b")], [Proc(d2, "a", WorkerState.Busy)])!; // a second worker may start alongside
        Assert.Null(a.Reuse);
        Assert.Null(a.Evict);
    }

    [Fact]
    public void LoadingCountsAsActive()
    {
        var d = Gpu(0);
        Assert.Null(Pick(d, [Job("b")], [Proc(d, "a", WorkerState.Loading)]));
    }

    [Fact]
    public void IdleWorkersStayLoadedUnlessMaxTotalIsReached()
    {
        var d = Gpu(0);
        var a = Pick(d, [Job("c")], [Proc(d, "a"), Proc(d, "b")])!;   // "any" total: just start another
        Assert.Null(a.Evict);

        var full = Gpu(1, maxTotal: 2);
        var older = Proc(full, "a", idleFor: TimeSpan.FromMinutes(5));
        var newer = Proc(full, "b", idleFor: TimeSpan.FromMinutes(1));
        var e = Pick(full, [Job("c")], [newer, older])!;
        Assert.Same(older, e.Evict);                                    // the longest-idle one makes room

        var busyFull = Gpu(2, maxActive: 3, maxTotal: 2);
        Assert.Null(Pick(busyFull, [Job("c")], [Proc(busyFull, "a", WorkerState.Busy), Proc(busyFull, "b", WorkerState.Busy)]));
    }

    [Fact]
    public void ParallelWorkersLimitProcessesPerDevice()
    {
        var d = Gpu(0, maxActive: 4);
        Assert.Null(Pick(d, [Job("a")], [Proc(d, "a", WorkerState.Busy)]));            // parallel 1: wait for it
        Assert.NotNull(Pick(d, [Job("wide")], [Proc(d, "wide", WorkerState.Busy)]));   // parallel 2: a second one starts
        Assert.Null(Pick(d, [Job("wide")], [Proc(d, "wide", WorkerState.Busy), Proc(d, "wide", WorkerState.Busy, lane: 1)]));
    }

    [Fact]
    public void ASecondProcessOfAWorkerWaitsUntilTheFirstIsPrepared()
    {
        var g0 = Gpu(0);
        var g1 = Gpu(1);
        var preparing = Proc(g0, "a", WorkerState.Starting);
        Assert.Null(Pick(g1, [Job("a")], [preparing], [g0, g1]));          // may still be downloading
        Assert.NotNull(Pick(g1, [Job("b")], [preparing], [g0, g1]));       // other workers start side by side
        var loading = Proc(g0, "a", WorkerState.Loading);
        Assert.NotNull(Pick(g1, [Job("a")], [loading], [g0, g1]));         // prepared: files are there
    }

    [Fact]
    public void LeavesJobForDeviceThatHasItsWorkerLoaded()
    {
        var g0 = Gpu(0);
        var g1 = Gpu(1);
        var q = new List<GenerationJob> { Job("a") };
        var procs = new List<WorkerProcess> { Proc(g1, "a") };
        Assert.Null(Pick(g0, q, procs, [g0, g1]));
        Assert.Same(q[0], Pick(g1, q, procs, [g0, g1])!.Job);
    }

    [Fact]
    public void ExternalWorkersOnlyRunOnTheirDevice()
    {
        var q = new List<GenerationJob> { Job("comfy") };
        Assert.Null(Pick(Gpu(0), q));
        var ext = new ComputeDevice(7, new DeviceConfig { Kind = DeviceKind.External, ExternalWorker = "comfy", Name = "ComfyUI" });
        Assert.Same(q[0], Pick(ext, q)!.Job);
    }

    [Fact]
    public void DisabledWorkerIsSkipped()
    {
        var d = Gpu(0);
        d.DisabledWorkers.Add("a");
        Assert.Null(Pick(d, [Job("a")]));
    }
}

/// <summary>A fake worker: writes an empty file after a short delay, optionally crashing.</summary>
internal sealed class FakeConnection(string workerId, Func<GenerateRequest, int, Task>? behavior = null) : IWorkerConnection
{
    private int _calls;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string WorkerId { get; } = workerId;
    public HelloInfo? Hello { get; private set; }
    public bool IsAlive { get; private set; }
    public event Action<int?>? Exited;
    public event Action<string>? LogLine { add { } remove { } }

    public Task StartAsync(CancellationToken ct)
    {
        IsAlive = true;
        Hello = new HelloInfo(1, WorkerId, "1", "cpu");
        return Task.CompletedTask;
    }

    /// <summary>Optional: makes the model load fail (e.g. a broken download).</summary>
    public Exception? LoadError { get; init; }

    public Task LoadAsync(IProgress<WorkerProgress>? progress, CancellationToken ct, Action? prepared = null)
    {
        if (LoadError != null) return Task.FromException(LoadError);
        prepared?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<GenerateResult> GenerateAsync(GenerateRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls);
        var work = behavior != null ? behavior(request, n) : Task.Delay(20, ct);
        // Like the real connection, a kill fails the pending request.
        if (await Task.WhenAny(work, _exited.Task) == _exited.Task) throw new WorkerCrashedException("killed");
        await work;
        ct.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(request.OutPath, [1, 2, 3], ct);
        return new GenerateResult(request.OutPath, 100, 1, 24000);
    }

    /// <summary>"Hears" the request's text, one word per 100 ms.</summary>
    public async Task<TranscriptResult> TranscribeAsync(TranscribeRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        await Task.Delay(20, ct);
        var words = (request.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => new TranscriptWord(w, i * 100, i * 100 + 80)).ToList();
        return new TranscriptResult(request.Text ?? "", words, "en");
    }

    public Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VoiceEntry>>([]);
    public Task ShutdownAsync(TimeSpan grace) { Crash(); return Task.CompletedTask; }

    public void Kill() => Crash();

    public void Crash()
    {
        if (!IsAlive) return;
        IsAlive = false;
        _exited.TrySetResult();
        Exited?.Invoke(1);
    }

    public ValueTask DisposeAsync() { IsAlive = false; return ValueTask.CompletedTask; }
}

public class GenerationSchedulerTests
{
    private static List<DeviceConfig> Gpus(int n) => Enumerable.Range(0, n)
        .Select(i => new DeviceConfig { Kind = DeviceKind.Gpu, GpuIndex = i, Name = $"GPU {i}" }).ToList();

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task RunsJobsAcrossDevicesAndNumbersTakes()
    {
        using var tmp = new TempDir();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(4));
        var completed = 0;
        s.JobCompleted += _ => Interlocked.Increment(ref completed);

        var itemId = Guid.NewGuid();
        var dir = Path.Combine(tmp.Path, itemId.ToString());
        s.EnqueueMany(Enumerable.Range(0, 20).Select(i => new GenerationJob
        {
            ItemId = i == 0 ? itemId : Guid.NewGuid(),
            WorkerId = "a",
            ReadingOrder = i,
            SegmentDir = i == 0 ? dir : Path.Combine(tmp.Path, i.ToString()),
        }));
        await WaitUntil(() => completed == 20);
        Assert.True(File.Exists(Path.Combine(dir, "take-0001.wav")));

        s.Enqueue(new GenerationJob { ItemId = itemId, WorkerId = "a", SegmentDir = dir });
        await WaitUntil(() => completed == 21);
        Assert.True(File.Exists(Path.Combine(dir, "take-0002.wav")));
    }

    [Fact]
    public async Task TranscriptionJobsReturnTheTranscriptAndWriteNoTake()
    {
        using var tmp = new TempDir();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        GenerationJob? done = null;
        s.JobCompleted += j => done = j;
        s.Enqueue(new GenerationJob
        {
            WorkerId = "asr",
            Transcribe = new TranscribeRequest { AudioPath = Path.Combine(tmp.Path, "in.wav"), Text = "hello there" },
        });
        await WaitUntil(() => done != null);
        Assert.Equal(JobState.Completed, done!.State);
        Assert.Equal(["hello", "there"], done.Transcript!.Words.Select(w => w.Text));
        Assert.Null(done.Result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp.Path));
    }

    [Fact]
    public async Task EnqueueReplacesQueuedJobForSameItem()
    {
        using var tmp = new TempDir();
        var gate = new TaskCompletionSource();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w, async (_, _) => await gate.Task), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        var id = Guid.NewGuid();
        s.Enqueue(new GenerationJob { ItemId = id, WorkerId = "a", SegmentDir = tmp.Path });
        var replacement = new GenerationJob { ItemId = id, WorkerId = "a", SegmentDir = tmp.Path, Priority = JobPriority.High };
        s.Enqueue(replacement);
        try
        {
            await WaitUntil(() => s.RunningCount == 1);
            Assert.Equal(1, s.QueuedCount);
            var queued = Assert.Single(s.JobSnapshots(), j => j.ItemId == id);
            Assert.Equal(replacement.Id, queued.Id);
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public async Task CrashRequeuesJobThenSucceeds()
    {
        using var tmp = new TempDir();
        var created = 0;
        await using var s = new GenerationScheduler((w, _) =>
        {
            var n = Interlocked.Increment(ref created);
            FakeConnection c = null!;
            c = new FakeConnection(w, async (_, _) =>
            {
                await Task.Delay(30);
                if (n == 1) { c.Crash(); throw new WorkerCrashedException("killed"); }
            });
            return c;
        }, _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        GenerationJob? done = null;
        s.JobCompleted += j => done = j;
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => done != null);
        Assert.Equal(1, done!.Attempts);
        Assert.Equal(2, created); // the worker was restarted
    }

    [Fact]
    public async Task FailsAfterTwoAttempts()
    {
        using var tmp = new TempDir();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w, (_, _) => throw new WorkerErrorException("bad input", false, null)), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        GenerationJob? failed = null;
        s.JobFailed += j => failed = j;
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => failed != null);
        Assert.Equal(JobState.Failed, failed!.State);
        Assert.Equal(2, failed.Attempts);
        Assert.Equal("bad input", failed.LastError);
    }

    [Fact]
    public async Task UnfusableFailsWithoutRetry()
    {
        using var tmp = new TempDir();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w,
            (_, _) => throw new WorkerErrorException("too many voices", false, null, ErrorCodes.Unfusable)), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        GenerationJob? failed = null;
        s.JobFailed += j => failed = j;
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => failed != null);
        Assert.Equal(1, failed!.Attempts);
    }

    [Fact]
    public async Task CancelRunningJob()
    {
        using var tmp = new TempDir();
        var hung = new TaskCompletionSource();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w, (_, _) => hung.Task), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        GenerationJob? ended = null;
        s.JobFailed += j => ended = j;
        // The fake never finishes and ignores cancellation, so only the kill after CancelKillDelay ends the job.
        s.Options.CancelKillDelay = TimeSpan.FromMilliseconds(100);
        var job = new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path };
        s.Enqueue(job);
        await WaitUntil(() => s.RunningCount == 1);
        s.Cancel(job.Id);
        await WaitUntil(() => ended != null);
        Assert.Equal(JobState.Cancelled, ended!.State);
    }

    [Fact]
    public async Task UnrunnableWorkerFailsImmediately()
    {
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w), _ => WorkerHosting.AnyLocal) { CheckWorkerRunnable = _ => "not installed" };
        s.ConfigureDevices(Gpus(1));
        GenerationJob? failed = null;
        s.JobFailed += j => failed = j;
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "x", SegmentDir = "." });
        Assert.NotNull(failed);
        Assert.Equal("not installed", failed!.LastError);
    }

    [Fact]
    public async Task GpuOnlyWorkerFailsWithoutGpu()
    {
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w), _ => new WorkerHosting(WorkerPlacement.Gpu));
        s.ConfigureDevices([new DeviceConfig { Kind = DeviceKind.Cpu, Name = "CPU" }]);
        GenerationJob? failed = null;
        s.JobFailed += j => failed = j;
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "big", SegmentDir = "." });
        Assert.NotNull(failed);
        Assert.Contains("set to run on a GPU", failed!.LastError);
    }

    [Fact]
    public async Task TwoWorkersShareOneGpuWithMaxActiveTwo()
    {
        using var tmp = new TempDir();
        var gate = new TaskCompletionSource();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w, async (_, _) => await gate.Task), _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices([new DeviceConfig { Kind = DeviceKind.Gpu, GpuIndex = 0, Name = "GPU 0", MaxActive = 2 }]);
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "b", SegmentDir = tmp.Path });
        await WaitUntil(() => s.RunningCount == 2);
        Assert.Equal(2, s.WorkerSnapshots().Count);
        Assert.Equal(2, s.DeviceSnapshots().Single().Active);
        gate.SetResult();
        await WaitUntil(() => s.RunningCount == 0);
        // Both stay loaded (max total: any), ready for their next job.
        Assert.All(s.WorkerSnapshots(), w => Assert.Equal(WorkerState.Idle, w.State));
        Assert.Equal(2, s.UnloadIdleWorkers());
    }

    [Fact]
    public async Task WorkerSettingsChangeStopsIdleProcessesThatNoLongerFit()
    {
        using var tmp = new TempDir();
        var hosting = new WorkerHosting(WorkerPlacement.Gpu);
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w), _ => hosting);
        s.ConfigureDevices([.. Gpus(1), new DeviceConfig { Kind = DeviceKind.Cpu, Name = "CPU" }]);
        var done = 0;
        s.JobCompleted += _ => Interlocked.Increment(ref done);
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => done == 1 && s.RunningCount == 0);
        Assert.Equal(DeviceKind.Gpu, s.DeviceSnapshots()[s.WorkerSnapshots().Single().DeviceIndex].Kind);

        hosting = new WorkerHosting(WorkerPlacement.Cpu); // the user moved the worker to the CPU
        s.Reconsider();
        await WaitUntil(() => s.WorkerSnapshots().Count == 0);
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => done == 2);
        Assert.Equal(DeviceKind.Cpu, s.DeviceSnapshots().Single(d => d.Total == 1).Kind);
    }

    [Fact]
    public async Task StartErrorClearsWhenTheWorkerStartsFineLater()
    {
        using var tmp = new TempDir();
        var created = 0;
        await using var s = new GenerationScheduler((w, _) => Interlocked.Increment(ref created) == 1
            ? new FakeConnection(w) { LoadError = new WorkerErrorException("download incomplete", false, null) }
            : new FakeConnection(w), _ => WorkerHosting.AnyLocal);
        s.Options.StartFailureBackoff = TimeSpan.FromMilliseconds(50);
        s.ConfigureDevices(Gpus(1));
        var done = 0;
        s.JobCompleted += _ => Interlocked.Increment(ref done);
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => s.DeviceSnapshots().Single().Errors.Count == 1);
        Assert.Contains("download incomplete", s.DeviceSnapshots().Single().Errors[0].Message);

        await WaitUntil(() => done == 1); // the retry starts it fine
        await WaitUntil(() => s.DeviceSnapshots().Single().Errors.Count == 0);
    }

    [Fact]
    public async Task DismissClearsErrors()
    {
        using var tmp = new TempDir();
        await using var s = new GenerationScheduler((w, _) => new FakeConnection(w) { LoadError = new WorkerErrorException("broken", false, null) },
            _ => WorkerHosting.AnyLocal);
        s.ConfigureDevices(Gpus(1));
        s.Enqueue(new GenerationJob { ItemId = Guid.NewGuid(), WorkerId = "a", SegmentDir = tmp.Path });
        await WaitUntil(() => s.DeviceSnapshots().Single().Errors.Count == 1);
        s.DismissErrors(s.DeviceSnapshots().Single().Index);
        Assert.Empty(s.DeviceSnapshots().Single().Errors);
    }

    [Fact]
    public void ParsesNvidiaSmi()
    {
        var gpus = GpuDetector.Parse(["0, NVIDIA GeForce RTX 3090, 24576", "1, NVIDIA GeForce RTX 3090, 24576 MiB"]);
        Assert.Equal(2, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 3090", gpus[1].Name);
        Assert.Equal(24576, gpus[1].MemoryMb);
    }
}
