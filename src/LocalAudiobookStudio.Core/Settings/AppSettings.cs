using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Core.Settings;

/// <summary>
/// Machine-specific settings in <c>settings.json</c> in the config folder. Never stored in a project,
/// so a project opens on any machine regardless of its GPUs.
/// </summary>
public sealed class AppSettings
{
    public string? DataRoot { get; set; }
    public List<string> RecentProjects { get; set; } = new();
    public string? AudioOutputDevice { get; set; }
    /// <summary>Microphone used to record segments of speakers without a worker (null = system default).</summary>
    public string? AudioInputDevice { get; set; }
    public string? FfmpegPath { get; set; }
    /// <summary>The GPUs and the CPU workers run on, with their limits.</summary>
    public List<DeviceSettings> Devices { get; set; } = new();

    /// <summary>Per-worker settings on this machine (manifest "workerSettings"), by worker id.</summary>
    public Dictionary<string, JsonObject> WorkerSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Hugging Face access token for gated models (Stable Audio 3), sent only to the Hub. Stored in plain text
    /// in settings.json, so it should be a read-only token.
    /// </summary>
    public string? HuggingFaceToken { get; set; }

    /// <summary>Models the user points at their own folder (a finetune), by model id (workers/models.json).</summary>
    public Dictionary<string, string> ModelOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Silence trim of new takes: quieter than this counts as silence (dBFS).</summary>
    public double SilenceThresholdDb { get; set; } = -50;

    /// <summary>Silence trim of new takes: how much of the silence to keep before and after the sound (ms).</summary>
    public double TrimPaddingMs { get; set; } = 100;

    public int WorkerIdleTimeoutMin { get; set; } = 15;
    public int GenerationTimeoutMin { get; set; } = 10;
    public bool FirstRunCompleted { get; set; }
    public UiSettings Ui { get; set; } = new();

    public void AddRecent(string folder)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, folder);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
    }
}

/// <summary>
/// One device workers run on: a GPU or the CPU. Its limits apply to the worker processes on it.
/// </summary>
public sealed class DeviceSettings
{
    /// <summary>GPU index for CUDA_VISIBLE_DEVICES, or -1 for the CPU.</summary>
    public int Gpu { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>How many workers may generate (or load a model) at the same time here. Null = 1.</summary>
    public int? MaxActive { get; set; }

    /// <summary>How many worker processes may exist here, idle ones included. Null = any.</summary>
    public int? MaxTotal { get; set; }

    /// <summary>CPU only: threads a worker may use (LAS_CPU_THREADS); 0 leaves it to PyTorch (all cores).</summary>
    public int CpuThreads { get; set; }
}

public sealed class UiSettings
{
    public string TextFont { get; set; } = "Georgia";
    public int TextSizePx { get; set; } = 15;
    /// <summary>
    /// Leaving a segment's changed text asks whether to save it. Off: it is saved (Escape still discards).
    /// </summary>
    public bool ConfirmTextEdits { get; set; } = true;
    /// <summary>Segment rows show their number in the chapter (as a per-segment export numbers them).</summary>
    public bool ShowSegmentNumbers { get; set; } = true;
    /// <summary>Segment rows show their length and end time under the speaker.</summary>
    public bool ShowSegmentTimes { get; set; } = true;
    public PanelSettings Panels { get; set; } = new();
    public WindowBounds? WindowBounds { get; set; }
    public double ChapterPanelWidth { get; set; } = 220;
    /// <summary>Heights of the Speakers and Assets sections of the left panel (Chapters takes the rest).</summary>
    public double SpeakersPanelHeight { get; set; } = 170;
    public double AssetsPanelHeight { get; set; } = 170;
    public double InspectorWidth { get; set; } = 320;
    public double StackSpeakerColumnWidth { get; set; } = 100;
    public double StackAudioColumnWidth { get; set; } = 340;
    public double Volume { get; set; } = 0.9;
    /// <summary>Workers window: also list workers whose runtimes are all experimental.</summary>
    public bool ShowAllWorkers { get; set; }
}

public sealed class PanelSettings
{
    public bool Speakers { get; set; } = true;
    public bool Chapters { get; set; } = true;
    public bool Assets { get; set; } = true;
    public bool Inspector { get; set; } = true;
}

public sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}
