using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.App.ViewModels;

public enum ImportAudioPhase
{
    /// <summary>Choosing the workers and options; nothing has run yet (or it failed).</summary>
    Options,

    /// <summary>Reading and listening to the file.</summary>
    Working,

    /// <summary>The preview: what the import would add. Structure options change it at once.</summary>
    Review,

    /// <summary>Adding it to the project (cutting the takes is the slow part).</summary>
    Importing,
}

/// <summary>
/// Import audio: a recording is listened to as a whole (kept in memory), turned into chapters, segments, pauses and speakers
/// for a preview that follows the options, and then added to the open project. Cancelling aborts everything and closes the window.
/// </summary>
public sealed partial class ImportAudioViewModel : ObservableObject, IDisposable
{
    private readonly ProjectSession _session;
    private readonly MainWindowViewModel? _player;
    private CancellationTokenSource _cts = new();
    private AudioSource? _source;
    private TranscriptResult? _transcript;
    private IReadOnlyList<TranscriptWord> _words = [];
    private AudioStructure? _structure;
    private Listeners? _listeners;
    private DispatcherTimer? _recomputeTimer;
    private bool _disposed;

    public ImportAudioViewModel(ProjectSession session, MainWindowViewModel? player, string file)
    {
        _session = session;
        _player = player;
        File = file;
        FileName = Path.GetFileName(file);

        var root = Path.Combine(AppHost.DataRoot.Root, "one-shot");
        string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        Hearer = new ListenerPicker(() => AppHost.Catalog.Transcribers.Where(w => w.CanTranscribe), root, Resolve, ListenOptionsChanged, preferred: "whisper");
        Aligner = new ListenerPicker(() => AppHost.Catalog.Transcribers.Where(w => w.AlignsText), root, Resolve, ListenOptionsChanged, preferred: "mms_fa");
        Diarizer = new ListenerPicker(() => AppHost.Catalog.All.Where(w => w.Diarizes), root, Resolve, ListenOptionsChanged, preferred: "pyannote");
        // The aligner makes pauses and chapter breaks sharper (and is cut to fit); on when there is one.
        _useAligner = Aligner.Workers.Count > 0;
        MaxSegmentChars = ProjectImport.MaxCharsFor(AppHost.Host!, session.Project, null);
    }

    public string File { get; }
    public string FileName { get; }

