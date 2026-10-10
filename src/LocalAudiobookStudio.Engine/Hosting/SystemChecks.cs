using System.Globalization;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Engine.Hosting;

/// <summary>
/// Ok: a green checkmark. Partial: what's needed is there, but something optional is missing (a yellow checkmark).
/// Warning: a yellow warning sign. Missing: a red X, the app can't work properly without it. Info: no icon.
/// </summary>
public enum CheckStatus { Ok, Partial, Warning, Missing, Info }

/// <summary>
/// One line of a check's details: text, or a path (shown in monospace), with an optional label in front.
/// A warning line's text (not its label) is yellow.
/// </summary>
public sealed record CheckLine(string? Label, string Text, bool IsPath, bool IsWarning = false)
{
    public static CheckLine Plain(string text, string? label = null) => new(label, text, false);
    public static CheckLine Warn(string text, string? label = null) => new(label, text, false, IsWarning: true);
    public static CheckLine Path(string path, string? label = null) => new(label, path, true);
    public bool HasLabel => Label != null;
    public bool IsText => !IsPath;
    public override string ToString() => (Label != null ? Label + ": " : "") + Text;
}

/// <summary>What the app can do about a failed check.</summary>
public enum CheckFixKind
{
    /// <summary>Download ffmpeg into the data folder (Windows).</summary>
    DownloadFfmpeg,
    /// <summary>Download uv into the data folder.</summary>
    DownloadUv,
    /// <summary>A command for the user to run (for example installing ffmpeg with the package manager, which needs sudo).</summary>
    RunCommand,
}

/// <summary>A fix offered next to a check: a download button, or a command to copy.</summary>
public sealed record CheckFix(CheckFixKind Kind, string Label, string? Command = null);

/// <summary>One entry of the system check (the setup window, Help → System check, <c>las doctor</c>).</summary>
public sealed record SystemCheck(string Title, CheckStatus Status, IReadOnlyList<CheckLine> Lines, CheckFix? Fix = null)
{
    public SystemCheck(string title, CheckStatus status, string detail, CheckFix? fix = null) : this(title, status, [CheckLine.Plain(detail)], fix) { }

    /// <summary>The details as plain text (logs).</summary>
    public string Detail => string.Join("\n", Lines);
    public bool IsOk => Status == CheckStatus.Ok;
    public bool IsPartial => Status == CheckStatus.Partial;
    public bool IsWarning => Status == CheckStatus.Warning;
    public bool IsMissing => Status == CheckStatus.Missing;
    public bool IsInfo => Status == CheckStatus.Info;
}

/// <summary>
/// What the app needs from the machine, checked before anything is installed: the GPU and its driver, the
/// bundled tools and export. Runs before the <see cref="StudioHost"/> starts, so it only uses static helpers.
/// Audio devices are checked by the app, which passes that check in.
/// </summary>
public static class SystemChecks
{
    /// <summary>
    /// The Python workers' torch build (workers/environments.json: torch 2.11, CUDA 12.8) needs this driver and
    /// at least compute capability 7.5 (Turing, RTX 20 series).
    /// </summary>
    public const int MinDriverMajor = 570;

    /// <summary>ONNX Runtime's GPU build (workers/environments.json: onnx1_30-cu13) uses CUDA 13.</summary>
    public const int MinDriverMajorCuda13 = 580;
    public const double MinComputeCapability = 7.5;

    /// <summary>Below this much free space on the data root's drive the check warns.</summary>
    public const long LowDiskBytes = 40L * 1024 * 1024 * 1024;

    /// <summary>torch wheels for Linux are manylinux_2_28: they need this glibc or newer.</summary>
    public static readonly Version MinGlibc = new(2, 28);

