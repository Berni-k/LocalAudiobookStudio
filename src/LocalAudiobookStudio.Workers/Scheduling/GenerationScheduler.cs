using LocalAudiobookStudio.Workers.Protocol;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Workers.Scheduling;

public sealed class SchedulerOptions
{
    /// <summary>Idle workers are shut down after this long (zero = never).</summary>
    public TimeSpan WorkerIdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>After cancelling a running job, kill the worker if it hasn't stopped within this time.</summary>
    public TimeSpan CancelKillDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A job is marked failed after this many failed attempts.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>This many crashes of one worker on one device within <see cref="CrashWindow"/> disables it there.</summary>
    public int CrashLimit { get; set; } = 3;
    public TimeSpan CrashWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>After a worker failed to start on a device, it isn't started there again for this long.</summary>
    public TimeSpan StartFailureBackoff { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Creates a worker connection for a new worker process (the app supplies paths and environment).</summary>
public delegate IWorkerConnection WorkerConnectionFactory(string workerId, WorkerProcess process);

/// <summary>
/// Owns the job queue and the worker processes. Devices (GPUs, the CPU, external workers' compute)
/// get processes as jobs need them, within each device's limits and each worker's "parallel workers"
/// (see <see cref="SchedulingPolicy"/>). Runs on its own task; all state is guarded by one lock.
/// </summary>
public sealed partial class GenerationScheduler : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly List<GenerationJob> _queue = new();
    private readonly List<GenerationJob> _finished = new(); // recent failed/cancelled
    private readonly List<ComputeDevice> _devices = new();
    private readonly List<WorkerProcess> _processes = new();
    private readonly Dictionary<Guid, HashSet<int>> _reservedTakes = new();
    private readonly WorkerConnectionFactory _factory;
    private readonly Func<string, WorkerHosting> _hostingOf;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _sequence;
    private int _processIds;
    private int _deviceIds;

    public GenerationScheduler(
        WorkerConnectionFactory factory,
        Func<string, WorkerHosting> hostingOf,
        SchedulerOptions? options = null,
        ILogger? logger = null)
    {
        _factory = factory;
        _hostingOf = hostingOf;
        Options = options ?? new SchedulerOptions();
        _logger = logger;
        _loop = Task.Run(RunLoopAsync);
    }

    public SchedulerOptions Options { get; }

    /// <summary>Raised (on scheduler threads) whenever queue or worker state changes. The UI should batch these.</summary>
    public event Action? Changed;

    /// <summary>
    /// Called on the worker's task after the worker wrote the WAV, before the job counts as done.
    /// The app measures loudness, writes peaks and take metadata here.
    /// </summary>
    public Func<GenerationJob, GenerateResult, Task>? OnGenerated { get; set; }

    /// <summary>Raised after <see cref="OnGenerated"/> succeeded.</summary>
    public event Action<GenerationJob>? JobCompleted;

    /// <summary>Raised when a job is finally marked failed (after retries) or cancelled.</summary>
    public event Action<GenerationJob>? JobFailed;

    /// <summary>Worker diagnostic output: (process, worker, line).</summary>
    public event Action<WorkerProcess, string, string>? WorkerLog;

    /// <summary>Returns a user-facing reason if the worker can't run at all (for example not installed).</summary>
    public Func<string, string?>? CheckWorkerRunnable { get; set; }

    // ---------------------------------------------------------------- devices

    /// <summary>
    /// Replaces the device configuration. Devices are matched by kind and GPU index, so running workers
    /// keep running; workers on removed or disabled devices finish their job and stop.
    /// </summary>
    public void ConfigureDevices(IEnumerable<DeviceConfig> configs)
    {
        lock (_lock)
        {
            var keep = new List<ComputeDevice>();
            foreach (var c in configs)
            {
                var existing = _devices.FirstOrDefault(d => d.Config.SameDevice(c));
                if (existing != null)
                {
                    existing.Config = c;
                    existing.Removed = false;
                    keep.Add(existing);
                }
                else
                {
                    keep.Add(new ComputeDevice(_deviceIds++, c));
                }
            }
            // Devices no longer configured stay listed while workers still run on them.
            foreach (var old in _devices.Where(d => !keep.Contains(d)))
            {
                old.Removed = true;
                if (_processes.Any(p => ReferenceEquals(p.Device, old))) keep.Add(old);
            }
            _devices.Clear();
            _devices.AddRange(keep);
        }
        RaiseChanged();
        Signal();
    }

    public IReadOnlyList<DeviceSnapshot> DeviceSnapshots()
    {
        lock (_lock)
            return _devices.Select(d => new DeviceSnapshot(d.Index, d.Kind, d.GpuIndex, d.Name, d.Enabled,
                d.MaxActive(_hostingOf), d.MaxTotal, SchedulingPolicy.ActiveOn(d, _processes), SchedulingPolicy.TotalOn(d, _processes),
                d.Errors.Values.OrderBy(e => e.AtUtc).ToList(), d.DisabledWorkers.ToList(), d.Config.ExternalWorker)).ToList();
    }

    public IReadOnlyList<WorkerSnapshot> WorkerSnapshots()
    {
        lock (_lock)
            return _processes.Select(p => new WorkerSnapshot(p.Id, p.Device.Index, p.WorkerId, p.Lane, p.State,
                p.CurrentJob?.Id, p.CurrentJob?.ItemId, p.CurrentJob?.Label, p.CurrentJob?.Progress ?? 0,
                p.CurrentJob?.ProgressMessage, p.StartedUtc, p.BusySinceUtc, p.IdleSinceUtc, p.StatusMessage)).ToList();
    }

    /// <summary>
    /// Stops an idle worker process to free its memory now (like the idle timeout does).
    /// Returns false when it is busy or gone; running jobs are never interrupted.
    /// </summary>
    public bool UnloadWorker(int processId)
    {
        WorkerProcess? p;
        lock (_lock)
        {
            // Checked and removed under one lock, so no job can be assigned to it in between.
            p = _processes.FirstOrDefault(x => x.Id == processId);
            if (p is not { IsIdle: true }) return false;
            _processes.Remove(p);
        }
        _ = StopProcessAsync(p);
        RaiseChanged();
        return true;
    }

    /// <summary>Stops every idle worker process. Returns how many were stopped.</summary>
    public int UnloadIdleWorkers()
    {
        List<WorkerProcess> idle;
        lock (_lock)
        {
            idle = _processes.Where(p => p.IsIdle).ToList();
            foreach (var p in idle) _processes.Remove(p);
        }
        foreach (var p in idle) _ = StopProcessAsync(p);
        RaiseChanged();
        return idle.Count;
    }

    /// <summary>
    /// Restarts every process of this worker before its next job (e.g. its worker settings changed):
    /// idle ones stop now, busy ones after their job.
    /// </summary>
    public void RestartWorker(string workerId)
    {
        lock (_lock)
            foreach (var p in _processes.Where(p => string.Equals(p.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)))
                p.NeedsRestart = true;
        Signal();
    }

    /// <summary>
    /// Stops this worker's idle processes now and waits until they have exited; busy ones restart after their job.
    /// Returns false when one was busy (it still has its files open).
    /// </summary>
    public async Task<bool> StopWorkerAsync(string workerId)
    {
        List<WorkerProcess> idle;
        bool anyBusy;
        lock (_lock)
        {
            var mine = _processes.Where(p => string.Equals(p.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)).ToList();
            idle = mine.Where(p => p.IsIdle).ToList();
            foreach (var p in idle) _processes.Remove(p);
            foreach (var p in mine.Except(idle)) p.NeedsRestart = true;
            anyBusy = idle.Count < mine.Count;
        }
        await Task.WhenAll(idle.Select(StopProcessAsync)).ConfigureAwait(false);
        RaiseChanged();
        return !anyBusy;
    }

    /// <summary>Re-checks where workers may run (call after a worker's device, parallel or GPU ids changed).</summary>
    public void Reconsider() => Signal();

    public void ReenableWorker(int deviceIndex, string workerId)
    {
        lock (_lock)
        {
            var d = _devices.FirstOrDefault(x => x.Index == deviceIndex);
            if (d == null) return;
            d.DisabledWorkers.Remove(workerId);
            d.Crashes.Remove(workerId);
            d.StartBackoffUntil.Remove(workerId);
            d.Errors.Remove(workerId);
        }
        RaiseChanged();
        Signal();
    }

    /// <summary>Clears the errors shown for a device (workers disabled after crashes stay disabled).</summary>
    public void DismissErrors(int deviceIndex)
    {
        lock (_lock)
        {
            var d = _devices.FirstOrDefault(x => x.Index == deviceIndex);
            if (d == null) return;
            foreach (var w in d.Errors.Keys.Where(w => !d.DisabledWorkers.Contains(w)).ToList()) d.Errors.Remove(w);
        }
        RaiseChanged();
    }

    // ---------------------------------------------------------------- jobs

    /// <summary>Adds a job. A queued job for the same segment is replaced rather than duplicated.</summary>
    public void Enqueue(GenerationJob job) => EnqueueMany([job]);

    public void EnqueueMany(IEnumerable<GenerationJob> jobs)
    {
        var failed = new List<GenerationJob>();
        lock (_lock)
        {
            foreach (var job in jobs)
            {
                var reason = CheckWorkerRunnable?.Invoke(job.WorkerId) ?? WhyNoDevice(job.WorkerId);
                if (reason != null)
                {
                    job.State = JobState.Failed;
                    job.LastError = reason;
                    job.FinishedUtc = DateTime.UtcNow;
                    AddFinished(job);
                    failed.Add(job);
                    continue;
                }
                if (job.ItemId != Guid.Empty)
                    _queue.RemoveAll(q => q.ItemId == job.ItemId && q.State == JobState.Queued);
                _finished.RemoveAll(f => f.ItemId == job.ItemId && job.ItemId != Guid.Empty);
                job.Sequence = ++_sequence;
                job.EnqueuedUtc = DateTime.UtcNow;
                job.State = JobState.Queued;
                _queue.Add(job);
            }
        }
        foreach (var f in failed) JobFailed?.Invoke(f);
        RaiseChanged();
        Signal();
    }

    /// <summary>Why no configured device can run the worker (null when one can). Call under the lock.</summary>
    private string? WhyNoDevice(string workerId)
    {
        var hosting = _hostingOf(workerId);
        if (_devices.Count == 0 || _devices.Any(d => d.Allows(workerId, hosting))) return null;
        if (hosting.Device.HasFlag(WorkerPlacement.External)) return $"{workerId} has no device to run on.";
        if (hosting.Device == WorkerPlacement.Gpu)
            return hosting.Gpus is { Count: > 0 } ids
                ? $"{workerId} may only use GPU {string.Join(", ", ids.Order())}, and none of those is an enabled GPU."
                : $"{workerId} is set to run on a GPU, and no enabled GPU may run it. Its worker settings can switch it to the CPU if it supports that.";
        if (hosting.Device == WorkerPlacement.Cpu) return $"{workerId} is set to run on the CPU, and the CPU is disabled.";
        return $"No enabled device may run {workerId}.";
    }

    private GenerationJob? RunningJob(Guid jobId) =>
        _processes.Select(p => p.CurrentJob).FirstOrDefault(j => j?.Id == jobId);

    private IEnumerable<GenerationJob> RunningJobs => _processes.Select(p => p.CurrentJob).OfType<GenerationJob>();

    /// <summary>Cancels a queued job (removed) or a running job (cancel sent; worker killed after a delay).</summary>
    public void Cancel(Guid jobId)
    {
        GenerationJob? cancelled = null;
        lock (_lock)
        {
            var job = _queue.FirstOrDefault(j => j.Id == jobId);
            if (job != null)
            {
                _queue.Remove(job);
                job.State = JobState.Cancelled;
                cancelled = job;
            }
            else if (RunningJob(jobId) is { } running)
            {
                CancelRunning(running);
            }
            _finished.RemoveAll(f => f.Id == jobId);
        }
        if (cancelled != null) JobFailed?.Invoke(cancelled);
        RaiseChanged();
    }

    public void CancelForItems(IEnumerable<Guid> itemIds)
    {
        var set = itemIds.ToHashSet();
        List<Guid> ids;
        lock (_lock)
            ids = _queue.Where(j => set.Contains(j.ItemId)).Select(j => j.Id)
                .Concat(RunningJobs.Where(j => set.Contains(j.ItemId)).Select(j => j.Id))
                .ToList();
        foreach (var id in ids) Cancel(id);
    }

    /// <summary>Clears the queue and cancels running jobs.</summary>
    public void CancelAll()
    {
        List<GenerationJob> removed;
        lock (_lock)
        {
            removed = _queue.ToList();
            _queue.Clear();
            foreach (var j in removed) j.State = JobState.Cancelled;
            foreach (var j in RunningJobs.ToList()) CancelRunning(j);
        }
        foreach (var j in removed) JobFailed?.Invoke(j);
        RaiseChanged();
    }

    private void CancelRunning(GenerationJob job)
    {
        if (job.CancelRequested) return;
        job.CancelRequested = true;
        job.ProgressMessage = "Cancelling…";
        job.Cancellation.Cancel();
        _ = Task.Run(async () =>
        {
            await Task.Delay(Options.CancelKillDelay).ConfigureAwait(false);
            WorkerProcess? p;
            lock (_lock) p = _processes.FirstOrDefault(x => ReferenceEquals(x.CurrentJob, job));
            if (p?.Connection != null)
            {
                _logger?.LogInformation("Killing {Worker} on {Device}: cancel not acknowledged", p.WorkerId, p.Device.Name);
                p.NeedsRestart = true;
                p.Connection.Kill();
            }
        });
    }

    public void Reprioritize(Guid jobId, JobPriority priority)
    {
        lock (_lock)
        {
            var job = _queue.FirstOrDefault(j => j.Id == jobId);
            if (job != null) job.Priority = priority;
        }
        RaiseChanged();
        Signal();
    }

    /// <summary>Re-queues every recently failed job.</summary>
    public void RetryFailed()
    {
        List<GenerationJob> retry;
        lock (_lock)
        {
            retry = _finished.Where(j => j.State == JobState.Failed).ToList();
            _finished.RemoveAll(j => j.State == JobState.Failed);
        }
        foreach (var j in retry) { j.Attempts = 0; j.LastError = null; j.Cancellation = new CancellationTokenSource(); j.CancelRequested = false; }
        EnqueueMany(retry);
    }

    public void ClearFinished()
    {
        lock (_lock) _finished.Clear();
        RaiseChanged();
    }

    public bool IsQueuedOrRunning(Guid itemId)
    {
        lock (_lock)
            return _queue.Any(j => j.ItemId == itemId) || RunningJobs.Any(j => j.ItemId == itemId);
    }

    public int QueuedCount { get { lock (_lock) return _queue.Count; } }
    public int RunningCount { get { lock (_lock) return RunningJobs.Count(); } }
    public int FailedCount { get { lock (_lock) return _finished.Count(j => j.State == JobState.Failed); } }

    public IReadOnlyList<JobSnapshot> JobSnapshots()
    {
        lock (_lock)
        {
            var list = new List<JobSnapshot>();
            foreach (var j in RunningJobs) list.Add(Snap(j, 0));
            var ordered = _queue.ToList();
            ordered.Sort(SchedulingPolicy.Compare);
            for (int i = 0; i < ordered.Count; i++) list.Add(Snap(ordered[i], i + 1));
            list.AddRange(_finished.Select(j => Snap(j, 0)));
            return list;
        }
    }

    /// <summary>Queue position (1-based) and progress for each queued/running item, for row overlays.</summary>
    public Dictionary<Guid, (JobState state, int position, double progress)> ItemStates()
    {
        lock (_lock)
        {
            var d = new Dictionary<Guid, (JobState, int, double)>();
            var ordered = _queue.ToList();
            ordered.Sort(SchedulingPolicy.Compare);
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].ItemId != Guid.Empty) d[ordered[i].ItemId] = (JobState.Queued, i + 1, 0);
            foreach (var j in RunningJobs)
                if (j.ItemId != Guid.Empty) d[j.ItemId] = (JobState.Running, 0, j.Progress);
            return d;
        }
    }

    private static JobSnapshot Snap(GenerationJob j, int pos) =>
        new(j.Id, j.ItemId, j.WorkerId, j.Label, j.Priority, j.State, j.ProcessId, j.Progress, j.ProgressMessage,
            j.LastError, j.Attempts, j.EnqueuedUtc, j.StartedUtc, pos);

    private void AddFinished(GenerationJob job)
    {
        _finished.Add(job);
        if (_finished.Count > 500) _finished.RemoveAt(0);
    }

    // ---------------------------------------------------------------- loop

    private void Signal() => _signal.Release();

    private void RaiseChanged() => Changed?.Invoke();

    private async Task RunLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                while (_signal.CurrentCount > 0) await _signal.WaitAsync(ct).ConfigureAwait(false);
                Dispatch();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Scheduler loop error");
            }
        }
    }

    private void Dispatch()
    {
        var now = DateTime.UtcNow;
        var toStart = new List<(WorkerProcess process, GenerationJob job, bool needsStart)>();
        var toStop = new List<WorkerProcess>();
        var changed = false;

        lock (_lock)
        {
            // Idle processes that may no longer run where they are (device disabled or removed, the
            // worker moved to another device or GPU, fewer parallel workers), need a restart, died, or
            // idled too long stop now.
            foreach (var p in _processes.Where(p => p.IsIdle).ToList())
            {
                var hosting = _hostingOf(p.WorkerId);
                var lowerLanes = _processes.Count(o => ReferenceEquals(o.Device, p.Device) && o.Lane < p.Lane &&
                                                       string.Equals(o.WorkerId, p.WorkerId, StringComparison.OrdinalIgnoreCase));
                var timedOut = Options.WorkerIdleTimeout > TimeSpan.Zero && now - p.IdleSinceUtc > Options.WorkerIdleTimeout;
                if (p.NeedsRestart || p.Connection is not { IsAlive: true } || !p.Device.Allows(p.WorkerId, hosting) ||
                    lowerLanes >= hosting.Lanes || timedOut)
                {
                    _processes.Remove(p);
                    toStop.Add(p);
                }
            }
            // A lowered "max total": stop the longest-idle processes until the device fits again.
            foreach (var d in _devices)
            {
                if (d.MaxTotal is not int max) continue;
                while (SchedulingPolicy.TotalOn(d, _processes) > max &&
                       _processes.Where(p => ReferenceEquals(p.Device, d) && p.IsIdle).OrderBy(p => p.IdleSinceUtc).FirstOrDefault() is { } idle)
                {
                    _processes.Remove(idle);
                    toStop.Add(idle);
                }
            }
            // Removed devices disappear once their last worker is gone.
            _devices.RemoveAll(d => d.Removed && !_processes.Any(p => ReferenceEquals(p.Device, d)));

            // New processes start side by side; the policy only holds back a second process of a worker
            // whose first one is still preparing (it may be downloading the model files).
            bool assigned;
            do
            {
                assigned = false;
                foreach (var device in _devices)
                {
                    var a = SchedulingPolicy.PickNext(device, _queue, _processes, _devices, _hostingOf, now);
                    if (a == null) continue;

                    var job = a.Job;
                    _queue.Remove(job);
                    if (a.Evict != null)
                    {
                        _processes.Remove(a.Evict);
                        toStop.Add(a.Evict);
                    }
                    var process = a.Reuse;
                    if (process == null)
                    {
                        process = new WorkerProcess(++_processIds, device, job.WorkerId, FreeLane(device, job.WorkerId));
                        _processes.Add(process);
                    }
                    process.State = a.Reuse != null ? WorkerState.Busy : WorkerState.Starting;
                    process.CurrentJob = job;
                    process.BusySinceUtc = now;
                    job.State = JobState.Running;
                    job.ProcessId = process.Id;
                    job.DeviceName = device.Name;
                    job.StartedUtc = now;
                    job.Progress = 0;
                    job.ProgressMessage = a.Reuse == null ? "Starting worker…" : null;
                    toStart.Add((process, job, a.Reuse == null));
                    assigned = changed = true;
                }
            } while (assigned);
            changed |= toStop.Count > 0;
        }

        foreach (var p in toStop) _ = StopProcessAsync(p);
        foreach (var (process, job, needsStart) in toStart) _ = Task.Run(() => RunJobAsync(process, job, needsStart));
        if (changed) RaiseChanged();
    }

    /// <summary>The lowest lane number not used by another process of this worker on the device. Call under the lock.</summary>
    private int FreeLane(ComputeDevice device, string workerId)
    {
        var used = _processes.Where(p => ReferenceEquals(p.Device, device) && string.Equals(p.WorkerId, workerId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Lane).ToHashSet();
        var lane = 0;
        while (used.Contains(lane)) lane++;
        return lane;
    }

    /// <summary>Shuts a process down (it has already been removed from the list).</summary>
    private async Task StopProcessAsync(WorkerProcess p)
    {
        IWorkerConnection? conn;
        lock (_lock)
        {
            conn = p.Connection;
            p.Connection = null;
        }
        if (conn != null)
        {
            try { await conn.ShutdownAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        RaiseChanged();
    }

    private async Task RunJobAsync(WorkerProcess process, GenerationJob job, bool needsStart)
    {
        var progress = new Progress<WorkerProgress>(p =>
        {
            job.Progress = p.Fraction;
            job.ProgressMessage = p.Message;
            RaiseChanged();
        });
        // Progress<T> posts to the thread pool here (no sync context); that's fine for UI batching.

        try
        {
            if (needsStart) await StartWorkerAsync(process, job).ConfigureAwait(false);

            if (job.CancelRequested) throw new OperationCanceledException();

            var conn = process.Connection ?? throw new WorkerCrashedException("Worker is not running.");
            lock (_lock)
            {
                process.State = WorkerState.Busy;
                job.ProgressMessage = null;
                job.Progress = 0;
            }
            RaiseChanged();

            if (job.Transcribe != null)
            {
                job.Transcript = await conn.TranscribeAsync(job.Transcribe, progress, job.Cancellation.Token).ConfigureAwait(false);
                job.State = JobState.Completed;
                job.FinishedUtc = DateTime.UtcNow;
                JobCompleted?.Invoke(job);
                return;
            }

            PrepareOutput(job);
            var result = await conn.GenerateAsync(job.Request, progress, job.Cancellation.Token).ConfigureAwait(false);
            if (!File.Exists(result.Path))
                throw new WorkerErrorException($"The worker reported success but {result.Path} does not exist.", false, null);
            job.Result = result;
            if (OnGenerated != null) await OnGenerated(job, result).ConfigureAwait(false);
            job.State = JobState.Completed;
            job.FinishedUtc = DateTime.UtcNow;
            JobCompleted?.Invoke(job);
        }
        catch (Exception ex) when (job.CancelRequested && ex is OperationCanceledException or WorkerCrashedException or WorkerErrorException)
        {
            job.State = JobState.Cancelled;
            job.FinishedUtc = DateTime.UtcNow;
            JobFailed?.Invoke(job);
        }
        catch (Exception ex)
        {
            HandleFailure(process, job, ex);
        }
        finally
        {
            var stop = false;
            lock (_lock)
            {
                ReleaseTake(job);
                TryDeletePartial(job);
                process.CurrentJob = null;
                process.BusySinceUtc = null;
                process.IdleSinceUtc = DateTime.UtcNow;
                process.State = WorkerState.Idle;
                // Restart before the next job (crash, out of memory, cancel timeout, fatal error, failed start).
                if (process.NeedsRestart || process.Connection is not { IsAlive: true })
                {
                    _processes.Remove(process);
                    stop = true;
                }
            }
            if (stop) await StopProcessAsync(process).ConfigureAwait(false);
            RaiseChanged();
            Signal();
        }
    }

    private async Task StartWorkerAsync(WorkerProcess process, GenerationJob job)
    {
        lock (_lock)
        {
            process.State = WorkerState.Starting;
            process.StatusMessage = $"Starting {job.WorkerId}…";
        }
        RaiseChanged();

        var conn = _factory(job.WorkerId, process);
        conn.LogLine += line => WorkerLog?.Invoke(process, job.WorkerId, line);
        conn.Exited += _ => OnConnectionExited(process, conn);
        lock (_lock) process.Connection = conn;

        try
        {
            await conn.StartAsync(job.Cancellation.Token).ConfigureAwait(false);
            lock (_lock)
            {
                process.StatusMessage = $"Preparing {job.WorkerId}…";
                job.ProgressMessage = "Preparing…";
            }
            RaiseChanged();

            var loadProgress = new Progress<WorkerProgress>(p =>
            {
                job.ProgressMessage = p.Message ?? $"Loading model… {p.Fraction:P0}";
                process.StatusMessage = job.ProgressMessage;
                RaiseChanged();
            });
            // "prepared": the model files are in place, so other processes of this worker may start now.
            void Prepared()
            {
                lock (_lock) if (process.State == WorkerState.Starting) process.State = WorkerState.Loading;
                RaiseChanged();
                Signal();
            }
            await conn.LoadAsync(loadProgress, job.Cancellation.Token, Prepared).ConfigureAwait(false);
            lock (_lock)
            {
                process.State = WorkerState.Loading; // workers without a prepare stage get here directly
                process.StatusMessage = null;
                process.Device.StartBackoffUntil.Remove(job.WorkerId);
                // It works now (e.g. the download succeeded on retry): its old problem here is resolved.
                if (!process.Device.DisabledWorkers.Contains(job.WorkerId)) process.Device.Errors.Remove(job.WorkerId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                process.Device.Errors[job.WorkerId] = new DeviceError(job.WorkerId, $"Failed to start: {ex.Message}", DateTime.UtcNow);
                process.Device.StartBackoffUntil[job.WorkerId] = DateTime.UtcNow + Options.StartFailureBackoff;
                process.StatusMessage = null;
                process.NeedsRestart = true;
            }
            conn.Kill();
            throw;
        }
    }

    private void OnConnectionExited(WorkerProcess process, IWorkerConnection conn)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(process.Connection, conn)) return;
            process.Connection = null;
            process.StatusMessage = null;
            // An idle process that exited is simply gone; a busy one is cleaned up when its job ends.
            if (process.IsIdle) _processes.Remove(process);
        }
        RaiseChanged();
        Signal();
    }

    private void HandleFailure(WorkerProcess process, GenerationJob job, Exception ex)
    {
        var message = ex switch
        {
            WorkerErrorException we => we.Message,
            WorkerCrashedException ce => ce.Message,
            _ => ex.Message,
        };
        _logger?.LogWarning("Job {Label} failed on {Device}: {Message}", job.Label, process.Device.Name, message);

        var crashed = ex is WorkerCrashedException;
        var fatal = crashed || ex is WorkerErrorException { Fatal: true } || ex is WorkerErrorException { IsOutOfMemory: true };
        if (fatal) process.NeedsRestart = true;

        if (crashed) RecordCrash(process.Device, job.WorkerId);

        job.Attempts++;
        job.LastError = message;
        if (job.Attempts < Options.MaxAttempts)
        {
            lock (_lock)
            {
                job.State = JobState.Queued;
                job.ProcessId = null;
                job.Progress = 0;
                job.ProgressMessage = $"Retrying after: {message}";
                job.Cancellation = new CancellationTokenSource();
                _queue.Add(job);
            }
            return;
        }

        job.State = JobState.Failed;
        job.FinishedUtc = DateTime.UtcNow;
        lock (_lock) AddFinished(job);
        JobFailed?.Invoke(job);
    }

    private void RecordCrash(ComputeDevice device, string workerId)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!device.Crashes.TryGetValue(workerId, out var list)) device.Crashes[workerId] = list = new();
            list.Add(now);
            list.RemoveAll(t => now - t > Options.CrashWindow);
            if (list.Count >= Options.CrashLimit)
            {
                device.DisabledWorkers.Add(workerId);
                device.Errors[workerId] = new DeviceError(workerId,
                    $"Crashed {list.Count} times in {Options.CrashWindow.TotalMinutes:0} minutes and was disabled here.", DateTime.UtcNow);
                _logger?.LogWarning("Disabled {Worker} on {Device} after repeated crashes", workerId, device.Name);
            }
        }
    }

    // ---------------------------------------------------------------- takes

    /// <summary>Allocates the next take number for the job's segment and sets the output path.</summary>
    private void PrepareOutput(GenerationJob job)
    {
        if (job.FixedOutPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.FixedOutPath)!);
            job.Request.OutPath = job.FixedOutPath;
            return;
        }
        var dir = job.SegmentDir ?? throw new InvalidOperationException("Job has no output folder.");
        Directory.CreateDirectory(dir);
        lock (_lock)
        {
            // Never a number used before (TakeStore.LastTakeFile), also when that take was deleted since.
            var max = Core.IO.TakeStore.HighestTakeNumber(dir);
            if (!_reservedTakes.TryGetValue(job.ItemId, out var reserved)) _reservedTakes[job.ItemId] = reserved = new();
            if (reserved.Count > 0) max = Math.Max(max, reserved.Max());
            job.TakeNumber = max + 1;
            reserved.Add(job.TakeNumber);
            Core.IO.TakeStore.RecordTakeNumber(dir, job.TakeNumber);
        }
        job.Request.OutPath = Path.Combine(dir, $"take-{job.TakeNumber:0000}.wav");
    }

    private void ReleaseTake(GenerationJob job)
    {
        if (job.TakeNumber == 0 || !_reservedTakes.TryGetValue(job.ItemId, out var set)) return;
        set.Remove(job.TakeNumber);
        if (set.Count == 0) _reservedTakes.Remove(job.ItemId);
    }

    private static void TryDeletePartial(GenerationJob job)
    {
        if (string.IsNullOrEmpty(job.Request.OutPath)) return;
        try { File.Delete(job.Request.OutPath + ".partial"); } catch { }
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>Sends shutdown to all workers and kills any still running after 5 seconds. Queued jobs are dropped.</summary>
    public async Task ShutdownAsync()
    {
        List<WorkerProcess> all;
        lock (_lock)
        {
            _queue.Clear();
            all = _processes.ToList();
            _processes.Clear();
            foreach (var p in all) p.CurrentJob?.Cancellation.Cancel();
        }
        await Task.WhenAll(all.Select(StopProcessAsync)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
    }
}
