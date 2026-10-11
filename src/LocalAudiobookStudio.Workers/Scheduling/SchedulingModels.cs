using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Workers.Scheduling;

public enum JobPriority
{
    Low = 0,     // speaker previews
    Normal = 1,  // batch commands
    High = 2,    // manual regenerate of a few items
}

public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>One generation for one segment (or a speaker preview).</summary>
public sealed class GenerationJob
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The segment this job generates; <see cref="Guid.Empty"/> for previews.</summary>
    public Guid ItemId { get; init; }

    public string WorkerId { get; init; } = "";
    public JobPriority Priority { get; set; } = JobPriority.Normal;

    /// <summary>Reading-order key (chapter index, then item index) so review can start at the beginning.</summary>
    public long ReadingOrder { get; init; }

    /// <summary>Monotonic enqueue number; ties within a priority break on reading order, then this.</summary>
    public long Sequence { get; internal set; }

    /// <summary>The segment's inputHash when the job was created. Compared again on completion.</summary>
    public string? InputHash { get; init; }

    public GenerateRequest Request { get; init; } = new();

    /// <summary>
    /// Set for a transcription job (a worker tagged Transcription or Alignment): the job sends this instead of
    /// <see cref="Request"/>, writes no audio, and its result is <see cref="Transcript"/>.
    /// </summary>
    public TranscribeRequest? Transcribe { get; init; }

    /// <summary>
    /// Set for a voice comparison job (a worker tagged Speaker Verification): the job sends this instead of
    /// <see cref="Request"/>, writes no audio, and its result is <see cref="VoiceScores"/>.
    /// </summary>
    public VoiceCompareRequest? CompareVoices { get; init; }

    /// <summary>Folder that receives take-NNNN.wav. Null when <see cref="FixedOutPath"/> is used.</summary>
    public string? SegmentDir { get; init; }

    /// <summary>For previews: write exactly here instead of allocating a take number.</summary>
    public string? FixedOutPath { get; init; }

    /// <summary>Human-readable label for the Jobs panel.</summary>
    public string Label { get; init; } = "";

    /// <summary>App-specific data (for example the kind of job).</summary>
    public object? Tag { get; init; }

    /// <summary>
    /// For a fused generation: the segments it makes a take for (<see cref="ItemId"/> is the first). The worker's audio
    /// goes to <see cref="FixedOutPath"/>; each member gets a take number of its own, which <see cref="AfterRun"/> fills.
    /// </summary>
    public IReadOnlyList<JobMember>? Members { get; init; }

    /// <summary>The segments this job generates: its members, or its one item.</summary>
    public IEnumerable<Guid> Items => Members?.Select(m => m.ItemId) ?? (ItemId == Guid.Empty ? [] : [ItemId]);

    public bool Covers(Guid itemId) => itemId != Guid.Empty && (Members?.Any(m => m.ItemId == itemId) ?? ItemId == itemId);

    /// <summary>
    /// For a listening job: the job that continues it, made from its result (an aligner timing the words a transcriber
    /// heard), or null to end here. When there is one, this job ends quietly (no <see cref="AfterRun"/>, no
    /// <see cref="GenerationScheduler.JobCompleted"/>) and the next one finishes in its place; give it the same
    /// <see cref="Tag"/> so it is handled the same way.
    /// </summary>
    public Func<GenerationJob, GenerationJob?>? Then { get; init; }

    /// <summary>The job this one continues (<see cref="Then"/>): its transcript's language carries over.</summary>
    public GenerationJob? ContinuesFrom { get; internal set; }

    /// <summary>Runs on the worker's task before the request is sent (e.g. decoding context audio for it).</summary>
    public Func<GenerationJob, CancellationToken, Task>? BeforeRun { get; init; }

    /// <summary>
    /// Runs on the worker's task after the worker finished (after <see cref="GenerationScheduler.OnGenerated"/>),
    /// before the job counts as done; failing it fails the job. Cutting a fused generation into takes happens here.
    /// </summary>
    public Func<GenerationJob, Task>? AfterRun { get; init; }

    // --- runtime state, owned by the scheduler ---
    public JobState State { get; internal set; } = JobState.Queued;
    public int Attempts { get; internal set; }
    public string? LastError { get; internal set; }
    /// <summary>The worker process running this job (<see cref="WorkerProcess.Id"/>).</summary>
    public int? ProcessId { get; internal set; }
    public DateTime EnqueuedUtc { get; internal set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; internal set; }
    public DateTime? FinishedUtc { get; internal set; }
    public double Progress { get; internal set; }
    public string? ProgressMessage { get; internal set; }
    public int TakeNumber { get; internal set; }
    public GenerateResult? Result { get; internal set; }
    public TranscriptResult? Transcript { get; internal set; }

    /// <summary>The result of a <see cref="CompareVoices"/> job.</summary>
    public VoiceScores? VoiceScores { get; internal set; }
    /// <summary>The device it ran on, e.g. "GPU 0 (NVIDIA GeForce RTX 3090)" or "CPU".</summary>
    public string? DeviceName { get; internal set; }
    internal CancellationTokenSource Cancellation { get; set; } = new();
    internal bool CancelRequested { get; set; }
}