    public ListenerPicker Hearer { get; }
    public ListenerPicker Aligner { get; }
    public ListenerPicker Diarizer { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOptions), nameof(IsWorking), nameof(IsReview), nameof(IsImporting), nameof(CanAnalyze), nameof(CanImport), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ImportCommand))]
    private ImportAudioPhase _phase = ImportAudioPhase.Options;

    public bool IsOptions => Phase == ImportAudioPhase.Options;
    public bool IsWorking => Phase == ImportAudioPhase.Working;
    public bool IsReview => Phase == ImportAudioPhase.Review;
    public bool IsImporting => Phase == ImportAudioPhase.Importing;
    public bool IsBusy => Phase is ImportAudioPhase.Working or ImportAudioPhase.Importing;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _lengthWarning;

    // ------------------------------------------------------------------ listening options

    [ObservableProperty] private bool _useAligner;
    [ObservableProperty] private bool _useSpeakers;
    [ObservableProperty] private decimal? _voices;

    private void ListenOptionsChanged()
    {
        if (Phase == ImportAudioPhase.Review) IsStale = true;
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    partial void OnUseAlignerChanged(bool value) => ListenOptionsChanged();
    partial void OnUseSpeakersChanged(bool value) => ListenOptionsChanged();
    partial void OnVoicesChanged(decimal? value) => ListenOptionsChanged();

    /// <summary>The listening options changed since the preview was made: analyze again to see their effect.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanImport), nameof(StaleText))] [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _isStale;

    public string StaleText => IsStale ? "The listening options changed. Analyze again to update the preview." : "";

    public bool CanAnalyze => Phase is ImportAudioPhase.Options or ImportAudioPhase.Review && Hearer.Worker != null
                              && (!UseAligner || Aligner.Worker != null) && (!UseSpeakers || Diarizer.Worker != null);

    // ------------------------------------------------------------------ structure options (the preview follows them)

    [ObservableProperty] private bool _chaptersFromMetadata = true;
    [ObservableProperty] private bool _chaptersFromHeadings = true;
    [ObservableProperty] private bool _chaptersFromPauses;
    [ObservableProperty] private decimal _chapterPauseSec = 3;
    [ObservableProperty] private bool _speakHeadings = true;
    [ObservableProperty] private decimal _maxSegmentChars = 1500;
    [ObservableProperty] private decimal _insertPauseMs = 600;
    [ObservableProperty] private bool _keepAudio = true;
    [ObservableProperty] private bool _lockTakes = true;

    /// <summary>Whether the file has chapters of its own (known once it is read).</summary>
    [ObservableProperty] private bool _hasFileChapters;

    partial void OnChaptersFromMetadataChanged(bool value) => RecomputeSoon();
    partial void OnChaptersFromHeadingsChanged(bool value) => RecomputeSoon();
    partial void OnChaptersFromPausesChanged(bool value) => RecomputeSoon();
    partial void OnChapterPauseSecChanged(decimal value) => RecomputeSoon();
    partial void OnSpeakHeadingsChanged(bool value) => RecomputeSoon();
    partial void OnMaxSegmentCharsChanged(decimal value) => RecomputeSoon();
    partial void OnInsertPauseMsChanged(decimal value) => RecomputeSoon();
    partial void OnLockTakesChanged(bool value) => OnPropertyChanged(nameof(LockHint));

    public string LockHint => KeepAudio && LockTakes ? "Locked segments are skipped by Generate, so the recording stays." : "";

    // ------------------------------------------------------------------ analyzing

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task Analyze()
    {
        if (Hearer.Worker is not { } hearer) return;
        Error = null;
        IsStale = false;
        var problems = new[] { (Worker: hearer, Used: true), (Worker: UseAligner ? Aligner.Worker : null, Used: UseAligner), (Worker: UseSpeakers ? Diarizer.Worker : null, Used: UseSpeakers) }
            .Where(p => p.Used && p.Worker != null).Select(p => AppHost.WhyWorkerCannotRun(p.Worker!.Id)).OfType<string>().ToList();
        if (problems.Count > 0) { Error = string.Join(" ", problems); return; }

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Phase = ImportAudioPhase.Working;
        Progress = 0;
        ProgressText = "Starting…";
        var progress = new Progress<(double Fraction, string Message)>(r => { Progress = r.Fraction; ProgressText = r.Message; });
        try
        {
            if (_source == null || KeepAudio && _source.OriginalWav == null)
            {
                _source?.Dispose();
                _source = await AudioSource.PrepareAsync(AppHost.Host!, File, KeepAudio, progress, ct);
                HasFileChapters = _source.Chapters.Count > 0;
                if (!HasFileChapters) ChaptersFromMetadata = false;
            }
            LengthWarning = LengthWarningFor(_source.DurationMs, hearer, UseSpeakers ? Diarizer.Worker : null);

            _listeners = new Listeners(hearer, Hearer.Merged(), UseAligner ? Aligner.Worker : null, UseAligner ? Aligner.Merged() : new JsonObject(),
                UseSpeakers ? Diarizer.Worker : null, UseSpeakers ? Diarizer.Merged() : null);
            ProgressText = "Listening…";
            _transcript = await AudioAnalyzer.AnalyzeAsync(AppHost.Scheduler, _source, _listeners, UseSpeakers && Voices is > 0 ? (int)Voices.Value : null, progress, ct);
            _words = AlignWindows.UnitsOf(_transcript);
            if (_words.Count == 0)
                throw new InvalidDataException(string.IsNullOrWhiteSpace(_transcript.Text)
                    ? "Nothing was heard in the recording."
                    : $"{hearer.DisplayName} gives no word times, which the import needs. Choose a worker that times words, or add an aligner.");
            Recompute();
            Phase = ImportAudioPhase.Review;
        }
        catch (OperationCanceledException)
        {
            // Closing the window cancels: nothing to show.
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Error = ex.Message;
            Phase = ImportAudioPhase.Options;
        }
    }

    /// <summary>A warning when the file is within 90 % of what a worker takes in one go (it is still allowed to try).</summary>
    private static string? LengthWarningFor(int durationMs, params WorkerManifest?[] workers)
    {
        var minutes = durationMs / 60_000.0;
        var near = workers.OfType<WorkerManifest>().Where(w => w.MaxAudioMinutes is > 0 and var max && minutes >= max * 0.9)
            .Select(w => $"{w.DisplayName} takes up to {w.MaxAudioMinutes:0} minutes").ToList();
        return near.Count == 0 ? null : $"This recording is {minutes:0} minutes. {string.Join("; ", near)}. It may fail or be inaccurate: cut the file shorter, or try anyway.";
    }

    // ------------------------------------------------------------------ structure and preview

    public ObservableCollection<AudioChapterRow> Chapters { get; } = new();
    public ObservableCollection<AudioSegmentRow> Rows { get; } = new();
    public ObservableCollection<AudioSpeakerRow> Speakers { get; } = new();

    [ObservableProperty] private AudioChapterRow? _selectedChapter;
    [ObservableProperty] private string _summary = "";
    public bool HasSpeakers => Speakers.Count > 0;

    partial void OnSelectedChapterChanged(AudioChapterRow? value) => FillRows();

    private void RecomputeSoon()
    {
        if (Phase != ImportAudioPhase.Review) return;
        _recomputeTimer?.Stop();
        _recomputeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _recomputeTimer.Tick += (_, _) => { _recomputeTimer?.Stop(); if (!_disposed && Phase == ImportAudioPhase.Review) Recompute(); };
        _recomputeTimer.Start();
    }

    private AudioStructureOptions StructureOptions() => new()
    {
        ChaptersFromMetadata = ChaptersFromMetadata,
        ChaptersFromHeadings = ChaptersFromHeadings,
        ChaptersFromPauses = ChaptersFromPauses,
        ChapterPauseSec = (double)ChapterPauseSec,
        SpeakHeadings = SpeakHeadings,
        MaxSegmentChars = (int)MaxSegmentChars,
        InsertPauseMs = (int)InsertPauseMs,
        MaxCharsForSpeaker = label => ProjectImport.MaxCharsFor(AppHost.Host!, _session.Project, label),
    };

    private void Recompute()
    {
        if (_source == null) return;
        var structure = AudioStructurer.Build(_words, _source.Chapters, StructureOptions());
        _structure = structure;

        var selected = SelectedChapter?.Index ?? 0;
        Chapters.Clear();
        for (var i = 0; i < structure.Result.Chapters.Count; i++)
            Chapters.Add(new AudioChapterRow(i, structure.Result.Chapters[i], structure));
        SelectedChapter = Chapters.Count == 0 ? null : Chapters[Math.Min(selected, Chapters.Count - 1)];

        BuildSpeakers(structure);
        var minutes = structure.AudioMs / 60_000.0;
        Summary = $"{Chapters.Count} chapter(s), {structure.SpokenCount} segment(s), {structure.PauseCount} pause(s), " +
                  $"{structure.Result.SpeakerLabels.Count} voice(s), {minutes:0.#} minutes";
    }

    private void BuildSpeakers(AudioStructure structure)
    {
        var labels = structure.Result.SpeakerLabels;
        var old = Speakers.ToDictionary(s => s.Mapping.Label, StringComparer.OrdinalIgnoreCase);
        foreach (var s in Speakers) s.Mapping.PropertyChanged -= OnMappingChanged;
        Speakers.Clear();
        var existing = _session.Speakers.ToList();
        foreach (var label in labels)
        {
            var mapping = new LabelMappingViewModel(label, existing, labels.Where(l => l != label).ToList(), allowExisting: true);
            if (old.TryGetValue(label, out var previous)) mapping.CopyFrom(previous.Mapping);
            mapping.PropertyChanged += OnMappingChanged;
            var stats = structure.Speakers.First(x => x.Label == label);
            Speakers.Add(new AudioSpeakerRow(mapping, stats, PlayRange));
        }
        OnPropertyChanged(nameof(HasSpeakers));
    }

    private void OnMappingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => FillRows(rebuild: false);

    /// <summary>What a voice is called in the preview: the name typed, the existing speaker chosen, or its label.</summary>
    private string NameOf(string? label)
    {
        if (label == null) return _session.Project.FindSpeaker(_session.Project.Defaults.Speaker)?.DisplayName ?? "";
        var resolved = SpeakerMapping.Resolve(Speakers.Select(s => s.Mapping), label);
        var m = Speakers.FirstOrDefault(s => s.Mapping.Label.Equals(resolved, StringComparison.OrdinalIgnoreCase))?.Mapping;
        return m == null ? label : m.IsExisting && m.ExistingSpeaker != null ? m.ExistingSpeaker.Name : string.IsNullOrWhiteSpace(m.NewName) ? m.Label : m.NewName.Trim();
    }

    private void FillRows(bool rebuild = true)
    {
        if (_structure == null) return;
        if (!rebuild)
        {
            foreach (var row in Rows) row.Speaker = row.IsPause ? "" : NameOf(row.Label);
            return;
        }
        Rows.Clear();
        if (SelectedChapter == null) return;
        foreach (var item in _structure.Result.Chapters[SelectedChapter.Index].Items)
        {
            if (item.Kind == ItemKind.Pause) { Rows.Add(AudioSegmentRow.Pause(item.DurationMs ?? 0)); continue; }
            var span = _structure.Spans[item];
            Rows.Add(new AudioSegmentRow(span.StartMs, span.EndMs, item.SpeakerLabel, NameOf(item.SpeakerLabel), item.Text ?? "", item.IsHeading, PlayRange));
        }
    }

    private void PlayRange(int startMs, int endMs)
    {
        if (_source != null) _ = _player?.PlayFileAsync(_source.AnalysisWav, startMs, endMs);
    }

    [RelayCommand]
    private void StopPlayback() => _player?.StopPlayback();

    // ------------------------------------------------------------------ importing

    public bool CanImport => Phase == ImportAudioPhase.Review && !IsStale && _structure is { SpokenCount: > 0 };

    /// <summary>The first chapter that was added (for the window to select).</summary>
    public ChapterViewModel? FirstChapter { get; private set; }

    /// <summary>The window closes when the import is done (true) or the user cancels (false).</summary>
    public event Action<bool>? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        if (_structure is not { } structure || _source == null || _transcript == null || _listeners == null) return;
        Error = null;
        Phase = ImportAudioPhase.Importing;
        Progress = 0;
        ProgressText = "Adding to the project…";
        var progress = new Progress<(double Fraction, string Message)>(r => { Progress = r.Fraction; ProgressText = r.Message; });
        try
        {
            var map = SpeakerMapping.Apply(_session, Speakers.Select(s => s.Mapping).ToList());
            var imported = AudioImporter.ToChapters(structure, label => label == null ? _session.Project.Defaults.Speaker : map[label]);
            if (KeepAudio)
                await AudioImporter.WriteTakesAsync(_session.Engine, _source, imported.Segments, _listeners.Names, _transcript.Language, LockTakes, progress, _cts.Token);
            foreach (var chapter in imported.Chapters)
            {
                var added = _session.AddChapter(chapter.Title, null, chapter.Items);
                FirstChapter ??= added;
            }
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: the window closes.
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Error = "Import failed: " + ex.Message;
            Phase = ImportAudioPhase.Review;
        }
    }

    /// <summary>Aborts whatever is running and closes the window.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        CloseRequested?.Invoke(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recomputeTimer?.Stop();
        _cts.Cancel();
        _player?.StopPlayback();
        _source?.Dispose();
    }
}

