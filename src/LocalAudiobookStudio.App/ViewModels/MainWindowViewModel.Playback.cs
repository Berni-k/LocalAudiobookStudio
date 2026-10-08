using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Controls;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.App.Devices;
using LocalAudiobookStudio.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Transport: play/pause, seeking and the playhead clock. The playing item is always scrolled
/// into view (follow-playhead is always on).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly DispatcherTimer _clock;
    private PlaybackEngine? _engine;
    private readonly HashSet<ItemViewModel> _playheadItems = new();
    private ItemViewModel? _currentPlayingItem;
    private ChapterViewModel? _currentPlayingChapter;
    private IReadOnlyList<ItemViewModel>? _selectionAtPause;
    private bool _wasPlayingBeforeScrub;

    /// <summary>Paused (not stopped): the green "playing" highlight stays visible.</summary>
    private bool _paused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    private double _totalMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    private bool _totalIsEstimate;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _volume = 0.9;
    [ObservableProperty] private string _playbackNote = "";
    [ObservableProperty] private bool _playbackNoteIsWarning;
    [ObservableProperty] private bool _playbackNoteIsError;
    [ObservableProperty] private bool _playbackNoteIsPlaying;
    [ObservableProperty] private string _currentSpeakerName = "";
    [ObservableProperty] private Avalonia.Media.IBrush? _currentSpeakerBrush;

    /// <summary>
    /// The small status row under the time counter: playing/paused (or what is wrong with the
    /// segment at the playhead) on the left, the segment's speaker on the right.
    /// </summary>
    private void UpdatePlaybackNote(ItemViewModel? item)
    {
        var active = IsPlaying || _paused;
        string note;
        bool warn = false, error = false;
        if (!active || item == null) note = "";
        else if (item.HasAudio && item.Status == ItemStatus.Stale) { note = "stale take"; warn = true; }
        else if (item.HasAudio && item.Status == ItemStatus.Failed) { note = "failed"; error = true; }
        else if (item.HasAudio && item.DurationIsEstimate) { note = "not generated"; error = true; }
        else note = IsPlaying ? "playing" : "paused";
        PlaybackNote = note;
        PlaybackNoteIsWarning = warn;
        PlaybackNoteIsError = error;
        PlaybackNoteIsPlaying = IsPlaying && !warn && !error;

        if (!active || item == null)
        {
            CurrentSpeakerName = "";
            CurrentSpeakerBrush = null;
        }
        else
        {
            CurrentSpeakerName = item.IsTts ? item.SpeakerName : item.KindLabel;
            CurrentSpeakerBrush = item.IsTts ? item.SpeakerBrush : Avalonia.Media.Brushes.Gray;
        }
    }

    [ObservableProperty] private double[]? _chapterStarts;

    // ------------------------------------------------------------------ bottom timeline
    // By default the scrub bar spans the current chapter (the playing one, else the selected one)
    // with a tick per segment; "Book" switches it to the whole book with a tick per chapter.

    [ObservableProperty] private bool _showWholeBook;
    [ObservableProperty] private double _timelineStartMs;
    [ObservableProperty] private double _timelineEndMs;
    [ObservableProperty] private double[]? _timelineMarks;
    [ObservableProperty] private IReadOnlyList<string>? _timelineMarkTitles;
    [ObservableProperty] private TimelineSegment[]? _timelineSegments;
    private bool _timelineIsEstimate;
    private (ChapterViewModel? chapter, long layoutVersion, bool book) _timelineKey;

    /// <summary>Position within the shown range (chapter-relative in chapter mode).</summary>
    public string PositionText => TimeFormat.Hms(Math.Max(0, PositionMs - TimelineStartMs));

    /// <summary>Length of the shown range: the chapter, or the whole book.</summary>
    public string TotalText => TimeFormat.Hms(Math.Max(0, TimelineEndMs - TimelineStartMs), _timelineIsEstimate);

    public string TimelineTooltip => ShowWholeBook
        ? "Whole book (ticks mark chapters). Click to show only the current chapter."
        : "Current chapter (ticks mark segments). Click to show the whole book.";

    /// <summary>Label of the toggle: the range the scrub bar currently shows.</summary>
    public string TimelineModeText => ShowWholeBook ? "Book" : "Chapter";

    partial void OnShowWholeBookChanged(bool value)
    {
        if (IsPlaying && _engine?.Mode == PlaybackMode.Timeline) _engine.SetStopAt(ChapterStopFor(PositionMs));
        OnPropertyChanged(nameof(TimelineTooltip));
        OnPropertyChanged(nameof(TimelineModeText));
        UpdateTimelineRange();
    }

    partial void OnTimelineStartMsChanged(double value) { OnPropertyChanged(nameof(PositionText)); OnPropertyChanged(nameof(TotalText)); }
    partial void OnTimelineEndMsChanged(double value) => OnPropertyChanged(nameof(TotalText));

    /// <summary>Recomputes the scrub bar's range and tick marks (cheap when nothing changed).</summary>
    private void UpdateTimelineRange()
    {
        var layout = Session?.Layout ?? TimelineLayout.Empty;
        var chapter = ShowWholeBook ? null : (IsPlaying ? _currentPlayingChapter : null) ?? SelectedChapter;
        var key = (chapter, layout.Version, ShowWholeBook);
        if (key == _timelineKey && TimelineMarks != null) return;
        _timelineKey = key;

        if (ShowWholeBook || chapter == null || Session == null)
        {
            TimelineStartMs = 0;
            TimelineEndMs = layout.TotalMs;
            _timelineIsEstimate = layout.HasEstimates;
            TimelineMarks = layout.ChapterStarts;
            TimelineMarkTitles = Session?.Chapters.Select(c => $"{c.Number}. {c.Title}").ToList() ?? [];
            TimelineSegments = Session == null ? [] : MergeRuns(BuildTimelineSegments(Session.AllItemVms()));
        }
        else
        {
            TimelineStartMs = chapter.StartMs;
            TimelineEndMs = chapter.StartMs + chapter.DurationMs;
            _timelineIsEstimate = chapter.DurationIsEstimate;
            TimelineMarks = chapter.Items.Select(i => i.StartMs).ToArray();
            TimelineSegments = BuildTimelineSegments(chapter.Items);
            TimelineMarkTitles = chapter.Items.Select(i =>
            {
                var t = i.IsTts ? $"{i.SpeakerName}: {i.DisplayText}" : $"[{i.KindLabel}] {i.DisplayText}";
                return t.Length > 80 ? t[..77] + "…" : t;
            }).ToList();
        }
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(TotalText));
    }

    [RelayCommand]
    private void ToggleWholeBook() => ShowWholeBook = !ShowWholeBook;

    /// <summary>
    /// In chapter mode, book playback stops at the end of the chapter's last segment; in book
    /// mode it runs on (null).
    /// </summary>
    private double? ChapterStopFor(double ms)
    {
        if (ShowWholeBook || Session == null) return null;
        var layout = Session.Layout;
        if (layout.ChapterStarts.Length == 0) return null;
        var ci = layout.ChapterAt(ms);
        if (ci < 0 || ci >= Session.Chapters.Count) return null;
        var items = Session.Chapters[ci].Items;
        if (items.Count == 0) return null;
        return items.Max(i => i.StartMs + i.DurationMs);
    }

    /// <summary>
    /// Book mode: thousands of segments are far narrower than a pixel, so neighbouring segments
    /// with the same status are merged into one run (speaker colors aren't drawn there).
    /// </summary>
    private static TimelineSegment[] MergeRuns(TimelineSegment[] segments)
    {
        var runs = new List<TimelineSegment>();
        foreach (var seg in segments)
        {
            if (runs.Count > 0 && runs[^1].State == seg.State)
                runs[^1] = runs[^1] with { EndMs = Math.Max(runs[^1].EndMs, seg.EndMs) };
            else
                runs.Add(seg with { SpeakerColor = null });
        }
        return runs.ToArray();
    }

    /// <summary>Per-segment colors for the scrub bar (status line and speaker band).</summary>
    private TimelineSegment[] BuildTimelineSegments(IEnumerable<ItemViewModel> items)
    {
        var list = new List<TimelineSegment>();
        foreach (var i in items)
        {
            if (i.DurationMs <= 0) continue;
            var status = i.Status switch
            {
                ItemStatus.Ready => SegmentState.Ready,
                ItemStatus.Stale => SegmentState.Stale,
                ItemStatus.NotGenerated or ItemStatus.Failed => SegmentState.Missing,
                // Queued or generating: show what is there right now.
                _ => i.SelectedTakeInfo == null ? SegmentState.Missing
                    : Session!.IsStale(i.Model) ? SegmentState.Stale : SegmentState.Ready,
            };
            if (i.IsPause) status = SegmentState.Ready;
            var color = i.IsTts && i.Speaker != null ? SpeakerViewModel.ParseColor(i.Speaker.Model.Color) : (Avalonia.Media.Color?)null;
            list.Add(new TimelineSegment(i.StartMs, i.StartMs + i.DurationMs, status, color));
        }
        return list.ToArray();
    }

    partial void OnVolumeChanged(double value)
    {
        if (_engine != null) _engine.Volume = (float)value;
        AppHost.Settings.Ui.Volume = value;
    }

    private void CreateEngine(ProjectSession session)
    {
        _engine?.Dispose();
        _engine = new PlaybackEngine(session.AudioCache) { DeviceId = AppHost.Settings.AudioOutputDevice, Volume = (float)Volume };
        _engine.Ended += () => Dispatcher.UIThread.Post(OnPlaybackEnded);
        _engine.DeviceError += ex => Dispatcher.UIThread.Post(() =>
        {
            AppHost.Log.LogError(ex, "Audio device error");
            StatusText = "Audio device error: " + ex.Message;
            StopPlayback();
        });
    }

    private void OnLayoutChanged()
    {
        if (Session == null) return;
        TotalMs = Session.Layout.TotalMs;
        TotalIsEstimate = Session.Layout.HasEstimates;
        ChapterStarts = Session.Layout.ChapterStarts;
        OnPropertyChanged(nameof(ChapterTitles));
        UpdateTimelineRange();
        _engine?.UpdatePlan(Session.PlaybackPlan);
        Inspector.RefreshTiming();
    }

    // ================================================================== commands

    [RelayCommand]
    private void PlayPause()
    {
        if (Session == null || _engine == null) return;
        if (IsPlaying)
        {
            _engine.Pause();
            IsPlaying = false;
            _paused = true;
            ClearAssetPlayback();
            _clock.Stop();
            UpdatePlaybackNote(_currentPlayingItem);
            _selectionAtPause = SelectedItems;
            return;
        }
        // Continue from the paused/seeked position if the selection hasn't changed since;
        // otherwise start at the selected item.
        if (ReferenceEquals(_selectionAtPause, SelectedItems) || SelectedItems.Count == 0)
        {
            PlayTimeline(PositionMs >= TotalMs - 1 ? 0 : PositionMs);
            return;
        }
        PlayTimeline(SelectedItems.Min(i => i.StartMs));
    }

    [RelayCommand]
    private void PlaySelectedOnly()
    {
        if (Session == null || _engine == null || SelectedItems.Count == 0) return;
        var item = SelectedItems[0];
        PlayItemOnly(item);
    }

    /// <summary>Plays one item alone (Ctrl+Space, takes panel audition).</summary>
    public void PlayItemOnly(ItemViewModel item, int? take = null)
    {
        if (Session == null || _engine == null) return;
        var plan = SingleItemPlan(item, take);
        if (plan == null) { StatusText = "This item has no audio yet."; return; }
        _engine.Play(plan, 0, plan.TotalMs, PlaybackMode.SingleItem);
        _singleItemOffset = item.StartMs;
        _singleItem = item;
        StartClock();
    }

    private double _singleItemOffset;
    private ItemViewModel? _singleItem;

    /// <summary>A plan containing just one item (optionally a different take) starting at 0.</summary>
    private MixPlan? SingleItemPlan(ItemViewModel item, int? take)
    {
        if (Session == null) return null;
        var src = Session.Takes.SourceOf(item.Model);
        if (take is int t && Session.Takes.Find(item.Id, t) is { } info)
            src = new AudioSourceInfo(Session.Paths.TakeAudio(item.Id, info), info.DurationMs, info.LoudnessLufs, info.TrimStartMs, info.TrimEndMs);
        if (src == null) return null;
        var (ts, te) = take == null ? ProjectMixPlanBuilder.Trims(item.Model, src) : (src.DefaultTrimStartMs, src.DefaultTrimEndMs);
        var len = TimelineCalculator.TrimmedMs(src.RawDurationMs, ts, te);
        var gain = ProjectMixPlanBuilder.EffectiveGainDb(item.Model, src, Session.Project.Defaults.TargetLoudnessLufs);
        return new MixPlan([new MixItem(item.Id, 0, len, src.Path, ts, gain, item.Model.FadeInMs ?? 0, item.Model.FadeOutMs ?? 0)], len);
    }

    /// <summary>Plays an audio file on its own (assets, voice references, speaker previews): WAV, Opus or MP3.</summary>
    public Task PlayFileAsync(string path)
    {
        if (_engine == null) return Task.CompletedTask;
        try
        {
            var info = Audio.Decoding.AudioDecoder.ReadInfo(path);
            var plan = new MixPlan([new MixItem(Guid.Empty, 0, info.DurationMs, path, 0, 0, 0, 0)], info.DurationMs);
            _engine.Play(plan, 0, plan.TotalMs, PlaybackMode.Preview);
            _singleItem = null;
            StartClock();
        }
        catch (Exception ex)
        {
            StatusText = "Cannot play file: " + ex.Message;
        }
        return Task.CompletedTask;
    }

    /// <summary>A/B comparison: plays the previous take, then the selected take.</summary>
    public void PlayAB(ItemViewModel item, int takeA, int takeB)
    {
        if (Session == null || _engine == null) return;
        var a = SingleItemPlan(item, takeA);
        var b = SingleItemPlan(item, takeB);
        if (a == null || b == null) return;
        const double gap = 600;
        var ai = a.Items[0];
        var bi = b.Items[0] with { StartMs = ai.DurationMs + gap };
        var plan = new MixPlan([ai, bi], bi.EndMs);
        _engine.Play(plan, 0, plan.TotalMs, PlaybackMode.Preview);
        _singleItem = null;
        StatusText = $"A/B: take {takeA}, then take {takeB}";
        StartClock();
    }

    public void PlayTimeline(double fromMs)
    {
        if (Session == null || _engine == null) return;
        try
        {
            fromMs = Math.Max(0, fromMs);
            _engine.Play(Session.PlaybackPlan, fromMs, ChapterStopFor(fromMs), PlaybackMode.Timeline);
            _singleItem = null;
            StartClock();
        }
        catch (Exception ex)
        {
            StatusText = "Cannot start playback: " + ex.Message;
        }
    }

    [RelayCommand]
    public void StopPlayback()
    {
        _paused = false;
        _engine?.Stop();
        IsPlaying = false;
        _clock.Stop();
        ClearPlayhead();
        ClearAssetPlayback();
    }

    [RelayCommand]
    private void PreviousItem() => JumpItem(-1);

    [RelayCommand]
    private void NextItem() => JumpItem(+1);

    private void JumpItem(int delta)
    {
        if (Session == null) return;
        var layout = Session.Layout;
        if (layout.Items.Length == 0) return;
        var idx = layout.ItemAt(PositionMs + (delta < 0 ? -1500 : 0));
        idx = Math.Clamp(idx + (delta > 0 ? 1 : 0), 0, layout.Items.Length - 1);
        var target = layout.Items[idx];
        SeekTo(target.StartMs);
        if (Session.ItemVm(target.ItemId) is { } vm) SelectAndReveal(vm);
    }

    /// <summary>
    /// The bottom scrub bar. While playing it seeks. While stopped, scrubbing also selects the
    /// segment under the cursor and scrolls it into view.
    /// </summary>
    public void ScrubTimeline(double ms)
    {
        var wasPlaying = IsPlaying;
        SeekTo(ms);
        if (wasPlaying || Session == null) return;
        // Selecting (and possibly switching chapter) on every mouse move is expensive; apply it
        // once the cursor rests for a moment.
        _scrubSelectMs = ms;
        _scrubSelectTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) => ApplyScrubSelection());
        _scrubSelectTimer.Stop();
        _scrubSelectTimer.Start();
    }

    private DispatcherTimer? _scrubSelectTimer;
    private double _scrubSelectMs;

    private void ApplyScrubSelection()
    {
        _scrubSelectTimer?.Stop();
        if (Session == null || IsPlaying) return;
        var idx = Session.Layout.ItemAt(_scrubSelectMs);
        if (idx < 0 || Session.ItemVm(Session.Layout.Items[idx].ItemId) is not { } item) return;
        if (SelectedItems.Count != 1 || !ReferenceEquals(SelectedItems[0], item)) SelectAndReveal(item);
        _selectionAtPause = SelectedItems; // Space continues from the scrubbed position
    }

    /// <summary>
    /// The user picked a chapter in the chapter list: show it, put the playhead at its start, and
    /// if playing, continue playing from there.
    /// </summary>
    public void GoToChapter(ChapterViewModel chapter)
    {
        if (Session == null) return;
        if (!ReferenceEquals(SelectedChapter, chapter)) SelectedChapter = chapter;
        if (chapter.Items.Count > 0) ScrollIntoViewRequested?.Invoke(chapter.Items[0]);
        if (IsPlaying) PlayTimeline(chapter.StartMs);
        else SeekTo(chapter.StartMs);
        UpdateTimelineRange();
    }

    /// <summary>Seek from the scrub bar (or keyboard). Keeps playing if playing.</summary>
    public void SeekTo(double ms)
    {
        if (Session == null || _engine == null) return;
        ms = Math.Clamp(ms, 0, Math.Max(0, TotalMs));
        PositionMs = ms;
        if (IsPlaying && _engine.Mode == PlaybackMode.Timeline)
        {
            _engine.Seek(ms);
            _engine.SetStopAt(ChapterStopFor(ms));
        }
        else if (IsPlaying) PlayTimeline(ms); // seeking during segment/preview playback continues through the book
        else
        {
            if (_engine.Mode != PlaybackMode.Timeline) _engine.Stop();
            _selectionAtPause = SelectedItems; // Space continues from here
            _engine.Seek(ms);
        }
        // Green highlight only while playing or paused; when stopped, seeking shows nothing.
        if (IsPlaying || _paused) UpdatePlayhead(ms);
        else ClearPlayhead();
    }

    /// <summary>
    /// Clicking a waveform (or a row's play button) plays from that point to the end of that
    /// segment only. The mix is used, so audio overlapping the segment is heard too.
    /// The transport's Play button is what plays on through the book.
    /// </summary>
    private void OnPlayItemRequested(ItemViewModel item, double offsetMs)
    {
        if (Session == null || _engine == null) return;
        var from = item.StartMs + Math.Clamp(offsetMs, 0, Math.Max(0, item.DurationMs));
        // While the book is playing (transport Play), a click just moves the playhead.
        if (IsPlaying && _engine.Mode == PlaybackMode.Timeline)
        {
            SeekTo(from);
            return;
        }
        var end = item.StartMs + item.DurationMs;
        if (end - from < 1) from = item.StartMs;
        try
        {
            _engine.Play(Session.PlaybackPlan, from, end, PlaybackMode.Section);
            _singleItem = null;
            StartClock();
        }
        catch (Exception ex)
        {
            StatusText = "Cannot start playback: " + ex.Message;
        }
    }

    private void OnScrubRequested(ItemViewModel item, double offsetMs)
    {
        if (Session == null || _engine == null) return;
        if (!_clock.IsEnabled) _wasPlayingBeforeScrub = false;
        else if (_engine.Mode == PlaybackMode.Timeline) _wasPlayingBeforeScrub = true;
        var ms = item.StartMs + offsetMs;
        ClearAssetPlayback();
        _engine.ScrubGrain(Session.PlaybackPlan, ms);
        PositionMs = ms;
        UpdatePlayhead(ms);
    }

    private void OnScrubEnded()
    {
        if (_wasPlayingBeforeScrub) PlayTimeline(PositionMs);
        else { _engine?.Stop(); IsPlaying = false; _clock.Stop(); }
        _wasPlayingBeforeScrub = false;
    }

    private void StartClock()
    {
        ClearAssetPlayback();
        _paused = false;
        IsPlaying = true;
        _clock.Start();
        UpdatePlaybackNote(_currentPlayingItem);
    }

    private void OnPlaybackEnded()
    {
        if (_engine?.Mode == PlaybackMode.Scrub) return;
        ClearAssetPlayback();
        IsPlaying = false;
        _paused = false;
        _clock.Stop();
        if (_engine?.Mode == PlaybackMode.Timeline && ShowWholeBook) PositionMs = TotalMs;
        ClearPlayhead();
    }

    private void OnClockTick()
    {
        if (_engine == null || Session == null) return;
        var pos = _engine.PositionMs;
        switch (_engine.Mode)
        {
            case PlaybackMode.Timeline:
            case PlaybackMode.Section:
            case PlaybackMode.Scrub:
                PositionMs = pos;
                UpdatePlayhead(pos);
                break;
            case PlaybackMode.SingleItem when _singleItem != null:
                PositionMs = _singleItemOffset + pos;
                SetPlayheadItems([(_singleItem, pos)]);
                break;
        }
    }

    // ================================================================== playhead display

    private void UpdatePlayhead(double pos)
    {
        if (Session == null) return;
        var layout = Session.Layout;
        var list = new List<(ItemViewModel, double)>();
        foreach (var idx in layout.Overlapping(pos, pos + 1))
        {
            var t = layout.Items[idx];
            if (Session.ItemVm(t.ItemId) is not { } vm) continue;
            var rel = pos - t.StartMs;
            // A looping background repeats its (trimmed) audio: wrap the playhead back to the start each round.
            if (vm.Model is { Background: true, BackgroundLoop: true } && Session.DurationOf(vm.Model).Ms is var period and > 0)
                rel %= period;
            list.Add((vm, rel));
        }

        // The "current" item is the latest-starting one at this position.
        var cur = layout.ItemAt(pos);

        // In the gap after an item nothing is audible; keep that item highlighted until the next
        // one starts (no playhead line, since the position is past its audio).
        if (list.Count == 0 && cur >= 0 && Session.ItemVm(layout.Items[cur].ItemId) is { } inGap)
            list.Add((inGap, -1));
        SetPlayheadItems(list);

        var curVm = cur >= 0 ? Session.ItemVm(layout.Items[cur].ItemId) : null;
        if (!ReferenceEquals(curVm, _currentPlayingItem))
        {
            _currentPlayingItem = curVm;
            UpdatePlaybackNote(curVm);
            if (curVm != null && IsPlaying && _engine?.Mode is PlaybackMode.Timeline or PlaybackMode.Section)
            {
                if (!ReferenceEquals(curVm.Chapter, SelectedChapter) && !IsFiltering) SelectedChapter = curVm.Chapter;
                ScrollIntoViewRequested?.Invoke(curVm);
            }
        }
        var ch = curVm?.Chapter;
        if (!ReferenceEquals(ch, _currentPlayingChapter))
        {
            if (_currentPlayingChapter != null) _currentPlayingChapter.IsPlaying = false;
            _currentPlayingChapter = ch;
            if (ch != null) ch.IsPlaying = true;
            UpdateTimelineRange();
        }
    }

    private void SetPlayheadItems(List<(ItemViewModel vm, double rel)> items)
    {
        var now = new HashSet<ItemViewModel>();
        foreach (var (vm, rel) in items)
        {
            now.Add(vm);
            vm.PlayheadMs = rel;
            vm.IsPlaying = true;
        }
        foreach (var old in _playheadItems)
        {
            if (now.Contains(old)) continue;
            old.PlayheadMs = -1;
            old.IsPlaying = false;
        }
        _playheadItems.Clear();
        _playheadItems.UnionWith(now);
    }

    private void ClearPlayhead()
    {
        SetPlayheadItems([]);
        _currentPlayingItem = null;
        UpdatePlaybackNote(null);
        if (_currentPlayingChapter != null) _currentPlayingChapter.IsPlaying = false;
        _currentPlayingChapter = null;
        UpdateTimelineRange();
    }

    /// <summary>Re-creates the output (after the device setting changed).</summary>
    public void ResetAudioDevice()
    {
        StopPlayback();
        if (_engine != null)
        {
            _engine.ResetDevice();
            _engine.DeviceId = AppHost.Settings.AudioOutputDevice;
        }
    }
}
