using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>Create and edit speakers: worker, voice, generated settings form, aliases and preview.</summary>
public sealed partial class SpeakersDialogViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    /// <summary>Plays previews and reference clips (null without a main window, e.g. in tests).</summary>
    private readonly MainWindowViewModel? _player;
    private bool _loading;

    public SpeakersDialogViewModel(ProjectSession session, MainWindowViewModel? player, IReadOnlyList<AssetViewModel>? assets = null)
    {
        _session = session;
        _player = player;
        if (_player != null) _player.PropertyChanged += OnPlayerChanged;
        Assets = assets ?? [];
        Selected = session.Speakers.FirstOrDefault();
    }

    /// <summary>The preview generated here is playing: its Stop button shows.</summary>
    [ObservableProperty] private bool _isPlayingPreview;

    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPlaying) && _player?.IsPlaying != true) IsPlayingPreview = false;
    }

    [RelayCommand]
    private void StopPreview()
    {
        if (IsPlayingPreview) _player?.StopPlayback();
        IsPlayingPreview = false;
    }

    /// <summary>When the dialog closes: a preview still playing stops, and the dialog stops listening.</summary>
    public void Close()
    {
        StopPreview();
        _follow?.Dispose();
        if (_player != null) _player.PropertyChanged -= OnPlayerChanged;
    }

    public ObservableCollection<SpeakerViewModel> Speakers => _session.Speakers;
    /// <summary>
    /// "None": the speaker has no model and its segments are recorded with the microphone (the speaker's
    /// worker id is empty). Not a real worker; it only stands for that choice in the dropdown.
    /// </summary>
    public static readonly WorkerManifest NoWorker = new()
    {
        Id = "",
        DisplayName = "None",
        Description = "No generation; record the segments yourself.",
    };

    /// <summary>
    /// The dropdown: None, then the installed workers. When the selected speaker uses
    /// a worker that isn't installed here (e.g. a project from another machine), that one is added too, so it
    /// shows (in red, with a warning) until it is cleared or replaced.
    /// </summary>
    public ObservableCollection<WorkerManifest> Workers { get; } = new();

    private void RebuildWorkers(Speaker? sp)
    {
        var list = new List<WorkerManifest> { NoWorker };
        list.AddRange(AppHost.Catalog.Generators.Where(w => AppHost.Environments.IsWorkerReady(w.Id)));
        if (sp != null && !string.IsNullOrEmpty(sp.Worker) && list.All(w => w.Id != sp.Worker))
            list.Add(AppHost.Catalog.Get(sp.Worker) ?? new WorkerManifest { Id = sp.Worker, DisplayName = sp.Worker + " (unknown worker)" });
        SyncList(Workers, list);
    }

    /// <summary>Updates a dropdown's items in place: replacing the collection would make the dropdown push a null selection back.</summary>
    private static void SyncList(ObservableCollection<WorkerManifest> target, List<WorkerManifest> list)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!list.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
            if (i >= target.Count || !ReferenceEquals(target[i], list[i]))
            {
                target.Remove(list[i]);
                target.Insert(i, list[i]);
            }
    }

    /// <summary>"None": the speaker has no transcription worker. Only stands for that choice in the dropdown.</summary>
    public static readonly WorkerManifest NoTranscriber = new()
    {
        Id = "",
        DisplayName = "None",
        Description = "No transcription.",
    };

    /// <summary>The transcription dropdown: None, then the installed transcription workers (plus the speaker's own if it isn't installed).</summary>
    public ObservableCollection<WorkerManifest> Transcribers { get; } = new();

    private void RebuildTranscribers(Speaker? sp)
    {
        var list = new List<WorkerManifest> { NoTranscriber };
        list.AddRange(AppHost.Catalog.Transcribers.Where(w => AppHost.Environments.IsWorkerReady(w.Id)));
        if (sp != null && !string.IsNullOrEmpty(sp.Transcriber) && list.All(w => w.Id != sp.Transcriber))
            list.Add(AppHost.Catalog.Get(sp.Transcriber) ?? new WorkerManifest { Id = sp.Transcriber, DisplayName = sp.Transcriber + " (unknown worker)" });
        SyncList(Transcribers, list);
    }

    public bool HasTranscriber => Transcriber != null && !ReferenceEquals(Transcriber, NoTranscriber);
    public bool IsTranscriberMissing => HasTranscriber && !AppHost.Environments.IsWorkerReady(Transcriber!.Id);
    public string MissingTranscriberText => Transcriber == null ? "" : $"{Transcriber.DisplayName} is not installed. Install it in Workers, or pick another.";
    public string TranscriberInfo => Transcriber == null ? "" : Transcriber.Description ?? "";

    /// <summary>The speaker's worker isn't installed on this machine.</summary>
    public bool IsWorkerMissing => Worker != null && !IsRecorded && !AppHost.Environments.IsWorkerReady(Worker.Id);
    public string MissingWorkerText => Worker == null ? "" : $"{Worker.DisplayName} is not installed. Install it in Workers, or clear it and pick another.";
    public IReadOnlyList<string> Palette => ProjectStore.SpeakerPalette;
    /// <summary>The Assets panel's files (live); its audio files are offered as voice-cloning reference clips.</summary>
    public IReadOnlyList<AssetViewModel> Assets { get; }

    [ObservableProperty] private SpeakerViewModel? _selected;

    // ------------------------------------------------------------------ editor fields

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _color = "";
    [ObservableProperty] private WorkerManifest? _worker;
    [ObservableProperty] private string? _voiceFile;
    [ObservableProperty] private string _aliases = "";
    [ObservableProperty] private SettingsEditorViewModel? _settings;
    [ObservableProperty] private WorkerManifest? _transcriber;
    [ObservableProperty] private SettingsEditorViewModel? _transcriberSettings;
    [ObservableProperty] private bool _contextInclude;
    [ObservableProperty] private decimal _contextBefore;
    [ObservableProperty] private decimal _contextAfter;
    [ObservableProperty] private decimal _contextMaxChars;
    [ObservableProperty] private bool _contextBatch;
    [ObservableProperty] private decimal? _batchMaxSegments;
    [ObservableProperty] private decimal? _batchMaxChars;
    [ObservableProperty] private string _previewText = "The quick brown fox jumps over the lazy dog. How does this voice sound?";
    [ObservableProperty] private string? _previewStatus;

    /// <summary>The preview's length in seconds; empty uses the guess (or lets the model decide when it's optional).</summary>
    [ObservableProperty] private decimal? _previewLength;
    [ObservableProperty] private bool _isPreviewing;

    // ------------------------------------------------------------------ preview transcription

    /// <summary>The text the preview's transcript was checked against, and the result (null until a preview was transcribed).</summary>
    [ObservableProperty] private string? _transcriptSource;
    [ObservableProperty] private TranscriptCheck? _transcriptCheck;

    /// <summary>"Transcribing…", a failure, or how the transcript compares with the text.</summary>
    [ObservableProperty] private string? _transcriptStatus;

    /// <summary>What the transcription worker heard, as plain text.</summary>
    [ObservableProperty] private string? _heardText;

    /// <summary>Where the preview is playing, in ms (for highlighting the word); -1 when it isn't.</summary>
    [ObservableProperty] private double _playbackMs = -1;

    private IDisposable? _follow;

    public bool HasTranscript => TranscriptCheck != null;
    public bool HasMismatch => TranscriptCheck is { Errors: > 0 };
    public bool HasTranscriptStatus => !string.IsNullOrEmpty(TranscriptStatus);
    public bool ShowTranscript => HasTranscript || HasTranscriptStatus;

    partial void OnTranscriptCheckChanged(TranscriptCheck? value)
    {
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(HasMismatch));
        OnPropertyChanged(nameof(ShowTranscript));
    }

    partial void OnTranscriptStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasTranscriptStatus));
        OnPropertyChanged(nameof(ShowTranscript));
    }

    private void ClearTranscript()
    {
        TranscriptCheck = null;
        TranscriptSource = null;
        TranscriptStatus = null;
        HeardText = null;
    }

    /// <summary>Follows the preview's playback position until it stops, so the transcript can highlight the word being said.</summary>
    private void FollowPlayback()
    {
        _follow?.Dispose();
        _follow = DispatcherTimer.Run(() =>
        {
            if (_player?.FilePositionMs is double p && IsPlayingPreview)
            {
                PlaybackMs = p;
                return true;
            }
            PlaybackMs = -1;
            return false;
        }, TimeSpan.FromMilliseconds(33));
    }

    /// <summary>
    /// Listens to a finished preview with the speaker's transcription worker and compares what it heard with the
    /// text. Nothing happens without a (usable) transcription worker.
    /// </summary>
    private async Task TranscribePreviewAsync(Speaker sp, string audioPath, string text)
    {
        if (Transcriber is not { } worker || ReferenceEquals(worker, NoTranscriber)) return;
        var reason = AppHost.WhyWorkerCannotRun(worker.Id);
        if (reason != null) { TranscriptStatus = reason; return; }

        TranscriptStatus = "Transcribing…";
        var wav = Path.ChangeExtension(audioPath, null) + "-16k.wav";
        try
        {
            await Task.Run(() => Transcription.PrepareAudio(audioPath, wav));
        }
        catch (Exception ex)
        {
            TranscriptStatus = "Can't read the preview: " + ex.Message;
            return;
        }
        var settings = _session.MergeSettings(sp.TranscriberSettings, null, worker);
        var job = Transcription.Job(worker, wav, text, settings, $"Transcribe preview: {sp.DisplayName}", JobPriority.Low, tag: "preview");

        var tcs = new TaskCompletionSource<GenerationJob>();
        void Done(GenerationJob j) { if (j.Id == job.Id) tcs.TrySetResult(j); }
        AppHost.Scheduler.JobCompleted += Done;
        AppHost.Scheduler.JobFailed += Done;
        try
        {
            AppHost.Scheduler.Enqueue(job);
            var finished = await tcs.Task;
            // The speaker or the preview may have changed while it was listening.
            if (!ReferenceEquals(Selected?.Model, sp) || _lastPreview?.Path != audioPath) return;
            if (finished.State == JobState.Completed && finished.Transcript is { } transcript)
            {
                var check = Transcription.Check(text, transcript);
                TranscriptSource = text;
                TranscriptCheck = check;
                HeardText = transcript.Text;
                TranscriptStatus = check.Errors == 0
                    ? null // a match needs no note
                    : $"{check.Errors} difference(s) in {check.ExpectedCount} words ({check.WordErrorRate:P0}). Hover the red marks to see what went wrong.";
            }
            else
            {
                TranscriptStatus = finished.State == JobState.Cancelled ? "Transcription cancelled." : "Transcription failed: " + finished.LastError;
            }
        }
        finally
        {
            AppHost.Scheduler.JobCompleted -= Done;
            AppHost.Scheduler.JobFailed -= Done;
            try { File.Delete(wav); } catch { /* the previews folder is cache */ }
        }
    }

    /// <summary>The last preview that finished, and who made it (for its asset name).</summary>
    private sealed record PreviewClip(string Path, string SpeakerId, string SpeakerName, string WorkerName);
    private PreviewClip? _lastPreview;
    private bool _lastPreviewSaved;

    /// <summary>The selected speaker has a generated preview that can be played again.</summary>
    public bool CanReplayPreview => _lastPreview != null && Selected?.Id == _lastPreview.SpeakerId && File.Exists(_lastPreview.Path);

    /// <summary>The play button shows when there is a preview to play and it isn't playing.</summary>
    public bool ShowPlayPreview => CanReplayPreview && !IsPlayingPreview && !IsPreviewing;

    partial void OnIsPlayingPreviewChanged(bool value) => OnPropertyChanged(nameof(ShowPlayPreview));
    partial void OnIsPreviewingChanged(bool value) => OnPropertyChanged(nameof(ShowPlayPreview));

    [RelayCommand]
    private async Task PlayPreview()
    {
        if (!CanReplayPreview || _player == null) return;
        await _player.PlayFileAsync(_lastPreview!.Path);
        IsPlayingPreview = _player.IsPlaying;
        if (IsPlayingPreview) FollowPlayback();
    }

    /// <summary>"Save as asset" shows for the selected speaker's last preview, until it is saved.</summary>
    public bool CanSavePreview => _lastPreview != null && !_lastPreviewSaved && Selected?.Id == _lastPreview.SpeakerId
                                  && File.Exists(_lastPreview.Path);

    public bool HasSelection => Selected != null;
    public string SpeakerId => Selected?.Id ?? "";
    public bool IsDefault => Selected != null && string.Equals(_session.Project.Defaults.Speaker, Selected.Id, StringComparison.OrdinalIgnoreCase);
    public bool SupportsCloning => Worker?.VoiceCloning == true;
    public bool IsRecorded => ReferenceEquals(Worker, NoWorker);
    /// <summary>Preview needs a model; recorded speakers have none.</summary>
    public bool CanPreview => Worker != null && !IsRecorded;

    // ------------------------------------------------------------------ preview length

    /// <summary>The speaker's worker takes a length, so the preview offers one.</summary>
    public bool ShowPreviewLength => CanPreview && Worker!.SupportsLength;

    private Workers.Manifest.WorkerManifest? LengthWorker => ShowPreviewLength ? Worker : null;
    private System.Text.Json.Nodes.JsonObject? PreviewSettings =>
        Selected == null || LengthWorker is not { } w ? null : _session.MergeSettings(Selected.Model.Settings, null, w);

    public decimal PreviewLengthMinimum => LengthWorker is { } w ? (decimal)w.MinLengthSec : 0;
    public decimal PreviewLengthMaximum => LengthWorker is { } w ? (decimal)w.MaxLengthSecFor(PreviewSettings) : 600;

    /// <summary>Shown while the length is empty: the guess a worker that needs a length will get, else "auto".</summary>
    public string PreviewLengthWatermark => LengthWorker is { RequiresLength: true } w
        ? OneShot.GuessLength(w, PreviewText, PreviewSettings).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)
        : "auto";

    private void RefreshPreviewLength()
    {
        foreach (var n in new[] { nameof(ShowPreviewLength), nameof(PreviewLengthMinimum), nameof(PreviewLengthMaximum),
                     nameof(PreviewLengthWatermark) })
            OnPropertyChanged(n);
    }

    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(PreviewLengthWatermark));
    public string WorkerInfo => Worker == null ? "" :
        (Worker.Description ?? "") + (IsRecorded || AppHost.Environments.IsWorkerReady(Worker.Id) ? "" : "  (not installed — see Workers)");
    /// <summary>Batching needs a worker that renders several voices in one go; for others the box is disabled.</summary>
    public bool CanBatch => Worker?.CanBatch == true;

    public bool ShowBatchSettings => ContextBatch && CanBatch;

    public string BatchTip =>
        CanBatch ? "Combine the generation of similar neighbouring segments that also have batching enabled"
        : IsRecorded ? "Recorded segments aren't generated, so they can't be batched"
        : $"{Worker?.DisplayName ?? "This worker"} renders one voice at a time, so it can't batch";

    /// <summary>Recorded speakers have the group too: their lines can be context for others.</summary>
    public bool ShowContext => Worker != null;

    /// <summary>A recorded speaker's segments get no context of their own; only whether its lines may be context.</summary>
    public bool ShowContextSettings => ContextInclude && !IsRecorded;

    public string IncludeTip => IsRecorded
        ? "Let neighbouring segments use this speaker's lines as context"
        : "Include context from neighbouring segments that also have context enabled. Generation is slower, as there is more text to generate.";

    /// <summary>
    /// Context or batching is on, and the worker doesn't time the segments in what it renders: transcription separates
    /// them. Only shown when there is a transcription worker (else <see cref="ContextSplitError"/>).
    /// </summary>
    public string? ContextSplitWarning =>
        NeedsTranscription && HasTranscriber ? $"{Worker!.DisplayName} doesn't time the segments, so transcription separates them." : null;

    /// <summary>Transcription would have to separate the segments, and there is no transcription worker.</summary>
    public string? ContextSplitError => NeedsTranscription && !HasTranscriber
        ? "No transcription worker is set, so the segments can't be separated: context and batching are ignored."
        : null;

    /// <summary>Context or batching is on, and the worker doesn't say where the segments are (a worker that doesn't fuse never does).</summary>
    private bool NeedsTranscription =>
        (ContextInclude || ContextBatch && CanBatch) && !IsRecorded && Worker != null && Worker.Fusion?.Spans != true;

    public bool HasContextSplitWarning => ContextSplitWarning != null;
    public bool HasContextSplitError => ContextSplitError != null;

    private static readonly string[] ContextWarnings =
        [nameof(CanBatch), nameof(ShowBatchSettings), nameof(BatchTip), nameof(ShowContext), nameof(ShowContextSettings), nameof(IncludeTip),
         nameof(ContextSplitWarning), nameof(HasContextSplitWarning), nameof(ContextSplitError), nameof(HasContextSplitError)];

    public int SegmentCount => Selected == null ? 0 : _session.Project.AllItems().Count(i => i.Kind == ItemKind.Tts && string.Equals(i.Speaker, Selected.Id, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedChanged(SpeakerViewModel? value)
    {
        // A transient deselection while saving must not blank the editor.
        if (_committing && value == null) return;
        ClearTranscript();
        Load();
        OnPropertyChanged(nameof(CanSavePreview));
        OnPropertyChanged(nameof(ShowPlayPreview));
        OnPropertyChanged(nameof(CanPasteSettings));
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var sp = Selected?.Model;
            DisplayName = sp?.DisplayName ?? "";
            Color = sp?.Color ?? "";
            RebuildWorkers(sp);
            Worker = sp == null ? null
                : string.IsNullOrEmpty(sp.Worker) ? NoWorker
                : Workers.FirstOrDefault(w => w.Id == sp.Worker);
            VoiceFile = sp?.Voice; // only a voice-cloning reference clip; built-in voices are a setting
            Aliases = sp == null ? "" : string.Join(", ", sp.MarkerAliases);
            Settings = BuildSettings();
            RebuildTranscribers(sp);
            Transcriber = sp == null ? null
                : string.IsNullOrEmpty(sp.Transcriber) ? NoTranscriber
                : Transcribers.FirstOrDefault(w => w.Id == sp.Transcriber);
            TranscriberSettings = BuildTranscriberSettings();
            var context = sp?.Context ?? new SpeakerContext();
            ContextInclude = context.Include;
            ContextBefore = context.Before;
            ContextAfter = context.After;
            ContextMaxChars = context.MaxChars;
            ContextBatch = context.Batch && CanBatch; // a worker that can't batch shows it off (the planner ignores it too)
            BatchMaxSegments = context.BatchMaxSegments;
            BatchMaxChars = context.BatchMaxChars;
        }
        finally
        {
            _loading = false;
        }
        foreach (var n in new[] { nameof(HasSelection), nameof(SpeakerId), nameof(IsDefault), nameof(SupportsCloning), nameof(WorkerInfo), nameof(SegmentCount), nameof(IsRecorded), nameof(CanPreview), nameof(IsWorkerMissing), nameof(MissingWorkerText) }.Concat(ContextWarnings))
            OnPropertyChanged(n);
        RefreshPreviewLength();
    }

    private SettingsEditorViewModel? BuildSettings()
    {
        if (Selected == null || Worker == null) return null;
        var sp = Selected.Model;
        return BuildEditor(sp, Worker, sp.Settings, () => Settings = BuildSettings(), RefreshPreviewLength);
    }

    private SettingsEditorViewModel? BuildTranscriberSettings()
    {
        if (Selected == null || Transcriber == null || !HasTranscriber) return null;
        var sp = Selected.Model;
        return BuildEditor(sp, Transcriber, sp.TranscriberSettings, () => TranscriberSettings = BuildTranscriberSettings(), null);
    }

    /// <summary>The settings form for one of the speaker's workers, writing into <paramref name="values"/>.</summary>
    private SettingsEditorViewModel BuildEditor(Speaker sp, WorkerManifest worker, JsonObject values, Action rebuild, Action? changed)
    {
        var definitions = worker.SettingsFor(values, _session.Paths.Resolve);
        return new SettingsEditorViewModel(definitions, values, allowInherit: false, (key, value) =>
        {
            if (value == null) values.Remove(key); else values[key] = value.DeepClone();
            changed?.Invoke(); // a setting such as the model can change the longest length
            // Another file (a ComfyUI workflow) brings other settings: drop the old one's values and show the new one's.
            var sourceChanged = definitions.Any(d => d.Key == key && d.AddsSettings != null);
            if (sourceChanged)
            {
                var keep = worker.SettingsFor(values, _session.Paths.Resolve).Select(d => d.Key).ToHashSet();
                foreach (var stale in definitions.Select(d => d.Key).Where(k => !keep.Contains(k))) values.Remove(stale);
            }
            Commit();
            if (sourceChanged) Dispatcher.UIThread.Post(() => { if (ReferenceEquals(Selected?.Model, sp)) rebuild(); });
        }, _session.Paths.Root, importAsset: (def, file) => _session.ImportAssetAsync(file, def.AssetFolder)) { Inline = true };
    }

    private void Commit()
    {
        if (_loading || Selected == null) return;
        var editing = Selected;
        _committing = true;
        try
        {
            _session.SpeakerChanged(editing.Model);
        }
        finally
        {
            _committing = false;
        }
        // Re-sorting the list (e.g. after a rename) can deselect the item; keep editing it.
        if (!ReferenceEquals(Selected, editing)) Selected = editing;
        editing.Refresh();
    }

    private bool _committing;

    partial void OnDisplayNameChanged(string value)
    {
        if (_loading || Selected == null || string.IsNullOrWhiteSpace(value)) return;
        Selected.Model.DisplayName = value.Trim();
        Commit();
    }

    partial void OnColorChanged(string value)
    {
        if (_loading || Selected == null || !Avalonia.Media.Color.TryParse(value, out _)) return;
        Selected.Model.Color = value;
        Commit();
    }

    partial void OnWorkerChanged(WorkerManifest? value)
    {
        OnPropertyChanged(nameof(SupportsCloning));
        foreach (var n in ContextWarnings) OnPropertyChanged(n);
        OnPropertyChanged(nameof(WorkerInfo));
        OnPropertyChanged(nameof(IsRecorded));
        OnPropertyChanged(nameof(CanPreview));
        RefreshPreviewLength();
        OnPropertyChanged(nameof(IsWorkerMissing));
        OnPropertyChanged(nameof(MissingWorkerText));
        if (_loading || Selected == null || value == null) return;
        var sp = Selected.Model;
        if (sp.Worker == value.Id) return;
        sp.Worker = value.Id;
        sp.Settings = value.DefaultSettings(_session.Speakers.IndexOf(Selected));
        if (!value.VoiceCloning) sp.Voice = null; // a reference clip means nothing to a worker without cloning
        if (!value.CanBatch) sp.Context.Batch = false; // nor batching to one that renders one voice at a time
        Commit();
        // Reload once the dropdown has finished this selection: Load rebuilds its item list (a missing worker
        // that was listed drops out), and changing the list while the ComboBox commits its selection makes it
        // read past the end (ArgumentOutOfRangeException in SelectedItems).
        Dispatcher.UIThread.Post(Load);
    }

    partial void OnContextIncludeChanged(bool value) => EditContext(c => c.Include = value);
    partial void OnContextBeforeChanged(decimal value) => EditContext(c => c.Before = (int)value);
    partial void OnContextAfterChanged(decimal value) => EditContext(c => c.After = (int)value);
    partial void OnContextMaxCharsChanged(decimal value) => EditContext(c => c.MaxChars = (int)value);
    partial void OnContextBatchChanged(bool value) => EditContext(c => c.Batch = value);
    // Empty means any.
    partial void OnBatchMaxSegmentsChanged(decimal? value) => EditContext(c => c.BatchMaxSegments = value is > 0 ? (int)value : null);
    partial void OnBatchMaxCharsChanged(decimal? value) => EditContext(c => c.BatchMaxChars = value is > 0 ? (int)value : null);

    private void EditContext(Action<SpeakerContext> edit)
    {
        foreach (var n in ContextWarnings) OnPropertyChanged(n); // they depend on whether context or batching is on
        if (_loading || Selected == null) return;
        edit(Selected.Model.Context);
        Commit();
    }

    partial void OnTranscriberChanged(WorkerManifest? value)
    {
        foreach (var n in new[] { nameof(HasTranscriber), nameof(IsTranscriberMissing), nameof(MissingTranscriberText), nameof(TranscriberInfo) }.Concat(ContextWarnings))
            OnPropertyChanged(n);
        if (_loading || Selected == null || value == null) return;
        var sp = Selected.Model;
        if (sp.Transcriber == value.Id) return;
        sp.Transcriber = value.Id;
        sp.TranscriberSettings = value.DefaultSettings();
        Commit();
        Dispatcher.UIThread.Post(Load); // as for the worker: rebuild the list after the dropdown has finished
    }

    /// <summary>
    /// Clears a missing worker from the dropdown so another can be picked. The speaker keeps its old worker
    /// until a new one is chosen.
    /// </summary>
    [RelayCommand]
    private void ClearMissingWorker()
    {
        if (!IsWorkerMissing || Worker is not { } missing) return;
        _loading = true;
        try
        {
            Worker = null;
            Workers.Remove(missing);
            Settings = null;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnAliasesChanged(string value)
    {
        if (_loading || Selected == null) return;
        Selected.Model.MarkerAliases = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Commit();
    }

    // ------------------------------------------------------------------ copy and paste

    /// <summary>
    /// The settings "Copy settings" took, for pasting onto other speakers (the list's context menu). Kept while the app
    /// runs, so they can go to another project's speakers too.
    /// </summary>
    private static Speaker? s_copied;

    public bool CanPasteSettings => s_copied != null && HasSelection;

    public string PasteHeader => s_copied == null ? "Paste settings" : $"Paste settings from {s_copied.DisplayName}";

    [RelayCommand]
    private void CopySettings()
    {
        if (Selected?.Model is not { } sp) return;
        s_copied = new Speaker
        {
            DisplayName = Selected.Name,
            Worker = sp.Worker,
            Voice = sp.Voice,
            Settings = (JsonObject)sp.Settings.DeepClone(),
            Transcriber = sp.Transcriber,
            TranscriberSettings = (JsonObject)sp.TranscriberSettings.DeepClone(),
            Context = sp.Context.Clone(),
        };
        OnPropertyChanged(nameof(CanPasteSettings));
        OnPropertyChanged(nameof(PasteHeader));
    }

    /// <summary>Everything but the name, colour and marker aliases.</summary>
    [RelayCommand] private void PasteAll() => Paste(generation: true, transcription: true, context: true);
    [RelayCommand] private void PasteGeneration() => Paste(generation: true);
    [RelayCommand] private void PasteTranscription() => Paste(transcription: true);
    [RelayCommand] private void PasteContext() => Paste(context: true);

    private void Paste(bool generation = false, bool transcription = false, bool context = false)
    {
        if (s_copied is not { } from || Selected?.Model is not { } sp) return;
        if (generation)
        {
            sp.Worker = from.Worker;
            sp.Voice = from.Voice;
            sp.Settings = (JsonObject)from.Settings.DeepClone();
        }
        if (transcription)
        {
            sp.Transcriber = from.Transcriber;
            sp.TranscriberSettings = (JsonObject)from.TranscriberSettings.DeepClone();
        }
        if (context) sp.Context = from.Context.Clone();
        Commit();
        Load();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void PickColor(string? color)
    {
        if (color != null) Color = color;
    }

    [RelayCommand]
    private async Task Add()
    {
        var name = await Dialogs.Prompt("New speaker", "Name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        // The shown worker if it is usable here, else the first installed worker, else None (record).
        var worker = Worker != null && (IsRecorded || !IsWorkerMissing) ? Worker.Id
            : AppHost.Catalog.Generators.FirstOrDefault(w => AppHost.Environments.IsWorkerReady(w.Id))?.Id ?? "";
        var sp = _session.AddSpeaker(name.Trim(), worker, _session.Speakers.Count);
        Selected = _session.SpeakerVm(sp.Id);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected == null) return;
        if (_session.Speakers.Count <= 1)
        {
            await Dialogs.ShowInfo("Delete speaker", "A project needs at least one speaker.");
            return;
        }
        var replacement = _session.Speakers.First(s => s != Selected);
        if (!await Dialogs.Confirm("Delete speaker",
                $"Delete {Selected.Name}? Its {SegmentCount} segment(s) will be assigned to {replacement.Name}. This clears the undo history.", "Delete", "Cancel"))
            return;
        _session.DeleteSpeaker(Selected.Model, replacement.Id);
        Selected = _session.Speakers.FirstOrDefault();
    }

    [RelayCommand]
    private void MakeDefault()
    {
        if (Selected == null) return;
        _session.Project.Defaults.Speaker = Selected.Id;
        _session.MarkProjectDirty();
        OnPropertyChanged(nameof(IsDefault));
    }

    [RelayCommand]
    private async Task BrowseVoiceFile()
    {
        if (Selected == null) return;
        var file = await Dialogs.OpenFile("Voice reference clip", new FileFilter("Audio", ProjectSession.AudioExtensions.Select(e => "*" + e).ToArray()));
        if (file == null) return;
        try
        {
            var sp = Selected.Model;
            string name;
            if (Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                name = _session.ImportVoiceFile(sp, file);
            }
            else
            {
                name = await _session.ConvertVoiceFileAsync(sp, file);
            }
            SetVoice(name);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Voice reference", ex.Message);
        }
    }

    /// <summary>Uses a project asset as the reference clip. WAVs are referenced in place (assets/…);
    /// other formats are converted once into the speaker's own reference WAV, like "From file".</summary>
    [RelayCommand]
    private async Task UseAsset(AssetViewModel? asset)
    {
        if (Selected == null || asset == null) return;
        try
        {
            var source = _session.Paths.Resolve(asset.RelativePath);
            if (!File.Exists(source)) throw new FileNotFoundException("The asset no longer exists.", source);
            string name;
            if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                name = asset.RelativePath;
            }
            else
            {
                name = await _session.ConvertVoiceFileAsync(Selected.Model, source);
            }
            SetVoice(name);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Voice reference", ex.Message);
        }
    }

    [RelayCommand]
    private void ClearVoice()
    {
        if (Selected?.Model.Voice != null) SetVoice(null);
    }

    private void SetVoice(string? name)
    {
        if (Selected == null) return;
        Selected.Model.Voice = name;
        _loading = true;
        VoiceFile = name;
        _loading = false;
        Commit();
    }

    [RelayCommand]
    private async Task PlayVoiceFile()
    {
        if (Selected?.Model.Voice is not { } v || _player == null) return;
        var path = _session.ResolveVoice(v);
        if (path != null && File.Exists(path)) await _player.PlayFileAsync(path);
    }

    [RelayCommand]
    private async Task Preview()
    {
        if (Selected == null || Worker == null || string.IsNullOrWhiteSpace(PreviewText)) return;
        var reason = AppHost.WhyWorkerCannotRun(Worker.Id);
        if (reason != null) { PreviewStatus = reason; return; }

        var sp = Selected.Model;
        var text = PreviewText; // the text box may change while this runs
        var workerName = Worker.DisplayName;
        var dir = Path.Combine(_session.Paths.CacheDir, "previews");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"{sp.Id}-{DateTime.Now:HHmmssfff}.wav");
        var settings = _session.MergeSettings(sp.Settings, null, Worker);
        var job = OneShot.Job(Worker, text, _session.ResolveVoice(sp.Voice), settings, outPath,
            $"Preview: {sp.DisplayName}", JobPriority.Low, durationSec: PreviewLength is decimal len ? Math.Round((double)len, 1) : null, tag: "preview");

        var tcs = new TaskCompletionSource<GenerationJob>();
        void Done(GenerationJob j) { if (j.Id == job.Id) tcs.TrySetResult(j); }
        AppHost.Scheduler.JobCompleted += Done;
        AppHost.Scheduler.JobFailed += Done;
        IsPreviewing = true;
        ClearTranscript();
        PreviewStatus = "Queued (low priority)…";
        try
        {
            AppHost.Scheduler.Enqueue(job);
            var poll = DispatcherTimer.Run(() =>
            {
                if (job.State == JobState.Running) PreviewStatus = job.ProgressMessage ?? $"Generating… {job.Progress:P0}";
                return !tcs.Task.IsCompleted;
            }, TimeSpan.FromMilliseconds(250));
            var finished = await tcs.Task;
            poll.Dispose();
            if (finished.State == JobState.Completed && File.Exists(outPath))
            {
                PreviewStatus = $"Done (seed {finished.Result?.Seed}).";
                _lastPreview = new PreviewClip(outPath, sp.Id, sp.DisplayName, workerName);
                _lastPreviewSaved = false;
                OnPropertyChanged(nameof(CanSavePreview));
                // Playback waits for the transcript, so the words are highlighted from the first one.
                await TranscribePreviewAsync(sp, outPath, text);
                if (_player != null && ReferenceEquals(Selected?.Model, sp))
                {
                    await _player.PlayFileAsync(outPath);
                    IsPlayingPreview = _player.IsPlaying;
                    if (IsPlayingPreview) FollowPlayback();
                }
            }
            else
            {
                PreviewStatus = finished.State == JobState.Cancelled ? "Cancelled." : "Failed: " + finished.LastError;
            }
        }
        finally
        {
            AppHost.Scheduler.JobCompleted -= Done;
            AppHost.Scheduler.JobFailed -= Done;
            IsPreviewing = false;
        }
    }

    /// <summary>The folder below assets/ that saved previews go into.</summary>
    private const string GeneratedAssetFolder = "Generated";

    /// <summary>
    /// Keeps the last preview as an asset in assets/Generated, named "&lt;speaker&gt; - &lt;worker&gt; - &lt;timestamp&gt;":
    /// a voice generated here can then be another speaker's reference clip.
    /// </summary>
    [RelayCommand]
    private async Task SavePreviewAsAsset()
    {
        if (_lastPreview is not { } clip || !File.Exists(clip.Path)) return;
        var who = $"{clip.SpeakerName} - {clip.WorkerName}";
        if (who.Length > 60) who = who[..60].TrimEnd(); // asset names are cut at 80 characters; keep the timestamp
        try
        {
            var rel = await _session.ImportAssetAsync(clip.Path, GeneratedAssetFolder, $"{who} - {DateTime.Now:yyyyMMdd-HHmmss}");
            _lastPreviewSaved = true;
            OnPropertyChanged(nameof(CanSavePreview));
            PreviewStatus = $"Saved as {Path.GetFileNameWithoutExtension(rel)} in {GeneratedAssetFolder}.";
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Save as asset", ex.Message);
        }
    }
}
