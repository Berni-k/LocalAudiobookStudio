using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Models;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Hosting;

public sealed class StudioHostOptions
{
    /// <summary>Log file name prefix: "session" for the app, "cli" for the command line.</summary>
    public string LogPrefix { get; init; } = "session";

    /// <summary>Never write the settings file (the command line only reads what the app configured).</summary>
    public bool ReadOnlySettings { get; init; }

    /// <summary>Another log sink besides the log file (the command line's console), or null.</summary>
    public ILoggerProvider? ExtraLogProvider { get; init; }
}

/// <summary>
/// The services that outlive any one project: settings, logging, worker catalog, environments, devices
/// and the scheduler. The app has one (through its AppHost); so does each command line run.
/// </summary>
public sealed class StudioHost
{
    private readonly StudioHostOptions _options;
    private FileLoggerProvider? _fileLogger;

    private StudioHost(StudioHostOptions options) => _options = options;

    public AppSettings Settings { get; private set; } = new();
    public ILoggerFactory LoggerFactory { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    public ILogger Log { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public DataRootPaths DataRoot { get; private set; } = new(SettingsStore.DefaultDataRoot);
    public WorkerCatalog Catalog { get; private set; } = null!;
    public EnvironmentManager Environments { get; private set; } = null!;
    /// <summary>The model folders; serves the workers' model requests.</summary>
    public ModelStore Models { get; private set; } = null!;
    public GenerationScheduler Scheduler { get; private set; } = null!;
    public IReadOnlyList<GpuInfo> Gpus { get; private set; } = [];
    public string? LogFilePath => _fileLogger?.FilePath;

    /// <summary>A worker's log line, formatted with its device ("[GPU 0 · kokoro] …"). Raised on worker threads.</summary>
    public event Action<string>? WorkerLogLine;

    /// <summary>Loads settings, opens the log, reads the worker catalog and creates the scheduler. GPUs are detected separately.</summary>
    public static StudioHost Start(StudioHostOptions? options = null)
    {
        var host = new StudioHost(options ?? new StudioHostOptions());
        host.Initialize();
        return host;
    }

    /// <summary>
    /// A host for unit tests: the given catalog and scheduler, default settings, no log file and no environments
    /// (so nothing that installs or checks workers). Its data folder is the user's unless <paramref name="dataRoot"/> is given.
    /// </summary>
    internal static StudioHost CreateForTests(WorkerCatalog catalog, GenerationScheduler scheduler, AppSettings? settings = null,
        string? dataRoot = null) =>
        new(new StudioHostOptions { ReadOnlySettings = true })
        {
            Catalog = catalog, Scheduler = scheduler, Settings = settings ?? new AppSettings(), _fakeWorkers = true,
            DataRoot = dataRoot != null ? new DataRootPaths(dataRoot) : new DataRootPaths(SettingsStore.DefaultDataRoot),
        };

    /// <summary>A host for tests: its scheduler runs fake workers, so every known worker counts as installed.</summary>
    private bool _fakeWorkers;

    private void Initialize()
    {
        Settings = SettingsStore.Load();
        DataRoot = new DataRootPaths(SettingsStore.ResolveDataRoot(Settings));
        try { DataRoot.EnsureCreated(); } catch { /* reported by the first-run dialog */ }

        var logPath = Path.Combine(DataRoot.Logs, $"{_options.LogPrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            _fileLogger = new FileLoggerProvider(logPath);
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Debug);
                b.AddProvider(_fileLogger);
                if (_options.ExtraLogProvider != null) b.AddProvider(_options.ExtraLogProvider);
            });
            Log = LoggerFactory.CreateLogger("App");
        }
        catch
        {
            // Logging must never stop the app.
        }
        Log.LogInformation("Local Audiobook Studio {Version} starting ({Prefix}); data root {Root}",
            AppVersion.Display, _options.LogPrefix, DataRoot.Root);
        PruneOldLogs();

        Catalog = new WorkerCatalog(WorkerCatalog.DefaultRoot) { RuntimeChoice = RuntimeOf };
        foreach (var e in Catalog.LoadErrors) Log.LogWarning("Manifest error: {Error}", e);
        Environments = new EnvironmentManager(DataRoot, Catalog, EnvironmentManager.FindUv(DataRoot.Root), EnvironmentManager.FindHartsyWorker(),
            EnvironmentManager.FindInternalWorker());
        if (Environments.UvPath == null) Log.LogWarning("uv not found (neither under tools/uv nor on the PATH)");
        if (Environments.HartsyWorkerPath == null) Log.LogWarning("HartsyInference worker not found under tools/hartsy/");
        if (Environments.InternalWorkerPath == null) Log.LogWarning("internal worker not found under tools/internal-worker/");
        KeepInstalledRuntimes();
        Models = new ModelStore(Catalog.Models, DataRoot.ModelStore, DataRoot.Models, LoggerFactory.CreateLogger("Models"))
        {
            Token = () => Settings.HuggingFaceToken is { Length: > 0 } t ? t.Trim() : null,
            Override = id => Settings.ModelOverrides.GetValueOrDefault(id),
        };

