using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.ViewModels;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.Hashing;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Core.Undo;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.Session;

/// <summary>
/// The open project. Owns the model, the undo stack, the save queue, the view models and the
/// computed timeline, and is the only place that edits the project. The project's rules (hashes,
/// jobs, takes, timeline) are in its <see cref="ProjectEngine"/>. Lives on the UI thread;
/// background work (layout, analysis, saving) reports back through the dispatcher.
/// </summary>
public sealed partial class ProjectSession : IAsyncDisposable
{
    private readonly Dictionary<Guid, ItemViewModel> _itemVms = new();
    private readonly Dictionary<string, SpeakerViewModel> _speakerVms = new(StringComparer.OrdinalIgnoreCase);
    private readonly UiThrottle _jobThrottle;
    private readonly ILogger _log = AppHost.LoggerFactory.CreateLogger("Session");
    private HashSet<Guid> _jobItems = new();
    private bool _layoutPending;
    private long _layoutVersion;
    private readonly ProjectLock? _openLock;

    private ProjectSession(Project project)
    {
        // Keeps the command line (las) from changing the project while it is open here.
        _openLock = ProjectLock.TryAcquire(project.Paths.Root, "Local Audiobook Studio");

        Engine = new ProjectEngine(AppHost.Host!, project)
        {
            SpeakerChanged = sp => { MarkSpeakerDirty(sp); RebuildSpeakerVms(); },
        };
        // Producers serialize the model, so they run on the UI thread that owns it.
        Saves = new SaveQueue(snapshotInvoker: producer => Dispatcher.UIThread.InvokeAsync(producer).GetTask());
        Saves.SaveFailed += (path, ex) => _log.LogError(ex, "Saving {Path} failed", path);
        _jobThrottle = new UiThrottle(RefreshJobStates);
    }

    public ProjectEngine Engine { get; }

    /// <summary>True when another program (the command line, or another copy of the app) had the project open.</summary>
    public bool OpenElsewhere => _openLock == null;
    public Project Project => Engine.Project;
    public ProjectPaths Paths => Project.Paths;
    public UndoStack Undo { get; } = new();
    public SaveQueue Saves { get; }
    public TakeIndex Takes => Engine.Takes;
    public PeaksCache PeaksCache { get; } = new();
    public AudioBufferCache AudioCache { get; private set; } = null!;

    public ObservableCollection<ChapterViewModel> Chapters { get; } = new();
    public ObservableCollection<SpeakerViewModel> Speakers { get; } = new();

    public TimelineLayout Layout { get; private set; } = TimelineLayout.Empty;
    /// <summary>The real mix (export uses this).</summary>
    public MixPlan MixPlan { get; private set; } = MixPlan.Empty;

    /// <summary>The mix for playback: like <see cref="MixPlan"/>, plus quiet static where audio isn't generated yet.</summary>
    public MixPlan PlaybackPlan { get; private set; } = MixPlan.Empty;

    /// <summary>Raised on the UI thread after the timeline was recomputed.</summary>
    public event Action? LayoutChanged;

    /// <summary>Raised when a row asks to play (item, offset into the item in ms).</summary>
    public event Action<ItemViewModel, double>? PlayItemRequested;

    /// <summary>Raised when a row's record button is clicked (start, or stop if it is recording).</summary>
    public event Action<ItemViewModel>? RecordToggleRequested;

    /// <summary>Raised when structure changes (items inserted/removed/moved).</summary>
    public event Action? StructureChanged;

    /// <summary>Raised when <see cref="AudioCache"/> was replaced (a new sample rate): playback must use the new one.</summary>
    public event Action? AudioCacheReplaced;

    /// <summary>Raised for messages the user should see in the status bar.</summary>
    public event Action<string>? StatusMessage;

    // ================================================================== open / create

    public static async Task<ProjectSession> OpenAsync(string folder)
    {
        var project = await Task.Run(() => ProjectStore.Load(folder));
        var s = new ProjectSession(project);
        await s.InitializeAsync();
        return s;
    }

    public static async Task<ProjectSession> CreateAsync(string folder, string title, IEnumerable<Chapter>? chapters = null,
        IEnumerable<Speaker>? speakers = null)
    {
        var host = AppHost.Host!;
        var project = await Task.Run(() => ProjectImport.Create(host, folder, title, chapters, speakers));
        var s = new ProjectSession(project);
        await s.InitializeAsync();
        return s;
    }

