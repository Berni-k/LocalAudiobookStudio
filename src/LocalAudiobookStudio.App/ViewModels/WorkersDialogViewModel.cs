using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Workers window: every worker in a list with a rough status, the selected one's details, actions
/// (install, repair, remove, self-test log, disk size, folders) and its machine settings; a live log below.
/// Environments are per group, so installing one worker installs the others in its group too.
/// </summary>
public sealed partial class WorkersDialogViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public WorkersDialogViewModel()
    {
        _allWorkers = AppHost.Catalog.All.Select(m => new WorkerItemViewModel(this, m)).ToList();
        _showAllWorkers = AppHost.Settings.Ui.ShowAllWorkers;
        FillWorkers();
        Selected = Workers.FirstOrDefault(w => w.IsReady) ?? Workers.FirstOrDefault();
        RefreshUnusedEnvironments();
        AppHost.Environments.StatusChanged += _ => Dispatcher.UIThread.Post(RefreshAll);
        // A worker finished downloading a model (or one was deleted): update the shown worker's model list.
        AppHost.Models.Changed += _ => Dispatcher.UIThread.Post(() => { foreach (var m in Selected?.Models ?? []) m.Refresh(); });
        _ = LoadUvVersionAsync();
    }

    private readonly List<WorkerItemViewModel> _allWorkers;

    /// <summary>
    /// The listed workers: all of them with "Show all workers", else installed ones and those with a non-experimental
    /// runtime. Applied when the window opens and when the box is toggled, so an uninstall doesn't make the row vanish.
    /// </summary>
    public ObservableCollection<WorkerItemViewModel> Workers { get; } = new();

    /// <summary>Also list workers whose runtimes are all experimental (remembered in the app settings).</summary>
    [ObservableProperty] private bool _showAllWorkers;

    partial void OnShowAllWorkersChanged(bool value)
    {
        AppHost.Settings.Ui.ShowAllWorkers = value;
        AppHost.SaveSettings();
        var selected = Selected;
        FillWorkers();
        Selected = selected != null && Workers.Contains(selected) ? selected : Workers.FirstOrDefault();
    }

    private void FillWorkers()
    {
        Workers.Clear();
        foreach (var w in _allWorkers.Where(w => ShowAllWorkers || !w.IsExperimental || w.IsInstalled)) Workers.Add(w);
    }

    public ObservableCollection<string> LogLines { get; } = new();
    public string DataRoot => AppHost.DataRoot.Root;
    public string? UvPath => AppHost.Environments.UvPath;
    public bool UvMissing => UvPath == null;
    public IReadOnlyList<string> ManifestErrors => AppHost.Catalog.LoadErrors;

    [ObservableProperty] private WorkerItemViewModel? _selected;
    [ObservableProperty] private string _uvVersion = "…";
    [ObservableProperty] private bool _isBusy;

    private async Task LoadUvVersionAsync() => UvVersion = await AppHost.Environments.GetUvVersionAsync() ?? "not found";

    public void RefreshAll()
    {
        foreach (var w in _allWorkers) w.Refresh();
        RefreshUnusedEnvironments();
    }

    // ------------------------------------------------------------------ unused environments

    /// <summary>Environment folders no worker uses any more (workers moved to another environment).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnusedEnvironments), nameof(UnusedEnvironmentsText))]
    private IReadOnlyList<UnusedEnvironment> _unusedEnvironments = [];

    public bool HasUnusedEnvironments => UnusedEnvironments.Count > 0;

    /// <summary>Which old environments there are and which workers to install again (they now use another one).</summary>
    public string UnusedEnvironmentsText
    {
        get
        {
            var moved = UnusedEnvironments.SelectMany(e => e.InstalledWorkers)
                .Where(id => AppHost.Catalog.Get(id) != null && !AppHost.Environments.IsWorkerReady(id))
                .Select(id => AppHost.Catalog.Get(id)?.DisplayName ?? id).Distinct().ToList();
            var names = string.Join(", ", UnusedEnvironments.Select(e => e.Name));
            return UnusedEnvironments.Count == 1
                ? $"The environment {names} is no longer used by any worker."
                  + (moved.Count > 0 ? $" Workers moved to a new environment: install {string.Join(", ", moved)} again." : "")
                : $"{UnusedEnvironments.Count} environments are no longer used by any worker ({names})."
                  + (moved.Count > 0 ? $" Workers moved to new environments: install {string.Join(", ", moved)} again." : "");
        }
    }

    private void RefreshUnusedEnvironments() => UnusedEnvironments = AppHost.Environments.UnusedEnvironments();

    [RelayCommand]
    private async Task RemoveUnusedEnvironments()
    {
        var unused = AppHost.Environments.UnusedEnvironments();
        if (unused.Count == 0) return;
        var sizes = await Task.Run(() => unused.Select(e => WorkerDiskUsage.FolderSize(e.Folder)).ToList());
        var lines = unused.Select((e, i) => $"• {e.Name} ({WorkerDiskUsage.Format(sizes[i])})");
        if (!await Dialogs.Confirm("Remove unused environments",
                $"Delete these environment folders? No worker uses them any more.\n\n{string.Join("\n", lines)}\n\n" +
                "Models stay in the models folder. Afterwards, Clear cache also frees the old packages kept for reinstalls.",
                "Remove", "Cancel"))
            return;
        await RunAsync((log, _) =>
        {
            foreach (var e in unused) AppHost.Environments.DeleteUnusedEnvironment(e.Name, log);
            log("Done.");
            return Task.CompletedTask;
        });
    }

    public void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 3000) LogLines.RemoveAt(0);
        });
    }

    public async Task RunAsync(Func<Action<string>, CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await Task.Run(() => action(AppendLog, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task ClearCache()
    {
        if (!await Dialogs.Confirm("Clear cache", "Empty uv's package cache? Installed environments keep working; the next install downloads packages again.", "Clear", "Cancel"))
            return;
        await RunAsync((log, ct) => AppHost.Environments.ClearCacheAsync(log, ct));
    }

    [RelayCommand]
    private void OpenDataRoot() => Dialogs.Reveal(DataRoot);

    /// <summary>Starts installing <paramref name="workerId"/>'s environment (used by first-run setup).</summary>
    public Task InstallWorkerAsync(string workerId)
    {
        var w = _allWorkers.FirstOrDefault(x => x.Id == workerId);
        if (w == null) return Task.CompletedTask;
        if (!Workers.Contains(w)) ShowAllWorkers = true;
        Selected = w;
        return w.InstallCommand.ExecuteAsync(null);
    }
}

/// <summary>One worker in the Workers window.</summary>
public sealed partial class WorkerItemViewModel : ObservableObject
{
    private readonly WorkersDialogViewModel _owner;

    public WorkerItemViewModel(WorkersDialogViewModel owner, WorkerManifest manifest)
    {
        _owner = owner;
        _fallback = manifest;
        Id = manifest.Id;
        Settings = BuildSettings();
        LoadHosting();
        LoadRuntime();
        Refresh();
    }

    private readonly WorkerManifest _fallback;

    /// <summary>The worker with the runtime chosen in this window (each runtime has its own manifest).</summary>
    public WorkerManifest Manifest => AppHost.Catalog.Get(Id) ?? _fallback;
    public string Id { get; }
    public string Name => Manifest.DisplayName;
    public string? Description => Manifest.Broken is { } why ? $"Warning, known to be broken: {why}\n\n{Manifest.Description}" : Manifest.Description;
    /// <summary>Every runtime is experimental: hidden unless "Show all workers", tinted when shown.</summary>
    public bool IsExperimental => AppHost.Catalog.IsExperimental(Id);
    /// <summary>The manifest's tags, led by "Experimental" when the chosen runtime is.</summary>
    public IReadOnlyList<WorkerTag> Tags =>
        Manifest.Tags.Prepend(Manifest.Experimental ? WorkerTag.Experimental : null).Prepend(Manifest.Broken != null ? WorkerTag.Broken : null).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(t => new WorkerTag(t)).ToList();
    public string Folder => Manifest.Folder;
    public string EnvFolder => AppHost.DataRoot.EnvDir(Manifest.EnvGroup);
    /// <summary>Bundled workers have no venv; their folder holds the install state (and the internal GPU runtime's builds).</summary>
    public string EnvLabel => Manifest.IsInternalGpu ? "GPU builds" : Manifest.IsBundled ? "Install state" : "Environment";

    /// <summary>Which other installed workers share this worker's environment.</summary>
    public string SharedEnvText
    {
        get
        {
            if (Manifest.IsHartsy) return "Runs on the HartsyInference worker that comes with the app; there is no environment to delete.";
            if (Manifest.IsInternalGpu)
                return $"Runs on the internal worker that comes with the app. Installing downloads its GPU builds " +
                       $"({InternalGpuRuntime.DownloadBytes(Manifest.Engine!) / (1024 * 1024)} MB); uninstalling deletes them. It uses CUDA on an NVIDIA GPU " +
                       "(with the CUDA 12 or 13 toolkit installed), else Vulkan, else the CPU.";
            if (Manifest.IsInternal) return "Runs on the internal worker that comes with the app, on the CPU; there is no environment to delete.";
            var others = AppHost.Environments.InstalledWorkers(Manifest.EnvGroup)
                .Where(id => !string.Equals(id, Id, StringComparison.OrdinalIgnoreCase))
                .Select(id => AppHost.Catalog.Get(id)?.DisplayName ?? id).ToList();
            return others.Count == 0
                ? $"Environment '{Manifest.EnvGroup}'. It is created on install and deleted when no installed worker uses it."
                : $"Environment '{Manifest.EnvGroup}', also used by {string.Join(", ", others)}. Uninstalling keeps it for them.";
        }
    }

    /// <summary>The worker's own machine settings (manifest "workerSettings"), or null when it has none.
    /// Device, parallel workers and GPU ids have their own compact row (see below).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettings), nameof(HasAnySettings))]
    private SettingsEditorViewModel? _settings;
    public bool HasSettings => Settings != null;
    /// <summary>The "Worker settings" group: the hosting row and/or the worker's own settings.</summary>
    public bool HasAnySettings => HasHosting || HasSettings;

    // ------------------------------------------------------------------ runtime (Python or HartsyInference)

    private bool _loadingRuntime;

    /// <summary>The runtimes this worker has; the selector only shows when there are several.</summary>
    /// <summary>The runtimes this worker has; installed ones show green (see <see cref="RuntimeOption.IsReady"/>).</summary>
    public IReadOnlyList<RuntimeOption> RuntimeOptions => _runtimeOptions ??= BuildRuntimeOptions();
    private IReadOnlyList<RuntimeOption>? _runtimeOptions;

    private List<RuntimeOption> BuildRuntimeOptions() =>
        Manifest.Runtimes.Select(r => AppHost.Catalog.Get(Id, r) is { } m
            ? new RuntimeOption(r, m.RuntimeLabel, AppHost.Environments.IsRuntimeReady(m))
            : new RuntimeOption(r, WorkerRuntimes.Label(r), false)).ToList();

    /// <summary>After an install or uninstall: new options when a runtime's ready state changed (the selection stays).</summary>
    private void RefreshRuntimeOptions()
    {
        var fresh = BuildRuntimeOptions();
        if (_runtimeOptions != null && fresh.Select(o => o.IsReady).SequenceEqual(_runtimeOptions.Select(o => o.IsReady))) return;
        _runtimeOptions = fresh;
        OnPropertyChanged(nameof(RuntimeOptions));
        LoadRuntime();
    }

    /// <summary>The selector is always shown; with a single runtime it is greyed out and shows that runtime.</summary>
    public bool HasRuntimeChoice => Manifest.Runtimes.Count > 1;

    public string RuntimeHelp => HasRuntimeChoice
        ? "What runs the model. Each runtime is installed separately; speakers keep their settings when you switch."
        : $"This worker only runs on {Manifest.RuntimeLabel}.";

    [ObservableProperty] private RuntimeOption? _runtime;

    /// <summary>The chosen runtime is experimental: a note shows next to the selector.</summary>
    public bool IsRuntimeExperimental => Manifest.Experimental;

    private void LoadRuntime()
    {
        _loadingRuntime = true;
        try { Runtime = RuntimeOptions.FirstOrDefault(o => o.Value == Manifest.Runtime); }
        finally { _loadingRuntime = false; }
    }

    /// <summary>
    /// Switches the worker to another runtime. Each runtime is installed separately; the speakers keep their
    /// worker and settings, and running processes restart on the new runtime before their next job.
    /// </summary>
    partial void OnRuntimeChanged(RuntimeOption? value)
    {
        if (_loadingRuntime) return;
        // Clicking another worker in the list briefly hands the dropdown's old selection (the previous worker's
        // option, or none) to this worker: not a choice made here. Put this worker's own runtime back.
        if (value == null || !RuntimeOptions.Any(o => ReferenceEquals(o, value)))
        {
            Dispatcher.UIThread.Post(LoadRuntime);
            return;
        }
        if (value.Value == Manifest.Runtime) return;
        SaveWorkerSetting(WorkerRuntimes.SettingKey, JsonValue.Create(value.Value));
        // Everything below comes from the new runtime's manifest.
        Settings = BuildSettings();
        SizeLines.Clear();
        SizeError = null;
        Refresh();
        // The device dropdown gets new items here and re-selects one; that isn't the user's choice, and saving it would
        // pin the worker to the CPU after a visit to a CPU-only runtime.
        _loadingHosting = true;
        try
        {
            foreach (var n in new[]
                     {
                         nameof(Manifest), nameof(Tags), nameof(Description), nameof(EnvFolder), nameof(HasHosting), nameof(DeviceOptions),
                         nameof(ShowDevice), nameof(CanChooseDevice), nameof(DeviceHelp), nameof(ParallelHelp), nameof(GpuIdsHelp),
                         nameof(ShowGpuIds), nameof(ShowGpuCount), nameof(GpuCountHelp), nameof(HasSize), nameof(CanCheckSize),
                         nameof(EnvLabel), nameof(IsRuntimeExperimental),
                     })
                OnPropertyChanged(n);
        }
        finally
        {
            _loadingHosting = false;
        }
        LoadHosting();
    }

    // ------------------------------------------------------------------ hosting row (device, parallel, GPU ids)

    private bool _loadingHosting;

    /// <summary>Every worker has hosting settings; external ones (ComfyUI bridge) only "parallel".</summary>
    public bool HasHosting => Manifest.HostingSettings.Count > 0;
    private SettingDefinition? DeviceSetting => Manifest.HostingSettings.FirstOrDefault(s => s.Key == WorkerManifest.DeviceKey);
    private string? HostingHelp(string key) => Manifest.HostingSettings.FirstOrDefault(s => s.Key == key)?.Description;

    public IReadOnlyList<DeviceOption> DeviceOptions =>
        DeviceSetting is { } d ? d.Options.Select((o, i) => new DeviceOption(o, d.OptionLabel(i))).ToList() : [];
    /// <summary>External workers (ComfyUI bridge) have no device to choose, only Parallel.</summary>
    public bool ShowDevice => DeviceSetting != null;
    /// <summary>Only one device (GPU-only workers): the selector is shown but can't change.</summary>
    public bool CanChooseDevice => DeviceOptions.Count > 1;
    public string? DeviceHelp => HostingHelp(WorkerManifest.DeviceKey);
    public string? ParallelHelp => HostingHelp(WorkerManifest.ParallelKey);
    public string? GpuIdsHelp => HostingHelp(WorkerManifest.GpusKey);
    public string? GpuCountHelp => HostingHelp(WorkerManifest.GpuCountKey);
    public int MaxGpuCount => WorkerManifest.MaxGpuCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGpuIds), nameof(ShowGpuCount))]
    private DeviceOption? _device;
    [ObservableProperty] private decimal? _parallel;
    [ObservableProperty] private string _gpuIds = "";
    [ObservableProperty] private decimal? _gpuCount;

    /// <summary>GPU ids only matter on the GPU.</summary>
    public bool ShowGpuIds => Device?.Value == WorkerManifest.DeviceGpu && Manifest.RunsOnGpu;

    /// <summary>Multi-GPU workers on the GPU: how many GPUs a process uses.</summary>
    public bool ShowGpuCount => ShowGpuIds && Manifest.MultiGpu;

    private void LoadHosting()
    {
        if (!HasHosting) return;
        _loadingHosting = true;
        try
        {
            var saved = AppHost.Settings.WorkerSettings.GetValueOrDefault(Id);
            var hosting = AppHost.HostingOf(Id); // the effective device, including the no-GPU default
            var value = hosting.Device == Workers.Scheduling.WorkerPlacement.Cpu ? WorkerManifest.DeviceCpu : WorkerManifest.DeviceGpu;
            Device = DeviceOptions.FirstOrDefault(o => o.Value == value) ?? DeviceOptions.FirstOrDefault();
            Parallel = hosting.Lanes;
            GpuIds = saved?[WorkerManifest.GpusKey] is JsonValue v && v.TryGetValue<string>(out var ids) ? ids : "";
            // On the CPU the hosting says 1; the saved count still applies when it goes back to the GPU.
            GpuCount = Manifest.Hosting(saved).GpuCount;
        }
        finally
        {
            _loadingHosting = false;
        }
    }

    partial void OnDeviceChanged(DeviceOption? value)
    {
        // A runtime with one device (GPU only, CPU only) offers no choice: nothing to save.
        if (!_loadingHosting && value != null && CanChooseDevice) SaveWorkerSetting(WorkerManifest.DeviceKey, JsonValue.Create(value.Value));
    }

    partial void OnParallelChanged(decimal? value)
    {
        if (!_loadingHosting && value != null)
            SaveWorkerSetting(WorkerManifest.ParallelKey, JsonValue.Create((int)Math.Clamp(value.Value, 1, 16)));
    }

    partial void OnGpuCountChanged(decimal? value)
    {
        if (!_loadingHosting && value != null)
            SaveWorkerSetting(WorkerManifest.GpuCountKey, JsonValue.Create((int)Math.Clamp(value.Value, 1, WorkerManifest.MaxGpuCount)));
    }

    partial void OnGpuIdsChanged(string value)
    {
        if (_loadingHosting) return;
        var ids = (value ?? "").Trim();
        SaveWorkerSetting(WorkerManifest.GpusKey, ids.Length == 0 ? null : JsonValue.Create(ids));
    }

    /// <summary>Saves one worker setting (null removes it, back to the default).</summary>
    private void SaveWorkerSetting(string key, JsonNode? value)
    {
        var saved = (JsonObject?)AppHost.Settings.WorkerSettings.GetValueOrDefault(Id)?.DeepClone() ?? new JsonObject();
        if (value == null) saved.Remove(key); else saved[key] = value.DeepClone();
        AppHost.SetWorkerSettings(Id, saved);
        _owner.AppendLog($"{Name}: settings saved; running processes restart before their next job.");
    }

    /// <summary>The models this runtime can ask for, then the models they require (tokenizers).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModels))]
    private IReadOnlyList<ModelItemViewModel> _models = [];
    public bool HasModels => Models.Count > 0;

    private void RefreshModels()
    {
        var store = AppHost.Models;
        var list = Manifest.Models.Select(id => new ModelItemViewModel(_owner, id, null)).ToList();
        foreach (var id in store.Catalog.WithRequired(Manifest.Models).Where(r => !Manifest.Models.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            var by = Manifest.Models.Select(store.Catalog.Get).FirstOrDefault(m => m != null && store.Catalog.WithRequired([m.Id]).Contains(id, StringComparer.OrdinalIgnoreCase));
            list.Add(new ModelItemViewModel(_owner, id, by?.Name));
        }
        Models = list;
    }

    [ObservableProperty] private EnvStatus _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _installedText = "";
    [ObservableProperty] private bool _selfTestPassed;
    /// <summary>Result of "Check size": one line per part, then the total. Empty until measured.</summary>
    public ObservableCollection<SizeLine> SizeLines { get; } = new();
    [ObservableProperty] private string? _sizeError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckSize))]
    private bool _isMeasuring;

    public bool HasSize => SizeLines.Count > 0;
    public bool CanCheckSize => !IsMeasuring && !HasSize;

    public bool IsInstalled => Status is not EnvStatus.NotInstalled;
    public bool IsReady => AppHost.Environments.IsWorkerReady(Id);
    public bool CanInstall => Status is EnvStatus.NotInstalled;
    public bool CanRepair => Status is EnvStatus.Broken or EnvStatus.UpdateAvailable or EnvStatus.Ready;
    public bool CanRemove => Status is not EnvStatus.NotInstalled and not EnvStatus.Installing;

    /// <summary>Icon in front of the name: check (ready), arrow (not installed), hourglass, or warning.</summary>
    public Geometry? StatusIcon => Icon(Status switch
    {
        EnvStatus.NotInstalled => "IconDownload",
        EnvStatus.Installing => "IconHourglass",
        _ when IsReady && Status == EnvStatus.Ready => "IconCheck",
        _ => "IconWarning",
    });

    // Status colour classes for the icon and status line (the view styles them with theme colours).
    public bool IsOk => IsReady && Status == EnvStatus.Ready;
    public bool IsWarn => IsReady && Status == EnvStatus.UpdateAvailable;
    public bool IsBad => IsInstalled && Status != EnvStatus.Installing && !IsReady;

    private static Geometry? Icon(string key) =>
        Avalonia.Application.Current!.TryFindResource(key, out var r) ? r as Geometry : null;

    public void Refresh()
    {
        Status = AppHost.Environments.GetWorkerStatus(Id);
        var state = AppHost.Environments.ReadState(Manifest.EnvGroup);
        SelfTestPassed = state?.SelfTests.TryGetValue(Id, out var ok) == true && ok;
        var envReady = Status == EnvStatus.NotInstalled && EnvironmentReady();
        StatusText = Status switch
        {
            EnvStatus.NotInstalled => "Not installed",
            EnvStatus.Installing => "Installing…",
            EnvStatus.Ready => "Ready",
            EnvStatus.UpdateAvailable => "Ready · update available (requirements changed), Repair to update",
            _ when !SelfTestPassed => "Self-test failed. See the self-test log, then Repair.",
            _ => "Broken. Repair to reinstall.",
        };
        EnvReady = envReady;
        InstalledText = Status == EnvStatus.NotInstalled ? NotInstalledText(envReady)
            : state == null ? ""
            : (Manifest.IsHartsy ? $"HartsyInference worker ({Manifest.HartsyModel})"
                  : Manifest.IsInternal ? $"internal worker ({(Manifest.IsInternalGpu ? "GPU builds downloaded" : "CPU")}), installed {state.InstalledUtc.ToLocalTime():g}"
                  : $"Python {state.PythonVersion}, environment created {state.InstalledUtc.ToLocalTime():g}")
              + (SelfTestPassed ? " · self-test passed" : " · self-test FAILED");
        OnPropertyChanged(nameof(SharedEnvText));
        RefreshRuntimeOptions();
        RefreshModels();
        foreach (var n in new[] { nameof(IsInstalled), nameof(IsReady), nameof(CanInstall), nameof(CanRepair), nameof(CanRemove), nameof(StatusIcon), nameof(IsOk), nameof(IsWarn), nameof(IsBad) })
            OnPropertyChanged(n);
    }

    /// <summary>
    /// What installing has to do: nothing big when the shared environment already exists (only this worker's
    /// packages are added, mostly from the cache) or for a bundled worker (only a quick check; the internal GPU
    /// runtime also downloads its engine's GPU builds unless they are there).
    /// </summary>
    private bool EnvironmentReady() =>
        Manifest.IsHartsy ? AppHost.Environments.HartsyWorkerPath != null
        : Manifest.IsInternal ? AppHost.Environments.InternalWorkerPath != null
                                  && (!Manifest.IsInternalGpu || InternalGpuRuntime.IsInstalled(AppHost.DataRoot.EnvDir(Manifest.EnvGroup), Manifest.Engine!))
        : File.Exists(AppHost.DataRoot.EnvPython(Manifest.EnvGroup)) && AppHost.Environments.ReadState(Manifest.EnvGroup) != null;

    /// <summary>Not installed, and the environment it needs is prepared: the caption shows green (installing is quick).</summary>
    [ObservableProperty] private bool _envReady;

    private string NotInstalledText(bool envReady) =>
        Manifest.IsHartsy
            ? (envReady ? "The bundled HartsyInference worker is ready" : "The bundled HartsyInference worker is missing")
        : Manifest.IsInternalGpu
            ? (envReady ? "The GPU builds are already downloaded"
                : AppHost.Environments.InternalWorkerPath == null ? "The bundled internal worker is missing"
                : $"Installing downloads the GPU builds ({InternalGpuRuntime.DownloadBytes(Manifest.Engine!) / (1024 * 1024)} MB)")
        : Manifest.IsInternal
            ? (envReady ? "The bundled internal worker is ready" : "The bundled internal worker is missing")
            : (envReady ? $"Environment '{Manifest.EnvGroup}' is already prepared" : $"Environment '{Manifest.EnvGroup}' needs to be installed");

    private SettingsEditorViewModel? BuildSettings()
    {
        if (Manifest.WorkerSettings.Count == 0) return null;
        var values = Manifest.WorkerSettingValues(AppHost.Settings.WorkerSettings.GetValueOrDefault(Id));
        return new SettingsEditorViewModel(Manifest.WorkerSettings, values, allowInherit: false, SaveWorkerSetting) { Inline = true };
    }

    [RelayCommand]
    private Task Install() => _owner.RunAsync((log, ct) => AppHost.Environments.InstallWorkerAsync(Id, log, ct));

    [RelayCommand]
    private Task Repair() => _owner.RunAsync((log, ct) => AppHost.Environments.RepairWorkerAsync(Id, log, ct));

    [RelayCommand]
    private async Task Remove()
    {
        if (!await Dialogs.Confirm("Uninstall worker",
                $"Uninstall {Name}? {SharedEnvText}\n\nDownloaded packages stay in the cache and models stay in the models folder, so reinstalling is fast.",
                "Uninstall", "Cancel"))
            return;
        await _owner.RunAsync((log, _) => AppHost.Environments.UninstallWorkerAsync(Id, log));
    }

    [RelayCommand]
    private void ShowSelfTest()
    {
        var state = AppHost.Environments.ReadState(Manifest.EnvGroup);
        if (state == null || !state.SelfTestOutput.TryGetValue(Id, out var output))
        {
            _owner.AppendLog($"--- {Name}: no self-test output (not installed yet) ---");
            return;
        }
        _owner.AppendLog($"--- self-test output: {Name} ---");
        foreach (var l in output.Split('\n')) _owner.AppendLog(l.TrimEnd('\r'));
    }

    [RelayCommand]
    private async Task CheckSize()
    {
        IsMeasuring = true;
        SizeError = null;
        try
        {
            var u = await Task.Run(() => WorkerDiskUsage.Measure(Manifest, AppHost.DataRoot, AppHost.Models));
            var shared = AppHost.Environments.InstalledWorkers(Manifest.EnvGroup).Count > 1 ? " (shared with other workers)" : "";
            SizeLines.Add(new SizeLine(WorkerDiskUsage.Format(u.EnvBytes), "Environment" + shared, SizeKind.Environment));
            foreach (var m in u.Models)
                SizeLines.Add(new SizeLine(WorkerDiskUsage.Format(m.Bytes), "Model " + m.Name, SizeKind.Model));
            if (u.Models.Count == 0 && Manifest.Models.Count > 0)
                SizeLines.Add(new SizeLine("–", "Models: none downloaded yet", SizeKind.Model));
            SizeLines.Add(new SizeLine(WorkerDiskUsage.Format(u.FilesBytes), "Worker files", SizeKind.Files));
            SizeLines.Add(new SizeLine(WorkerDiskUsage.Format(u.TotalBytes), "Total", SizeKind.Total));
        }
        catch (Exception ex)
        {
            SizeError = "Could not measure: " + ex.Message;
        }
        finally
        {
            IsMeasuring = false;
            OnPropertyChanged(nameof(HasSize));
            OnPropertyChanged(nameof(CanCheckSize));
        }
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.Reveal(Folder);

    [RelayCommand]
    private void OpenEnvFolder()
    {
        if (Directory.Exists(EnvFolder)) Dialogs.Reveal(EnvFolder);
        else _owner.AppendLog($"{Name}: the environment folder doesn't exist yet (not installed).");
    }
}

