using System.Runtime.InteropServices;

namespace LocalAudiobookStudio.Workers.Environments;

/// <summary>Layout of the data root that holds uv's cache, Python builds, venvs and model weights.</summary>
public sealed class DataRootPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string UvCache => Path.Combine(Root, "uv-cache");
    public string Python => Path.Combine(Root, "python");
    public string Envs => Path.Combine(Root, "envs");
    public string Models => Path.Combine(Root, "models");

    /// <summary>The app's model store: one folder per model of workers/models.json (see ModelStore).</summary>
    public string ModelStore => Path.Combine(Models, "store");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");
    public string Temp => Path.Combine(Root, "tmp");

    /// <summary>HartsyInference's settings file for the Hartsy workers (instead of ~/.config/hartsyinference).</summary>
    public string HartsySettingsFile => Path.Combine(Root, "hartsy-settings.json");

    public string EnvDir(string group) => Path.Combine(Envs, group);
    public string EnvStateFile(string group) => Path.Combine(EnvDir(group), ".env-state.json");

    /// <summary>The venv's interpreter: Scripts\python.exe on Windows, bin/python elsewhere.</summary>
    public string EnvPython(string group) => VenvPython(EnvDir(group));

    public static string VenvPython(string venvDir) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(venvDir, "Scripts", "python.exe")
            : Path.Combine(venvDir, "bin", "python");

    /// <summary>
    /// False when the venv's pyvenv.cfg names a base Python ("home = …") that no longer has an interpreter.
    /// True when it does, or when there's no pyvenv.cfg to tell.
    /// </summary>
    public static bool VenvBaseExists(string venvDir)
    {
        var cfg = Path.Combine(venvDir, "pyvenv.cfg");
        if (!File.Exists(cfg)) return true;
        string? home = null;
        try
        {
            foreach (var line in File.ReadLines(cfg))
            {
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals("home", StringComparison.OrdinalIgnoreCase)) home = line[(eq + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            return true;
        }
        if (string.IsNullOrEmpty(home)) return true;
        string[] names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["python.exe"] : ["python3", "python"];
        return names.Any(n => File.Exists(Path.Combine(home, n)));
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, UvCache, Python, Envs, Models, Logs, Cache, Temp }) Directory.CreateDirectory(d);
    }

    /// <summary>
    /// Environment variables for every uv invocation and every worker process, so nothing touches
    /// the system Python, a global uv, or the user's global caches.
    /// </summary>
    public Dictionary<string, string> BaseEnvironment() => new()
    {
        ["UV_CACHE_DIR"] = UvCache,
        ["UV_PYTHON_INSTALL_DIR"] = Python,
        ["UV_LINK_MODE"] = "hardlink",
        ["UV_PYTHON_PREFERENCE"] = "only-managed",
        ["UV_NO_CONFIG"] = "1",
        ["HF_HOME"] = Models,
        ["TORCH_HOME"] = Path.Combine(Models, "torch"),
        // Libraries without their own variable fall back to XDG_CACHE_HOME (or ~/.cache).
        ["XDG_CACHE_HOME"] = Cache,
        // These ignore XDG_CACHE_HOME on Windows and would write into the user profile (a portable copy
        // must keep everything in its own folder).
        ["PIP_CACHE_DIR"] = Path.Combine(Cache, "pip"),
        ["MPLCONFIGDIR"] = Path.Combine(Cache, "matplotlib"),
        ["NUMBA_CACHE_DIR"] = Path.Combine(Cache, "numba"),
        ["TRITON_CACHE_DIR"] = Path.Combine(Cache, "triton"),
        ["CUDA_CACHE_PATH"] = Path.Combine(Cache, "nvidia-compute"),
        ["TEMP"] = Temp,
        ["TMP"] = Temp,
        ["TMPDIR"] = Temp,
        // Windows without Developer Mode can't create symlinks; HF then copies files, which is fine.
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        ["PYTHONUNBUFFERED"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1",
    };

    /// <summary>True when both paths are on the same volume (uv can only hardlink within one filesystem).</summary>
    public static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }
}