    private async Task InitializeAsync()
    {
        AudioCache = new AudioBufferCache(Project.Defaults.SampleRate);
        // Older project formats; the migrations may refresh item hashes, so changed chapters are re-saved.
        var converted = Engine.Migrate();
        RebuildSpeakerVms();
        var n = 1;
        foreach (var ch in Project.Chapters)
        {
            var vm = new ChapterViewModel(this, ch) { Number = n++ };
            Chapters.Add(vm);
            foreach (var i in vm.Items) _itemVms[i.Id] = i;
            if (converted.Contains(ch)) MarkChapterDirty(vm);
        }

        // A read-only project starts clean: takes a previous session couldn't delete on close go now.
        if (Project.IsReadOnly && !OpenElsewhere) await Task.Run(() => ReadOnlyProject.DiscardSessionTakes(Paths.Root));

        // Takes and clips are indexed in the background, then statuses and timing are refreshed.
        await Engine.IndexAudioAsync();

        AppHost.Scheduler.OnGenerated = Engine.SaveGeneratedTake;
        AppHost.Scheduler.JobCompleted += OnJobCompleted;
        AppHost.Scheduler.JobFailed += OnJobFailed;
        AppHost.Scheduler.Changed += _jobThrottle.Trigger;

        foreach (var vm in _itemVms.Values) vm.UpdateStatus(null);
        foreach (var c in Chapters) c.RecountStatus();
        InvalidateLayout();
    }

    public async ValueTask DisposeAsync()
    {
        AppHost.Scheduler.CancelAll();
        AppHost.Scheduler.JobCompleted -= OnJobCompleted;
        AppHost.Scheduler.JobFailed -= OnJobFailed;
        AppHost.Scheduler.Changed -= _jobThrottle.Trigger;
        if (AppHost.Scheduler.OnGenerated == Engine.SaveGeneratedTake) AppHost.Scheduler.OnGenerated = null;
        await Saves.DisposeAsync();
        // Takes of a read-only project don't outlive it (only when this window owns the project, not another copy).
        if (Project.IsReadOnly && !OpenElsewhere)
        {
            AudioCache?.Clear();
            var deleted = await Task.Run(() => ReadOnlyProject.DiscardSessionTakes(Paths.Root));
            _log.LogInformation("Read-only project closed: {Deleted} take files deleted", deleted);
        }
        _openLock?.Dispose();
    }

    public Task FlushAsync() => Saves.FlushAsync();

    // ================================================================== lookups

    public ItemViewModel? ItemVm(Guid id) => _itemVms.GetValueOrDefault(id);

    public SpeakerViewModel? SpeakerVm(string? id) => id != null ? _speakerVms.GetValueOrDefault(id) : null;

    public ChapterViewModel? ChapterOf(ItemViewModel item) => item.Chapter;

    public IEnumerable<ItemViewModel> AllItemVms() => Chapters.SelectMany(c => c.Items);

    public int ChapterIndex(ChapterViewModel ch) => Chapters.IndexOf(ch);

    // ================================================================== saving

    // A read-only project (the bundled examples) works normally but is never saved: these do nothing.

    public void MarkChapterDirty(ChapterViewModel ch)
    {
        if (Project.IsReadOnly) return;
        var model = ch.Model;
        Saves.Enqueue(Paths.ChapterFile(model.Id), () => CoreJson.SerializeToUtf8(model));
    }

    public void MarkProjectDirty()
    {
        Project.SyncChapterOrder();
        if (Project.IsReadOnly) return;
        var file = Project.File;
        Saves.Enqueue(Paths.ProjectFile, () => CoreJson.SerializeToUtf8(file));
    }

    public void MarkSpeakerDirty(Speaker sp)
    {
        if (Project.IsReadOnly) return;
        Saves.Enqueue(Paths.SpeakerFile(sp.Id), () => CoreJson.SerializeToUtf8(sp));
    }

    // ================================================================== hashing / staleness

    public WorkerManifest? WorkerFor(TimelineItem item) => Engine.WorkerFor(item);
    public string? ComputeHash(TimelineItem item) => Engine.ComputeHash(item);
    public bool IsStale(TimelineItem item) => Engine.IsStale(item);

    // ================================================================== timeline

    /// <summary>Schedules a timeline recomputation (coalesced; the prefix sum runs on a background thread).</summary>
    public void InvalidateLayout()
    {
        if (_layoutPending) return;
        _layoutPending = true;
        Dispatcher.UIThread.Post(RecomputeLayout, DispatcherPriority.Background);
    }

