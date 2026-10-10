using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// Which whisper.cpp build the process loads. Whisper.net tries the builds in <see cref="RuntimeOptions.RuntimeLibraryOrder"/>
/// and keeps the first that loads (a CUDA build only when the toolkit's runtime finds a GPU); builds whose folder isn't
/// there are skipped. So the CPU runtime, which only has the CPU builds inside the exe, never tries a GPU, and the GPU
/// runtime falls back from CUDA to Vulkan to the CPU by itself.
/// </summary>
internal static class WhisperBackend
{
    /// <summary>The "backend" worker setting: "auto" follows the device the app assigned; the others force one build.</summary>
    private static string _choice = "auto";

    /// <summary>Whisper.net's and whisper.cpp's own messages while loading, shown when no build loads.</summary>
    private static readonly Queue<string> LoadLog = new();

    public static bool OnCpuSlot => Environment.GetEnvironmentVariable("LAS_DEVICE") == "cpu";

    /// <summary>
    /// Must run before the first <see cref="WhisperFactory"/> call: the library is loaded once per process.
    /// <paramref name="gpuFolder"/>: the GPU runtime's whisper folder (runtimes/vulkan, runtimes/cuda…), or null.
    /// </summary>
    public static void Configure(string? gpuFolder, string? choice)
    {
        _choice = (choice ?? "auto").Trim().ToLowerInvariant();
        // whisper.cpp's messages go to the worker log: Info and up as they come, Debug (Whisper.net's library search)
        // only into LoadLog, which a load failure prints. "Cont" continues the previous message.
        var lastShown = false;
        LogProvider.AddLogger((level, message) =>
        {
            var text = message?.TrimEnd() ?? "";
            if (text.Length == 0) return;
            lock (LoadLog)
            {
                LoadLog.Enqueue(text);
                while (LoadLog.Count > 60) LoadLog.Dequeue();
            }
            var show = level == WhisperLogLevel.Cont ? lastShown : level is WhisperLogLevel.Error or WhisperLogLevel.Warning or WhisperLogLevel.Info;
            lastShown = show;
            if (show) Console.Error.WriteLine("whisper.cpp: " + text);
        });
        // Whisper.net searches <directory of LibraryPath>/runtimes/<backend>/<rid> before the exe's own folder.
        if (gpuFolder != null) RuntimeOptions.LibraryPath = Path.Combine(gpuFolder, "whisper");
        RuntimeOptions.RuntimeLibraryOrder = _choice switch
        {
            "cpu" => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            // A forced GPU build alone: if it can't load, the error says so instead of quietly running on the CPU.
            // CUDA 13 or 12: only the build for the user's toolkit is downloaded, and each checks its toolkit before loading.
            "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
            "vulkan" => [RuntimeLibrary.Vulkan],
            // "auto": the device the app picked. A CPU process stays on the CPU even when GPU builds are installed.
            _ when OnCpuSlot => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        };
    }

    /// <summary>Loads the native library (without a model) and returns which build it is.</summary>
    public static RuntimeLibrary Load()
    {
        if (RuntimeOptions.LoadedLibrary is { } loaded) return loaded;
        try
        {
            WhisperFactory.GetRuntimeInfo();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(LoadFailure(ex.Message), ex);
        }
        return RuntimeOptions.LoadedLibrary
               ?? throw new InvalidOperationException(LoadFailure("no build of whisper.cpp could be loaded"));
    }

    public static bool UsesGpu(RuntimeLibrary library) => library is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 or RuntimeLibrary.Vulkan;

    /// <summary>
    /// The GPU index whisper.cpp should use. CUDA_VISIBLE_DEVICES already narrows CUDA to the app's chosen GPU (index 0).
    /// Vulkan ignores that variable and numbers the GPUs itself, usually in the same order for NVIDIA cards, so the
    /// app's index is passed on (a machine whose integrated GPU comes first may need the "GPU ids" worker setting).
    /// </summary>
    public static int GpuDevice(RuntimeLibrary library) =>
        library == RuntimeLibrary.Vulkan && int.TryParse(Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES"), out var g) ? g : 0;

    public static string Label(RuntimeLibrary library) => library switch
    {
        RuntimeLibrary.Cuda => "CUDA 13",
        RuntimeLibrary.Cuda12 => "CUDA 12",
        RuntimeLibrary.Vulkan => "Vulkan",
        RuntimeLibrary.CpuNoAvx => "CPU (without AVX)",
        _ => "CPU",
    };

    /// <summary>For hello, before anything is loaded: where the app asked this process to run.</summary>
    public static string DeviceDescription()
    {
        if (OnCpuSlot && _choice == "auto") return "cpu";
        var cvd = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        return _choice == "auto" ? $"gpu {cvd ?? "0"} (backend chosen when loading)" : $"{_choice} (forced)";
    }

    private static string LoadFailure(string detail)
    {
        var hint = _choice switch
        {
            "cuda" => " The CUDA build needs an NVIDIA GPU, the CUDA 12 or 13 toolkit and the GPU runtime installed (Workers window). Set Backend to Auto to fall back to Vulkan or the CPU.",
            "vulkan" => " The Vulkan build needs a GPU driver with Vulkan support and the GPU runtime installed. Set Backend to Auto to fall back to the CPU.",
            _ => "",
        };
        if (!VcRuntime.Available)
            hint += " The Microsoft Visual C++ Redistributable (x64) seems to be missing: install it from https://aka.ms/vs/17/release/vc_redist.x64.exe and try again.";
        string log;
        lock (LoadLog) log = string.Join("\n", LoadLog);
        return $"whisper.cpp could not be loaded: {detail}.{hint}" + (log.Length > 0 ? $"\nWhisper.net's log:\n{log}" : "");
    }
}
