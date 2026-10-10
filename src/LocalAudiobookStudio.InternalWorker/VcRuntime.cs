using System.Runtime.InteropServices;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// The Microsoft Visual C++ runtime that whisper.cpp's and ONNX Runtime's Windows builds need. A fresh Windows may not
/// have it, so the build bundles its DLLs into the worker (app-local deployment, which the redistributable's licence
/// allows; self-extract unpacks them into AppContext.BaseDirectory). They are loaded only when Windows has none of its own: the system's copy is kept up to date by Windows
/// Update. Loading them up front makes every later library find them by name, also the GPU builds in the data folder.
/// </summary>
internal static class VcRuntime
{
    // In load order: the runtime first, then the libraries built on it.
    private static readonly string[] Dlls = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "vcomp140.dll"];

    /// <summary>Whether the runtime is there: in Windows, or the worker's own copy. Always true off Windows.</summary>
    public static bool Available =>
        !OperatingSystem.IsWindows() || Dlls.All(d => File.Exists(Path.Combine(Environment.SystemDirectory, d)) || File.Exists(Path.Combine(AppContext.BaseDirectory, d)));

    public static void LoadIfMissing()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var dll in Dlls)
        {
            var local = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(Path.Combine(Environment.SystemDirectory, dll)) || !File.Exists(local)) continue;
            if (!NativeLibrary.TryLoad(local, out _)) Console.Error.WriteLine($"Could not load {local}");
        }
    }
}