public enum SizeKind { Environment, Model, Files, Total }

/// <summary>One line of the size breakdown: the size first, then what it is. The kind tints the line.</summary>
public sealed record SizeLine(string Size, string Label, SizeKind Kind)
{
    public bool IsEnvironment => Kind == SizeKind.Environment;
    public bool IsModel => Kind == SizeKind.Model;
    public bool IsFiles => Kind == SizeKind.Files;
    public bool IsTotal => Kind == SizeKind.Total;
}

/// <summary>One capability tag of a worker. Common tags get a colour (the view styles them by these flags).</summary>
public sealed record WorkerTag(string Text)
{
    public const string Experimental = "Experimental";
    public const string Broken = "Broken";

    private bool Is(string tag) => string.Equals(Text, tag, StringComparison.OrdinalIgnoreCase);
    public bool IsVoice => Is("Voice");
    public bool IsVoiceClone => Is("Voice clone");
    public bool IsVoiceDesign => Is("Voice design");
    public bool IsMusic => Is("Music");
    public bool IsSfx => Is("SFX");
    public bool IsListening => Is(WorkerTags.Transcription) || Is(WorkerTags.Alignment) || Is(WorkerTags.ForcedAlignment) || Is(WorkerTags.SpeakerVerification);
    public bool IsExperimental => Is(Experimental);
    public bool IsBroken => Is(Broken);
}

/// <summary>One choice of the Runtime selector: the stored value ("python"/"hartsy") and its label.</summary>
/// <summary>
/// One choice of the Runtime selector: the stored value ("python"/"onnx"/"hartsy"…), its label, and whether it
/// is installed and ready (shown green). Compared by reference: each worker has its own options, so one
/// worker's choice can't pass for another's (see OnRuntimeChanged).
/// </summary>
public sealed class RuntimeOption(string value, string label, bool isReady)
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    public bool IsReady { get; } = isReady;
    public override string ToString() => Label;
}

/// <summary>One choice of the Device selector: the stored value ("gpu"/"cpu") and its label.</summary>
public sealed record DeviceOption(string Value, string Label)
{
    public override string ToString() => Label;
}
