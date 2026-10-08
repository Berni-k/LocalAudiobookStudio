using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Core.IO;

public sealed class ProjectFormatException(string message) : Exception(message);

/// <summary>Creates, loads and writes project folders.</summary>
public static class ProjectStore
{
    public static readonly string[] SpeakerPalette =
    [
        "#6FA8DC", "#E06666", "#93C47D", "#F6B26B", "#8E7CC3", "#76A5AF", "#FFD966", "#C27BA0", "#A4C2F4", "#B6D7A8"
    ];

    /// <summary>Creates a new, empty project folder with a default narrator.</summary>
    public static Project Create(string root, string title, string defaultWorker = "kokoro")
    {
        var paths = new ProjectPaths(root);
        if (File.Exists(paths.ProjectFile))
            throw new IOException($"A project already exists in {paths.Root}.");

        Directory.CreateDirectory(paths.Root);
        foreach (var d in new[] { paths.SpeakersDir, paths.ChaptersDir, paths.AssetsDir, paths.AudioDir })
            Directory.CreateDirectory(d);

        var project = new Project(paths, new ProjectFile { Title = title });
        var narrator = new Speaker
        {
            Id = "narrator",
            DisplayName = "Narrator",
            Color = SpeakerPalette[0],
            Worker = defaultWorker,
            MarkerAliases = ["Narrator"],
        };
        project.Speakers[narrator.Id] = narrator;
        SaveAll(project);
        return project;
    }

    /// <summary>True when the folder has a project file written by this app (see <see cref="ProjectFile.AppId"/>).</summary>
    public static bool IsProjectFolder(string folder)
    {
        var file = Path.Combine(folder, ProjectFile.FileName);
        if (!File.Exists(file)) return false;
        try { return IsOurs(JsonNode.Parse(File.ReadAllText(file)) as JsonObject); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
    }

    private static bool IsOurs(JsonObject? node) => node?["app"] is JsonValue v && v.TryGetValue(out string? s) && s == ProjectFile.AppId;

    /// <summary>Loads a project folder, refusing newer formats.</summary>
    public static Project Load(string root)
    {
        var paths = new ProjectPaths(root);
        if (!File.Exists(paths.ProjectFile))
            throw new FileNotFoundException($"This folder does not contain a {ProjectFile.FileName}.", paths.ProjectFile);

        var node = JsonNode.Parse(File.ReadAllText(paths.ProjectFile)) as JsonObject
                   ?? throw new ProjectFormatException($"{ProjectFile.FileName} is not a JSON object.");
        if (!IsOurs(node))
            throw new ProjectFormatException($"This {ProjectFile.FileName} does not belong to Local Audiobook Studio.");
        var version = node["formatVersion"]?.GetValue<int>() ?? ProjectFile.CurrentFormatVersion;
        if (version > ProjectFile.CurrentFormatVersion)
            throw new ProjectFormatException(
                $"This project was saved by a newer version of Local Audiobook Studio (format {version}; this version supports up to {ProjectFile.CurrentFormatVersion}). Please update the app.");

        var file = CoreJson.Deserialize<ProjectFile>(node.ToJsonString());
        var project = new Project(paths, file);

        if (Directory.Exists(paths.SpeakersDir))
        {
            foreach (var f in Directory.EnumerateFiles(paths.SpeakersDir, "*.json"))
            {
                var sp = CoreJson.Deserialize<Speaker>(File.ReadAllText(f));
                if (string.IsNullOrEmpty(sp.Id)) sp.Id = Path.GetFileNameWithoutExtension(f);
                project.Speakers[sp.Id] = sp;
            }
        }

        var seen = new HashSet<Guid>();
        foreach (var id in file.ChapterOrder)
        {
            var path = paths.ChapterFile(id);
            if (!File.Exists(path) || !seen.Add(id)) continue;
            project.Chapters.Add(CoreJson.Deserialize<Chapter>(File.ReadAllText(path)));
        }
        // Chapter files that aren't listed (for example after a hand edit) are appended rather than lost.
        if (Directory.Exists(paths.ChaptersDir))
        {
            foreach (var f in Directory.EnumerateFiles(paths.ChaptersDir, "*.json").Order())
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id) || seen.Contains(id)) continue;
                seen.Add(id);
                project.Chapters.Add(CoreJson.Deserialize<Chapter>(File.ReadAllText(f)));
            }
        }
        project.SyncChapterOrder();
        return project;
    }

    // Read-only projects (ProjectFile.ReadOnly) are never written: the saves and deletes below do nothing.

    public static void SaveAll(Project project)
    {
        project.SyncChapterOrder();
        if (project.IsReadOnly) return;
        AtomicFile.WriteAllBytes(project.Paths.ProjectFile, CoreJson.SerializeToUtf8(project.File));
        foreach (var s in project.Speakers.Values) SaveSpeaker(project, s);
        foreach (var c in project.Chapters) SaveChapter(project, c);
    }

    public static void SaveSpeaker(Project project, Speaker speaker)
    {
        if (project.IsReadOnly) return;
        AtomicFile.WriteAllBytes(project.Paths.SpeakerFile(speaker.Id), CoreJson.SerializeToUtf8(speaker));
    }

    public static void SaveChapter(Project project, Chapter chapter)
    {
        if (project.IsReadOnly) return;
        AtomicFile.WriteAllBytes(project.Paths.ChapterFile(chapter.Id), CoreJson.SerializeToUtf8(chapter));
    }

    public static void DeleteChapterFile(Project project, Guid chapterId)
    {
        if (project.IsReadOnly) return;
        var path = project.Paths.ChapterFile(chapterId);
        if (File.Exists(path)) File.Delete(path);
    }

    public static void DeleteSpeakerFile(Project project, string speakerId)
    {
        if (project.IsReadOnly) return;
        var path = project.Paths.SpeakerFile(speakerId);
        if (File.Exists(path)) File.Delete(path);
    }
}
