using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// The main window: owns the open <see cref="ProjectSession"/> and exposes everything the menus,
/// toolbar, panels and transport bind to. Split into partial files by area.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly UiThrottle _slotThrottle;

    public MainWindowViewModel()
    {
        Inspector = new InspectorViewModel(this);
        _slotThrottle = new UiThrottle(RefreshSlots, TimeSpan.FromMilliseconds(100));
        AppHost.Scheduler.Changed += _slotThrottle.Trigger;
        RefreshRecent();
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnClockTick());
        // No volume control in the UI: playback runs at full level (use the OS mixer).
        Volume = 1.0;
        ShowSpeakersPanel = AppHost.Settings.Ui.Panels.Speakers;
        ShowChapters = AppHost.Settings.Ui.Panels.Chapters;
        ShowAssets = AppHost.Settings.Ui.Panels.Assets;
        ShowInspector = AppHost.Settings.Ui.Panels.Inspector;
    }

    // ================================================================== state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(IsReadOnlyProject), nameof(WindowTitle), nameof(Chapters), nameof(Speakers))]
    private ProjectSession? _session;

    public bool HasProject => Session != null;

    /// <summary>A read-only project (the bundled examples) is open: the menu bar shows a badge.</summary>
    public bool IsReadOnlyProject => Session?.Project.IsReadOnly == true;
    public ObservableCollection<ChapterViewModel>? Chapters => Session?.Chapters;
    public ObservableCollection<SpeakerViewModel>? Speakers => Session?.Speakers;

    public string WindowTitle => Session == null
        ? "Local Audiobook Studio"
        : $"{(string.IsNullOrWhiteSpace(Session.Project.File.Title) ? Path.GetFileName(Session.Paths.Root) : Session.Project.File.Title)} — Local Audiobook Studio";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StackItems), nameof(CurrentChapterTitle))]
    private ChapterViewModel? _selectedChapter;

    public string CurrentChapterTitle => SelectedChapter?.Title ?? "";

    public InspectorViewModel Inspector { get; }

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _showSpeakersPanel = true;
    [ObservableProperty] private bool _showChapters = true;
    [ObservableProperty] private bool _showAssets = true;
    [ObservableProperty] private bool _showInspector = true;

    partial void OnShowSpeakersPanelChanged(bool value) { AppHost.Settings.Ui.Panels.Speakers = value; }
    partial void OnShowChaptersChanged(bool value) { AppHost.Settings.Ui.Panels.Chapters = value; }
    partial void OnShowAssetsChanged(bool value) { AppHost.Settings.Ui.Panels.Assets = value; }
    partial void OnShowInspectorChanged(bool value) { AppHost.Settings.Ui.Panels.Inspector = value; }

    public ObservableCollection<string> RecentProjects { get; } = new();
    public bool HasRecent => RecentProjects.Count > 0;

    /// <summary>One small square per GPU (and the CPU when used) under the queue button.</summary>
    public ObservableCollection<DeviceIndicatorViewModel> Devices { get; } = new();
    [ObservableProperty] private string _queueText = "Queue: 0";
    [ObservableProperty] private bool _queueBusy;

    /// <summary>Segment stack column widths (shared with every row).</summary>
    public StackColumns Columns => StackColumns.Shared;

    /// <summary>View hooks (the window subscribes; keeps view-only concerns out of the view model).</summary>
    public event Action<ItemViewModel>? ScrollIntoViewRequested;
    public event Action<IReadOnlyList<ItemViewModel>>? SelectItemsRequested;
    public event Action<ItemViewModel>? BeginEditRequested;
    public event Action? FocusSearchRequested;

    // ================================================================== startup / shutdown

    /// <summary>
    /// Command line: <c>[projectFolder | package.abproj]</c>, or
    /// <c>--import book.txt --project NewFolder</c> to import a text file into a new project without dialogs.
    /// </summary>
    /// <param name="installWorker">A worker to install right away (setup's quick test), or null.</param>
    /// <param name="openProject">A project to open instead of the most recent one (setup's example project), or null.</param>
    public async Task OnStartupAsync(string[] args, string? installWorker = null, string? openProject = null)
    {
        await AppHost.DetectGpusAsync(addMissing: true);
        RefreshSlots();

        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--import") is { } importFile && Arg("--project") is { } projectFolder)
        {
            await ImportHeadlessAsync(importFile, projectFolder);
            return;
        }

        // Setup's quick test: install a worker (Kokoro) in the Workers window.
        if (installWorker != null) await WindowService.ShowWorkersAsync(installWorker: installWorker);

        var projectArg = args.FirstOrDefault(a => !a.StartsWith("--"));
        var toOpen = projectArg ?? openProject ?? AppHost.Settings.RecentProjects.FirstOrDefault();
        if (toOpen != null && Directory.Exists(toOpen) && ProjectStore.IsProjectFolder(toOpen))
            await OpenProjectAsync(toOpen);
        else if (projectArg != null && projectArg.EndsWith(ProjectPackager.Extension, StringComparison.OrdinalIgnoreCase))
            await UnpackAsync(projectArg);
    }

    /// <summary>Called when the window closes: saves, stops playback, shuts workers down.</summary>
    public async Task ShutdownAsync()
    {
        _clock.Stop();
        _engine?.Dispose();
        if (Session != null)
        {
            await Session.DisposeAsync();
        }
        await AppHost.ShutdownAsync();
    }

    // ================================================================== open / close

    /// <summary>Text of the loading overlay over the main window; null hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private string? _busyText;

    [ObservableProperty] private string? _busyDetail;

    public bool IsBusy => BusyText != null;

    /// <summary>
    /// Shows the loading overlay while <paramref name="work"/> runs. Part of opening a project runs on the
    /// UI thread, so the overlay is given a moment to draw before the work starts.
    /// </summary>
    public async Task RunBusyAsync(string text, string? detail, Func<Task> work)
    {
        BusyText = text;
        BusyDetail = detail;
        try
        {
            await Task.Delay(60);
            await work();
        }
        finally
        {
            BusyText = null;
            BusyDetail = null;
        }
    }

    public async Task OpenProjectAsync(string folder)
    {
        try
        {
            StatusText = $"Opening {folder}…";
            await RunBusyAsync("Opening project…", folder, async () =>
            {
                var session = await ProjectSession.OpenAsync(folder);
                await SetSessionAsync(session);
            });
            StatusText = $"Opened {folder}";
        }
        catch (ProjectFormatException ex)
        {
            await Dialogs.ShowError("Cannot open project", ex.Message);
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            AppHost.Log.LogError(ex, "Opening {Folder} failed", folder);
            await Dialogs.ShowError("Cannot open project", $"{folder}\n\n{ex.Message}");
            StatusText = "Ready";
        }
    }

    public async Task SetSessionAsync(ProjectSession session)
    {
        await CloseProjectAsync();
        Session = session;
        session.LayoutChanged += OnLayoutChanged;
        session.PlayItemRequested += OnPlayItemRequested;
        session.RecordToggleRequested += ToggleRecording;
        session.StopPlaybackRequested += StopPlayback;
        session.ScrubRequested += OnScrubRequested;
        session.ScrubEnded += OnScrubEnded;
        session.StructureChanged += OnStructureChanged;
        session.AssetsChanged += OnAssetsChanged;
        session.AudioCacheReplaced += OnAudioCacheReplaced;
        session.StatusMessage += m => StatusText = m;
        session.Undo.Changed += (_, _) => { UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(UndoText)); OnPropertyChanged(nameof(RedoText)); };
        CreateEngine(session);
        SelectedChapter = session.Chapters.FirstOrDefault();
        AppHost.Settings.AddRecent(session.Paths.Root);
        AppHost.SaveSettings();
        RefreshRecent();
        OnPropertyChanged(nameof(WindowTitle));
        NotifyCommands();
        await RefreshAssetsAsync();
        if (session.OpenElsewhere)
            StatusText = "This project is also open in another program (the las command line or another window); changes there and here can overwrite each other.";
    }

    public async Task CloseProjectAsync()
    {
        if (Session == null) return;
        StopPlayback();
        await StopRecordingAsync(); // a recording in progress is kept as a take
        var s = Session;
        s.LayoutChanged -= OnLayoutChanged;
        s.PlayItemRequested -= OnPlayItemRequested;
        s.RecordToggleRequested -= ToggleRecording;
        s.StopPlaybackRequested -= StopPlayback;
        s.ScrubRequested -= OnScrubRequested;
        s.ScrubEnded -= OnScrubEnded;
        s.StructureChanged -= OnStructureChanged;
        s.AssetsChanged -= OnAssetsChanged;
        s.AudioCacheReplaced -= OnAudioCacheReplaced;
        Session = null;
        SelectedChapter = null;
        UpdateSelection([]);
        _engine?.Dispose();
        _engine = null;
        _collapsedAssetFolders.Clear();
        await RefreshAssetsAsync();
        await s.DisposeAsync();
        NotifyCommands();
    }

    private void RefreshRecent()
    {
        RecentProjects.Clear();
        foreach (var r in AppHost.Settings.RecentProjects) RecentProjects.Add(r);
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>The project's sample rate changed: playback restarts on the new audio cache.</summary>
    private void OnAudioCacheReplaced()
    {
        StopPlayback();
        if (Session != null) CreateEngine(Session);
    }

    /// <summary>A file was added to the assets elsewhere (a setting's browse button): show it.</summary>
    private async void OnAssetsChanged() => await RefreshAssetsAsync();

    private void OnStructureChanged()
    {
        if (IsFiltering) RebuildFilter();
        OnPropertyChanged(nameof(ChapterTitles));
        UpdateAssetUsage();
        if (SelectedChapter != null && Session != null && !Session.Chapters.Contains(SelectedChapter))
            SelectedChapter = Session.Chapters.FirstOrDefault();
    }

    // ================================================================== devices / queue

    private void RefreshSlots()
    {
        var groups = DeviceGroup.Build(AppHost.Scheduler.DeviceSnapshots(), AppHost.Scheduler.WorkerSnapshots(), DeviceGroup.AnyWorkerOnCpu());
        while (Devices.Count > groups.Count) Devices.RemoveAt(Devices.Count - 1);
        for (int i = 0; i < groups.Count; i++)
        {
            if (i >= Devices.Count) Devices.Add(new DeviceIndicatorViewModel());
            Devices[i].Update(groups[i]);
        }
        var queued = AppHost.Scheduler.QueuedCount;
        var running = AppHost.Scheduler.RunningCount;
        QueueText = running > 0 ? $"Queue: {queued} · running {running}" : $"Queue: {queued}";
        QueueBusy = queued + running > 0;
    }

    private void NotifyCommands()
    {
        foreach (var c in new IRelayCommand[]
                 {
                     ImportTextCommand, ExportScriptCommand, SaveCommand, PackProjectCommand, ExportAudiobookCommand,
                     CleanUpTakesCommand, CompressTakesCommand, UndoCommand, RedoCommand, GenerateStaleChapterCommand, GenerateStaleBookCommand,
                     ShowSpeakersCommand, AddChapterCommand, ProjectPropertiesCommand, CloseProjectCommand, ImportAssetsCommand,
                 })
            c.NotifyCanExecuteChanged();
        NotifySelectionCommands();
    }

    private bool CanUseProject() => Session != null;

    public IReadOnlyList<string> ChapterTitles => Session?.Chapters.Select(c => c.Title).ToList() ?? [];
}