        Scheduler = new GenerationScheduler(CreateConnection, HostingOf, new SchedulerOptions
        {
            WorkerIdleTimeout = Settings.WorkerIdleTimeoutMin <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(Settings.WorkerIdleTimeoutMin),
        }, LoggerFactory.CreateLogger("Scheduler"))
        {
            CheckWorkerRunnable = WhyWorkerCannotRun,
        };
        Scheduler.WorkerLog += (process, worker, line) =>
        {
            // Short device names keep log lines readable: "GPU 1", not "GPU 1 (NVIDIA GeForce RTX 3090)".
            var where = process.Device.Kind == DeviceKind.Gpu ? $"GPU {string.Join("+", process.Devices.Select(d => d.GpuIndex))}" : "CPU";
            WorkerLogLine?.Invoke($"[{where} · {worker}{(process.Lane > 0 ? $" #{process.Lane + 1}" : "")}] {line}");
            Log.LogDebug("[{Device} · {Worker}] {Line}", where, worker, line);
        };
        ApplyDeviceSettings();
    }

    /// <summary>How new takes' silence is trimmed (Settings).</summary>
    public LocalAudiobookStudio.Audio.Analysis.SilenceTrim SilenceTrim => new(Settings.SilenceThresholdDb, Settings.TrimPaddingMs);

    /// <summary>The worker new speakers and projects use: Kokoro when it is in the catalog, else the first speech worker.</summary>
    public string DefaultTtsWorker =>
        Catalog.Get("kokoro") != null ? "kokoro" : Catalog.Generators.FirstOrDefault()?.Id ?? "kokoro";

    /// <summary>
    /// Saves a worker's settings. Its idle processes stop now and busy ones after their job, so the next
    /// job starts it with the new settings; a new device, parallel count or GPU ids apply from then on.
    /// </summary>
    public void SetWorkerSettings(string workerId, JsonObject values)
    {
        Settings.WorkerSettings[workerId] = values;
        SaveSettings();
        Scheduler.RestartWorker(workerId);
    }

    /// <summary>
    /// A worker without a chosen runtime uses its default (the first in its manifest's "runtimeOrder"). When
    /// that default changes in an update, a worker installed on another runtime would suddenly show as not
    /// installed; so it is set to the runtime it has.
    /// </summary>
    private void KeepInstalledRuntimes()
    {
        var changed = false;
        foreach (var worker in Catalog.All)
        {
            var runtimes = Catalog.RuntimesOf(worker.Id);
            if (runtimes.Count < 2 || RuntimeOf(worker.Id) != null || Environments.IsRuntimeInstalled(runtimes[0])) continue;
            if (runtimes.FirstOrDefault(Environments.IsRuntimeInstalled) is not { } installed) continue;
            if (!Settings.WorkerSettings.TryGetValue(worker.Id, out var values)) Settings.WorkerSettings[worker.Id] = values = new JsonObject();
            values[WorkerRuntimes.SettingKey] = installed.Runtime;
            Log.LogInformation("{Worker}: staying on its installed runtime {Runtime} (the default is now {Default})",
                worker.Id, installed.Runtime, runtimes[0].Runtime);
            changed = true;
        }
        if (changed) SaveSettings();
    }

    /// <summary>The runtime chosen for a worker in the Workers window (the "runtime" worker setting), or null.</summary>
    public string? RuntimeOf(string workerId) =>
        Settings.WorkerSettings.GetValueOrDefault(workerId)?[WorkerRuntimes.SettingKey] is JsonValue v && v.TryGetValue<string>(out var r) ? r : null;

