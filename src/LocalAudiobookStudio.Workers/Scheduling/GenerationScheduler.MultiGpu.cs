namespace LocalAudiobookStudio.Workers.Scheduling;

/// <summary>
/// Multi-GPU jobs. A worker set to use several GPUs per process ("GPU count", <see cref="WorkerHosting.GpuCount"/>) gets
/// them all before its job starts. When the policy picks such a job on a GPU, the job claims that GPU: no other job
/// starts there. Then each GPU it may use that is free (or becomes free when its job ends) goes to the claim before
/// any other job, until it has enough; then its process starts on all of them, or its idle process (which already
/// has its GPUs) takes the job. One claim fills at a time, so two of them never each hold part of what the other
/// needs.
/// </summary>
public sealed partial class GenerationScheduler
{
    private sealed class GpuClaim(GenerationJob job, int count, WorkerProcess? reuse)
    {
        public GenerationJob Job { get; } = job;

        /// <summary>GPUs the job needs.</summary>
        public int Count { get; } = count;

        /// <summary>An idle process of the worker to run the job: the claim waits for its GPUs and no others.</summary>
        public WorkerProcess? Reuse { get; } = reuse;

        /// <summary>The GPUs held for the job, the one it was picked on first.</summary>
        public List<ComputeDevice> Held { get; } = new();
    }

    private readonly List<GpuClaim> _claims = new();

    /// <summary>Whether a multi-GPU job holds the device (no other job may start there). Call under the lock.</summary>
    private bool IsClaimed(ComputeDevice d) => _claims.Any(c => c.Held.Contains(d));

    /// <summary>
    /// Starts a claim for a multi-GPU job the policy picked on a device. False when another claim is still filling
    /// (the job waits its turn). Call under the lock.
    /// </summary>
    private bool StartClaim(Assignment a, DateTime now, List<(WorkerProcess, GenerationJob, bool)> toStart, List<WorkerProcess> toStop)
    {
        if (_claims.Count > 0) return false;
        var count = _hostingOf(a.Job.WorkerId).DevicesPerProcess(a.Device.Kind);
        var claim = new GpuClaim(a.Job, count, a.Reuse);
        // An idle process already has its GPUs: the claim waits for exactly those.
        claim.Held.AddRange(a.Reuse?.Devices ?? [a.Device]);
        _claims.Add(claim);
        AdvanceClaim(claim, now, toStart, toStop);
        return true;
    }

    /// <summary>
    /// Takes the free GPUs a claim may use, and starts its job once it has enough and all of them have room.
    /// Drops a claim whose job is gone or that can no longer be met. Returns whether anything changed. Call under the lock.
    /// </summary>
    private bool AdvanceClaim(GpuClaim claim, DateTime now, List<(WorkerProcess, GenerationJob, bool)> toStart, List<WorkerProcess> toStop)
    {
        var job = claim.Job;
        var workerId = job.WorkerId;
        var hosting = _hostingOf(workerId);
        // Cancelled, replaced, or its worker's settings changed (GPU count, GPU ids, device): back to the queue's rules.
        if (job.State != JobState.Queued || !_queue.Contains(job) ||
            hosting.DevicesPerProcess(DeviceKind.Gpu) != claim.Count ||
            claim.Held.Any(d => !d.Allows(workerId, hosting) || !_devices.Contains(d)) ||
            claim.Reuse is { } r && (!r.IsIdle || r.NeedsRestart || !_processes.Contains(r)))
        {
            Release(claim);
            return true;
        }

        var changed = false;
        while (claim.Held.Count < claim.Count &&
               _devices.Where(d => d.Kind == DeviceKind.Gpu && !IsClaimed(d) && d.Allows(workerId, hosting) &&
                                   SchedulingPolicy.HasRoom(d, _processes, _hostingOf))
                   .OrderBy(d => d.GpuIndex).FirstOrDefault() is { } free)
        {
            claim.Held.Add(free);
            changed = true;
        }

        if (claim.Held.Count < claim.Count || !claim.Held.All(d => SchedulingPolicy.HasRoom(d, _processes, _hostingOf)))
        {
            var message = $"Waiting for GPUs ({claim.Held.Count(d => SchedulingPolicy.HasRoom(d, _processes, _hostingOf))} of {claim.Count} free)";
            if (job.ProgressMessage != message)
            {
                job.ProgressMessage = message;
                changed = true;
            }
            return changed;
        }

        _claims.Remove(claim);
        _queue.Remove(job);
        var process = claim.Reuse;
        if (process == null)
        {
            // The model is split over these GPUs because it needs their memory: other workers idling there make room.
            foreach (var idle in _processes.Where(p => p.IsIdle && claim.Held.Any(p.Uses)).ToList())
            {
                _processes.Remove(idle);
                toStop.Add(idle);
            }
            var first = claim.Held[0];
            process = new WorkerProcess(++_processIds, first, workerId, FreeLane(first, workerId), claim.Held.Skip(1).ToList());
            _processes.Add(process);
        }
        Begin(process, job, claim.Reuse != null, now, toStart);
        return true;
    }

    private void Release(GpuClaim claim)
    {
        _claims.Remove(claim);
        if (claim.Job.ProgressMessage?.StartsWith("Waiting for GPUs", StringComparison.Ordinal) == true) claim.Job.ProgressMessage = null;
    }
}