    /// <param name="ffmpegPath">The ffmpeg path from the settings, if one is set.</param>
    /// <param name="dataRoot">The data folder: tools downloaded by <see cref="ToolInstaller"/> are found there.</param>
    /// <param name="extraChecks">More checks to run last, off the UI thread (the app's audio devices).</param>
    public static async Task<IReadOnlyList<SystemCheck>> RunAsync(string? ffmpegPath, string dataRoot, params Func<SystemCheck>[] extraChecks)
    {
        var gpus = await GpuDetector.DetectAsync();
        // Off the UI thread: the library lookups can start ldconfig, and listing audio devices can be slow.
        return await Task.Run(() =>
        {
            List<SystemCheck> checks = [GpuCheck(gpus, DisplayAdapterDetector.Detect())];
            // Without an NVIDIA GPU the driver doesn't matter (models run on the CPU).
            if (gpus.Count > 0) checks.Add(DriverCheck(gpus));
            if (OperatingSystem.IsLinux()) checks.Add(GlibcCheck());
            checks.Add(UvCheck(dataRoot));
            // The CUDA libraries only matter with an NVIDIA GPU (HartsyInference and the internal GPU runtime load them).
            if (gpus.Count > 0) checks.Add(CudaLibrariesCheck());
            checks.AddRange([HartsyCheck(), InternalWorkerCheck(), FfmpegCheck(ffmpegPath, dataRoot)]);
            checks.AddRange(extraChecks.Select(c => c()));
            return (IReadOnlyList<SystemCheck>)checks;
        });
    }

    /// <summary>
    /// The NVIDIA GPUs (nvidia-smi) workers run on, plus every other graphics adapter (DXGI / sysfs): an AMD
    /// or Intel GPU is named with a warning instead of looking like no GPU at all.
    /// </summary>
    private static SystemCheck GpuCheck(IReadOnlyList<GpuInfo> gpus, IReadOnlyList<DisplayAdapter> adapters)
    {
        var lines = new List<CheckLine>();
        foreach (var g in gpus)
            lines.Add(CheckLine.Plain($"GPU {g.Index}: {g.Name}, {g.MemoryMb / 1024.0:0} GB" + (g.ComputeCapability != null ? $" (compute {g.ComputeCapability})" : "")));
        var old = gpus.Where(g => ParseCapability(g.ComputeCapability) is { } cc && cc < MinComputeCapability).ToList();
        if (old.Count > 0)
            lines.Add(CheckLine.Warn($"{string.Join(", ", old.Select(g => $"GPU {g.Index}"))}: too old for the Python workers (they need an RTX 20 series or newer); HartsyInference workers may still run there."));

        // AMD and Intel GPUs (also a laptop's integrated graphics next to an NVIDIA GPU): named, not used.
        foreach (var a in adapters.Where(a => !a.IsNvidia)) lines.Add(CheckLine.Warn("Not supported", Describe(a)));
        if (gpus.Count > 0) return new("GPU", old.Count > 0 ? CheckStatus.Warning : CheckStatus.Ok, lines);

        if (adapters.Any(a => a.IsNvidia))
            return new("GPU", CheckStatus.Missing,
                $"{string.Join(", ", adapters.Where(a => a.IsNvidia).Select(Describe))}, but nvidia-smi doesn't answer: install or repair the NVIDIA driver (nvidia.com).");
        if (lines.Count == 0) return new("GPU", CheckStatus.Missing, "No GPU found: models will run on the CPU only.");
        lines.Add(CheckLine.Plain("Models will run on the CPU only."));
        return new("GPU", CheckStatus.Warning, lines);
    }

    private static string Describe(DisplayAdapter a) =>
        a.MemoryMb >= 1024 ? $"{a.Name}, {a.MemoryMb / 1024.0:0} GB" : a.MemoryMb > 0 ? $"{a.Name}, {a.MemoryMb} MB" : a.Name;

    private static SystemCheck DriverCheck(IReadOnlyList<GpuInfo> gpus)
    {
        var driver = gpus.Select(g => g.Driver).FirstOrDefault(d => d != null);
        if (driver == null || !int.TryParse(driver.Split('.')[0], out var major))
            return new("NVIDIA driver", CheckStatus.Warning,
                $"Version unknown (an old driver?). The Python workers need {MinDriverMajor} or newer (CUDA 12.8).");
        if (major >= MinDriverMajorCuda13) return new("NVIDIA driver", CheckStatus.Ok, driver);
        if (major >= MinDriverMajor)
            return new("NVIDIA driver", CheckStatus.Partial,
                $"{driver}: fine for the Python workers; the ONNX Runtime (CUDA) runtime needs {MinDriverMajorCuda13} or newer (CUDA 13).");
        return new("NVIDIA driver", CheckStatus.Missing,
            $"{driver} is too old: the Python workers need {MinDriverMajor} or newer (CUDA 12.8). Update the driver from nvidia.com.");
    }

