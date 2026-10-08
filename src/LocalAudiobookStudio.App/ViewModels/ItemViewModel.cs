using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

public enum ItemStatus
{
    Ready,
    NotGenerated,
    Queued,
    Generating,
    Stale,
    Failed,
}

/// <summary>
/// One row of the segment stack. Reads everything from the <see cref="TimelineItem"/> model;
/// edits go through <see cref="ProjectSession"/> so they are undoable and saved.
/// </summary>
public sealed partial class ItemViewModel : ObservableObject, Controls.IWaveformHost
{
    public ItemViewModel(ProjectSession session, ChapterViewModel chapter, TimelineItem model)
    {
        Session = session;
        Chapter = chapter;
        Model = model;
        _loadedText = _editText = SourceText;
    }

    public ProjectSession Session { get; }
    public ChapterViewModel Chapter { get; set; }
    public TimelineItem Model { get; }
    public Guid Id => Model.Id;

    // ------------------------------------------------------------------ kind and content

    public ItemKind Kind => Model.Kind;
    public bool IsTts => Kind == ItemKind.Tts;
    public bool IsClip => Kind == ItemKind.Clip;
    /// <summary>Play / regenerate / takes under the waveform; pauses and clips leave them out to keep rows compact.</summary>
    public bool ShowAudioControls => IsTts;
    public bool IsPause => Kind == ItemKind.Pause;
    /// <summary>A note in the book: editable text, never spoken, takes no time.</summary>
    public bool IsComment => Kind == ItemKind.Comment;
    public bool IsGenerated => Model.IsGenerated;
    public bool IsNonSpeech => Kind != ItemKind.Tts;
    public bool HasAudio => Model.HasAudio;
    /// <summary>Its number among the chapter's segments with audio (see <see cref="ChapterViewModel"/>); null for the others.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentNumberText), nameof(SegmentNumberTooltip))]
    private int? _segmentNumber;

    public string SegmentNumberText => SegmentNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string SegmentNumberTooltip => string.IsNullOrWhiteSpace(Model.ExportId)
        ? $"Segment {SegmentNumber} of this chapter (its number in a per-segment export)"
        : $"Segment {SegmentNumber} of this chapter; a per-segment export names it \"{Model.ExportId}\" (its Export ID)";

    /// <summary>Length and end time under the speaker; pauses and comments leave them out.</summary>
    public bool ShowTiming => Kind is not (ItemKind.Pause or ItemKind.Comment or ItemKind.Unknown);

    // ------------------------------------------------------------------ recording (speakers without a worker)