/// <summary>One small device square (GPU0, CPU) under the queue button.</summary>
public sealed partial class DeviceIndicatorViewModel : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _tooltip = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isIdleLoaded;
    [ObservableProperty] private bool _isDisabled;

    public void Update(DeviceGroup g)
    {
        var d = g.Device;
        // Several workers can share a device: show how many are generating ("GPU0·2").
        var busy = g.Workers.Count(w => w.State == WorkerState.Busy);
        Label = g.ShortLabel + (busy > 1 ? "·" + busy : "");
        IsBusy = busy > 0;
        IsLoading = !IsBusy && g.Workers.Any(w => w.State is WorkerState.Starting or WorkerState.Loading);
        IsIdleLoaded = g.Workers.Count > 0 && !IsBusy && !IsLoading;
        IsFailed = d.DisabledWorkers.Count > 0 || g.Errors.Any();
        IsDisabled = !d.Enabled;
        var lines = new List<string>
        {
            d.Name,
            $"{g.Active} of {d.MaxActive} active · {g.Workers.Count} loaded" + (d.MaxTotal is int t ? $" of {t}" : ""),
        };
        foreach (var w in g.Workers)
        {
            var name = AppHost.Catalog.Get(w.WorkerId)?.DisplayName ?? w.WorkerId;
            var line = $"{name}{(w.Lane > 0 ? $" #{w.Lane + 1}" : "")}: {w.State.ToString().ToLowerInvariant()}";
            if (w.JobLabel != null) line += $" · {w.JobLabel}";
            if (w.BusySinceUtc is DateTime since) line += $" · {(int)(DateTime.UtcNow - since).TotalMinutes}:{(DateTime.UtcNow - since).Seconds:00}";
            lines.Add(line);
        }
        foreach (var e in g.ErrorLines) lines.Add("Error " + e);
        if (d.DisabledWorkers.Count > 0) lines.Add("Disabled here after crashes: " + string.Join(", ", d.DisabledWorkers) + " (re-enable in the Jobs panel)");
        Tooltip = string.Join(Environment.NewLine, lines);
    }
}