    private async void RecomputeLayout()
    {
        _layoutPending = false;
        var version = ++_layoutVersion;

        // Snapshot on the UI thread: ids, kinds, pause and background settings, and durations only.
        var input = Engine.SnapshotLayout();
        var layout = await Task.Run(() => ProjectEngine.ComputeLayout(input, version));
        if (version != _layoutVersion) return; // superseded by a newer edit

        Layout = layout;
        for (int ci = 0; ci < Chapters.Count && ci < layout.ChapterStarts.Length; ci++)
        {
            var ch = Chapters[ci];
            var start = layout.ChapterStarts[ci];
            ch.StartMs = start;
            var end = ci + 1 < layout.ChapterStarts.Length ? layout.ChapterStarts[ci + 1] : layout.TotalMs;
            ch.DurationMs = end - start;
        }
        var estimateByChapter = new bool[Chapters.Count];
        foreach (var t in layout.Items)
        {
            if (_itemVms.TryGetValue(t.ItemId, out var vm))
                vm.ApplyTiming(t, t.ChapterIndex < layout.ChapterStarts.Length ? layout.ChapterStarts[t.ChapterIndex] : 0);
            if (t.IsEstimate && t.ChapterIndex < estimateByChapter.Length) estimateByChapter[t.ChapterIndex] = true;
        }
        for (int ci = 0; ci < Chapters.Count; ci++) Chapters[ci].DurationIsEstimate = estimateByChapter[ci];

        MixPlan = Engine.BuildMixPlan(layout);
        PlaybackPlan = Engine.BuildMixPlan(layout, placeholders: true);
        LayoutChanged?.Invoke();
    }

    public ItemDuration DurationOf(TimelineItem item) => Engine.DurationOf(item);

    /// <inheritdoc cref="ProjectEngine.RequestedLengthSec"/>
    public double? RequestedLengthSec(TimelineItem item) => Engine.RequestedLengthSec(item);

    /// <inheritdoc cref="ProjectEngine.MaxLengthSec"/>
    public double MaxLengthSec(TimelineItem item) => Engine.MaxLengthSec(item);

    /// <inheritdoc cref="ProjectEngine.GuessLengthSec"/>
    public double GuessLengthSec(TimelineItem item, WorkerManifest? worker = null) => Engine.GuessLengthSec(item, worker);

    // ================================================================== job status

    private void RefreshJobStates()
    {
        var states = AppHost.Scheduler.ItemStates();
        var touched = new HashSet<ChapterViewModel>();
        foreach (var id in _jobItems.Union(states.Keys))
        {
            if (!_itemVms.TryGetValue(id, out var vm)) continue;
            vm.UpdateStatus(states.TryGetValue(id, out var s) ? s : null);
            touched.Add(vm.Chapter);
        }
        _jobItems = states.Keys.ToHashSet();
        foreach (var c in touched) c.RecountStatus();
    }

    private void RefreshItems(IEnumerable<ItemViewModel> items, bool layout = true)
    {
        _flaggedSims = null; // a take or speaker changed: who sounds unlike their speaker is worked out again
        var states = AppHost.Scheduler.ItemStates();
        var chapters = new HashSet<ChapterViewModel>();
        foreach (var vm in items)
        {
            vm.Refresh();
            vm.UpdateStatus(states.TryGetValue(vm.Id, out var s) ? s : null);
            chapters.Add(vm.Chapter);
        }
        foreach (var c in chapters)
        {
            c.RecountStatus();
            MarkChapterDirty(c);
        }
        if (layout) InvalidateLayout();
    }

    /// <summary>Refreshes every item (after speaker or worker changes, which can make many items stale).</summary>
    public void RefreshAll()
    {
        foreach (var s in Speakers) s.Refresh();
        _flaggedSims = null;
        var states = AppHost.Scheduler.ItemStates();
        foreach (var vm in _itemVms.Values)
        {
            vm.Refresh();
            vm.UpdateStatus(states.TryGetValue(vm.Id, out var st) ? st : null);
        }
        foreach (var c in Chapters) c.RecountStatus();
        InvalidateLayout();
    }

    public void RequestPlayItem(ItemViewModel item, double offsetMs) => PlayItemRequested?.Invoke(item, offsetMs);
    public void RequestRecordToggle(ItemViewModel item) => RecordToggleRequested?.Invoke(item);

    /// <summary>Raised when a row's play button is clicked while that segment plays.</summary>
    public event Action? StopPlaybackRequested;
    public void RequestStopPlayback() => StopPlaybackRequested?.Invoke();
    public void RequestScrub(ItemViewModel item, double offsetMs) => ScrubRequested?.Invoke(item, offsetMs);
    public void RequestScrubEnd() => ScrubEnded?.Invoke();

    /// <summary>Raised while dragging on a waveform (item, offset into the item in ms).</summary>
    public event Action<ItemViewModel, double>? ScrubRequested;
    public event Action? ScrubEnded;

    internal void Notify(string message) => StatusMessage?.Invoke(message);
}