public static class ImportAudioConverters
{
    /// <summary>A chapter heading is shown bold in the preview.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight> HeadingWeight =
        new(heading => heading ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);
}

/// <summary>A worker choice with its settings form (the same pattern as the One shot tabs), for the import's listening options.</summary>
public sealed partial class ListenerPicker : ObservableObject
{
    private readonly Dictionary<string, JsonObject> _values = new();
    private readonly string _root;
    private readonly Func<string, string> _resolve;
    private readonly Action _changed;

    public ListenerPicker(Func<IEnumerable<WorkerManifest>> candidates, string root, Func<string, string> resolve, Action changed, string preferred)
    {
        _root = root;
        _resolve = resolve;
        _changed = changed;
        foreach (var w in candidates().Where(w => AppHost.Environments.IsWorkerReady(w.Id))) Workers.Add(w);
        _worker = Workers.FirstOrDefault(w => w.Id == preferred) ?? Workers.FirstOrDefault();
        if (_worker != null) Settings = Build(_worker);
    }

    public ObservableCollection<WorkerManifest> Workers { get; } = new();
    public bool NoWorkerInstalled => Workers.Count == 0;
    public string WorkerInfo => Worker?.Description ?? "";

    [ObservableProperty] private WorkerManifest? _worker;
    [ObservableProperty] private SettingsEditorViewModel? _settings;

