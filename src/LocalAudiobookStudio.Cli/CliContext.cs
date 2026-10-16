using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Cli;

/// <summary>
/// One run of las: the global options, Ctrl+C, output (results on stdout, progress on stderr) and the
/// studio host, started on first use with the app's settings (read only) and data folder. In the las proxy a
/// run is one client's request: its console is the client's, and the host is shared with the other requests.
/// </summary>
public sealed class CliContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private readonly HashSet<Guid> _jobIds = new();

    public CliContext(bool verbose, bool quiet, bool json, CancellationToken cancel, CliIo? io = null)
    {
        Verbose = verbose;
        Quiet = quiet;
        Json = json;
        Cancel = cancel;
        Io = io ?? CliIo.ForConsole();
    }

    public bool Verbose { get; }
    public bool Quiet { get; }
    public bool Json { get; }
    public CancellationToken Cancel { get; }
    public CliIo Io { get; }
    public StudioHost? Host { get; private set; }

    /// <summary>
    /// --gpu: run on these GPUs only, for this run (several las processes can then each use their own). A list, so a
    /// worker that uses several GPUs at once has them.
    /// </summary>
    public IReadOnlySet<int>? OnlyGpus { get; init; }

    /// <summary>The folder relative paths are taken from: the client's, in the las proxy.</summary>
    public string Cwd { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>The las proxy's host, shared by all its requests; null for a run of its own.</summary>
    public Func<Task<StudioHost>>? SharedHost { get; init; }

    public bool Shared => SharedHost != null;

    /// <summary>Added to each job's reading order: in the las proxy, requests are served in the order they came.</summary>
    public long OrderBase { get; init; }

    /// <summary>Raised when the run queued jobs (the las proxy shows the client its place in the queue).</summary>
    public event Action? JobsQueued;

    /// <summary>A path the user gave, made absolute against <see cref="Cwd"/>.</summary>
    public string FullPath(string path) => Path.GetFullPath(path, Cwd);

    /// <summary>A job's reading order within this run, as the scheduler should see it.</summary>
    public long Order(long order) => OrderBase + order;

    /// <summary>The ids of the jobs this run queued (a copy).</summary>
    public IReadOnlyCollection<Guid> JobIds
    {
        get { lock (_jobIds) return _jobIds.ToHashSet(); }
    }

    /// <summary>Whether the job is one this run queued, or continues one of them.</summary>
    public bool Owns(GenerationJob job)
    {
        lock (_jobIds) return GenerationScheduler.InGroup(job, _jobIds);
    }

    /// <summary>
    /// Queues the run's jobs (on the --gpu GPU, if one was given). Disposing the result stops Ctrl+C from cancelling
    /// them; until then it cancels these jobs only, not other runs' jobs in a shared queue.
    /// </summary>
    public CancellationTokenRegistration Enqueue(GenerationScheduler scheduler, IReadOnlyList<GenerationJob> jobs)
    {
        lock (_jobIds)
            foreach (var job in jobs)
            {
                job.OnlyGpus = OnlyGpus;
                _jobIds.Add(job.Id);
            }
        scheduler.EnqueueMany(jobs);
        JobsQueued?.Invoke();
        // After queueing: a Ctrl+C that came first runs this at once.
        return Cancel.Register(() => scheduler.CancelMany(JobIds));
    }

    /// <summary>Starts the host (once): the app must have been set up on this machine.</summary>
    public async Task<StudioHost> StartHostAsync()
    {
        if (Host != null) return Host;
        if (SharedHost != null)
        {
            var shared = await SharedHost();
            // The devices are shared, so --gpu pins this run's jobs (Enqueue) instead of disabling the other GPUs.
            CheckGpusExist(shared);
            if (Verbose)
            {
                _workerLog = (process, worker, line) =>
                {
                    if (process.CurrentJob is { } job && Owns(job)) Io.Error.WriteLine($"[{WhereOf(process)} · {worker}] {line}");
                };
                shared.Scheduler.WorkerLog += _workerLog;
            }
            return Host = shared;
        }
        Host = await StartStudioHostAsync("cli", Verbose ? new ConsoleLoggerProvider() : null);
        if (Verbose) Host.WorkerLogLine += line => Console.Error.WriteLine(line);
        if (OnlyGpus is { Count: > 0 } only)
        {
            // In memory only: the CLI never saves the app's settings.
            CheckGpusExist(Host);
            // Project steps queue their jobs in the engine; with the other GPUs off they stay on these too.
            foreach (var d in Host.Settings.Devices.Where(d => d.Gpu >= 0))
                d.Enabled = only.Contains(d.Gpu);
            Host.ApplyDeviceSettings();
        }
        return Host;
    }

    private void CheckGpusExist(StudioHost host)
    {
        if (OnlyGpus?.FirstOrDefault(g => !host.Settings.Devices.Any(d => d.Gpu == g), -1) is >= 0 and var missing)
            throw new UsageException($"--gpu {missing}: no such GPU. See 'las devices'.");
    }

    private Action<WorkerProcess, string, string>? _workerLog;

    /// <summary>Ends a run on a shared host: its worker log lines (--verbose) stop going to its client.</summary>
    public void DetachFromSharedHost()
    {
        if (_workerLog != null && Host != null) Host.Scheduler.WorkerLog -= _workerLog;
        _workerLog = null;
    }

    /// <summary>A host with the app's settings (read only) and data folder, its GPUs detected.</summary>
    public static async Task<StudioHost> StartStudioHostAsync(string logPrefix, ILoggerProvider? extraLog = null)
    {
        var mode = SettingsStore.DetectMode()
                   ?? throw new SetupException("Local Audiobook Studio hasn't been set up on this machine. Start the app once and finish its setup.");
        SettingsStore.UseMode(mode);
        var host = StudioHost.Start(new StudioHostOptions
        {
            LogPrefix = logPrefix,
            ReadOnlySettings = true,
            ExtraLogProvider = extraLog,
        });
        await host.DetectGpusAsync(addMissing: false);
        return host;
    }

    private static string WhereOf(WorkerProcess process) =>
        process.Device.Kind == DeviceKind.Gpu ? $"GPU {string.Join("+", process.Devices.Select(d => d.GpuIndex))}"
        : process.Device.Kind == DeviceKind.Cpu ? "CPU" : process.Device.Name;

    /// <summary>Progress and messages (stderr), unless --quiet.</summary>
    public void Info(string line)
    {
        if (!Quiet) Io.Error.WriteLine(line);
    }

    /// <summary>Details (stderr) only with --verbose.</summary>
    public void Detail(string line)
    {
        if (Verbose) Io.Error.WriteLine(line);
    }

    /// <summary>An error (stderr), also with --quiet.</summary>
    public void Error(string line) => Io.Error.WriteLine(line);

    /// <summary>A result for scripts: text lines without --json.</summary>
    public void Out(string line)
    {
        if (!Json) Io.Out.WriteLine(line);
    }

    /// <summary>The run's JSON result (with --json).</summary>
    public void WriteJson(object value)
    {
        if (Json) Io.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    /// <summary>Writes log messages from Information up to stderr (--verbose).</summary>
    private sealed class ConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
        public void Dispose() { }

        private sealed class ConsoleLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var level = logLevel switch { LogLevel.Warning => "warn", LogLevel.Error => "error", LogLevel.Critical => "crit", _ => "info" };
                Console.Error.WriteLine($"{level}: {category}: {formatter(state, exception)}{(exception != null ? " — " + exception.Message : "")}");
            }
        }
    }
}
