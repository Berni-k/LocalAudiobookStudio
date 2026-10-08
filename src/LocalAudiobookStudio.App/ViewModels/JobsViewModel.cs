using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Jobs panel: the queue summary, every job in the pipeline (running, queued in the order they will run,
/// recently failed), the active workers grouped by device (GPU0, GPU1, CPU; several workers can share one),
/// and the worker log.
/// </summary>
public sealed partial class JobsViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly UiThrottle _throttle;
    private readonly DispatcherTimer _timer;

    public JobsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _throttle = new UiThrottle(Refresh, TimeSpan.FromMilliseconds(150));
        AppHost.Scheduler.Changed += _throttle.Trigger;
        AppHost.WorkerLogs.LineAdded += OnLogLine;
        foreach (var l in AppHost.WorkerLogs.Snapshot()) LogLines.Add(l);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<DeviceGroupViewModel> Groups { get; } = new();

    /// <summary>Running jobs, then the queue in the order it will run, then recently failed jobs.</summary>
    public ObservableCollection<JobRowViewModel> Queue { get; } = new();
    private readonly Dictionary<Guid, JobRowViewModel> _jobRows = new();

    [ObservableProperty] private string _queueCount = "";
    [ObservableProperty] private bool _queueEmpty = true;
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private bool _hasIdle;

    private void OnLogLine(string line) => Dispatcher.UIThread.Post(() =>
    {
        LogLines.Add(line);
        while (LogLines.Count > 2000) LogLines.RemoveAt(0);
    });

    public void Refresh()
    {
        var s = AppHost.Scheduler;
        var workers = s.WorkerSnapshots();
        var groups = DeviceGroup.Build(s.DeviceSnapshots(), workers, showIdleCpu: true);
        // Keep row objects by key so the list doesn't flicker or lose its scroll position.
        for (int i = 0; i < groups.Count; i++)
        {
            var existing = Groups.FirstOrDefault(g => g.Key == groups[i].Key);
            if (existing == null) Groups.Insert(Math.Min(i, Groups.Count), existing = new DeviceGroupViewModel(this, groups[i].Key));
            else if (Groups.IndexOf(existing) != i) Groups.Move(Groups.IndexOf(existing), i);
            existing.Update(groups[i]);
        }
        while (Groups.Count > groups.Count) Groups.RemoveAt(Groups.Count - 1);

        RefreshQueue(s.JobSnapshots());

        var running = s.RunningCount;
        var queued = s.QueuedCount;
        var failed = s.FailedCount;
        HasFailed = failed > 0;
        HasIdle = workers.Any(w => w.State == WorkerState.Idle);
        Summary = $"{running} running · {queued} queued" + (failed > 0 ? $" · {failed} failed" : "");
    }

    /// <summary>
    /// Brings <see cref="Queue"/> in line with the scheduler, reusing rows by job id. A whole book can queue
    /// thousands of jobs, so rows are moved, inserted and removed one by one (the list stays put while
    /// scrolled) instead of rebuilt.
    /// </summary>
    private void RefreshQueue(IReadOnlyList<JobSnapshot> jobs)
    {
        var shown = jobs.Where(j => j.State is JobState.Running or JobState.Queued or JobState.Failed).ToList();
        var ids = shown.Select(j => j.Id).ToHashSet();
        for (int i = Queue.Count - 1; i >= 0; i--)
            if (!ids.Contains(Queue[i].Id))
            {
                _jobRows.Remove(Queue[i].Id);
                Queue.RemoveAt(i);
            }
        for (int i = 0; i < shown.Count; i++)
        {
            var job = shown[i];
            if (!_jobRows.TryGetValue(job.Id, out var row))
            {
                _jobRows[job.Id] = row = new JobRowViewModel(this, job.Id);
                Queue.Insert(i, row);
            }
            else if (!ReferenceEquals(Queue[i], row))
            {
                Queue.Move(Queue.IndexOf(row), i);
            }
            row.Update(job);
        }
        QueueEmpty = shown.Count == 0;
        var failed = shown.Count(j => j.State == JobState.Failed);
        QueueCount = shown.Count == 0 ? "" : $"{shown.Count - failed} in the pipeline" + (failed > 0 ? $" · {failed} failed" : "");
    }

    [RelayCommand]
    private void RetryFailed() => AppHost.Scheduler.RetryFailed();

    [RelayCommand]
    private void CancelAll() => AppHost.Scheduler.CancelAll();

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    /// <summary>Stops every idle worker, freeing its memory.</summary>
    [RelayCommand]
    private void UnloadIdle()
    {
        AppHost.Scheduler.UnloadIdleWorkers();
        Refresh();
    }

    public void Reveal(Guid itemId)
    {
        if (_main.Session?.ItemVm(itemId) is { } vm) _main.SelectAndReveal(vm);
    }

    public void Dispose()
    {
        _timer.Stop();
        AppHost.Scheduler.Changed -= _throttle.Trigger;
        AppHost.WorkerLogs.LineAdded -= OnLogLine;
    }
}

/// <summary>A GPU or the CPU with its running workers.</summary>
public sealed partial class DeviceGroupViewModel(JobsViewModel owner, string key) : ObservableObject
{
    private List<(int device, string worker)> _disabled = [];
    private List<int> _devices = [];

