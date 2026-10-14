using System.Text.Json.Nodes;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary>
/// <c>las workers install|repair|uninstall &lt;id…&gt;</c>: what the Workers window's buttons do, from the command line.
/// </summary>
public static class WorkerSetupCommands
{
    public static readonly string[] Actions = ["install", "repair", "uninstall"];

    public static async Task<int> RunAsync(CliContext ctx, string action, CliArgs a)
    {
        var ids = a.Positionals.Skip(1).ToList();
        if (ids.Count == 0) throw new UsageException($"workers {action}: name the worker(s), for example 'las workers {action} parakeet'.");
        var host = await ctx.StartHostAsync();
        var runtime = a.Get("--runtime");
        if (runtime != null && ids.Count > 1) throw new UsageException($"workers {action}: --runtime goes with one worker.");
        foreach (var id in ids)
            if (host.Catalog.Get(id) == null)
                throw new UsageException($"workers {action}: unknown worker '{id}'. Workers: {string.Join(", ", host.Catalog.All.Select(m => m.Id))}.");

        var failed = new List<string>();
        foreach (var id in ids)
        {
            var m = runtime != null ? UseRuntime(host, host.Catalog.Get(id)!, runtime, $"workers {action}") : host.Catalog.Get(id)!;
            ctx.Info($"{Verb(action)} {m.DisplayName} ({m.RuntimeLabel})…");
            void Log(string line) => ctx.Info("  " + line);
            var ok = action switch
            {
                "install" => await host.Environments.InstallWorkerAsync(m.Id, Log, ctx.Cancel),
                "repair" => await host.Environments.RepairWorkerAsync(m.Id, Log, ctx.Cancel),
                _ => await Uninstall(host, m, Log),
            };
            ctx.Cancel.ThrowIfCancellationRequested();
            var status = host.Environments.GetWorkerStatus(m.Id);
            // Installing reports failure only for the install itself; a failed self-test leaves it installed but broken.
            ok &= action == "uninstall" ? status == EnvStatus.NotInstalled : host.Environments.IsWorkerReady(m.Id);
            if (!ok) failed.Add(m.Id);
            ctx.Info(ok ? $"{m.DisplayName}: {Done(action)}." : $"{m.DisplayName}: {action} failed ({status}). See the log above.");
            ctx.Out($"{m.Id} {status}");
        }
        ctx.WriteJson(new
        {
            action,
            results = ids.Select(id => new { worker = id, ok = !failed.Contains(id), status = host.Environments.GetWorkerStatus(id) }),
        });
        return failed.Count > 0 ? Program.ExitFailures : Program.ExitOk;
    }

    private static async Task<bool> Uninstall(StudioHost host, WorkerManifest m, Action<string> log)
    {
        await host.Environments.UninstallWorkerAsync(m.Id, log);
        return true;
    }

    private static string Verb(string action) => action switch { "install" => "Installing", "repair" => "Repairing", _ => "Uninstalling" };

    private static string Done(string action) => action switch { "install" => "installed", "repair" => "repaired", _ => "uninstalled" };

    /// <summary>
    /// The worker with another of its runtimes for this run (--runtime). In memory only: the CLI never saves the app's
    /// settings, so the app keeps the runtime chosen in its Workers window. In the las proxy (<paramref name="shared"/>)
    /// other runs use the same worker: another runtime is only taken while the worker isn't loaded, and then stays for
    /// the proxy's later runs too.
    /// </summary>
    public static WorkerManifest UseRuntime(StudioHost host, WorkerManifest manifest, string runtime, string context, bool shared = false)
    {
        var runtimes = host.Catalog.RuntimesOf(manifest.Id);
        var chosen = runtimes.FirstOrDefault(r => r.Runtime.Equals(runtime, StringComparison.OrdinalIgnoreCase))
                     ?? throw new UsageException($"{context}: {manifest.DisplayName} has no runtime '{runtime}'. Its runtimes: {string.Join(", ", runtimes.Select(r => r.Runtime))}.");
        if (chosen.Runtime.Equals(manifest.Runtime, StringComparison.OrdinalIgnoreCase)) return manifest;
        if (shared) EnsureNotInUse(host, manifest, $"--runtime {chosen.Runtime}", $"runs on {manifest.Runtime}", context);
        if (!host.Settings.WorkerSettings.TryGetValue(manifest.Id, out var values)) host.Settings.WorkerSettings[manifest.Id] = values = new JsonObject();
        values[WorkerRuntimes.SettingKey] = chosen.Runtime;
        return host.Catalog.Get(manifest.Id)!;
    }

    /// <summary>Runs the worker on the GPU or the CPU for this run (--device), in memory like <see cref="UseRuntime"/>.</summary>
    public static void UseDevice(StudioHost host, WorkerManifest manifest, string device, string context, bool shared = false)
    {
        device = device.Trim().ToLowerInvariant();
        if (device is not (WorkerManifest.DeviceGpu or WorkerManifest.DeviceCpu))
            throw new UsageException($"{context}: --device is gpu or cpu.");
        if (device == WorkerManifest.DeviceGpu ? !manifest.RunsOnGpu : !manifest.RunsOnCpu)
            throw new UsageException($"{context}: {manifest.DisplayName} ({manifest.RuntimeLabel}) doesn't run on the {device.ToUpperInvariant()}.");
        var current = host.HostingOf(manifest.Id).Device == WorkerPlacement.Cpu ? WorkerManifest.DeviceCpu : WorkerManifest.DeviceGpu;
        if (device == current) return;
        if (shared) EnsureNotInUse(host, manifest, $"--device {device}", $"runs on the {current.ToUpperInvariant()}", context);
        if (!host.Settings.WorkerSettings.TryGetValue(manifest.Id, out var values)) host.Settings.WorkerSettings[manifest.Id] = values = new JsonObject();
        values[WorkerManifest.DeviceKey] = device;
    }

    /// <summary>In the las proxy a worker's runtime or device only changes while none of its processes or jobs are there.</summary>
    private static void EnsureNotInUse(StudioHost host, WorkerManifest manifest, string option, string now, string context)
    {
        var scheduler = host.Scheduler;
        bool Mine(string id) => string.Equals(id, manifest.Id, StringComparison.OrdinalIgnoreCase);
        if (scheduler.WorkerSnapshots().Any(w => Mine(w.WorkerId)) ||
            scheduler.JobSnapshots().Any(j => Mine(j.WorkerId) && j.State is JobState.Queued or JobState.Running))
            throw new UsageException($"{context}: {option} can't be used now: {manifest.DisplayName} is loaded in the las proxy and {now}. " +
                                     $"Unload it first ('las proxy unload {manifest.Id}'), or leave out --proxy.");
    }
}