    public void SaveSettings()
    {
        if (_options.ReadOnlySettings) return;
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) { Log.LogError(ex, "Could not save settings"); }
    }

    /// <summary>
    /// Where a worker runs: its device, parallel workers and GPU ids (worker settings), or its external
    /// lanes. Unknown workers are left to <see cref="WhyWorkerCannotRun"/>.
    /// </summary>
    public WorkerHosting HostingOf(string workerId) =>
        Catalog.Get(workerId) is { } m
            ? m.Hosting(Settings.WorkerSettings.GetValueOrDefault(workerId), machineHasGpu: Gpus.Count > 0)
            : WorkerHosting.AnyLocal;

    public string? WhyWorkerCannotRun(string workerId)
    {
        var m = Catalog.Get(workerId);
        if (m == null) return $"Unknown worker '{workerId}'. Check the speaker's worker in the Speakers dialog.";
        if (!_fakeWorkers && !Environments.IsWorkerReady(workerId))
            return $"{m.DisplayName} is not installed (or its self-test failed). Open Workers → Manage workers and install it.";
        return null;
    }

    private IWorkerConnection CreateConnection(string workerId, WorkerProcess process)
    {
        var m = Catalog.Get(workerId) ?? throw new InvalidOperationException($"Unknown worker {workerId}");
        // A GPU pins the process to that GPU (a multi-GPU process to all of its GPUs); the CPU and external workers
        // (ComfyUI bridge) see no GPU.
        var device = process.Device;
        var env = Environments.WorkerEnvironment(m, device.Kind == DeviceKind.Gpu ? device.GpuIndex : -1,
            process.Devices.Skip(1).Select(d => d.GpuIndex).ToList());
        // Machine-specific worker settings (Workers window) and which of the worker's processes this is.
        env["LAS_WORKER_SETTINGS"] = m.WorkerSettingValues(Settings.WorkerSettings.GetValueOrDefault(workerId)).ToJsonString();
        env["LAS_LANE"] = process.Lane.ToString();
        if (device.Kind == DeviceKind.Cpu && device.CpuThreads > 0) env["LAS_CPU_THREADS"] = device.CpuThreads.ToString();
        var (exe, args, dir) = Environments.WorkerCommand(m);
        return new StdioWorkerConnection(
            workerId,
            exe,
            args,
            dir,
            env,
            TimeSpan.FromMinutes(Math.Max(1, Settings.GenerationTimeoutMin)),
            LoggerFactory.CreateLogger("Worker." + workerId))
        {
            RequestHandler = Models.HandleRequestAsync,
        };
    }

    /// <summary>The workers (any runtime) that can use a model, directly or through a model that requires it.</summary>
    public IReadOnlyList<WorkerManifest> WorkersUsingModel(string modelId) =>
        WorkerIdsUsingModel(modelId).Select(id => Catalog.Get(id)!).ToList();

    private IEnumerable<string> WorkerIdsUsingModel(string modelId) =>
        Catalog.AllRuntimes.Where(m => Models.Catalog.WithRequired(m.Models).Contains(modelId, StringComparer.OrdinalIgnoreCase))
            .Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes a downloaded model, with every link made to it. The workers that can use it are stopped first
    /// (Windows can't delete files a worker has open); they start again on their next job.
    /// </summary>
    public async Task DeleteModelAsync(string modelId)
    {
        foreach (var w in WorkerIdsUsingModel(modelId))
            if (!await Scheduler.StopWorkerAsync(w))
                throw new ModelStoreException($"{Catalog.Get(w)?.DisplayName ?? w} is busy with a job and may be using this model. Delete it once the job is done.");
        await Models.DeleteAsync(modelId);
    }

    /// <summary>Points a model at the user's own folder (null to use the downloaded one again) and restarts the workers using it.</summary>
    public void SetModelOverride(string modelId, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) Settings.ModelOverrides.Remove(modelId);
        else Settings.ModelOverrides[modelId] = Path.GetFullPath(folder);
        SaveSettings();
        lock (_overrideFingerprints) _overrideFingerprints.Clear();
        foreach (var w in WorkerIdsUsingModel(modelId)) Scheduler.RestartWorker(w);
    }

    private readonly Dictionary<string, string> _overrideFingerprints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The worker's model version as it goes into each segment's input hash: the manifest's, plus a fingerprint of
    /// the user's own model folders when one of its models is overridden. Takes made with another model then show as
    /// out of date. The fingerprint (file names, sizes and times) is taken once per folder until the override changes.
    /// </summary>
    public string ModelVersionOf(WorkerManifest worker)
    {
        if (Settings.ModelOverrides.Count == 0 || Models == null) return worker.ModelVersion;
        var own = Models.Catalog.WithRequired(worker.Models)
            .Select(id => (Id: id, Folder: Settings.ModelOverrides.GetValueOrDefault(id)))
            .Where(o => !string.IsNullOrEmpty(o.Folder)).ToList();
        if (own.Count == 0) return worker.ModelVersion;
        return worker.ModelVersion + "+own:" + string.Join(",", own.Select(o => o.Id + "=" + OverrideFingerprint(o.Folder!)));
    }

    private string OverrideFingerprint(string folder)
    {
        lock (_overrideFingerprints)
        {
            if (_overrideFingerprints.TryGetValue(folder, out var known)) return known;
            var sb = new System.Text.StringBuilder(folder);
            if (Directory.Exists(folder))
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal))
                    sb.Append('|').Append(Path.GetRelativePath(folder, f.FullName)).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..12];
            return _overrideFingerprints[folder] = hash;
        }
    }

    /// <summary>Display name of a GPU or the CPU from its settings.</summary>
    public string DeviceName(DeviceSettings s)
    {
        if (s.Name is { Length: > 0 } custom) return custom;
        if (s.Gpu < 0) return "CPU";
        var gpuName = Gpus.FirstOrDefault(g => g.Index == s.Gpu)?.Name;
        return $"GPU {s.Gpu}" + (gpuName != null ? $" ({gpuName})" : "");
    }

    /// <summary>
    /// Gives the scheduler its devices: the configured GPUs, the CPU (always there; only workers set to
    /// the CPU use it), and one device per external worker (its compute is elsewhere; it has no limits
    /// of its own beyond its "parallel workers").
    /// </summary>
    public void ApplyDeviceSettings()
    {
        EnsureCpuDevice();
        var configs = new List<DeviceConfig>();
        foreach (var s in Settings.Devices)
        {
            var isCpu = s.Gpu < 0;
            configs.Add(new DeviceConfig
            {
                Kind = isCpu ? DeviceKind.Cpu : DeviceKind.Gpu,
                GpuIndex = isCpu ? -1 : s.Gpu,
                Name = DeviceName(s),
                Enabled = s.Enabled,
                MaxActive = s.MaxActive is > 0 and var a ? a : 1,
                MaxTotal = s.MaxTotal is > 0 and var t ? t : null,
                CpuThreads = isCpu ? Math.Max(0, s.CpuThreads) : 0,
            });
        }
        foreach (var m in Catalog.All.Where(m => m.External))
            configs.Add(new DeviceConfig { Kind = DeviceKind.External, ExternalWorker = m.Id, Name = m.DisplayName });
        Scheduler.ConfigureDevices(configs);
    }

    /// <summary>The CPU is always a device (last in the list); add it to older settings that lack it.</summary>
    private void EnsureCpuDevice()
    {
        if (Settings.Devices.Any(d => d.Gpu < 0)) return;
        Settings.Devices.Add(new DeviceSettings { Gpu = -1, Enabled = true });
        SaveSettings();
    }

    /// <summary>Runs nvidia-smi. On first run (no devices configured) adds every GPU found.</summary>
    public async Task DetectGpusAsync(bool addMissing)
    {
        Gpus = await GpuDetector.DetectAsync();
        Log.LogInformation("Detected {Count} NVIDIA GPU(s): {Names}", Gpus.Count, string.Join(", ", Gpus.Select(g => $"{g.Index}:{g.Name}")));
        var others = (await Task.Run(DisplayAdapterDetector.Detect)).Where(a => !a.IsNvidia).ToList();
        if (others.Count > 0)
            Log.LogInformation("Other graphics adapters (workers don't use them): {Names}", string.Join(", ", others.Select(a => a.Name)));
        if (addMissing && !Settings.Devices.Any(d => d.Gpu >= 0) && Gpus.Count > 0)
        {
            Settings.Devices.InsertRange(0, Gpus.Select(g => new DeviceSettings { Gpu = g.Index, Enabled = true }));
            SaveSettings();
        }
        ApplyDeviceSettings();
    }

    /// <summary>Logs an unhandled exception and writes it to a crash file in <paramref name="logsDir"/>.</summary>
    public static void LogCrash(ILogger log, string logsDir, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            log.LogCritical(ex, "Unhandled exception");
            var crash = Path.Combine(logsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(crash, ex.ToString());
        }
        catch
        {
            // Nothing else we can do.
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            foreach (var f in new DirectoryInfo(DataRoot.Logs).EnumerateFiles($"{_options.LogPrefix}-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(30))
                f.Delete();
        }
        catch
        {
            // Not important.
        }
    }

    /// <summary>Stops the workers (waiting up to <paramref name="timeout"/>), saves settings and closes the log.</summary>
    public async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        try
        {
            await Scheduler.ShutdownAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Worker shutdown incomplete");
        }
        SaveSettings();
        Log.LogInformation("Shutdown complete");
        LoggerFactory.Dispose();
        _fileLogger?.Dispose();
    }
}
