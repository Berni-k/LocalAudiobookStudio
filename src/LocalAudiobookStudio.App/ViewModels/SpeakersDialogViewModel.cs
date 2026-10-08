using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
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
        list.AddRange(AppHost.Catalog.All.Where(w => AppHost.Environments.IsWorkerReady(w.Id)));
        if (sp != null && !string.IsNullOrEmpty(sp.Worker) && list.All(w => w.Id != sp.Worker))
            list.Add(AppHost.Catalog.Get(sp.Worker) ?? new WorkerManifest { Id = sp.Worker, DisplayName = sp.Worker + " (unknown worker)" });
        // Update in place: replacing the collection would make the dropdown push a null selection back.
        for (int i = Workers.Count - 1; i >= 0; i--)
            if (!list.Contains(Workers[i])) Workers.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
            if (i >= Workers.Count || !ReferenceEquals(Workers[i], list[i]))
            {
                Workers.Remove(list[i]);
                Workers.Insert(i, list[i]);
            }
    }

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
    [ObservableProperty] private string _previewText = "The quick brown fox jumps over the lazy dog. How does this voice sound?";
    [ObservableProperty] private string? _previewStatus;

    /// <summary>The preview's length in seconds; empty uses the guess (or lets the model decide when it's optional).</summary>
    [ObservableProperty] private decimal? _previewLength;
    [ObservableProperty] private bool _isPreviewing;

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
    public int SegmentCount => Selected == null ? 0 : _session.Project.AllItems().Count(i => i.Kind == ItemKind.Tts && string.Equals(i.Speaker, Selected.Id, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedChanged(SpeakerViewModel? value)
    {
        // A transient deselection while saving must not blank the editor.
        if (_committing && value == null) return;
        Load();
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
        }
        finally
        {
            _loading = false;
        }
        foreach (var n in new[] { nameof(HasSelection), nameof(SpeakerId), nameof(IsDefault), nameof(SupportsCloning), nameof(WorkerInfo), nameof(SegmentCount), nameof(IsRecorded), nameof(CanPreview), nameof(IsWorkerMissing), nameof(MissingWorkerText) })
            OnPropertyChanged(n);
        RefreshPreviewLength();
    }

    private SettingsEditorViewModel? BuildSettings()
    {
        if (Selected == null || Worker == null) return null;
        var sp = Selected.Model;
        var worker = Worker;
        var definitions = _session.SettingDefinitionsFor(sp, worker);
        return new SettingsEditorViewModel(definitions, sp.Settings, allowInherit: false, (key, value) =>
        {
            if (value == null) sp.Settings.Remove(key); else sp.Settings[key] = value.DeepClone();
            RefreshPreviewLength(); // a setting such as the model can change the longest length
            // Another file (a ComfyUI workflow) brings other settings: drop the old one's values and show the new one's.
            var sourceChanged = definitions.Any(d => d.Key == key && d.AddsSettings != null);
            if (sourceChanged)
            {
                var keep = _session.SettingDefinitionsFor(sp, worker).Select(d => d.Key).ToHashSet();
                foreach (var stale in definitions.Select(d => d.Key).Where(k => !keep.Contains(k))) sp.Settings.Remove(stale);
            }
            Commit();
            if (sourceChanged) Dispatcher.UIThread.Post(() => { if (ReferenceEquals(Selected?.Model, sp)) Settings = BuildSettings(); });
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
        Commit();
        // Reload once the dropdown has finished this selection: Load rebuilds its item list (a missing worker
        // that was listed drops out), and changing the list while the ComboBox commits its selection makes it
        // read past the end (ArgumentOutOfRangeException in SelectedItems).
        Dispatcher.UIThread.Post(Load);
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
            : AppHost.Catalog.All.FirstOrDefault(w => AppHost.Environments.IsWorkerReady(w.Id))?.Id ?? "";
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
        var dir = Path.Combine(_session.Paths.CacheDir, "previews");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"{sp.Id}-{DateTime.Now:HHmmssfff}.wav");
        var settings = _session.MergeSettings(sp.Settings, null, Worker);
        var job = OneShot.Job(Worker, PreviewText, _session.ResolveVoice(sp.Voice), settings, outPath,
            $"Preview: {sp.DisplayName}", JobPriority.Low, durationSec: PreviewLength is decimal len ? Math.Round((double)len, 1) : null, tag: "preview");

        var tcs = new TaskCompletionSource<GenerationJob>();
        void Done(GenerationJob j) { if (j.Id == job.Id) tcs.TrySetResult(j); }
        AppHost.Scheduler.JobCompleted += Done;
        AppHost.Scheduler.JobFailed += Done;
        IsPreviewing = true;
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
                if (_player != null)
                {
                    await _player.PlayFileAsync(outPath);
                    IsPlayingPreview = _player.IsPlaying;
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
}