    partial void OnWorkerChanged(WorkerManifest? value)
    {
        OnPropertyChanged(nameof(WorkerInfo));
        Settings = value == null ? null : Build(value);
        _changed();
    }

    private JsonObject ValuesOf(WorkerManifest worker)
    {
        if (!_values.TryGetValue(worker.Id, out var values)) _values[worker.Id] = values = worker.DefaultSettings();
        return values;
    }

    private SettingsEditorViewModel Build(WorkerManifest worker)
    {
        var values = ValuesOf(worker);
        var definitions = worker.SettingsFor(values, _resolve);
        return new SettingsEditorViewModel(definitions, values, allowInherit: false, (key, value) =>
        {
            if (value == null) values.Remove(key); else values[key] = value.DeepClone();
            _changed();
        }, _root) { Inline = true };
    }

    /// <summary>The worker's settings as a job takes them.</summary>
    public JsonObject Merged() => Worker is { } w ? OneShot.MergeSettings(ValuesOf(w), null, w, _resolve) : new JsonObject();
}

/// <summary>A chapter in the preview list.</summary>
public sealed class AudioChapterRow
{
    public AudioChapterRow(int index, ImportedChapter chapter, AudioStructure structure)
    {
        Index = index;
        Title = chapter.Title;
        var spoken = chapter.Items.Where(i => structure.Spans.ContainsKey(i)).ToList();
        SegmentCount = spoken.Count;
        var ms = spoken.Count == 0 ? 0 : structure.Spans[spoken[^1]].EndMs - structure.Spans[spoken[0]].StartMs;
        Detail = $"{SegmentCount} segments · {TimeSpan.FromMilliseconds(ms):h\\:mm\\:ss}";
    }