    /// <summary>The speaker has no worker: this segment is recorded with the microphone instead of generated.</summary>
    public bool IsRecordable => Session.IsRecorded(Model);
    /// <summary>The regenerate button; recorded segments get the record button instead.</summary>
    public bool ShowGenerate => IsGenerated && !IsRecordable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaveformArea))]
    private bool _isRecording;

    /// <summary>Smoothed peak level 0–1 for the VU meter while recording.</summary>
    [ObservableProperty] private double _recordLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordElapsedText))]
    private double _recordElapsedMs;

    public string RecordElapsedText => TimeSpan.FromMilliseconds(RecordElapsedMs).ToString(@"m\:ss\.f");

    /// <summary>The waveform box; while recording, the meter and timer take its place.</summary>
    public bool ShowWaveformArea => HasAudio && !IsRecording;

    [RelayCommand]
    private void Record() => Session.RequestRecordToggle(this);

    public string DisplayText => Model.DisplayText;

    public string KindLabel => Kind switch
    {
        ItemKind.Clip => "Clip",
        ItemKind.Pause => "Pause",
        ItemKind.Comment => "Comment",
        ItemKind.Unknown => "Unknown",
        _ => "Speech",
    };

    public Geometry? KindIcon => Kind switch
    {
        ItemKind.Clip => Icon("IconClip"),
        ItemKind.Pause => Icon("IconHourglass"),
        ItemKind.Comment => Icon("IconComment"),
        ItemKind.Unknown => Icon("IconWarning"),
        _ => null,
    };

    private static Geometry? Icon(string key) =>
        Avalonia.Application.Current!.TryFindResource(key, out var r) ? r as Geometry : null;

    public SpeakerViewModel? Speaker => IsTts ? Session.SpeakerVm(Model.Speaker) : null;
    public string SpeakerName => Speaker?.Name ?? (IsTts ? Model.Speaker ?? "?" : KindLabel);
    public IBrush SpeakerBrush => Speaker?.ColorBrush ?? Brushes.Gray;


    // ------------------------------------------------------------------ timing (set by the session after layout)

    [ObservableProperty] private double _startMs;
    [ObservableProperty] private bool _startIsEstimate;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _durationIsEstimate;
    [ObservableProperty] private double _chapterStartMs;

    public string StartText => TimeFormat.Hms(StartMs, StartIsEstimate);
    private bool EndIsEstimate => StartIsEstimate || DurationIsEstimate;
    public string EndText => TimeFormat.Hms(StartMs + DurationMs, EndIsEstimate);
    public string DurationText => (DurationIsEstimate ? "+~" : "+") + TimeFormat.Short(DurationMs);

    /// <summary>Estimated length while there is no audio (the waveform view maps its playhead onto it), else 0.</summary>
    public double PlaceholderDurationMs => DurationIsEstimate ? DurationMs : 0;

    public string StartTooltip =>
        $"Start (book): {TimeFormat.Hms(StartMs, StartIsEstimate)}\nStart (chapter): {TimeFormat.Hms(StartMs - ChapterStartMs, StartIsEstimate)}\n" +
        $"End (book): {EndText}\nEnd (chapter): {TimeFormat.Hms(StartMs + DurationMs - ChapterStartMs, EndIsEstimate)}\n" +
        $"Length: {TimeFormat.Short(DurationMs)}{(DurationIsEstimate ? " (estimated)" : "")}\nDrag to reorder";

    // ------------------------------------------------------------------ background

    /// <summary>Plays under the next section instead of taking its own time.</summary>
    public bool IsBackground => Model.IsBackground;

    /// <summary>Short row label, e.g. "Background · loop 30 s".</summary>
    public string BackgroundText
    {
        get
        {
            var limit = Model.BackgroundLimit ?? 0;
            var len = Model.EffectiveBackgroundLength switch
            {
                BackgroundLength.Seconds when limit > 0 => $"{limit:0.#} s",
                BackgroundLength.Sections when limit >= 1 => (int)limit == 1 ? "1 section" : $"{(int)limit} sections",
                BackgroundLength.ChapterEnd => "to chapter end",
                _ => "",
            };
            var loop = Model.BackgroundLoop == true && len.Length > 0;
            return "Background" + (len.Length > 0 ? " · " + (loop ? "loop " : "") + len : "");
        }
    }

    /// <summary>Shared column widths (bound by the row template).</summary>
    public StackColumns Columns => StackColumns.Shared;

    partial void OnStartMsChanged(double value) => RaiseTiming();
    partial void OnStartIsEstimateChanged(bool value) => RaiseTiming();
    partial void OnDurationMsChanged(double value) => RaiseTiming();
    partial void OnDurationIsEstimateChanged(bool value) => RaiseTiming();
    partial void OnChapterStartMsChanged(double value) => OnPropertyChanged(nameof(StartTooltip));

    private void RaiseTiming()
    {
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlaceholderDurationMs));
        OnPropertyChanged(nameof(StartTooltip));
    }

    public void ApplyTiming(in ItemTiming t, double chapterStart)
    {
        StartMs = t.StartMs;
        StartIsEstimate = t.StartIsEstimate;
        DurationMs = t.DurationMs;
        DurationIsEstimate = t.IsEstimate;
        ChapterStartMs = chapterStart;
    }

    // ------------------------------------------------------------------ status

    [ObservableProperty] private ItemStatus _status = ItemStatus.NotGenerated;
    [ObservableProperty] private int _queuePosition;
    [ObservableProperty] private double _progress;

    public bool IsLocked => Model.Locked;
    public string? ErrorText => Model.LastError;

    public string StatusText => Status switch
    {
        ItemStatus.NotGenerated => IsClip ? "clip missing" : IsRecordable ? "not recorded" : "not generated",
        ItemStatus.Queued => QueuePosition > 0 ? $"queued #{QueuePosition}" : "queued",
        ItemStatus.Generating => $"generating {Progress:P0}",
        ItemStatus.Stale => "stale",
        ItemStatus.Failed => "failed",
        _ => "",
    };

    public string StatusTooltip => Status switch
    {
        ItemStatus.Failed => "Generation failed:\n" + (Model.LastError ?? "unknown error"),
        ItemStatus.Stale => IsRecordable
            ? "The text or speaker changed since this was recorded. Record again (R) to update."
            : "The text, speaker or settings changed since this take was generated. Regenerate (R) to update.",
        ItemStatus.NotGenerated => IsClip ? "The clip file is missing."
            : IsRecordable ? "Not recorded yet. Press the record button (or R) to record it." : "Not generated yet. Press R to generate.",
        ItemStatus.Queued => "Waiting for a free worker.",
        ItemStatus.Generating => "Generating…",
        _ => "",
    };

    public bool IsBusy => Status is ItemStatus.Queued or ItemStatus.Generating;
    public bool IsStale => Status == ItemStatus.Stale;
    public bool IsFailed => Status == ItemStatus.Failed;
    public bool IsGenerating => Status == ItemStatus.Generating;
    public bool IsQueued => Status == ItemStatus.Queued;
    public bool ShowStatusOverlay => Status is not ItemStatus.Ready && !(Status == ItemStatus.Stale && HasWaveform) && !IsPause;

    partial void OnStatusChanged(ItemStatus value) => RaiseStatus();
    partial void OnQueuePositionChanged(int value) => OnPropertyChanged(nameof(StatusText));
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StatusText));

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTooltip));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsGenerating));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(ShowStatusOverlay));
    }

    /// <summary>Recomputes the status from the model, the take index and the job queue.</summary>
    public void UpdateStatus((JobState state, int position, double progress)? job)
    {
        if (job is { } j)
        {
            QueuePosition = j.position;
            Progress = j.progress;
            Status = j.state == JobState.Running ? ItemStatus.Generating : ItemStatus.Queued;
            return;
        }
        QueuePosition = 0;
        Progress = 0;
        Status = ComputeStatus();
    }

    private ItemStatus ComputeStatus() => Session.Engine.StateOf(Model) switch
    {
        SegmentAudioState.Ready => ItemStatus.Ready,
        SegmentAudioState.Stale => ItemStatus.Stale,
        SegmentAudioState.Failed => ItemStatus.Failed,
        _ => ItemStatus.NotGenerated,
    };

    // ------------------------------------------------------------------ takes

    public IReadOnlyList<TakeInfo> Takes => Session.Takes.Get(Id);
    public TakeInfo? SelectedTakeInfo => Session.Takes.Selected(Model);
    public bool HasTakes => Takes.Count > 0;
    public bool HasMultipleTakes => Takes.Count > 1;

    public string TakeCounterText
    {
        get
        {
            var takes = Takes;
            if (takes.Count == 0) return "";
            var idx = takes.ToList().FindIndex(t => t.Take == Model.SelectedTake);
            return idx < 0 ? $"–/{takes.Count}" : $"{idx + 1}/{takes.Count}";
        }
    }

    // ------------------------------------------------------------------ waveform

    private Peaks? _peaks;
    private bool _peaksRequested;

    /// <summary>
    /// Waveform peaks. The first read (by a visible row's binding) starts a background load, so only
    /// rows that are actually on screen ever load peaks.
    /// </summary>
    public Peaks? Peaks
    {
        get
        {
            if (!_peaksRequested)
            {
                _peaksRequested = true;
                EnsurePeaks();
            }
            return _peaks;
        }
        private set
        {
            if (ReferenceEquals(_peaks, value)) return;
            _peaks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWaveform));
            OnPropertyChanged(nameof(ShowStatusOverlay));
        }
    }

    /// <summary>Playhead position relative to the item's (trimmed) start, or -1 when not playing here.</summary>
    [ObservableProperty] private double _playheadMs = -1;

    [ObservableProperty] private bool _isPlaying;

    public bool HasWaveform => _peaks != null;

    public AudioSourceInfo? Source => Session.Takes.SourceOf(Model);
    public double RawDurationMs => Source?.RawDurationMs ?? 0;
    public double TrimStartMs => Source is { } s ? ProjectMixPlanBuilder.Trims(Model, s).start : 0;
    public double TrimEndMs => Source is { } s ? ProjectMixPlanBuilder.Trims(Model, s).end : 0;

    /// <summary>Total playback gain (auto-normalization + item gain) for drawing the waveform height.</summary>
    public double EffectiveGainDb => Source is { } s
        ? ProjectMixPlanBuilder.EffectiveGainDb(Model, s, Session.Project.Defaults.TargetLoudnessLufs)
        : Model.GainDb;

    private string? _peaksPathLoaded;

    /// <summary>Called when the row becomes visible: loads peaks lazily in the background.</summary>
    public async void EnsurePeaks()
    {
        var path = Session.Takes.PeaksPathOf(Model);
        if (path == null)
        {
            Peaks = null;
            _peaksPathLoaded = null;
            return;
        }
        if (path == _peaksPathLoaded && _peaks != null) return;
        _peaksPathLoaded = path;
        var cached = Session.PeaksCache.TryGet(path);
        if (cached != null) { Peaks = cached; return; }
        var p = await Session.PeaksCache.LoadAsync(path, Session.Takes.WavPathOf(Model));
        if (_peaksPathLoaded == path) Peaks = p;
    }

    // ------------------------------------------------------------------ text editing
    //
    // The row's text box always shows EditText. It follows the model until the user types;
    // then it keeps the user's text until they save (CommitEdit) or discard (CancelEdit).

    /// <summary>A segment's text and comments; pauses and clips show a read-only description.</summary>
    public bool CanEditText => IsTts || IsComment;

    /// <summary>The row's text box is read-only for pauses and clips, and while the section is locked.</summary>
    public bool IsTextReadOnly => !CanEditText || IsLocked;
    private string SourceText => CanEditText ? Model.Text ?? "" : Model.DisplayText;

    private string _loadedText = "";
    [ObservableProperty] private string _editText = "";

    public bool IsDirty => CanEditText && EditText != _loadedText;

    partial void OnEditTextChanged(string value) => OnPropertyChanged(nameof(IsDirty));

    private void SyncEditText()
    {
        var source = SourceText;
        if (EditText == _loadedText) EditText = source;
        _loadedText = source;
        OnPropertyChanged(nameof(IsDirty));
    }

    public void CommitEdit()
    {
        if (!IsDirty) return;
        var text = EditText.Trim();
        EditText = _loadedText;
        if (CanEditText && text != (Model.Text ?? "")) Session.SetText(this, text);
    }

    public void CancelEdit() => EditText = _loadedText;

    // ------------------------------------------------------------------ row buttons

    [RelayCommand]
    private void Play()
    {
        // The row's play button is a stop button while this segment plays.
        if (IsPlaying) Session.RequestStopPlayback();
        else Session.RequestPlayItem(this, 0);
    }

    [RelayCommand]
    private void Regenerate() => Session.Generate([this], JobPriority.High, skipLocked: false);

    [RelayCommand]
    private void PreviousTake() => Session.StepTake(this, -1);

    [RelayCommand]
    private void NextTake() => Session.StepTake(this, +1);

    [RelayCommand]
    private async Task DeleteTake()
    {
        if (SelectedTakeInfo is { } take) await ConfirmDeleteTakeAsync(take.Take);
    }

    /// <summary>Deletes one take after confirmation (row trash button, inspector take cards).</summary>
    public async Task ConfirmDeleteTakeAsync(int take)
    {
        if (Session.RefuseIfLocked([this])) return;
        if (!await Services.Dialogs.Confirm("Delete take",
                $"Delete take {take} of this segment?\n\nIts audio file is removed from disk. This can't be undone.", "Delete"))
            return;
        try
        {
            Session.DeleteTake(this, take);
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ShowError("Delete take", "The take could not be deleted:\n" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ waveform interaction

    void Controls.IWaveformHost.WaveformSeek(double relativeMs) => Session.RequestPlayItem(this, relativeMs);
    void Controls.IWaveformHost.WaveformScrub(double relativeMs) => Session.RequestScrub(this, relativeMs);
    void Controls.IWaveformHost.WaveformScrubEnd() => Session.RequestScrubEnd();

    void Controls.IWaveformHost.WaveformTrim(double trimStartMs, double trimEndMs) =>
        Session.SetTrims(this, (int)Math.Round(trimStartMs), (int)Math.Round(trimEndMs));

    // ------------------------------------------------------------------ refresh

    /// <summary>Raises change notifications for everything derived from the model.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(SegmentNumberTooltip)); // the Export ID may have changed
        OnPropertyChanged(nameof(Speaker));
        OnPropertyChanged(nameof(IsRecordable));
        OnPropertyChanged(nameof(ShowGenerate));
        OnPropertyChanged(nameof(SpeakerName));
        OnPropertyChanged(nameof(SpeakerBrush));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsTextReadOnly));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(IsBackground));
        OnPropertyChanged(nameof(BackgroundText));
        SyncEditText();
        RefreshAudio();
        RaiseStatus();
    }

    /// <summary>Take selection, trims or gain changed.</summary>
    public void RefreshAudio()
    {
        OnPropertyChanged(nameof(Takes));
        OnPropertyChanged(nameof(SelectedTakeInfo));
        OnPropertyChanged(nameof(HasTakes));
        OnPropertyChanged(nameof(HasMultipleTakes));
        OnPropertyChanged(nameof(TakeCounterText));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(RawDurationMs));
        OnPropertyChanged(nameof(TrimStartMs));
        OnPropertyChanged(nameof(TrimEndMs));
        OnPropertyChanged(nameof(EffectiveGainDb));
        if (_peaksRequested) EnsurePeaks();
    }

    public override string ToString() => DisplayText;
}
