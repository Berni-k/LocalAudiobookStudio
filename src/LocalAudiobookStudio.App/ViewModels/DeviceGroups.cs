using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>A GPU or the CPU as the UI shows it, with the worker processes running there.</summary>
/// <param name="Device">The GPU or CPU (limits, errors).</param>
/// <param name="Workers">Its processes; external workers (ComfyUI bridge) are listed with the CPU.</param>
/// <param name="ExternalDevices">External devices folded into this group (CPU only), for their errors.</param>
public sealed record DeviceGroup(DeviceSnapshot Device, IReadOnlyList<WorkerSnapshot> Workers, IReadOnlyList<DeviceSnapshot> ExternalDevices)
{
    public string Key => Device.Kind == DeviceKind.Gpu ? $"gpu{Device.GpuIndex}" : "cpu";
    /// <summary>"GPU0", "CPU": the short label of the transport indicators.</summary>
    public string ShortLabel => Device.Kind == DeviceKind.Gpu ? $"GPU{Device.GpuIndex}" : "CPU";
    public int Active => Device.Active + ExternalDevices.Sum(d => d.Active);
    public IEnumerable<DeviceError> Errors => Device.Errors.Concat(ExternalDevices.SelectMany(d => d.Errors)).OrderBy(e => e.AtUtc);

    /// <summary>"12:04  Kokoro 82M: Failed to start: …" per error, newest last.</summary>
    public IEnumerable<string> ErrorLines => Errors.Select(e =>
        $"{e.AtUtc.ToLocalTime():HH:mm}  {AppHost.Catalog.Get(e.WorkerId)?.DisplayName ?? e.WorkerId}: {e.Message}");

    /// <summary>The scheduler devices in this group (the CPU and any external ones), for dismissing errors.</summary>
    public IEnumerable<int> DeviceIndexes => new[] { Device.Index }.Concat(ExternalDevices.Select(d => d.Index)).Where(i => i >= 0);

    /// <summary>
    /// Groups the scheduler's devices for display: each GPU, then the CPU with external workers'
    /// processes (they render elsewhere but are local processes as far as the user is concerned).
    /// The CPU is included when something runs there or <paramref name="showIdleCpu"/> is set.
    /// </summary>
    public static List<DeviceGroup> Build(IReadOnlyList<DeviceSnapshot> devices, IReadOnlyList<WorkerSnapshot> workers, bool showIdleCpu)
    {
        // A multi-GPU process is listed on each of its GPUs.
        IReadOnlyList<WorkerSnapshot> On(DeviceSnapshot d) => workers.Where(w => w.DeviceIndexes.Contains(d.Index)).OrderBy(w => w.WorkerId).ThenBy(w => w.Lane).ToList();

        var groups = devices.Where(d => d.Kind == DeviceKind.Gpu).OrderBy(d => d.GpuIndex)
            .Select(d => new DeviceGroup(d, On(d), [])).ToList();

        var external = devices.Where(d => d.Kind == DeviceKind.External).ToList();
        var cpu = devices.FirstOrDefault(d => d.Kind == DeviceKind.Cpu)
                  ?? new DeviceSnapshot(-1, DeviceKind.Cpu, -1, "CPU", true, 1, null, 0, 0, [], [], null);
        var cpuWorkers = On(cpu).Concat(external.SelectMany(On)).ToList();
        if (cpuWorkers.Count > 0 || showIdleCpu || external.Any(d => d.Errors.Count > 0))
            groups.Add(new DeviceGroup(cpu, cpuWorkers, external));
        return groups;
    }

    /// <summary>Whether any worker is set to run on the CPU (then the CPU is shown even when idle).</summary>
    public static bool AnyWorkerOnCpu() =>
        AppHost.Catalog.All.Any(m => !m.External && AppHost.HostingOf(m.Id).Device == WorkerPlacement.Cpu);
}