/// <summary>A segment a fused generation makes a take for.</summary>
public sealed class JobMember
{
    public Guid ItemId { get; init; }

    /// <summary>Folder that receives the member's take.</summary>
    public string SegmentDir { get; init; } = "";

    /// <summary>Its take number, reserved when the job starts.</summary>
    public int TakeNumber { get; internal set; }
}

public enum DeviceKind
{
    Gpu,
    Cpu,
    /// <summary>An external worker's own compute (ComfyUI bridge); shown with the CPU in the UI.</summary>
    External,
}

/// <summary>Where a worker runs: on a GPU, on the CPU, or only on its own external device.</summary>
[Flags]
public enum WorkerPlacement
{
    None = 0,
    Gpu = 1,
    Cpu = 2,
    /// <summary>Manages its own compute (ComfyUI bridge): runs only on the external device for that worker.</summary>
    External = 4,
    Local = Gpu | Cpu,
}

/// <summary>
/// Where and how wide a worker runs, from its worker settings ("device", "parallel", "gpus").
/// </summary>
/// <param name="Device">Gpu or Cpu (Local = either, for tests), or External.</param>
/// <param name="Parallel">How many processes of the worker may run on one device at once.</param>
/// <param name="Gpus">GPU ids the worker may use; null or empty = all.</param>
public sealed record WorkerHosting(WorkerPlacement Device, int Parallel = 1, IReadOnlySet<int>? Gpus = null)
{
    public static readonly WorkerHosting AnyLocal = new(WorkerPlacement.Local);
    public static readonly WorkerHosting External = new(WorkerPlacement.External);

    public int Lanes => Math.Max(1, Parallel);
    public bool AllowsGpu(int gpuIndex) => Gpus is not { Count: > 0 } || Gpus.Contains(gpuIndex);
}

/// <summary>A device as the app configures it (from settings).</summary>
public sealed record DeviceConfig
{
    public DeviceKind Kind { get; init; }
    /// <summary>CUDA device index for GPUs; -1 otherwise.</summary>
    public int GpuIndex { get; init; } = -1;
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>Workers generating (or loading) at once. External devices use the worker's "parallel" instead.</summary>
    public int MaxActive { get; init; } = 1;
    /// <summary>Worker processes on the device, idle ones included; null = any.</summary>
    public int? MaxTotal { get; init; }
    /// <summary>CPU: threads per worker (LAS_CPU_THREADS); 0 = PyTorch's default.</summary>
    public int CpuThreads { get; init; }
    /// <summary>External devices: the one worker that runs there.</summary>
    public string? ExternalWorker { get; init; }

