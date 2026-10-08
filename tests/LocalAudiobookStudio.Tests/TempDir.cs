namespace LocalAudiobookStudio.Tests;

/// <summary>A temporary folder, deleted when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "las-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

/// <summary>Files of the repository the tests run from (shipped workers, bundled examples).</summary>
public static class Repo
{
    public static string Root { get; } = Find();

    /// <summary>Walks up from this source file (its path is fixed at compile time), so it works wherever the build output goes.</summary>
    private static string Find([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(sourceFile)!);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "LocalAudiobookStudio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException($"No repository above {sourceFile}.");
    }

    public static string Combine(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);
}
