namespace LocalAudiobookStudio.Workers.Scheduling;

/// <summary>What the scheduler should do with one job on one device.</summary>
/// <param name="Reuse">An idle process of the job's worker on the device, or null to start a new one.</param>
/// <param name="Evict">An idle process (another worker's) to stop first, to stay within the device's "max total".</param>
public sealed record Assignment(GenerationJob Job, ComputeDevice Device, WorkerProcess? Reuse, WorkerProcess? Evict);

/// <summary>
/// Pure job-selection rules, kept separate from process management so they can be unit-tested.
/// When a device has room for one more active worker (fewer than its "max active" are generating or loading):
/// 1. Consider the highest-priority jobs the device may run, in reading order.
/// 2. Prefer a job whose worker already has an idle process here (no model load), unless a
///    higher-priority job for another worker has waited longer than <see cref="AffinityMaxWait"/>.
/// 3. A job that needs a new process is only taken when the worker has fewer than its "parallel
///    workers" processes here, no other process of the same worker is still preparing (it may be
///    downloading the model files), and the device stays within "max total", stopping its longest-idle
///    process of another worker if needed. Different workers start and load side by side.
/// 4. If a job needs a new process here but another device with room already has an idle process of
///    that worker, leave the job for that device.
/// </summary>
public static class SchedulingPolicy
{
    public static TimeSpan AffinityMaxWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Queue order: priority (high first), then reading order, then enqueue sequence.</summary>
    public static int Compare(GenerationJob a, GenerationJob b)
    {
        var c = b.Priority.CompareTo(a.Priority);
        if (c != 0) return c;
        c = a.ReadingOrder.CompareTo(b.ReadingOrder);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }

    public static int ActiveOn(ComputeDevice d, IReadOnlyList<WorkerProcess> processes) =>
        processes.Count(p => ReferenceEquals(p.Device, d) && p.IsActive);

    public static int TotalOn(ComputeDevice d, IReadOnlyList<WorkerProcess> processes) =>
        processes.Count(p => ReferenceEquals(p.Device, d));

    public static bool HasRoom(ComputeDevice d, IReadOnlyList<WorkerProcess> processes, Func<string, WorkerHosting> hostingOf) =>
        d.Enabled && ActiveOn(d, processes) < d.MaxActive(hostingOf);

    public static Assignment? PickNext(
        ComputeDevice device,
        IReadOnlyList<GenerationJob> queue,
        IReadOnlyList<WorkerProcess> processes,
        IReadOnlyList<ComputeDevice> devices,
        Func<string, WorkerHosting> hostingOf,
        DateTime nowUtc)
    {
        if (!HasRoom(device, processes, hostingOf)) return null;

        Assignment? best = null;      // rule 1
        Assignment? affinity = null;  // rule 2
        var total = TotalOn(device, processes);

        foreach (var job in queue)
        {
            if (job.State != JobState.Queued) continue;
            var hosting = hostingOf(job.WorkerId);
            if (!device.Allows(job.WorkerId, hosting)) continue;

            var idle = processes.FirstOrDefault(p => ReferenceEquals(p.Device, device) && p.IsIdle && !p.NeedsRestart &&
                                                     string.Equals(p.WorkerId, job.WorkerId, StringComparison.OrdinalIgnoreCase));
            Assignment candidate;
            if (idle != null)
            {
                candidate = new Assignment(job, device, idle, null);
            }
            else
            {
                if (processes.Any(p => p.IsPreparing && string.Equals(p.WorkerId, job.WorkerId, StringComparison.OrdinalIgnoreCase)))
                    continue; // another process of this worker is still preparing (maybe downloading)
                if (device.StartBackoffUntil.TryGetValue(job.WorkerId, out var until) && until > nowUtc) continue;
                var mine = processes.Count(p => ReferenceEquals(p.Device, device) &&
                                                string.Equals(p.WorkerId, job.WorkerId, StringComparison.OrdinalIgnoreCase));
                if (mine >= hosting.Lanes) continue; // all of its processes here are busy: wait for one
                WorkerProcess? evict = null;
                if (device.MaxTotal is int max && total >= max)
                {
                    evict = processes.Where(p => ReferenceEquals(p.Device, device) && p.IsIdle)
                        .OrderBy(p => p.IdleSinceUtc).FirstOrDefault();
                    if (evict == null) continue; // full of busy processes
                }
                if (OtherDeviceHasIdle(device, job.WorkerId, hosting, processes, hostingOf)) continue; // rule 4
                candidate = new Assignment(job, device, null, evict);
            }

            if (best == null || Compare(job, best.Job) < 0) best = candidate;
            if (candidate.Reuse != null && (affinity == null || Compare(job, affinity.Job) < 0)) affinity = candidate;
        }

        if (best == null) return null;
        if (affinity == null || ReferenceEquals(affinity, best)) return best;

        // The best job needs a model load. Keep the loaded worker busy unless the best job outranks
        // it and has been waiting too long.
        if (best.Job.Priority > affinity.Job.Priority && nowUtc - best.Job.EnqueuedUtc > AffinityMaxWait) return best;
        return affinity;
    }

    private static bool OtherDeviceHasIdle(ComputeDevice self, string workerId, WorkerHosting hosting,
        IReadOnlyList<WorkerProcess> processes, Func<string, WorkerHosting> hostingOf)
    {
        foreach (var p in processes)
        {
            if (ReferenceEquals(p.Device, self) || !p.IsIdle || p.NeedsRestart) continue;
            if (!string.Equals(p.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Device.Allows(workerId, hosting) && HasRoom(p.Device, processes, hostingOf)) return true;
        }
        return false;
    }
}
