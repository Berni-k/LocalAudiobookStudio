using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Devices;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Workers.Environments;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>Machine settings: data root, devices (GPUs, CPU) and their limits, audio device, ffmpeg, timeouts and fonts.</summary>
public sealed partial class AppSettingsViewModel : ObservableObject
{
    public AppSettingsViewModel()
    {
        var s = AppHost.Settings;
        DataRoot = SettingsStore.ResolveDataRoot(s);
        FfmpegPath = s.FfmpegPath ?? "";
        IdleTimeout = s.WorkerIdleTimeoutMin;
        GenerationTimeout = s.GenerationTimeoutMin;
        HuggingFaceToken = s.HuggingFaceToken ?? "";
        SilenceThreshold = (decimal)s.SilenceThresholdDb;
        TrimPadding = (decimal)s.TrimPaddingMs;
        TextFont = s.Ui.TextFont;
        TextSize = s.Ui.TextSizePx;
        ConfirmTextEdits = s.Ui.ConfirmTextEdits;
        ShowSegmentNumbers = s.Ui.ShowSegmentNumbers;
        ShowSegmentTimes = s.Ui.ShowSegmentTimes;
        Devices.Add(new AudioDeviceInfo("", "System default"));
        foreach (var d in AudioOutputFactory.ListDevices()) Devices.Add(d);
        Device = Devices.FirstOrDefault(d => d.Id == (s.AudioOutputDevice ?? "")) ?? Devices[0];
        InputDevices.Add(new AudioDeviceInfo("", "System default"));
        foreach (var d in AudioRecorder.ListDevices()) InputDevices.Add(d);
        InputDevice = InputDevices.FirstOrDefault(d => d.Id == (s.AudioInputDevice ?? "")) ?? InputDevices[0];
        // GPUs first, the CPU last (it is always there).
        foreach (var d in s.Devices.OrderBy(d => d.Gpu < 0)) ComputeDevices.Add(new DeviceSettingsViewModel(d));
        if (ComputeDevices.All(d => !d.IsCpu)) ComputeDevices.Add(new DeviceSettingsViewModel(new DeviceSettings { Gpu = -1 }));
        FfmpegFound = Ffmpeg.Locate(s.FfmpegPath, AppHost.DataRoot.Root) ?? "not found";
    }

    [ObservableProperty] private string _dataRoot;
    [ObservableProperty] private string _ffmpegPath;
    [ObservableProperty] private string _ffmpegFound;
    [ObservableProperty] private decimal _idleTimeout;
    [ObservableProperty] private decimal _generationTimeout;
    /// <summary>For gated models; sent only to Hugging Face when the app downloads a model.</summary>
    [ObservableProperty] private string _huggingFaceToken = "";
    /// <summary>Silence trim of new takes: threshold in dBFS and the silence kept in ms.</summary>
    [ObservableProperty] private decimal _silenceThreshold;
    [ObservableProperty] private decimal _trimPadding;
    [ObservableProperty] private string _textFont;
    [ObservableProperty] private decimal _textSize;
    [ObservableProperty] private bool _confirmTextEdits;
    [ObservableProperty] private bool _showSegmentNumbers;
    [ObservableProperty] private bool _showSegmentTimes;
    [ObservableProperty] private AudioDeviceInfo? _device;
    /// <summary>Microphone for recording (speakers without a worker).</summary>
    [ObservableProperty] private AudioDeviceInfo? _inputDevice;
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    [ObservableProperty] private string? _dataRootWarning;

    /// <summary>Portable or user profile, and where the settings file is.</summary>
    public string StorageText => SettingsStore.Mode == StorageMode.Portable
        ? $"Portable: settings are in {SettingsStore.SettingsFile}. Delete or rename that 'data' folder to use the user profile instead."
        : $"Settings are in {SettingsStore.SettingsFile}. For a portable copy, create a 'data' folder next to the app (or move these files there).";

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    /// <summary>The GPUs and the CPU, with their limits (<see cref="Devices"/> are the audio outputs).</summary>
    public ObservableCollection<DeviceSettingsViewModel> ComputeDevices { get; } = new();
    public IReadOnlyList<string> Fonts { get; } = ["Georgia", "Cambria", "Palatino Linotype", "Book Antiqua", "Times New Roman", "Segoe UI", "Arial", "Verdana", "Noto Serif", "DejaVu Serif", "Liberation Serif"];

