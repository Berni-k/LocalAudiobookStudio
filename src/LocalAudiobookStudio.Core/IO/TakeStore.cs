using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Core.IO;

/// <summary>Reads and writes take files under <c>audio/&lt;segmentId&gt;/</c>. Takes are never overwritten.</summary>
public static partial class TakeStore
{
    [GeneratedRegex(@"^take-(\d{4,})\.(wav|opus|json)$", RegexOptions.IgnoreCase)]
    private static partial Regex TakeFileRegex();

    /// <summary>A take's side files (peaks, transcript) named like its metadata file, so one pattern finds the take number.</summary>
    private static string AsTakeFile(string name) => name.Replace(".peaks", ".json").Replace(".transcript", ".json");

    /// <summary>Loads the metadata of every take of a segment, ordered by take number.
    /// Takes whose audio file is missing are skipped.</summary>
    public static List<TakeInfo> LoadTakes(ProjectPaths paths, Guid itemId)
    {
        var dir = paths.SegmentAudioDir(itemId);
        var result = new List<TakeInfo>();
        if (!Directory.Exists(dir)) return result;

        foreach (var f in Directory.EnumerateFiles(dir, "take-*.json"))
        {
            try
            {
                var info = CoreJson.Deserialize<TakeInfo>(File.ReadAllText(f));
                if (File.Exists(paths.TakeAudio(itemId, info))) result.Add(info);
            }
            catch
            {
                // A corrupt metadata file only hides that take; the audio stays on disk.
            }
        }
        result.Sort((a, b) => a.Take.CompareTo(b.Take));
        return result;
    }

    /// <summary>
    /// In each segment folder: the highest take number ever handed out. Deleting the newest takes must not
    /// free their numbers, because take paths key caches (waveform peaks): a new take-0001 would show the
    /// deleted take-0001's waveform.
    /// </summary>
    public const string LastTakeFile = ".last-take";

    /// <summary>The next unused take number for a segment (see <see cref="NextTakeNumber(string)"/>).</summary>
    public static int NextTakeNumber(ProjectPaths paths, Guid itemId) => NextTakeNumber(paths.SegmentAudioDir(itemId));

    /// <summary>
    /// The next take number for a segment folder, recorded as used right away: never a number used before,
    /// even when that take's files were deleted (in the app, by "Clean up takes", or by hand).
    /// </summary>
    public static int NextTakeNumber(string segmentDir)
    {
        var take = HighestTakeNumber(segmentDir) + 1;
        RecordTakeNumber(segmentDir, take);
        return take;
    }

    /// <summary>The highest take number used in a segment folder: its take files (also .partial ones) and <see cref="LastTakeFile"/>.</summary>
    public static int HighestTakeNumber(string segmentDir)
    {
        if (!Directory.Exists(segmentDir)) return 0;
        var max = 0;
        foreach (var f in Directory.EnumerateFiles(segmentDir))
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) name = name[..^".partial".Length];
            var m = TakeFileRegex().Match(AsTakeFile(name));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) max = Math.Max(max, n);
        }
        try
        {
            var marker = Path.Combine(segmentDir, LastTakeFile);
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out var last)) max = Math.Max(max, last);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable marker: the take files still count.
        }
        return max;
    }

    /// <summary>Raises the folder's <see cref="LastTakeFile"/> to <paramref name="take"/> (never lowers it).</summary>
    public static void RecordTakeNumber(string segmentDir, int take)
    {
        try
        {
            Directory.CreateDirectory(segmentDir);
            var marker = Path.Combine(segmentDir, LastTakeFile);
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out var last) && last >= take) return;
            File.WriteAllText(marker, take.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only project: numbering falls back to the take files on disk.
        }
    }

    public static void SaveTakeInfo(ProjectPaths paths, Guid itemId, TakeInfo info) =>
        AtomicFile.WriteAllBytes(paths.TakeJson(itemId, info.Take), CoreJson.SerializeToUtf8(info));

    /// <summary>
    /// Deletes every take that isn't the selected take of a live item. Segment folders of items
    /// that no longer exist are removed entirely. Returns the number of files deleted.
    /// </summary>
    public static int CleanUpUnusedTakes(Project project)
    {
        var selected = project.AllItems()
            .Where(i => i.IsGenerated)
            .ToDictionary(i => i.Id, i => i.SelectedTake);
        var deleted = 0;
        if (!Directory.Exists(project.Paths.AudioDir)) return 0;

        foreach (var dir in Directory.EnumerateDirectories(project.Paths.AudioDir))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out var id)) continue;
            if (!selected.TryGetValue(id, out var keep))
            {
                deleted += Directory.EnumerateFiles(dir).Count();
                Directory.Delete(dir, recursive: true);
                continue;
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var m = TakeFileRegex().Match(AsTakeFile(Path.GetFileName(f)));
                if (!m.Success) continue;
                if (int.TryParse(m.Groups[1].Value, out var n) && n == keep) continue;
                File.Delete(f);
                deleted++;
            }
        }
        return deleted;
    }
}
