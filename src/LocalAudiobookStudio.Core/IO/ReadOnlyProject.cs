using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.IO;

/// <summary>
/// Housekeeping for read-only projects (<see cref="ProjectFile.ReadOnly"/>). Their chapter files are never
/// saved, so takes generated in a session are selected only in memory; on disk nothing refers to them.
/// </summary>
public static partial class ReadOnlyProject
{
    [GeneratedRegex(@"^take-(\d{4,})\.(wav|opus|json|peaks)(\.partial)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TakeFileRegex();

    /// <summary>
    /// Deletes the takes the project's saved files don't select: everything generated or recorded while it was
    /// open. Takes a read-only project ships with (selected in its chapter files) stay. Best effort: a file that
    /// can't be deleted (still playing, say) is skipped and goes next time. Returns the number of files deleted.
    /// </summary>
    public static int DiscardSessionTakes(string projectRoot)
    {
        var paths = new ProjectPaths(projectRoot);
        if (!Directory.Exists(paths.AudioDir)) return 0;
        Project saved;
        try { saved = ProjectStore.Load(projectRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ProjectFormatException or System.Text.Json.JsonException)
        {
            return 0;
        }
        if (!saved.IsReadOnly) return 0;

        var keep = saved.AllItems().Where(i => i.SelectedTake != null).ToDictionary(i => i.Id, i => i.SelectedTake!.Value);
        var deleted = 0;
        foreach (var dir in SafeEnumerate(() => Directory.EnumerateDirectories(paths.AudioDir)))
        {
            var kept = Guid.TryParse(Path.GetFileName(dir), out var id) && keep.TryGetValue(id, out var take) ? take : (int?)null;
            var keptAny = false;
            foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
            {
                var m = TakeFileRegex().Match(Path.GetFileName(file));
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n == kept) { keptAny = true; continue; }
                // Where a take stays, only other takes go (the take-number marker stays with it).
                if (kept != null && !m.Success) continue;
                if (TryDelete(file)) deleted++;
            }
            // A folder left without a take (only the take-number marker, if anything) goes too.
            if (!keptAny) TryDeleteDirectory(dir);
        }
        TryDeleteDirectory(paths.AudioDir, onlyIfEmpty: true);
        return deleted;
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> list)
    {
        try { return list().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string dir, bool onlyIfEmpty = false)
    {
        try
        {
            if (onlyIfEmpty && Directory.EnumerateFileSystemEntries(dir).Any()) return;
            Directory.Delete(dir, recursive: !onlyIfEmpty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still in use: next time.
        }
    }
}