    internal bool SameDevice(DeviceConfig o) =>
        Kind == o.Kind && GpuIndex == o.GpuIndex && string.Equals(ExternalWorker, o.ExternalWorker, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A GPU, the CPU, or an external worker's compute. Worker processes start on it as jobs need them,
/// within its limits: at most <see cref="DeviceConfig.MaxActive"/> generating at once and at most
/// <see cref="DeviceConfig.MaxTotal"/> processes in all (idle ones are stopped to make room).
/// </summary>
public sealed class ComputeDevice
{
    internal ComputeDevice(int index, DeviceConfig config)
    {
        Index = index;
        Config = config;
    }

    public int Index { get; }
    public DeviceConfig Config { get; internal set; }
    public DeviceKind Kind => Config.Kind;
    public int GpuIndex => Config.GpuIndex;
    public string Name => Config.Name;
    public bool Enabled => Config.Enabled && !Removed;
    public int CpuThreads => Config.CpuThreads;

    /// <summary>No longer configured: its workers finish their jobs and stop.</summary>
    internal bool Removed { get; set; }

    /// <summary>
    /// The latest problem of each worker here (failed start, disabled after crashes), by worker id.
    /// A worker's entry is cleared when it next starts here successfully, when it is re-enabled, or
    /// when the user dismisses the errors.
    /// </summary>
    internal Dictionary<string, DeviceError> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Workers disabled here after repeated crashes, until the user re-enables them.</summary>
    public HashSet<string> DisabledWorkers { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<DateTime>> Crashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>After a worker failed to start here, it isn't started again here until this time.</summary>
    internal Dictionary<string, DateTime> StartBackoffUntil { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Workers generating or loading at once: external devices take the worker's "parallel".</summary>
    public int MaxActive(Func<string, WorkerHosting> hostingOf) =>
        Kind == DeviceKind.External && Config.ExternalWorker is { } w ? hostingOf(w).Lanes : Math.Max(1, Config.MaxActive);

    public int? MaxTotal => Kind == DeviceKind.External ? null : Config.MaxTotal is > 0 and var t ? t : null;

    /// <summary>Whether the worker may run here at all (ignores what is running now).</summary>
    public bool Allows(string workerId, WorkerHosting hosting)
    {
        if (!Enabled || DisabledWorkers.Contains(workerId)) return false;
        if (Kind == DeviceKind.External) return string.Equals(Config.ExternalWorker, workerId, StringComparison.OrdinalIgnoreCase);
        if (hosting.Device.HasFlag(WorkerPlacement.External)) return false;
        return Kind switch
        {
            DeviceKind.Gpu => hosting.Device.HasFlag(WorkerPlacement.Gpu) && hosting.AllowsGpu(GpuIndex),
            DeviceKind.Cpu => hosting.Device.HasFlag(WorkerPlacement.Cpu),
            _ => false,
        };
    }
}

public enum WorkerState
{
    /// <summary>The process is starting (Python imports) and checking or downloading its model files.</summary>
    Starting,
    /// <summary>Files are in place; the model is being read into memory.</summary>
    Loading,
    Idle,
    Busy,
}

/// <summary>One running worker process on a device. It belongs to one worker for its whole life.</summary>
public sealed class WorkerProcess
{
    internal WorkerProcess(int id, ComputeDevice device, string workerId, int lane)
    {
        Id = id;
        Device = device;
        WorkerId = workerId;
        Lane = lane;
    }

    public int Id { get; }
    public ComputeDevice Device { get; }
    public string WorkerId { get; }

    /// <summary>0-based number among this worker's processes on the device (LAS_LANE; the ComfyUI bridge picks its server by it).</summary>
    public int Lane { get; }

    public WorkerState State { get; internal set; } = WorkerState.Starting;
    public GenerationJob? CurrentJob { get; internal set; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime IdleSinceUtc { get; internal set; } = DateTime.UtcNow;
    public DateTime? BusySinceUtc { get; internal set; }
    public string? StatusMessage { get; internal set; }

    internal IWorkerConnection? Connection { get; set; }
    internal bool NeedsRestart { get; set; }

    /// <summary>
    /// Still before its prepare stage finished (imports, model files). Another process of the same worker
    /// waits until then, so two processes never download the same model at once.
    /// </summary>
    public bool IsPreparing => State == WorkerState.Starting;

    /// <summary>Generating or loading: counts against the device's "max active".</summary>
    public bool IsActive => State != WorkerState.Idle;
    public bool IsIdle => State == WorkerState.Idle;
}

/// <summary>A worker's problem on a device, with when it happened.</summary>
public sealed record DeviceError(string WorkerId, string Message, DateTime AtUtc);

/// <summary>Read-only copies for the UI.</summary>
public sealed record DeviceSnapshot(
    int Index, DeviceKind Kind, int GpuIndex, string Name, bool Enabled, int MaxActive, int? MaxTotal,
    int Active, int Total, IReadOnlyList<DeviceError> Errors, IReadOnlyCollection<string> DisabledWorkers, string? ExternalWorker);

public sealed record WorkerSnapshot(
    int Id, int DeviceIndex, string WorkerId, int Lane, WorkerState State, Guid? JobId, Guid? ItemId,
    string? JobLabel, double Progress, string? ProgressMessage, DateTime StartedUtc, DateTime? BusySinceUtc,
    DateTime IdleSinceUtc, string? StatusMessage);

public sealed record JobSnapshot(
    Guid Id, Guid ItemId, string WorkerId, string Label, JobPriority Priority, JobState State,
    int? ProcessId, double Progress, string? ProgressMessage, string? LastError, int Attempts,
    DateTime EnqueuedUtc, DateTime? StartedUtc, int QueuePosition);