    private static SystemCheck UvCheck(string dataRoot) =>
        EnvironmentManager.FindUv(dataRoot) is { } uv
            ? new("Python installer (uv)", CheckStatus.Ok, [CheckLine.Path(Display(uv))])
            : new("Python installer (uv)", CheckStatus.Missing,
                ToolInstaller.CanDownloadUv
                    ? "Not found: Python workers can't be installed without it. The app can download it, or install uv (https://docs.astral.sh/uv/) so it is on the PATH."
                    : "Not found: Python workers can't be installed without it. Install uv (https://docs.astral.sh/uv/) so it is on the PATH.",
                ToolInstaller.CanDownloadUv ? new CheckFix(CheckFixKind.DownloadUv, "Download uv") : null);

    /// <summary>The bundled HartsyInference worker (its CUDA libraries are <see cref="CudaLibrariesCheck"/>).</summary>
    private static SystemCheck HartsyCheck() =>
        EnvironmentManager.FindHartsyWorker() is { } worker
            ? new("HartsyInference", CheckStatus.Ok, [CheckLine.Path(Display(worker))])
            : new("HartsyInference", CheckStatus.Info,
                "Not found (optional): workers that only run on HartsyInference aren't available. Python workers are not affected.");

    /// <summary>
    /// The bundled internal worker (whisper.cpp, ONNX Runtime), and on Windows which Visual C++ runtime its native
    /// libraries use: Windows' own, else the copy inside the worker.
    /// </summary>
    private static SystemCheck InternalWorkerCheck()
    {
        if (EnvironmentManager.FindInternalWorker() is not { } worker)
            return new("Internal worker", CheckStatus.Warning,
                "Not found: the internal runtimes (whisper.cpp, ONNX on the CPU) aren't available. Reinstall the app.");
        List<CheckLine> lines = [CheckLine.Path(Display(worker))];
        if (!OperatingSystem.IsWindows()) return new("Internal worker", CheckStatus.Ok, lines);

        // The worker carries its own copy inside the exe and loads it when Windows has none (VcRuntime.cs).
        string[] vcRuntime = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "vcomp140.dll"];
        var installed = vcRuntime.All(d => File.Exists(Path.Combine(Environment.SystemDirectory, d)));
        return new("Internal worker", CheckStatus.Ok,
            [.. lines, CheckLine.Plain(installed ? "installed" : "not installed; the worker uses its own copy", "Visual C++ runtime")]);
    }

    /// <summary>
    /// The NVIDIA libraries GPU workers outside Python load from the system: the CUDA runtime and cuBLAS (the CUDA 12 or
    /// 13 toolkit) for HartsyInference and the internal GPU runtime's CUDA build, and cuDNN 9, which HartsyInference
    /// uses for faster convolutions when it's there. (Python workers bring their own.)
    /// </summary>
    private static SystemCheck CudaLibrariesCheck()
    {
        const string title = "CUDA libraries";
        var cudart = FindLibrary(CudartNames, out var cudartName);
        var cublas = FindLibrary(CublasNames, out _);
        var cudnn = FindLibrary(CudnnNames, out _);
        var lines = new List<CheckLine>();
        if (cudart != null)
            lines.Add(CheckLine.Path(cudart, $"CUDA {(cudartName!.Contains("13") ? 13 : 12)} runtime"));
        else
            lines.Add(CheckLine.Warn("not found (CUDA 12 or 13; the CUDA toolkit provides it)", "CUDA runtime"));
        lines.Add(cublas != null
            ? CheckLine.Path(cublas, "cuBLAS")
            : CheckLine.Warn("not found (CUDA 12 or 13; the CUDA toolkit provides it)", "cuBLAS"));
        if (cudnn != null)
        {
            lines.Add(CheckLine.Path(cudnn, "cuDNN 9"));
        }
        else if (FindInstalledCudnn() is { } installed)
        {
            lines.Add(CheckLine.Warn("not on the PATH (optional, for faster convolutions); add this folder:", "cuDNN 9"));
            lines.Add(CheckLine.Path(installed));
        }
        else
        {
            lines.Add(CheckLine.Warn("not found (optional, for faster convolutions)", "cuDNN 9"));
        }
        if (cudart == null || cublas == null)
            lines.Add(CheckLine.Plain("Without the CUDA toolkit, HartsyInference workers run on the CPU, and the internal GPU runtime uses Vulkan or the CPU. Python workers are not affected."));
        var status = cudart == null || cublas == null ? CheckStatus.Warning : cudnn == null ? CheckStatus.Partial : CheckStatus.Ok;
        return new(title, status, lines);
    }

    private static SystemCheck FfmpegCheck(string? configured, string dataRoot)
    {
        if (Ffmpeg.Locate(configured, dataRoot) is { } ffmpeg)
            return new("ffmpeg (export)", CheckStatus.Ok, [CheckLine.Path(Display(ffmpeg))]);
        const string needed = "Not found. Export (M4B, MP3 and WAV) and importing non-WAV audio need it.";
        if (ToolInstaller.CanDownloadFfmpeg)
            return new("ffmpeg (export)", CheckStatus.Warning, needed + " The app can download it (about 100 MB), or set its path in Settings.",
                new CheckFix(CheckFixKind.DownloadFfmpeg, "Download ffmpeg"));
        if (ToolInstaller.FfmpegInstallCommand() is { } command)
            return new("ffmpeg (export)", CheckStatus.Warning, needed + " Install it with your package manager, then check again:",
                new CheckFix(CheckFixKind.RunCommand, "Copy", command));
        return new("ffmpeg (export)", CheckStatus.Warning, needed + " Install it so it is on the PATH, or set its path in Settings.");
    }

    /// <summary>Free space on the data root's drive.</summary>
    public static SystemCheck DiskCheck(string dataRoot)
    {
        try
        {
            var full = Path.GetFullPath(dataRoot);
            // On Linux the path root is always "/", so pick the mount point that actually holds the folder.
            var drive = OperatingSystem.IsWindows()
                ? new DriveInfo(Path.GetPathRoot(full)!)
                : DriveInfo.GetDrives()
                    .Where(d => full == d.Name || full.StartsWith(d.Name.TrimEnd('/') + "/", StringComparison.Ordinal))
                    .MaxBy(d => d.Name.Length) ?? new DriveInfo("/");
            var free = drive.AvailableFreeSpace;
            var name = OperatingSystem.IsWindows() ? drive.Name.TrimEnd('\\', '/') : drive.Name;
            return new("Disk space", free < LowDiskBytes ? CheckStatus.Warning : CheckStatus.Ok,
                $"{free / (1024.0 * 1024 * 1024):0} GB free on {name}");
        }
        catch (Exception ex)
        {
            return new("Disk space", CheckStatus.Warning, "Couldn't check the drive: " + ex.Message);
        }
    }

    private static double? ParseCapability(string? cc) =>
        double.TryParse(cc, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>A path inside the app folder relative to it (tools\uv\win-x64\uv.exe); others as they are.</summary>
    private static string Display(string path)
    {
        var relative = Path.GetRelativePath(SettingsStore.AppDir, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    // What the GPU workers outside Python load: the CUDA runtime and cuBLAS from CUDA 13 or 12, and cuDNN 9 (optional).
    private static string[] CudartNames => OperatingSystem.IsWindows()
        ? ["cudart64_13.dll", "cudart64_12.dll"]
        : ["libcudart.so.13", "libcudart.so.12"];
    private static string[] CublasNames => OperatingSystem.IsWindows()
        ? ["cublas64_13.dll", "cublas64_12.dll"]
        : ["libcublas.so.13", "libcublas.so.12"];
    private static string[] CudnnNames => OperatingSystem.IsWindows() ? ["cudnn64_9.dll"] : ["libcudnn.so.9"];

    /// <summary>
    /// The folder holding one of <paramref name="names"/> (earlier names first), and which one: on Linux where the
    /// dynamic loader finds it (ldconfig's cache, LD_LIBRARY_PATH), otherwise on the PATH or in CUDA_PATH.
    /// </summary>
    private static string? FindLibrary(string[] names, out string? found)
    {
        found = null;
        if (OperatingSystem.IsLinux())
            foreach (var name in names)
                if (LdconfigCache().FirstOrDefault(e => e.Name == name) is { Path: { } cached })
                {
                    found = name;
                    return Path.GetDirectoryName(cached);
                }
        var dirs = new List<string>();
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (Environment.GetEnvironmentVariable("CUDA_PATH") is { Length: > 0 } cuda)
            dirs.AddRange([Path.Combine(cuda, "bin"), Path.Combine(cuda, "bin", "x64"), Path.Combine(cuda, "lib64")]);
        if (!OperatingSystem.IsWindows())
        {
            dirs.AddRange((Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries));
            dirs.AddRange(["/usr/local/cuda/lib64", "/usr/lib/x86_64-linux-gnu", "/usr/lib64"]);
        }
        foreach (var name in names)
        foreach (var d in dirs)
        {
            try
            {
                var dir = d.Trim('"');
                if (!File.Exists(Path.Combine(dir, name))) continue;
                found = name;
                return dir;
            }
            catch
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    private static List<(string Name, string Path)>? _ldconfig;

    /// <summary>
    /// The libraries the Linux loader knows (<c>ldconfig -p</c>: "libcublas.so.12 (libc6,x86-64) => /usr/…/libcublas.so.12").
    /// ldconfig is often outside a normal user's PATH, so /sbin and /usr/sbin are tried first.
    /// </summary>
    private static List<(string Name, string Path)> LdconfigCache()
    {
        if (_ldconfig != null) return _ldconfig;
        var result = new List<(string, string)>();
        foreach (var exe in new[] { "/sbin/ldconfig", "/usr/sbin/ldconfig", "ldconfig" })
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-p")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
                if (p == null) continue;
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } continue; }
                foreach (var line in output.Result.Split('\n'))
                {
                    var arrow = line.IndexOf("=>", StringComparison.Ordinal);
                    if (arrow < 0) continue;
                    var name = line[..arrow].Trim().Split(' ')[0];
                    result.Add((name, line[(arrow + 2)..].Trim()));
                }
                break;
            }
            catch
            {
                // Not there: try the next location.
            }
        }
        return _ldconfig = result;
    }

    /// <summary>Linux only: the glibc version, which decides whether torch's wheels install at all.</summary>
    private static SystemCheck GlibcCheck()
    {
        var version = GlibcVersion();
        if (version == null)
            return new("glibc", CheckStatus.Warning,
                $"Not found (a musl-based system?). The Python workers' torch needs glibc {MinGlibc} or newer.");
        return version >= MinGlibc
            ? new("glibc", CheckStatus.Ok, $"{version}")
            : new("glibc", CheckStatus.Missing,
                $"{version} is too old: the Python workers' torch needs glibc {MinGlibc} or newer (Ubuntu 20.04, Debian 10, RHEL 8 or later).");
    }

    [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "gnu_get_libc_version")]
    private static extern IntPtr GnuGetLibcVersion();

    private static Version? GlibcVersion()
    {
        try
        {
            var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(GnuGetLibcVersion());
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch
        {
            return null; // not glibc
        }
    }

    /// <summary>The cuDNN installer for Windows puts it under Program Files\NVIDIA\CUDNN without adding it to the PATH.</summary>
    private static string? FindInstalledCudnn()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA", "CUDNN");
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, CudnnNames[0], SearchOption.AllDirectories).Select(Path.GetDirectoryName).Max()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