    partial void OnDataRootChanged(string value)
    {
        var current = AppHost.DataRoot.Root;
        if (string.Equals(Path.GetFullPath(value), current, StringComparison.OrdinalIgnoreCase)) { DataRootWarning = null; return; }
        DataRootWarning = "Takes effect after restarting the app. Environments and models are not moved: reinstall workers at the new location" +
                          (DataRootPaths.SameVolume(value, current) ? "." : ". The new folder is on a different drive; uv can only hardlink within one drive, so keep the whole data root together.");
    }

    [RelayCommand]
    private async Task BrowseDataRoot()
    {
        var f = await Dialogs.PickFolder("Data root (environments, models, caches)");
        if (f != null) DataRoot = f;
    }

    [RelayCommand]
    private async Task BrowseFfmpeg()
    {
        var f = await Dialogs.OpenFile("ffmpeg executable");
        if (f != null) { FfmpegPath = f; FfmpegFound = Ffmpeg.Locate(f, AppHost.DataRoot.Root) ?? "not found"; }
    }

    [RelayCommand]
    private async Task DetectGpus()
    {
        await AppHost.DetectGpusAsync(addMissing: false);
        foreach (var g in AppHost.Gpus)
            if (ComputeDevices.All(d => d.Gpu != g.Index))
                ComputeDevices.Insert(ComputeDevices.Count(d => !d.IsCpu), new DeviceSettingsViewModel(new DeviceSettings { Gpu = g.Index, Enabled = true }));
    }

    public void Apply()
    {
        var s = AppHost.Settings;
        var root = Path.GetFullPath(DataRoot);
        s.DataRoot = SettingsStore.ToStoredDataRoot(root);
        s.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath;
        s.WorkerIdleTimeoutMin = (int)IdleTimeout;
        s.GenerationTimeoutMin = Math.Max(1, (int)GenerationTimeout);
        s.HuggingFaceToken = string.IsNullOrWhiteSpace(HuggingFaceToken) ? null : HuggingFaceToken.Trim();
        s.SilenceThresholdDb = (double)Math.Clamp(SilenceThreshold, -90, -20);
        s.TrimPaddingMs = (double)Math.Clamp(TrimPadding, 0, 1000);
        s.Ui.TextFont = TextFont;
        s.Ui.TextSizePx = (int)TextSize;
        s.Ui.ConfirmTextEdits = ConfirmTextEdits;
        s.Ui.ShowSegmentNumbers = ShowSegmentNumbers;
        s.Ui.ShowSegmentTimes = ShowSegmentTimes;
        StackColumns.Shared.ShowNumbers = ShowSegmentNumbers;
        StackColumns.Shared.ShowTimes = ShowSegmentTimes;
        s.AudioOutputDevice = string.IsNullOrEmpty(Device?.Id) ? null : Device!.Id;
        s.AudioInputDevice = string.IsNullOrEmpty(InputDevice?.Id) ? null : InputDevice!.Id;
        s.Devices = ComputeDevices.Select(v => v.ToSettings()).ToList();
        AppHost.Scheduler.Options.WorkerIdleTimeout = s.WorkerIdleTimeoutMin <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(s.WorkerIdleTimeoutMin);
        AppHost.SaveSettings();
        AppHost.ApplyDeviceSettings();
    }
}

/// <summary>One GPU or the CPU in the settings: enabled, and how many workers may run there.</summary>
public sealed partial class DeviceSettingsViewModel : ObservableObject
{
    private readonly DeviceSettings _source;

    public DeviceSettingsViewModel(DeviceSettings s)
    {
        _source = s;
        Gpu = s.Gpu;
        Name = s.Gpu >= 0 ? $"GPU {s.Gpu}" : "CPU";
        Detail = s.Gpu < 0 ? "Workers set to the CPU" : AppHost.Gpus.FirstOrDefault(g => g.Index == s.Gpu) is { } g
            ? $"{g.Name} · {g.MemoryMb / 1024.0:0} GB"
            : "not detected";
        Enabled = s.Enabled;
        MaxActive = s.MaxActive is > 0 and var a ? a : null;
        MaxTotal = s.MaxTotal is > 0 and var t ? t : null;
    }

    public int Gpu { get; }
    public bool IsCpu => Gpu < 0;
    public string Name { get; }
    public string Detail { get; }

    [ObservableProperty] private bool _enabled;
    /// <summary>Empty = 1.</summary>
    [ObservableProperty] private decimal? _maxActive;
    /// <summary>Empty = any.</summary>
    [ObservableProperty] private decimal? _maxTotal;

    public DeviceSettings ToSettings() => new()
    {
        Gpu = Gpu,
        Name = _source.Name,
        Enabled = Enabled,
        MaxActive = MaxActive is decimal a && a >= 1 ? (int)a : null,
        MaxTotal = MaxTotal is decimal t && t >= 1 ? (int)t : null,
        CpuThreads = _source.CpuThreads,
    };
}