    public int Index { get; }
    public string Title { get; }
    public int SegmentCount { get; }
    public string Detail { get; }
}

/// <summary>A segment or a pause in the preview, with its time and a button that plays that stretch of the original.</summary>
public sealed partial class AudioSegmentRow : ObservableObject
{
    private readonly Action<int, int>? _play;

    public AudioSegmentRow(int startMs, int endMs, string? label, string speaker, string text, bool isHeading, Action<int, int>? play)
    {
        StartMs = startMs;
        EndMs = endMs;
        Label = label;
        _speaker = speaker;
        Text = text;
        IsHeading = isHeading;
        _play = play;
        TimeText = TimeSpan.FromMilliseconds(startMs).ToString(startMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
    }

    public static AudioSegmentRow Pause(int ms) => new(0, 0, null, "", $"pause {ms / 1000.0:0.0} s", false, null) { IsPause = true, TimeText = "" };

    public int StartMs { get; }
    public int EndMs { get; }
    public string? Label { get; }
    public string Text { get; }
    public bool IsHeading { get; }
    public bool IsPause { get; private init; }
    public string TimeText { get; private init; }
    public bool IsSegment => !IsPause;

    [ObservableProperty] private string _speaker;

    [RelayCommand]
    private void Play() => _play?.Invoke(StartMs, EndMs);
}

/// <summary>A voice found in the recording: its mapping to a speaker, how much it speaks, and a stretch to listen to.</summary>
public sealed partial class AudioSpeakerRow(LabelMappingViewModel mapping, SpeakerStats stats, Action<int, int> play) : ObservableObject
{
    public LabelMappingViewModel Mapping { get; } = mapping;
    public string Detail => $"{stats.SpeechMs / 60_000.0:0.#} min of speech";

    [RelayCommand]
    private void Listen() => play(stats.ReferenceStartMs, stats.ReferenceEndMs);
}
