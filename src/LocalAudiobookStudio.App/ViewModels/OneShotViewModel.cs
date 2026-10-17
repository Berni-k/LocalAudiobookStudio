using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// The One shot window: one job for a worker, outside any project, on whatever input is given. One tab per thing
/// workers do. Each tab picks a worker (with its settings), takes its inputs and shows the result.
/// </summary>
public sealed partial class OneShotViewModel : ObservableObject
{
    private readonly MainWindowViewModel? _player;

    public OneShotViewModel(MainWindowViewModel? player)
    {
        _player = player;
        Root = Path.Combine(AppHost.DataRoot.Root, "one-shot");
        Directory.CreateDirectory(Root);
        Tabs = [new OneShotGenerateViewModel(this), new OneShotTranscribeViewModel(this), new OneShotAlignViewModel(this),
            new OneShotDiarizeViewModel(this), new OneShotCompareViewModel(this)];
        _selectedTab = Tabs[0];
    }

    /// <summary>Where files picked for asset settings (a ComfyUI workflow, a reference clip) are kept: this window has no project.</summary>
    public string Root { get; }

    public ObservableCollection<OneShotTabViewModel> Tabs { get; }

    [ObservableProperty] private OneShotTabViewModel _selectedTab;

    public void Show(OneShotKind kind) => SelectedTab = Tabs.First(t => t.Kind == kind);

    /// <summary>A setting's relative path (as stored in its values) as a full path.</summary>
    public string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(Root, path);

    /// <summary>Copies a file picked for an asset setting into this window's own assets folder and returns its relative path.</summary>
    public Task<string> ImportAsset(SettingDefinition def, string file)
    {
        var folder = string.IsNullOrWhiteSpace(def.AssetFolder) ? "" : def.AssetFolder.Replace('\\', '/').Trim('/') + "/";
        var dir = Path.Combine(Root, ProjectPaths.AssetsFolder, folder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var name = stem + ext;
        for (var n = 2; File.Exists(Path.Combine(dir, name)); n++) name = $"{stem}-{n}{ext}";
        File.Copy(file, Path.Combine(dir, name));
        return Task.FromResult($"{ProjectPaths.AssetsFolder}/{folder}{name}");
    }

    public Task PlayAsync(string path) => _player?.PlayFileAsync(path) ?? Task.CompletedTask;
    public void StopPlayback() => _player?.StopPlayback();

    /// <summary>A scratch folder for one run's files, emptied the next time that tab runs (and when the window closes).</summary>
    public string ScratchFor(string tab)
    {
        var dir = Path.Combine(AppHost.DataRoot.Temp, "one-shot", tab);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { /* a file still playing */ }
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Close()
    {
        foreach (var tab in Tabs) tab.Cancel();
        StopPlayback();
        try { Directory.Delete(Path.Combine(AppHost.DataRoot.Temp, "one-shot"), true); } catch (IOException) { /* cache */ }
    }
}

public enum OneShotKind { Generate, Transcribe, Align, Diarize, Compare }

/// <summary>What every tab has: a worker with its settings, a Run button, a status line and a result.</summary>
public abstract partial class OneShotTabViewModel : ObservableObject
{
    private readonly Dictionary<string, JsonObject> _values = new();
    private List<GenerationJob> _jobs = [];

    protected OneShotTabViewModel(OneShotViewModel owner, OneShotKind kind, string title)
    {
        Owner = owner;
        Kind = kind;
        Title = title;
        RebuildWorkers();
    }

    public OneShotViewModel Owner { get; }
    public OneShotKind Kind { get; }
    public string Title { get; }

    /// <summary>The installed workers that can do this tab's job.</summary>
    public ObservableCollection<WorkerManifest> Workers { get; } = new();

    [ObservableProperty] private WorkerManifest? _worker;
    [ObservableProperty] private SettingsEditorViewModel? _settings;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _resultText = "";

    protected abstract IEnumerable<WorkerManifest> Candidates();

    /// <summary>The tag a worker needs to be offered here, for the warning when none is installed.</summary>
    public abstract string RequiredTags { get; }

    public bool NoWorkerInstalled => Workers.Count == 0;
    public string NoWorkerText => $"No installed worker is tagged {RequiredTags}. Install one in Workers.";
    public string WorkerInfo => Worker?.Description ?? "";
    public bool HasResult => ResultText.Length > 0;

    public bool CanRun => !IsRunning && Worker != null;

    // Files dropped on a field (see OneShotWindow): the field's role says what it takes.
    public virtual bool AcceptsDrop(string role, IReadOnlyList<string> paths) => false;
    public virtual void Drop(string role, IReadOnlyList<string> paths) { }

    /// <summary>The audio the app can read (WAV, Opus, MP3).</summary>
    protected static bool IsAudio(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".opus" or ".mp3";

    protected static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md";

    partial void OnIsRunningChanged(bool value) { OnPropertyChanged(nameof(CanRun)); RunCommand.NotifyCanExecuteChanged(); }
    partial void OnResultTextChanged(string value) => OnPropertyChanged(nameof(HasResult));

    /// <summary>Lists the installed workers again (the window stays open while workers are installed).</summary>
    public void RebuildWorkers()
    {
        var list = Candidates().Where(w => AppHost.Environments.IsWorkerReady(w.Id)).ToList();
        for (var i = Workers.Count - 1; i >= 0; i--)
            if (!list.Contains(Workers[i])) Workers.RemoveAt(i);
        foreach (var w in list)
            if (!Workers.Contains(w)) Workers.Add(w);
        if (Worker == null || !Workers.Contains(Worker)) Worker = Workers.FirstOrDefault();
        OnPropertyChanged(nameof(NoWorkerInstalled));
    }

    partial void OnWorkerChanged(WorkerManifest? value)
    {
        OnPropertyChanged(nameof(WorkerInfo));
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
        Settings = value == null ? null : BuildSettings(value);
        WorkerSelected(value);
    }

    protected virtual void WorkerSelected(WorkerManifest? worker) { }

    /// <summary>The settings as last edited for this worker (this window's only; nothing is saved).</summary>
    protected JsonObject ValuesOf(WorkerManifest worker)
    {
        if (!_values.TryGetValue(worker.Id, out var values)) _values[worker.Id] = values = worker.DefaultSettings();
        return values;
    }

    private SettingsEditorViewModel BuildSettings(WorkerManifest worker)
    {
        var values = ValuesOf(worker);
        var definitions = worker.SettingsFor(values, Owner.Resolve);
        return new SettingsEditorViewModel(definitions, values, allowInherit: false, (key, value) =>
        {
            if (value == null) values.Remove(key); else values[key] = value.DeepClone();
            // Another file (a ComfyUI workflow) brings other settings: drop the old one's values and show the new one's.
            if (definitions.Any(d => d.Key == key && d.AddsSettings != null))
            {
                var keep = worker.SettingsFor(values, Owner.Resolve).Select(d => d.Key).ToHashSet();
                foreach (var stale in definitions.Select(d => d.Key).Where(k => !keep.Contains(k))) values.Remove(stale);
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(Worker, worker)) Settings = BuildSettings(worker); });
            }
        }, Owner.Root, importAsset: Owner.ImportAsset) { Inline = true };
    }

