using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Right-hand panel: properties of the selected item(s). With several items selected only shared
/// fields are editable, and fields whose values differ show blank.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private bool _loading;

    public InspectorViewModel(MainWindowViewModel main) => _main = main;

    public IReadOnlyList<ItemViewModel> Items { get; private set; } = [];
    public ItemViewModel? Item => Items.Count == 1 ? Items[0] : null;
    public bool IsEmpty => Items.Count == 0;
    public bool IsSingle => Items.Count == 1;
    public bool IsMulti => Items.Count > 1;
    public bool AnyTts => Items.Any(i => i.IsTts);
    public bool IsSingleTts => Item?.IsTts == true;

    // ------------------------------------------------------------------ length

    private IEnumerable<WorkerManifest?> SelectedWorkers =>
        Items.Select(i => i.IsTts ? _main.Session?.WorkerFor(i.Model) : null);

    /// <summary>Every selected segment's worker can be given a length.</summary>
    public bool ShowLength => Items.Count > 0 && SelectedWorkers.All(w => w?.SupportsLength == true);
    /// <summary>Every selected segment's worker needs a length; empty then means "use the guess".</summary>
    public bool LengthRequired => ShowLength && SelectedWorkers.All(w => w!.RequiresLength);
    public decimal LengthMinimum => ShowLength ? (decimal)SelectedWorkers.Max(w => w!.MinLengthSec) : 0;
    /// <summary>The tightest maximum among the selection; it can depend on a setting such as the model.</summary>
    public decimal LengthMaximum => ShowLength && _main.Session is { } s ? (decimal)Items.Min(i => s.MaxLengthSec(i.Model)) : 600;
    /// <summary>A segment's own length is set, so clearing it means something.</summary>
    public bool CanClearLength => ShowLength && Items.Any(i => i.Model.DurationSec != null);
    public string LengthWatermark => Items.Count > 1 ? "(mixed)" : "auto";

    public string LengthHint
    {
        get
        {
            if (!ShowLength || _main.Session is not { } s) return "";
            if (Items.Any(i => i.Model.DurationSec != null)) return "";
            if (LengthRequired) return "A length in the text (such as 4s) is used, else 5 s. Type a value to fix it.";
            return Item is { } one
                ? $"Empty: the model decides (about {s.GuessLengthSec(one.Model):0.0} s)."
                : "Empty: the model decides.";
        }
    }

    [ObservableProperty] private decimal? _lengthSec;
    public bool IsSingleClip => Item?.IsClip == true;
    public bool IsSinglePause => Item?.IsPause == true;
    public bool IsSingleWithAudio => Item?.HasAudio == true;
    public bool IsSingleGenerated => Item?.IsGenerated == true;

    /// <summary>Everything selected is locked: only the lock and the note can be changed.</summary>
    public bool AllLocked => Items.Count > 0 && Items.All(i => i.IsLocked);
    public bool CanEdit => !AllLocked;

    public string Header => Items.Count switch
    {
        0 => "Nothing selected",
        1 => $"{Item!.KindLabel} · chapter {Item.Chapter.Number}, item {Item.Chapter.Items.IndexOf(Item) + 1}",
        _ => $"{Items.Count} items selected",
    };

    public ObservableCollection<SpeakerViewModel>? Speakers => _main.Session?.Speakers;

    // ------------------------------------------------------------------ shared fields

    [ObservableProperty] private SpeakerViewModel? _speaker;
    [ObservableProperty] private decimal? _gainDb;
    [ObservableProperty] private bool? _locked;
    [ObservableProperty] private bool? _normalize;
    [ObservableProperty] private decimal? _fadeInMs;
    [ObservableProperty] private decimal? _fadeOutMs;

    // ------------------------------------------------------------------ background (shared)

    public bool AnyWithAudio => Items.Any(i => i.HasAudio);
    private List<ItemViewModel> WithAudio => Items.Where(i => i.HasAudio).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundOptions))]
    private bool? _background;

    /// <summary>Index into <see cref="BackgroundLengthOptions"/> (= <see cref="BackgroundLength"/> value); −1 when mixed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundLimit), nameof(BackgroundLimitLabel), nameof(BackgroundLimitIncrement), nameof(BackgroundLoopEnabled))]
    private int _backgroundLengthIndex = -1;

    [ObservableProperty] private decimal? _backgroundLimit;
    [ObservableProperty] private bool? _backgroundLoop;

    public static IReadOnlyList<string> BackgroundLengthOptions { get; } =
        ["Its full length", "A number of seconds", "The next sections", "Until the chapter ends"];

    public bool ShowBackgroundOptions => Background == true;
    private BackgroundLength? SelectedLength => BackgroundLengthIndex >= 0 ? (BackgroundLength)BackgroundLengthIndex : null;
    public bool ShowBackgroundLimit => SelectedLength is BackgroundLength.Seconds or BackgroundLength.Sections;
    public string BackgroundLimitLabel => SelectedLength == BackgroundLength.Sections ? "Number of sections" : "Seconds";
    public decimal BackgroundLimitIncrement => SelectedLength == BackgroundLength.Sections ? 1 : 5;
    public bool BackgroundLoopEnabled => SelectedLength is not null and not BackgroundLength.Full;

    // ------------------------------------------------------------------ single-item fields

    [ObservableProperty] private decimal? _trimStartMs;
    [ObservableProperty] private decimal? _trimEndMs;
    [ObservableProperty] private decimal? _pauseMs;
    [ObservableProperty] private string? _clipLabel;
    [ObservableProperty] private string? _exportId;
    [ObservableProperty] private SettingsEditorViewModel? _overrides;

    /// <summary>
    /// The "Override settings" group is open. On when the section has overrides; ticking it just opens the
    /// editor, unticking removes the overrides (so nothing hidden keeps applying).
    /// </summary>
    [ObservableProperty] private bool _overridesEnabled;

    public string? ClipFile => Item?.Model.ClipFile;
    public string TimingText => Item is { } i
        ? $"Starts {TimeFormat.Hms(i.StartMs, i.StartIsEstimate)} · length {TimeFormat.Short(i.DurationMs)}{(i.DurationIsEstimate ? " (estimate)" : "")}"
        : "";
    public string LoudnessText => Item?.SelectedTakeInfo is { } t
        ? $"Measured {(t.LoudnessLufs is double l ? l.ToString("0.0") + " LUFS" : "silent")} · auto gain {LoudnessMeter.AutoGainDb(_main.Session?.Project.Defaults.TargetLoudnessLufs ?? -18, t.LoudnessLufs):+0.0;−0.0} dB"
        : Item?.IsClip == true && _main.Session?.Takes.Clip(Item.Model.ClipFile) is { } c
            ? $"Measured {(c.LoudnessLufs is double cl ? cl.ToString("0.0") + " LUFS" : "silent")}"
            : "";
    public string? ErrorText => Item?.Model.LastError;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public ObservableCollection<TakeRowViewModel> Takes { get; } = new();
    public bool HasTakes => Takes.Count > 0;

    public void SetSelection(IReadOnlyList<ItemViewModel> items)
    {
        foreach (var old in Items) old.PropertyChanged -= OnItemPropertyChanged;
        Items = items;
        foreach (var i in Items) i.PropertyChanged += OnItemPropertyChanged;
        _loading = true;
        OverridesEnabled = false; // a new selection starts closed unless it has overrides (see Reload)
        _loading = false;
        Reload();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Reload when the model changed underneath us (undo, generation finished, row edit).
        if (e.PropertyName is nameof(ItemViewModel.DisplayText) or nameof(ItemViewModel.TakeCounterText)
            or nameof(ItemViewModel.Status) or nameof(ItemViewModel.BackgroundText) or nameof(ItemViewModel.EffectiveGainDb)
            or nameof(ItemViewModel.TrimStartMs) or nameof(ItemViewModel.IsLocked) or nameof(ItemViewModel.SpeakerName))
            ReloadSoon();
    }

    private bool _reloadPending;

    private void ReloadSoon()
    {
        if (_reloadPending) return;
        _reloadPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _reloadPending = false; Reload(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void RefreshTiming()
    {
        OnPropertyChanged(nameof(TimingText));
    }

    private static T? Same<T>(IEnumerable<ItemViewModel> items, Func<ItemViewModel, T> get)
    {
        var values = items.Select(get).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : default;
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            var items = Items;
            var tts = items.Where(i => i.IsTts).ToList();
            Speaker = tts.Count > 0 ? _main.Session?.SpeakerVm(Same(tts, i => i.Model.Speaker)) : null;
            Locked = items.Count > 0 && items.Select(i => i.IsLocked).Distinct().Count() == 1 ? items[0].IsLocked : null;
            // Level and fades only mean something for items with audio (not pauses or comments).
            var audio = WithAudio;
            GainDb = audio.Count > 0 && audio.Select(i => i.Model.GainDb).Distinct().Count() == 1 ? (decimal)audio[0].Model.GainDb : null;
            Normalize = audio.Count > 0 && audio.Select(i => i.Model.Normalize != false).Distinct().Count() == 1 ? audio[0].Model.Normalize != false : null;
            FadeInMs = Same(audio, i => i.Model.FadeInMs);
            FadeOutMs = Same(audio, i => i.Model.FadeOutMs);

            var bg = WithAudio;
            Background = Same(bg, i => (bool?)i.IsBackground);
            BackgroundLengthIndex = Same(bg, i => (int?)(int)i.Model.EffectiveBackgroundLength) ?? -1;
            BackgroundLimit = Same(bg, i => i.Model.BackgroundLimit) is double lim ? (decimal)lim : null;
            BackgroundLoop = Same(bg, i => (bool?)(i.Model.BackgroundLoop == true));

            var item = Item;
            TrimStartMs = item?.Model.TrimStartMs;
            TrimEndMs = item?.Model.TrimEndMs;
            PauseMs = item?.Model.DurationMs;
            // Required lengths show the guess when the segment has none (the field is prefilled);
            // optional ones stay empty, meaning the model decides.
            LengthSec = ShowLength && _main.Session is { } session
                ? Same(items, i => (decimal?)(i.Model.DurationSec ?? (session.WorkerFor(i.Model)!.RequiresLength ? session.GuessLengthSec(i.Model) : null)))
                : null;
            ClipLabel = item?.Model.Label;
            ExportId = item?.Model.ExportId;
            Overrides = BuildOverrides(items);
            if (Overrides != null && items.Any(i => i.Model.SettingsOverride is { Count: > 0 })) OverridesEnabled = true;

            Takes.Clear();
            if (item != null && item.IsGenerated && _main.Session != null)
            {
                foreach (var t in item.Takes.Reverse())
                    Takes.Add(new TakeRowViewModel(_main, item, t));
            }
        }
        finally
        {
            _loading = false;
        }
        foreach (var name in new[]
                 {
                     nameof(Items), nameof(Item), nameof(IsEmpty), nameof(IsSingle), nameof(IsMulti), nameof(AnyTts), nameof(IsSingleTts),
                     nameof(ShowLength), nameof(LengthRequired), nameof(LengthMinimum), nameof(LengthMaximum),
                     nameof(CanClearLength), nameof(LengthHint), nameof(LengthWatermark), nameof(IsSingleClip), nameof(IsSinglePause), nameof(IsSingleWithAudio), nameof(IsSingleGenerated),
                     nameof(Header), nameof(Speakers), nameof(ClipFile), nameof(TimingText), nameof(LoudnessText), nameof(ErrorText),
                     nameof(HasError), nameof(HasTakes), nameof(AnyWithAudio), nameof(AllLocked), nameof(CanEdit),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>
    /// The override editor for the selection: only when every section is generated by the same worker (its settings
    /// are the same). A value all sections share is shown; one that differs shows empty, marked "mixed". A change
    /// applies to every selected section. A ComfyUI workflow's settings are offered when every section has them.
    /// </summary>
    private SettingsEditorViewModel? BuildOverrides(IReadOnlyList<ItemViewModel> items)
    {
        if (items.Count == 0 || _main.Session is not { } session || items.Any(i => !i.IsGenerated)) return null;
        var workers = items.Select(i => session.WorkerFor(i.Model)?.Id).Distinct().ToList();
        if (workers.Count != 1 || workers[0] == null) return null;
        var manifest = session.WorkerFor(items[0].Model);
        if (manifest == null) return null;
        var definitions = session.SettingDefinitionsFor(items[0].Model);
        foreach (var other in items.Skip(1))
        {
            var keys = session.SettingDefinitionsFor(other.Model).Select(d => d.Key).ToHashSet();
            definitions.RemoveAll(d => !keys.Contains(d.Key));
        }
        if (definitions.Count == 0) return null;

        var shared = new JsonObject();
        var mixed = new HashSet<string>();
        foreach (var def in definitions)
        {
            var first = items[0].Model.SettingsOverride?[def.Key];
            if (items.All(i => JsonNode.DeepEquals(i.Model.SettingsOverride?[def.Key], first)))
            {
                if (first != null) shared[def.Key] = first.DeepClone();
            }
            else
            {
                mixed.Add(def.Key);
            }
        }
        return new SettingsEditorViewModel(definitions, shared, allowInherit: true, (key, value) =>
        {
            session.SetOverride(items, key, value);
            // Another file (a ComfyUI workflow) brings other settings.
            if (definitions.Any(d => d.Key == key && d.AddsSettings != null))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Overrides = BuildOverrides(items));
        }, session.Paths.Root, mixed, (def, file) => session.ImportAssetAsync(file, def.AssetFolder)) { Inline = true };
    }

    // ------------------------------------------------------------------ commits

    private bool CanCommit => !_loading && _main.Session != null && Items.Count > 0;

    partial void OnSpeakerChanged(SpeakerViewModel? value)
    {
        if (CanCommit && value != null) _main.Session!.SetSpeaker(Items, value.Id);
    }

    partial void OnGainDbChanged(decimal? value)
    {
        if (CanCommit && value != null) _main.Session!.SetGain(WithAudio, (double)value.Value);
    }

    partial void OnBackgroundChanged(bool? value)
    {
        if (CanCommit && value != null) _main.Session!.SetBackground(WithAudio, value.Value);
    }

    partial void OnBackgroundLengthIndexChanged(int value)
    {
        if (!CanCommit || SelectedLength is not { } mode) return;
        // Switching to seconds/sections starts from a sensible amount.
        double? limit = mode switch
        {
            BackgroundLength.Seconds => BackgroundLimit is decimal s && s > 0 ? (double)s : 30,
            BackgroundLength.Sections => BackgroundLimit is decimal n && n >= 1 ? Math.Round((double)n) : 1,
            _ => null,
        };
        _main.Session!.SetBackgroundLength(WithAudio, mode, limit);
    }

    partial void OnBackgroundLimitChanged(decimal? value)
    {
        if (!CanCommit || value == null || SelectedLength is not ({ } mode and (BackgroundLength.Seconds or BackgroundLength.Sections))) return;
        var limit = mode == BackgroundLength.Sections ? Math.Max(1, Math.Round((double)value.Value)) : (double)value.Value;
        _main.Session!.SetBackgroundLength(WithAudio, mode, limit);
    }

    partial void OnBackgroundLoopChanged(bool? value)
    {
        if (CanCommit && value != null) _main.Session!.SetBackgroundLoop(WithAudio, value.Value);
    }

    partial void OnLockedChanged(bool? value)
    {
        if (CanCommit && value != null) _main.Session!.SetLocked(Items, value.Value);
    }

    partial void OnNormalizeChanged(bool? value)
    {
        if (CanCommit && value != null) _main.Session!.SetNormalize(WithAudio, value.Value);
    }

    partial void OnFadeInMsChanged(decimal? value)
    {
        if (CanCommit) _main.Session!.Edit(WithAudio, "Set fade in", m => m.FadeInMs = value == null ? null : (int)value.Value, "fadein");
    }

    partial void OnFadeOutMsChanged(decimal? value)
    {
        if (CanCommit) _main.Session!.Edit(WithAudio, "Set fade out", m => m.FadeOutMs = value == null ? null : (int)value.Value, "fadeout");
    }

    partial void OnTrimStartMsChanged(decimal? value)
    {
        if (CanCommit && Item is { } i) _main.Session!.SetTrims(i, value == null ? null : (int)value.Value, i.Model.TrimEndMs);
    }

    partial void OnTrimEndMsChanged(decimal? value)
    {
        if (CanCommit && Item is { } i) _main.Session!.SetTrims(i, i.Model.TrimStartMs, value == null ? null : (int)value.Value);
    }

    partial void OnPauseMsChanged(decimal? value)
    {
        if (CanCommit && value != null) _main.Session!.SetPauseDuration(Items, (int)value.Value);
    }

    partial void OnLengthSecChanged(decimal? value)
    {
        if (!CanCommit || !ShowLength) return;
        if (value == null && !CanClearLength)
        {
            // Emptying a guessed length changes nothing; show the guess again.
            if (LengthRequired) ReloadSoon();
            return;
        }
        // Typing the prefilled guess back in doesn't pin it; only a different value does.
        if (value is decimal v && Items.All(i => i.Model.DurationSec == null && _main.Session!.WorkerFor(i.Model)!.RequiresLength
                                               && Math.Abs(_main.Session.GuessLengthSec(i.Model) - (double)v) < 0.05))
            return;
        _main.Session!.SetLength(Items, value == null ? null : (double)value.Value);
        ReloadSoon(); // the clear button and hint depend on whether a length is set
    }

    [RelayCommand]
    private void ClearLength()
    {
        if (!CanCommit || !ShowLength) return;
        _main.Session!.SetLength(Items, null);
        ReloadSoon();
    }

    partial void OnExportIdChanged(string? value)
    {
        if (CanCommit && Item is { HasAudio: true } i && (string.IsNullOrWhiteSpace(value) ? null : value.Trim()) != i.Model.ExportId)
            _main.Session!.SetExportId(i, value);
    }

    partial void OnClipLabelChanged(string? value)
    {
        if (CanCommit && Item is { IsClip: true } i && value != i.Model.Label) _main.Session!.SetClipLabel(i, value);
    }

    [RelayCommand]
    private void ResetTrims()
    {
        if (_main.Session != null && Item != null) _main.Session.SetTrims(Item, null, null);
    }

    partial void OnOverridesEnabledChanged(bool value)
    {
        var withOverrides = Items.Where(i => i.Model.SettingsOverride is { Count: > 0 }).ToList();
        if (!value && CanCommit && withOverrides.Count > 0)
            _main.Session!.Edit(withOverrides, "Clear overrides", m => m.SettingsOverride = null);
    }
}

/// <summary>One take in the inspector's takes panel.</summary>
public sealed partial class TakeRowViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public TakeRowViewModel(MainWindowViewModel main, ItemViewModel item, TakeInfo info)
    {
        _main = main;
        Item = item;
        Info = info;
        LoadPeaks();
    }

    public ItemViewModel Item { get; }
    public TakeInfo Info { get; }
    public int Take => Info.Take;
    public bool IsSelected => Item.Model.SelectedTake == Info.Take;
    public string Title => $"Take {Info.Take}" + (IsSelected ? "  ✓" : "");
    public string Details =>
        $"{Info.CreatedUtc.ToLocalTime():g} · {TimeFormat.Short(Info.DurationMs)} · seed {Info.Seed?.ToString() ?? "–"}" +
        (Info.Gpu != null ? $" · {Info.Gpu}" : "") + (Info.GenerationMs > 0 ? $" · {Info.GenerationMs / 1000.0:0.0}s" : "");
    public bool IsCurrentInput => Info.InputHash == _main.Session?.ComputeHash(Item.Model);

    // This take's transcript against the segment's text, read in the background. Nothing shows without a
    // transcript, or when the section ignores them.
    // The verdict is in the take's own file: how its transcript compared with the text it was made from.
    private bool IsApproved => Item.Model.ApprovedTranscriptTake == Info.Take;
    private int? Errors => Item.Model.IgnoreTranscript == true ? null : Info.TranscriptErrors;
    public bool TranscriptPassed => Errors is int e && (e == 0 || IsApproved);
    public bool TranscriptFailed => Errors > 0 && !IsApproved;

    public string TranscriptTooltip => Errors is not int e ? "" :
        e == 0 ? "The transcript matches the text"
        : IsApproved ? "Approved by you although the transcript differs"
        : $"The transcript doesn't match the text: {e} difference(s) in {Info.TranscriptWordCount} words";

    [ObservableProperty] private Peaks? _peaks;

    private async void LoadPeaks()
    {
        if (_main.Session == null) return;
        var s = _main.Session;
        Peaks = await s.PeaksCache.LoadAsync(s.Takes.TakePeaks(Item.Id, Take), s.Takes.TakeAudio(Item.Id, Take));
    }

    [RelayCommand]
    private void Audition() => _main.PlayItemOnly(Item, Take);

    [RelayCommand]
    private void Use() => _main.Session?.SelectTake(Item, Take);

    [RelayCommand]
    private Task Delete() => Item.ConfirmDeleteTakeAsync(Take);

    [RelayCommand]
    private void OpenFolder()
    {
        if (_main.Session != null) Dialogs.Reveal(_main.Session.Takes.TakeAudio(Item.Id, Take));
    }

    /// <summary>The take was cut out of a fused generation kept for the fused generation inspector.</summary>
    public bool HasFusionRecord => Info.Fusion?.Record != null;

    [RelayCommand]
    private void ShowFusionRecord()
    {
        if (_main.Session is not { } s || Info.Fusion?.Record is not { } id) return;
        if (!WindowService.ShowFusionRecord(_main, s, id, Item.Id))
            _main.StatusText = "The fused generation inspector has nothing for this take: its record is no longer kept.";
    }

    [RelayCommand]
    private async Task ExportWav()
    {
        if (_main.Session == null) return;
        var target = await Dialogs.SaveFile("Export take as WAV", $"take-{Take:0000}.wav", "wav", new FileFilter("WAV", "*.wav"));
        if (target != null) await _main.Session.ExportTakeAsWavAsync(Item.Id, Take, target);
    }
}
