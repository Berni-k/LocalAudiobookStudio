using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Cli.Proxy;

/// <summary>
/// <c>las proxy start|status|unload|stop</c>: the las proxy that --proxy commands run in. <c>las proxy serve</c> is the
/// proxy itself, started by the first --proxy command (or by start).
/// </summary>
public static class ProxyCommand
{
    /// <summary>The command a client sends for <c>las proxy unload</c>; the proxy runs it on its host (<see cref="UnloadAsync"/>).</summary>
    public const string UnloadCommand = "unload";

    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("proxy", tokens, new OptionSpec("--idle"));
        if (a.Positionals.Count == 0) throw new UsageException("proxy: say start, status, unload or stop.");
        var action = a.Positionals[0].ToLowerInvariant();
        if (a.Positionals.Count > 1 && action != "unload") throw new UsageException($"proxy {action}: unexpected argument '{a.Positionals[1]}'.");
        var idle = a.GetInt("--idle", 1, 7 * 24 * 60);
        if (idle != null && action is not ("start" or "serve")) throw new UsageException("proxy: --idle goes with start.");
        return action switch
        {
            "start" => await StartAsync(ctx, idle),
            "status" => await StatusAsync(ctx),
            "unload" => await SendUnloadAsync(ctx, a.Positionals.Skip(1).ToList()),
            "stop" => await StopAsync(ctx),
            "serve" => await ServeAsync(ctx, idle),
            var other => throw new UsageException($"proxy: unknown action '{other}'; say start, status, unload or stop."),
        };
    }

    /// <summary>las proxy unload: runs <see cref="UnloadAsync"/> in the proxy, with its output here.</summary>
    private static Task<int> SendUnloadAsync(CliContext ctx, List<string> targets)
    {
        var args = new List<string>();
        if (ctx.Json) args.Add("--json");
        if (ctx.Quiet) args.Add("-q");
        if (ctx.Verbose) args.Add("-v");
        args.Add(UnloadCommand);
        args.AddRange(targets);
        return ProxyClient.RunAsync(new GlobalOptions(ctx.Verbose, ctx.Quiet, ctx.Json, false, true, null, []), args.ToArray(),
            start: false, cancel: ctx.Cancel);
    }

    /// <summary>
    /// In the proxy: stops the processes of these workers, or of the workers that use these models (all loaded workers
    /// when none are named). Busy ones finish their job first; it returns once all of them have exited, so a script can
    /// switch a worker's --runtime or --device next.
    /// </summary>
    public static async Task<int> UnloadAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("proxy unload", tokens);
        var host = await ctx.StartHostAsync();
        var scheduler = host.Scheduler;
        var workers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in a.Positionals)
        {
            if (host.Catalog.Get(id) is { } worker) workers.Add(worker.Id);
            else if (host.Models?.Catalog.Get(id) != null) workers.UnionWith(host.WorkersUsingModel(id).Select(m => m.Id));
            else throw new UsageException($"proxy unload: '{id}' is neither a worker nor a model. See 'las workers' and 'las models'.");
        }
        if (a.Positionals.Count == 0) workers.UnionWith(scheduler.WorkerSnapshots().Select(w => w.WorkerId));

        var devices = scheduler.DeviceSnapshots().ToDictionary(d => d.Index, d => d.Name);
        var processes = scheduler.WorkerSnapshots().Where(w => workers.Contains(w.WorkerId)).ToList();
        if (processes.Count == 0)
        {
            ctx.Info(a.Positionals.Count == 0 ? "No workers are loaded." : $"Not loaded: {string.Join(", ", workers.Order())}.");
            ctx.WriteJson(new { unloaded = Array.Empty<object>() });
            return Program.ExitOk;
        }
        foreach (var w in processes.Select(p => p.WorkerId).Distinct(StringComparer.OrdinalIgnoreCase))
            await scheduler.StopWorkerAsync(w);
        // Busy ones stop after their job (queued jobs of the worker start a new process later; those aren't waited for).
        var ids = processes.Select(p => p.Id).ToHashSet();
        if (scheduler.WorkerSnapshots().Any(w => ids.Contains(w.Id)))
            ctx.Info("Waiting for busy workers to finish their job…");
        while (scheduler.WorkerSnapshots().Any(w => ids.Contains(w.Id)))
            await Task.Delay(200, ctx.Cancel);

        string Where(Workers.Scheduling.WorkerSnapshot p) => string.Join(" + ", p.DeviceIndexes.Select(i => devices.GetValueOrDefault(i) ?? "?"));
        foreach (var p in processes) ctx.Out($"{p.WorkerId} {Where(p)}");
        ctx.Info($"Unloaded {processes.Count} worker process(es).");
        ctx.WriteJson(new { unloaded = processes.Select(p => new { worker = p.WorkerId, device = Where(p) }) });
        return Program.ExitOk;
    }

    private static async Task<int> StartAsync(CliContext ctx, int? idle)
    {
        if (await ProxyClient.ConnectAsync(TimeSpan.FromMilliseconds(300)) is { } running)
        {
            await running.DisposeAsync();
            ctx.Info("The las proxy is already running.");
            return Program.ExitOk;
        }
        try
        {
            await (await ProxyClient.StartAndConnectAsync(chatty: false, idle)).DisposeAsync();
        }
        catch (ProxyException ex)
        {
            ctx.Error(ex.Message);
            return Program.ExitSetup;
        }
        ctx.Info("Started the las proxy.");
        return Program.ExitOk;
    }

    private static async Task<int> StatusAsync(CliContext ctx)
    {
        await using var pipe = await ProxyClient.ConnectAsync(TimeSpan.FromMilliseconds(300));
        if (pipe == null)
        {
            ctx.Out("No las proxy is running.");
            ctx.WriteJson(new { running = false });
            return Program.ExitOk;
        }
        var frames = new FrameStream(pipe);
        if (await frames.ReadAsync(ctx.Cancel) is not { Type: FrameType.Hello }) throw new IOException("The las proxy didn't answer.");
        frames.Write(FrameType.Status);
        if (await frames.ReadAsync(ctx.Cancel) is not { Type: FrameType.StatusReply } reply) throw new IOException("The las proxy didn't answer.");
        var s = FrameStream.Json<ProxyStatus>(reply.Payload);

        ctx.Out($"las proxy {s.Version}, process {s.Pid}, running since {s.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}");
        ctx.Out($"  {s.Clients} other client(s) connected, {s.Running} job(s) running, {s.Queued} queued" +
                (s.Clients == 0 && s.Running == 0 && s.Queued == 0 ? $", idle for {s.IdleMinutes:0.#} min" : ""));
        if (s.Workers.Count == 0) ctx.Out("  No workers loaded.");
        foreach (var w in s.Workers)
            ctx.Out($"  {$"{w.Worker} ({w.Runtime})",-26} {w.Device,-36} {w.State}{(w.Job != null ? $": {w.Job}" : "")}");
        ctx.WriteJson(new { running = true, s.Pid, s.Version, s.StartedUtc, s.Clients, s.Running, s.Queued, s.IdleMinutes, s.Workers });
        return Program.ExitOk;
    }

    private static async Task<int> StopAsync(CliContext ctx)
    {
        var pipe = await ProxyClient.ConnectAsync(TimeSpan.FromMilliseconds(300));
        if (pipe == null)
        {
            ctx.Info("No las proxy is running.");
            return Program.ExitOk;
        }
        int pid;
        await using (pipe)
        {
            var frames = new FrameStream(pipe);
            if (await frames.ReadAsync(ctx.Cancel) is not { Type: FrameType.Hello } hello) throw new IOException("The las proxy didn't answer.");
            pid = FrameStream.Json<ProxyHello>(hello.Payload).Pid;
            frames.Write(FrameType.Stop);
            await frames.ReadAsync(ctx.Cancel);
        }
        // Wait until it has gone: its workers and files are then released (a build can replace las.exe).
        try
        {
            using var process = Process.GetProcessById(pid);
            ctx.Info("Stopping the las proxy…");
            await process.WaitForExitAsync(ctx.Cancel).WaitAsync(TimeSpan.FromSeconds(60), ctx.Cancel);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (TimeoutException)
        {
            ctx.Error($"The las proxy (process {pid}) is still stopping after a minute.");
            return Program.ExitFailures;
        }
        ctx.Info("Stopped the las proxy.");
        return Program.ExitOk;
    }

    /// <summary>The proxy itself: runs until it has been idle for <paramref name="idleMinutes"/> (default: the workers' idle timeout).</summary>
    private static async Task<int> ServeAsync(CliContext ctx, int? idleMinutes)
    {
        // Nobody reads its console: everything goes to its log (logs/proxy-*.log).
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancel);
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        // The terminal that started the first client may close; the proxy stays.
        using var hup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true);

        Engine.Hosting.StudioHost host;
        try { host = await CliContext.StartStudioHostAsync("proxy"); }
        catch (SetupException) { return Program.ExitSetup; }
        try
        {
            var minutes = idleMinutes ?? (host.Settings.WorkerIdleTimeoutMin > 0 ? host.Settings.WorkerIdleTimeoutMin : 15);
            var server = new ProxyServer(ProxyPipe.Name, () => Task.FromResult(host), TimeSpan.FromMinutes(minutes), host.LoggerFactory.CreateLogger("Proxy"));
            await server.RunAsync(stop.Token);
            return Program.ExitOk;
        }
        catch (Exception ex)
        {
            host.Log.LogError(ex, "las proxy failed");
            return Program.ExitFailures;
        }
        finally
        {
            await host.ShutdownAsync(TimeSpan.FromSeconds(15));
        }
    }
}