    public string Key { get; } = key;
    public ObservableCollection<ActiveWorkerRowViewModel> Workers { get; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _limits = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _disabledText;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isDeviceDisabled;

    public void Update(DeviceGroup g)
    {
        var d = g.Device;
        Name = d.Name + (d.Enabled ? "" : " (disabled)");
        IsDeviceDisabled = !d.Enabled;
        Limits = $"{g.Active} of {d.MaxActive} active · {g.Workers.Count}" + (d.MaxTotal is int t ? $" of {t}" : "") + " loaded";
        Error = g.Errors.Any() ? string.Join(Environment.NewLine, g.ErrorLines) : null;
        _devices = g.DeviceIndexes.ToList();

        _disabled = new[] { d }.Concat(g.ExternalDevices)
            .SelectMany(x => x.DisabledWorkers.Select(w => (x.Index, w))).ToList();
        DisabledText = _disabled.Count > 0 ? "Disabled here after crashes: " + string.Join(", ", _disabled.Select(x => x.worker).Distinct()) : null;

        for (int i = 0; i < g.Workers.Count; i++)
        {
            var w = g.Workers[i];
            var row = Workers.FirstOrDefault(r => r.Id == w.Id);
            if (row == null) Workers.Insert(Math.Min(i, Workers.Count), row = new ActiveWorkerRowViewModel(owner, w.Id));
            else if (Workers.IndexOf(row) != i) Workers.Move(Workers.IndexOf(row), i);
            row.Update(w);
        }
        while (Workers.Count > g.Workers.Count) Workers.RemoveAt(Workers.Count - 1);
        IsEmpty = Workers.Count == 0;
    }

    [RelayCommand]
    private void Reenable()
    {
        foreach (var (device, worker) in _disabled) AppHost.Scheduler.ReenableWorker(device, worker);
        owner.Refresh();
    }

    /// <summary>Hides the errors shown here (they also clear by themselves once the worker starts fine).</summary>
    [RelayCommand]
    private void DismissErrors()
    {
        foreach (var d in _devices) AppHost.Scheduler.DismissErrors(d);
        owner.Refresh();
    }
}

/// <summary>One running worker process: what it is doing, and what can be done with it.</summary>
public sealed partial class ActiveWorkerRowViewModel(JobsViewModel owner, int id) : ObservableObject
{
    private Guid? _jobId;
    private Guid? _itemId;

    public int Id { get; } = id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isIdle;
    [ObservableProperty] private string _jobLabel = "";
    [ObservableProperty] private bool _hasJob;
    [ObservableProperty] private bool _canReveal;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _detail = "";

    public void Update(WorkerSnapshot w)
    {
        var name = AppHost.Catalog.Get(w.WorkerId)?.DisplayName ?? w.WorkerId;
        Name = w.Lane > 0 ? $"{name} #{w.Lane + 1}" : name;
        IsBusy = w.State == WorkerState.Busy;
        IsLoading = w.State is WorkerState.Starting or WorkerState.Loading;
        IsIdle = w.State == WorkerState.Idle;
        State = w.State switch
        {
            WorkerState.Starting => "starting",
            WorkerState.Loading => "loading",
            WorkerState.Busy => "generating",
            _ => "idle",
        };
        _jobId = w.JobId;
        _itemId = w.ItemId;
        HasJob = w.JobId != null;
        CanReveal = w.ItemId is { } item && item != Guid.Empty;
        JobLabel = w.JobLabel ?? "";
        Progress = IsBusy ? w.Progress * 100 : 0;
        var parts = new List<string>();
        var message = w.ProgressMessage ?? w.StatusMessage;
        if (message != null) parts.Add(message);
        // Elapsed time in whole seconds ("0:35"); idle workers just say so.
        if (w.BusySinceUtc is DateTime since) parts.Add(Elapsed(DateTime.UtcNow - since));
        else if (IsIdle) parts.Add("idle");
        Detail = string.Join(" · ", parts);
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    [RelayCommand]
    private void Unload()
    {
        AppHost.Scheduler.UnloadWorker(Id);
        owner.Refresh();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_jobId is { } id) AppHost.Scheduler.Cancel(id);
    }

    [RelayCommand]
    private void Reveal()
    {
        if (_itemId is { } id) owner.Reveal(id);
    }
}

/// <summary>One job in the Queue list: running, waiting (with its place in line) or failed.</summary>
public sealed partial class JobRowViewModel(JobsViewModel owner, Guid id) : ObservableObject
{
    private Guid _itemId;

    public Guid Id { get; } = id;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _worker = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _canReveal;
    [ObservableProperty] private bool _canPrioritize;

    public void Update(JobSnapshot j)
    {
        _itemId = j.ItemId;
        Label = j.Label;
        Worker = AppHost.Catalog.Get(j.WorkerId)?.DisplayName ?? j.WorkerId;
        IsRunning = j.State == JobState.Running;
        IsFailed = j.State == JobState.Failed;
        IsQueued = j.State == JobState.Queued;
        State = j.State switch
        {
            JobState.Running => "generating",
            JobState.Failed => "failed",
            _ => $"#{j.QueuePosition}",
        };
        Progress = IsRunning ? j.Progress * 100 : 0;
        Detail = j.State switch
        {
            JobState.Running => Join(Worker, j.ProgressMessage),
            JobState.Failed => Join(Worker, j.LastError),
            _ => Join(Worker, j.Priority == JobPriority.High ? "prioritized" : j.Priority == JobPriority.Low ? "low priority" : null),
        };
        CanReveal = j.ItemId != Guid.Empty;
        CanPrioritize = IsQueued && j.Priority < JobPriority.High;
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    [RelayCommand]
    private void Cancel() => AppHost.Scheduler.Cancel(Id);

    /// <summary>Moves a waiting job to the front of the line (high priority, like a manual regenerate).</summary>
    [RelayCommand]
    private void Prioritize() => AppHost.Scheduler.Reprioritize(Id, JobPriority.High);

    [RelayCommand]
    private void Reveal() => owner.Reveal(_itemId);
}
