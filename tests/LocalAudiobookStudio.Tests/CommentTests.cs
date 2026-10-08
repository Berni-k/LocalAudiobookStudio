using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.Tests;

/// <summary>Comments: notes in the book that are never spoken, take no time and survive the script format.</summary>
public class CommentTests
{
    private static ImportResult Parse(string text) => TextImporter.Parse(text, new ImportOptions());

    [Fact]
    public void ImportsBothFormsAndMultiParagraphComments()
    {
        var r = Parse("""
            # Chapter 1

            [comment: Check the pronunciation of Aoife.]

            She walked in.

            [comment] Short note

            [comment: First paragraph of a long note,
            still the first.

            Second paragraph.]

            [Alice] Hello.
            """);
        var items = r.Chapters.Single().Items;
        Assert.Equal(
            [ItemKind.Tts, ItemKind.Comment, ItemKind.Tts, ItemKind.Comment, ItemKind.Comment, ItemKind.Tts],
            items.Select(i => i.Kind));
        Assert.Equal("Check the pronunciation of Aoife.", items[1].Text);
        Assert.Equal("Short note", items[3].Text);
        Assert.Equal("First paragraph of a long note,\nstill the first.\n\nSecond paragraph.", items[4].Text);
        Assert.Equal("Alice", items[5].SpeakerLabel);
        Assert.DoesNotContain("comment", r.SpeakerLabels, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnclosedCommentStopsAtItsLine()
    {
        var items = Parse("[comment: forgot the bracket\n\nSpoken text.\n\n[pause 1s]").Chapters.Single().Items;
        Assert.Equal([ItemKind.Comment, ItemKind.Tts, ItemKind.Pause], items.Select(i => i.Kind));
        Assert.Equal("forgot the bracket", items[0].Text);
    }

    [Fact]
    public void CommentBeforeFirstHeadingGoesIntoFirstChapter()
    {
        var r = Parse("[comment: About this book]\n\n# One\n\nText.");
        var ch = r.Chapters.Single();
        Assert.Equal("One", ch.Title);
        Assert.Equal([ItemKind.Tts, ItemKind.Comment, ItemKind.Tts], ch.Items.Select(i => i.Kind));
    }

    [Fact]
    public void ScriptExportRoundTripsComments()
    {
        using var tmp = new TempDir();
        var p = ProjectStore.Create(Path.Combine(tmp.Path, "B"), "B");
        var ch = new Chapter { Title = "Chapter 1" };
        ch.Items.Add(new TimelineItem { Text = "Chapter 1", Speaker = "narrator" });
        ch.Items.Add(new TimelineItem { Kind = ItemKind.Comment, Text = "Line one\n\nLine three" });
        ch.Items.Add(new TimelineItem { Text = "The wind howled.", Speaker = "narrator" });
        p.Chapters.Add(ch);

        var chapters = TextImporter.ToChapters(TextImporter.Parse(ScriptExporter.Export(p), new ImportOptions()), _ => "narrator");
        var items = chapters.Single().Items;
        Assert.Equal([ItemKind.Tts, ItemKind.Comment, ItemKind.Tts], items.Select(i => i.Kind));
        Assert.Equal("Line one\n\nLine three", items[1].Text);

        // Re-importing over the chapter keeps the comment's id.
        var merged = ScriptMerger.Merge(ch.Items, items);
        Assert.Equal(ch.Items[1].Id, merged[1].Id);
    }

    [Fact]
    public void CommentsTakeNoTimeAndDontChangeGaps()
    {
        var defaults = new ProjectDefaults { GapAfterMs = 400, ChapterGapMs = 2000 };
        static ItemDuration Dur(TimelineItem i) => new(i.DurationMs ?? 0, false);
        TimelineItem Sec(int ms) => new() { Kind = ItemKind.Clip, DurationMs = ms };
        TimelineItem Comment() => new() { Kind = ItemKind.Comment, Text = "note" };

        var with = TimelineCalculator.Compute(
            [new Chapter { Items = { Sec(1000), Comment(), new TimelineItem { Kind = ItemKind.Pause, DurationMs = 0 }, Sec(500), Comment() } },
             new Chapter { Items = { Comment(), Sec(700) } }], defaults, Dur);
        var without = TimelineCalculator.Compute(
            [new Chapter { Items = { Sec(1000), new TimelineItem { Kind = ItemKind.Pause, DurationMs = 0 }, Sec(500) } },
             new Chapter { Items = { Sec(700) } }], defaults, Dur);

        Assert.Equal(without.TotalMs, with.TotalMs);
        Assert.Equal(without.ChapterStarts, with.ChapterStarts);
        Assert.Equal(1000, with.Items[1].StartMs); // where it stands, zero long
        Assert.Equal(0, with.Items[1].DurationMs);
        Assert.Equal(without.Items[2].StartMs, with.Items[3].StartMs);
    }
}