/// <summary>Title, author, cover and mixing defaults of the open project.</summary>
public sealed partial class ProjectPropertiesViewModel : ObservableObject
{
    private readonly Session.ProjectSession _session;

    public ProjectPropertiesViewModel(Session.ProjectSession session)
    {
        _session = session;
        var f = session.Project.File;
        Title = f.Title;
        Author = f.Author;
        Cover = f.CoverImage;
        TargetLufs = (decimal)f.Defaults.TargetLoudnessLufs;
        GapMs = f.Defaults.GapAfterMs;
        ChapterGapMs = f.Defaults.ChapterGapMs;
        DefaultSpeaker = session.SpeakerVm(f.Defaults.Speaker) ?? session.Speakers.FirstOrDefault();
        SampleRates = Core.Model.ProjectDefaults.SampleRates.Concat([f.Defaults.SampleRate]).Distinct().Order()
            .Select(hz => new SampleRateOption(hz)).ToList();
        SampleRate = SampleRates.First(o => o.Hz == f.Defaults.SampleRate);
        AudioFormat = AudioFormats.FirstOrDefault(o => o.Value == f.Defaults.AudioFormat) ?? AudioFormats[0];
        OpusQualities = Core.Model.ProjectDefaults.OpusKbpsOptions.Concat([f.Defaults.OpusKbps]).Distinct().Order()
            .Select(k => new OpusQualityOption(k)).ToList();
        OpusQuality = OpusQualities.First(o => o.Kbps == f.Defaults.OpusKbps);
        Folder = session.Paths.Root;
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _author;
    [ObservableProperty] private string? _cover;
    [ObservableProperty] private decimal _targetLufs;
    [ObservableProperty] private decimal _gapMs;
    [ObservableProperty] private decimal _chapterGapMs;
    [ObservableProperty] private SpeakerViewModel? _defaultSpeaker;
    /// <summary>The common rates, plus the project's own if it is another one.</summary>
    public IReadOnlyList<SampleRateOption> SampleRates { get; }
    [ObservableProperty] private SampleRateOption _sampleRate;

    public IReadOnlyList<EnumOption> AudioFormats { get; } =
    [
        new(Core.Model.AudioFormats.Opus, "Opus (small)"),
        new(Core.Model.AudioFormats.Wav, "WAV (uncompressed)"),
    ];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpus))]
    private EnumOption _audioFormat;

    public bool IsOpus => AudioFormat.Value == Core.Model.AudioFormats.Opus;

    /// <summary>The offered Opus qualities, plus the project's own if it is another one.</summary>
    public IReadOnlyList<OpusQualityOption> OpusQualities { get; }
    [ObservableProperty] private OpusQualityOption _opusQuality;
    public string Folder { get; }
    public ObservableCollection<SpeakerViewModel> Speakers => _session.Speakers;

    [RelayCommand]
    private async Task BrowseCover()
    {
        var f = await Dialogs.OpenFile("Cover image", new FileFilter("Images", "*.jpg", "*.jpeg", "*.png"));
        if (f == null) return;
        // Copy the cover into the project so it travels with it.
        var dest = Path.Combine(_session.Paths.Root, "cover" + Path.GetExtension(f).ToLowerInvariant());
        if (!string.Equals(Path.GetFullPath(f), dest, StringComparison.OrdinalIgnoreCase)) File.Copy(f, dest, overwrite: true);
        Cover = Path.GetFileName(dest);
    }

    public void Apply()
    {
        _session.Project.File.CoverImage = string.IsNullOrWhiteSpace(Cover) ? null : Cover;
        _session.SetProjectInfo(Title.Trim(), Author.Trim(), (double)TargetLufs, (int)GapMs, (int)ChapterGapMs,
            DefaultSpeaker?.Id ?? _session.Project.Defaults.Speaker, SampleRate.Hz, AudioFormat.Value, OpusQuality.Kbps);
    }
}

/// <summary>One entry of the Opus quality dropdown: "Standard (64 kbps per channel)".</summary>
public sealed record OpusQualityOption(int Kbps)
{
    public override string ToString() => Kbps switch
    {
        48 => "Smaller",
        64 => "Standard",
        96 => "High",
        _ => "Custom",
    } + $" ({Kbps} kbps per channel)";
}

/// <summary>One entry of the sample rate dropdown: "44.1 kHz".</summary>
public sealed record SampleRateOption(int Hz)
{
    public override string ToString() =>
        (Hz / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " kHz" + (Hz == 44100 ? " (CD)" : "");
}
