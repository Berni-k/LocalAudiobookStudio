using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Tests;

/// <summary>Read-only projects (the bundled examples): nothing is saved, and session takes are deleted.</summary>
public sealed class ReadOnlyProjectTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string _root => _tmp.Path;

    public void Dispose() => _tmp.Dispose();

    /// <summary>A project with one chapter of two segments, marked read-only; the first segment ships a selected take.</summary>
    private (Project Project, TimelineItem Shipped, TimelineItem Fresh) CreateExample()
    {
        var project = ProjectStore.Create(_root, "Example");
        var shipped = new TimelineItem { Kind = ItemKind.Tts, Text = "One.", Speaker = "narrator", SelectedTake = 1 };
        var fresh = new TimelineItem { Kind = ItemKind.Tts, Text = "Two.", Speaker = "narrator" };
        project.Chapters.Add(new Chapter { Title = "Chapter 1", Items = [shipped, fresh] });
        ProjectStore.SaveAll(project);
        WriteTake(project.Paths, shipped.Id, 1);

        // Set by hand, as for the bundled examples (saving refuses once the project is read-only).
        project.File.ReadOnly = true;
        File.WriteAllBytes(project.Paths.ProjectFile, CoreJson.SerializeToUtf8(project.File));
        return (ProjectStore.Load(_root), shipped, fresh);
    }

    private static void WriteTake(ProjectPaths paths, Guid itemId, int take)
    {
        Directory.CreateDirectory(paths.SegmentAudioDir(itemId));
        File.WriteAllText(paths.TakeWav(itemId, take), "wav");
        File.WriteAllText(paths.TakeJson(itemId, take), $$"""{ "take": {{take}} }""");
        File.WriteAllText(paths.TakePeaks(itemId, take), "peaks");
        TakeStore.RecordTakeNumber(paths.SegmentAudioDir(itemId), take);
    }

    [Fact]
    public void Nothing_is_saved_into_a_read_only_project()
    {
        var (project, _, fresh) = CreateExample();
        Assert.True(project.IsReadOnly);
        var chapter = project.Chapters[0];
        var before = File.ReadAllText(project.Paths.ChapterFile(chapter.Id));

        project.Chapters[0].Items.First(i => i.Id == fresh.Id).SelectedTake = 3;
        project.File.Title = "Changed";
        ProjectStore.SaveAll(project);
        ProjectStore.SaveChapter(project, chapter);
        ProjectStore.DeleteChapterFile(project, chapter.Id);
        ProjectStore.DeleteSpeakerFile(project, "narrator");

        Assert.Equal(before, File.ReadAllText(project.Paths.ChapterFile(chapter.Id)));
        Assert.Equal("Example", ProjectStore.Load(_root).File.Title);
        Assert.True(File.Exists(project.Paths.SpeakerFile("narrator")));
    }

    [Fact]
    public void Session_takes_are_deleted_and_shipped_takes_kept()
    {
        var (project, shipped, fresh) = CreateExample();
        // This session: a second take of the shipped segment and a first take of the other one.
        WriteTake(project.Paths, shipped.Id, 2);
        WriteTake(project.Paths, fresh.Id, 1);
        // A segment that only exists in memory (added this session).
        var added = Guid.NewGuid();
        WriteTake(project.Paths, added, 1);

        Assert.True(ReadOnlyProject.DiscardSessionTakes(_root) > 0);

        Assert.True(File.Exists(project.Paths.TakeWav(shipped.Id, 1)));
        Assert.True(File.Exists(project.Paths.TakeJson(shipped.Id, 1)));
        Assert.True(File.Exists(project.Paths.TakePeaks(shipped.Id, 1)));
        Assert.False(File.Exists(project.Paths.TakeWav(shipped.Id, 2)));
        Assert.False(File.Exists(project.Paths.TakeJson(shipped.Id, 2)));
        Assert.False(File.Exists(project.Paths.TakePeaks(shipped.Id, 2)));
        Assert.False(Directory.Exists(project.Paths.SegmentAudioDir(fresh.Id)));
        Assert.False(Directory.Exists(project.Paths.SegmentAudioDir(added)));
    }

    [Fact]
    public void The_audio_folder_goes_when_nothing_was_shipped()
    {
        var (project, shipped, _) = CreateExample();
        Directory.Delete(project.Paths.SegmentAudioDir(shipped.Id), recursive: true);
        WriteTake(project.Paths, shipped.Id, 4);

        ReadOnlyProject.DiscardSessionTakes(_root);

        Assert.False(Directory.Exists(project.Paths.AudioDir));
    }

    [Fact]
    public void Takes_of_a_normal_project_are_left_alone()
    {
        var project = ProjectStore.Create(_root, "Normal");
        var id = Guid.NewGuid();
        WriteTake(project.Paths, id, 1);
        Assert.Equal(0, ReadOnlyProject.DiscardSessionTakes(_root));
        Assert.True(File.Exists(project.Paths.TakeWav(id, 1)));
    }

    [Fact]
    public void The_bundled_example_is_read_only()
    {
        var example = ProjectStore.Load(Repo.Combine("examples", "The Last Lamp.audiobook"));
        Assert.True(example.IsReadOnly);
        Assert.NotEmpty(example.Chapters);
        // Every speaker used exists, and every clip file is there.
        var items = example.AllItems().ToList();
        Assert.All(items.Where(i => i.Speaker != null), i => Assert.True(example.Speakers.ContainsKey(i.Speaker!), i.Speaker));
        Assert.All(items.Where(i => i.Kind == ItemKind.Clip), i => Assert.True(File.Exists(example.Paths.Resolve(i.ClipFile!))));
    }
}
