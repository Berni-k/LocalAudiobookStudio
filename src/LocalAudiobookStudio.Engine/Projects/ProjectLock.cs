namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>
/// Marks a project as open (a file in its .cache folder, held open without sharing), so the command line
/// doesn't change a project the app has open, and the app can tell when the command line is using one.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    private readonly FileStream _stream;

    private ProjectLock(FileStream stream) => _stream = stream;

    public static string PathFor(string projectRoot) => Path.Combine(projectRoot, ".cache", "open.lock");

    /// <summary>The lock, or null when another program holds it.</summary>
    public static ProjectLock? TryAcquire(string projectRoot, string owner)
    {
        var path = PathFor(projectRoot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{owner} (process {Environment.ProcessId})");
            stream.SetLength(0);
            stream.Write(bytes);
            stream.Flush();
            return new ProjectLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
