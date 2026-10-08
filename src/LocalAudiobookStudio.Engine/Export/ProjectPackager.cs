using System.IO.Compression;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Engine.Export;

public sealed class PackOptions
{
    /// <summary>Only include the selected take of each segment (smaller file).</summary>
    public bool SelectedTakesOnly { get; set; }
    public bool IncludeExport { get; set; }
    public bool IncludeCache { get; set; }
}

public sealed record PackProgress(string CurrentFile, int Done, int Total);

/// <summary>
/// Packs a project folder into a single <c>.abproj</c> (a standard zip) and unpacks it again.
/// WAVs are stored without compression; JSON and other text is deflated.
/// </summary>
public static class ProjectPackager
{
    public const string Extension = ".abproj";

    private static readonly HashSet<string> Uncompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".opus", ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".jpg", ".jpeg", ".png", ".peaks"
    };

    /// <param name="selectedTakes">For "selected takes only": segment id → selected take number.</param>
    public static async Task PackAsync(string projectRoot, string outputFile, PackOptions options,
        IReadOnlyDictionary<Guid, int?>? selectedTakes, IProgress<PackProgress>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(projectRoot);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Include(root, f, options, selectedTakes))
            .ToList();

        var tmp = outputFile + ".tmp";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = files[i];
                    var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    progress?.Report(new PackProgress(rel, i, files.Count));
                    var level = Uncompressed.Contains(Path.GetExtension(f)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var entry = zip.CreateEntry(rel, level);
                    entry.LastWriteTime = File.GetLastWriteTime(f);
                    await using var src = File.OpenRead(f);
                    await using var dst = entry.Open();
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
            }
            File.Move(tmp, outputFile, overwrite: true);
            progress?.Report(new PackProgress("", files.Count, files.Count));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static bool Include(string root, string file, PackOptions o, IReadOnlyDictionary<Guid, int?>? selectedTakes)
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        var top = rel.Split('/')[0];
        if (!o.IncludeCache && top == ".cache") return false;
        if (!o.IncludeExport && top == "export") return false;
        if (rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) return false;
        if (top is ".git" or ".temp") return false;

        if (o.SelectedTakesOnly && top == "audio" && selectedTakes != null)
        {
            var parts = rel.Split('/');
            if (parts.Length == 3 && Guid.TryParse(parts[1], out var id))
            {
                if (!selectedTakes.TryGetValue(id, out var take) || take == null) return false;
                var stem = Path.GetFileNameWithoutExtension(parts[2]);
                return stem == $"take-{take:0000}";
            }
        }
        return true;
    }

    /// <summary>Extracts a package into <paramref name="destinationFolder"/> (must be empty or new).</summary>
    public static async Task<string> UnpackAsync(string packageFile, string destinationFolder,
        IProgress<PackProgress>? progress, CancellationToken ct)
    {
        var dest = Path.GetFullPath(destinationFolder);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new IOException($"The folder {dest} is not empty. Choose an empty or new folder.");
        Directory.CreateDirectory(dest);

        using var zip = ZipFile.OpenRead(packageFile);
        if (zip.GetEntry(ProjectFile.FileName) == null)
            throw new InvalidDataException($"This file is not a Local Audiobook Studio project package ({ProjectFile.FileName} is missing).");

        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = entries[i];
                var target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                // Zip-slip protection: never write outside the destination.
                if (!target.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in package: {e.FullName}");
                progress?.Report(new PackProgress(e.FullName, i, entries.Count));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var src = e.Open();
                await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            try { Directory.Delete(dest, recursive: true); } catch { }
            throw;
        }
        progress?.Report(new PackProgress("", entries.Count, entries.Count));
        if (!ProjectStore.IsProjectFolder(dest)) throw new InvalidDataException("Unpacked folder does not contain a project.");
        return dest;
    }
}
