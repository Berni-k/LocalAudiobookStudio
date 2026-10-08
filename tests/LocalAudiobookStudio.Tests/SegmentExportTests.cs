using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine.Projects;

namespace LocalAudiobookStudio.Tests;

/// <summary>Per-segment export: [id: …] in imported text, and how the files are named.</summary>
public sealed class SegmentExportTests
{
    private static List<ImportedItem> Items(string text) =>
        TextImporter.Parse(text, new ImportOptions()).Chapters.SelectMany(c => c.Items).ToList();

    [Fact]
    public void Ids_name_the_next_section_wherever_they_are_written()
    {
        var items = Items("""
            [id: intro]
            The wind howled.

            [id: greet] [Alice] Who's there?

            [Bob] [id: answer] Only me.

            [id: door]
            [Sounds] door creaking

            [id: first] One line.
            [id: second] Another line, same paragraph.

            No id here.
            """);

        Assert.Equal(["intro", "greet", "answer", "door", "first", "second", null], items.Select(i => i.ExportId));
        Assert.Equal("Who's there?", items[1].Text);
        Assert.Equal("Alice", items[1].SpeakerLabel);
        Assert.Equal("Only me.", items[2].Text);
        Assert.Equal("Sounds", items[3].SpeakerLabel);
        Assert.Equal("Another line, same paragraph.", items[5].Text);
    }

    [Fact]
    public void A_pause_or_comment_between_keeps_the_id_for_the_section_after_it()
    {
        var items = Items("[id: late]\n\n[pause 1s]\n\n[comment: note]\n\nSpoken.");
        Assert.Equal("late", items.Single(i => i.Kind == ItemKind.Tts).ExportId);
        Assert.Null(items.Single(i => i.Kind == ItemKind.Pause).ExportId);
    }

    [Fact]
    public void Ids_survive_a_script_round_trip()
    {
        using var tmp = new TempDir();
        var p = ProjectStore.Create(Path.Combine(tmp.Path, "B"), "B");
        var ch = new Chapter { Title = "Chapter 1" };
        ch.Items.Add(new TimelineItem { Text = "Hello.", Speaker = "narrator", ExportId = "hello" });
        ch.Items.Add(new TimelineItem { Text = "thunder", Speaker = "narrator", DurationSec = 4, ExportId = "boom" });
        ch.Items.Add(new TimelineItem { Text = "Plain.", Speaker = "narrator" });
        p.Chapters.Add(ch);

        var chapters = TextImporter.ToChapters(TextImporter.Parse(ScriptExporter.Export(p), new ImportOptions()), _ => "narrator");

        // The heading comes back as a spoken first segment, without an id.
        Assert.Equal([null, "hello", "boom", null], chapters.SelectMany(c => c.Items).Where(i => i.HasAudio).Select(i => i.ExportId));
    }

    private static Chapter Chapter(string title, params TimelineItem[] items)
    {
        var ch = new Chapter { Title = title };
        ch.Items.AddRange(items);
        return ch;
    }

    private static TimelineItem Line(string? id = null) => new() { Text = "x", Speaker = "narrator", ExportId = id };

    [Fact]
    public void One_chapter_is_numbered_plainly_and_pauses_and_comments_dont_count()
    {
        var chapters = new[]
        {
            Chapter("Chapter 1", Line(), new TimelineItem { Kind = ItemKind.Pause, DurationMs = 500 }, Line(),
                new TimelineItem { Kind = ItemKind.Comment, Text = "note" }, Line("named"), Line()),
        };
        Assert.Equal(["1", "2", "named", "4"], ExportPlanner.SegmentNames(chapters, [0]).Select(n => n.Name));
    }

    [Fact]
    public void Several_chapters_put_the_chapter_title_before_numbers_but_not_before_ids()
    {
        var chapters = new[] { Chapter("Prologue", Line(), Line("guard")), Chapter("Act: 2", Line(), Line()) };
        // The project's chapter count decides, so exporting one chapter names its files the same way.
        Assert.Equal(["Act_ 2 - 1", "Act_ 2 - 2"], ExportPlanner.SegmentNames(chapters, [1]).Select(n => n.Name));
        Assert.Equal(["Prologue - 1", "guard", "Act_ 2 - 1", "Act_ 2 - 2"], ExportPlanner.SegmentNames(chapters, [0, 1]).Select(n => n.Name));
    }

    [Fact]
    public void Clashing_names_get_a_number_until_unique()
    {
        var chapters = new[] { Chapter("Chapter 1", Line("hi"), Line("HI"), Line("hi-2"), Line("hi"), Line("2"), Line()) };
        // "HI" clashes regardless of case (Windows); "hi-2" is taken by then, so the next "hi" gets "hi-3".
        Assert.Equal(["hi", "HI-2", "hi-2-2", "hi-3", "2", "6"], ExportPlanner.SegmentNames(chapters, [0]).Select(n => n.Name));
    }
}