    /// <summary>The worker's settings as the job takes them, or null (with the reason in <see cref="Status"/>) when it can't run.</summary>
    protected JsonObject? JobSettings(WorkerManifest worker)
    {
        if (AppHost.WhyWorkerCannotRun(worker.Id) is { } reason) { Status = reason; return null; }
        var settings = OneShot.MergeSettings(ValuesOf(worker), null, worker, Owner.Resolve);
        if (worker.UnsupportedChoice(settings) is { } unsupported) { Status = unsupported; return null; }
        return settings;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (Worker is not { } worker) return;
        IsRunning = true;
        ResultText = "";
        Status = "Starting…";
        try
        {
            await RunJobAsync(worker);
        }
        catch (Exception ex)
        {
            Status = "Failed: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Does the tab's job and puts its result in <see cref="ResultText"/> and <see cref="Status"/>.</summary>
    protected abstract Task RunJobAsync(WorkerManifest worker);

    public void Cancel()
    {
        foreach (var job in _jobs.ToList())
            if (job.State is JobState.Queued or JobState.Running) AppHost.Scheduler.Cancel(job.Id);
    }

    [RelayCommand]
    private void CancelRun() => Cancel();

    /// <summary>Queues the jobs and waits until all are done (completed, failed or cancelled), showing progress in <see cref="Status"/>.</summary>
    protected async Task RunJobsAsync(List<GenerationJob> jobs)
    {
        _jobs = jobs;
        var pending = new HashSet<Guid>(jobs.Select(j => j.Id));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(GenerationJob j)
        {
            lock (pending)
                if (pending.Remove(j.Id) && pending.Count == 0) done.TrySetResult();
        }
        AppHost.Scheduler.JobCompleted += Finished;
        AppHost.Scheduler.JobFailed += Finished;
        try
        {
            AppHost.Scheduler.EnqueueMany(jobs);
            var poll = DispatcherTimer.Run(() =>
            {
                var finished = jobs.Count(j => j.State is JobState.Completed or JobState.Failed or JobState.Cancelled);
                var running = jobs.FirstOrDefault(j => j.State == JobState.Running);
                var of = jobs.Count > 1 ? $" {Math.Min(finished + 1, jobs.Count)}/{jobs.Count}" : "";
                Status = running == null ? "Queued…" : (running.ProgressMessage ?? $"Working… {running.Progress:P0}") + of;
                return !done.Task.IsCompleted;
            }, TimeSpan.FromMilliseconds(250));
            await done.Task;
            poll.Dispose();
        }
        finally
        {
            AppHost.Scheduler.JobCompleted -= Finished;
            AppHost.Scheduler.JobFailed -= Finished;
        }
    }

    /// <summary>The reason the first job that didn't complete gives, or null when all did.</summary>
    protected static string? FailureOf(IEnumerable<GenerationJob> jobs) =>
        jobs.FirstOrDefault(j => j.State != JobState.Completed) is { } bad
            ? bad.State == JobState.Cancelled ? "Cancelled." : "Failed: " + (bad.LastError ?? "unknown error")
            : null;

    /// <summary>"0:03.250", for word times.</summary>
    protected static string Time(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff");
}
