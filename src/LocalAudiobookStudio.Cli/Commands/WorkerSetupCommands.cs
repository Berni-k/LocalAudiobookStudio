using System.Text.Json.Nodes;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;

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
    /// settings, so the app keeps the runtime chosen in its Workers window.
    /// </summary>
    public static WorkerManifest UseRuntime(StudioHost host, WorkerManifest manifest, string runtime, string context)
    {
        var runtimes = host.Catalog.RuntimesOf(manifest.Id);
        var chosen = runtimes.FirstOrDefault(r => r.Runtime.Equals(runtime, StringComparison.OrdinalIgnoreCase))
                     ?? throw new UsageException($"{context}: {manifest.DisplayName} has no runtime '{runtime}'. Its runtimes: {string.Join(", ", runtimes.Select(r => r.Runtime))}.");
        if (!host.Settings.WorkerSettings.TryGetValue(manifest.Id, out var values)) host.Settings.WorkerSettings[manifest.Id] = values = new JsonObject();
        values[WorkerRuntimes.SettingKey] = chosen.Runtime;
        return host.Catalog.Get(manifest.Id)!;
    }

    /// <summary>Runs the worker on the GPU or the CPU for this run (--device), in memory like <see cref="UseRuntime"/>.</summary>
    public static void UseDevice(StudioHost host, WorkerManifest manifest, string device, string context)
    {
        device = device.Trim().ToLowerInvariant();
        if (device is not (WorkerManifest.DeviceGpu or WorkerManifest.DeviceCpu))
            throw new UsageException($"{context}: --device is gpu or cpu.");
        if (device == WorkerManifest.DeviceGpu ? !manifest.RunsOnGpu : !manifest.RunsOnCpu)
            throw new UsageException($"{context}: {manifest.DisplayName} ({manifest.RuntimeLabel}) doesn't run on the {device.ToUpperInvariant()}.");
        if (!host.Settings.WorkerSettings.TryGetValue(manifest.Id, out var values)) host.Settings.WorkerSettings[manifest.Id] = values = new JsonObject();
        values[WorkerManifest.DeviceKey] = device;
    }
}
