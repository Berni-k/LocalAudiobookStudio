using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Models;

namespace LocalAudiobookStudio.Workers.Environments;

/// <summary>One downloaded model: its name, its folder in the store and its size on disk.</summary>
public sealed record ModelUsage(string Name, string Folder, long Bytes);

/// <summary>Bytes on disk for one worker: its environment (venv), each downloaded model and its own folder.</summary>
public sealed record WorkerDiskUsage(long EnvBytes, IReadOnlyList<ModelUsage> Models, long FilesBytes)
{
    public long ModelBytes => Models.Sum(m => m.Bytes);
    public long TotalBytes => EnvBytes + ModelBytes + FilesBytes;

    /// <summary>Walks the folders; can take a few seconds for large environments, so call it off the UI thread.</summary>
    public static WorkerDiskUsage Measure(WorkerManifest worker, DataRootPaths dataRoot, ModelStore store, CancellationToken ct = default)
    {
        var models = store.Catalog.WithRequired(worker.Models)
            .Where(id => store.GetStatus(id) is not ModelStatus.NotDownloaded and not ModelStatus.Override)
            .Select(id => new ModelUsage(store.Catalog.Get(id)?.Name ?? id, store.FolderOf(id), store.SizeOnDisk(id)))
            .ToList();
        return new WorkerDiskUsage(FolderSize(dataRoot.EnvDir(worker.EnvGroup), ct), models, FolderSize(worker.Folder, ct));
    }

    public static long FolderSize(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Don't follow links (the HF cache links snapshots to blobs): each file is counted once.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            total += f.Length;
        }
        return total;
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}
