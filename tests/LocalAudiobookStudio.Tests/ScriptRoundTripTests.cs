using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Tests;

public class ScriptRoundTripTests
{
    [Fact]
    public void ExportThenImportPreservesStructure()
    {
        using var tmp = new TempDir();
        var p = ProjectStore.Create(Path.Combine(tmp.Path, "B"), "B");
        p.Speakers["alice"] = new Speaker { Id = "alice", DisplayName = "Alice", Worker = "kokoro" };
        var ch = new Chapter { Title = "Chapter 1" };
        ch.Items.Add(new TimelineItem { Text = "Chapter 1", Speaker = "narrator" });
        ch.Items.Add(new TimelineItem { Text = "The wind howled.", Speaker = "narrator" });
        ch.Items.Add(new TimelineItem { Text = "Who's there?", Speaker = "alice" });
        p.Speakers["sounds"] = new Speaker { Id = "sounds", DisplayName = "Sounds", Worker = "stable_audio_3" };
        ch.Items.Add(new TimelineItem { Text = "thunder", Speaker = "sounds", DurationSec = 4 });
        ch.Items.Add(new TimelineItem { Kind = ItemKind.Pause, DurationMs = 1500 });
        p.Chapters.Add(ch);

        var script = ScriptExporter.Export(p);
        var parsed = TextImporter.Parse(script, new ImportOptions());
        var chapters = TextImporter.ToChapters(parsed, l => l == null ? "narrator" : p.FindSpeakerByLabel(l)!.Id);

        var items = chapters.Single().Items;
        Assert.Equal(5, items.Count);
        Assert.Equal(new[] { "narrator", "narrator", "alice", "sounds", null }, items.Select(i => i.Speaker));
        Assert.Equal("thunder", items[3].Text);
        Assert.Equal(4, items[3].DurationSec); // [length: 4s]
        Assert.Equal(1500, items[4].DurationMs);
    }

    [Fact]
    public void ExportEscapesWhatWouldReadAsMarkers()
    {
        using var tmp = new TempDir();
        var p = ProjectStore.Create(Path.Combine(tmp.Path, "B"), "B");
        p.Speakers["who"] = new Speaker { Id = "who", DisplayName = "Dr. Who: Part [2]", Worker = "kokoro" };
        var ch = new Chapter { Title = "#1 \\ the start" };
        ch.Items.Add(new TimelineItem { Text = "[length: 3s] is not a tag, \\[nor] this.", Speaker = "who", ExportId = "a]b" });
        ch.Items.Add(new TimelineItem { Text = "Two lines:\n[Bob] isn't a speaker\n\nAlice: nor is she.", Speaker = "narrator" });
        ch.Items.Add(new TimelineItem { Kind = ItemKind.Comment, Text = "A note]\n[speaker: x] stays in it" });
        ch.Items.Add(new TimelineItem { Kind = ItemKind.Clip, ClipFile = "assets\\door [old].wav" });
        p.Chapters.Add(ch);

        var script = ScriptExporter.Export(p);
        var parsed = TextImporter.Parse(script, new ImportOptions { KnownSpeakerNames = ["Alice"], SpeakHeadings = false });

        var chapter = Assert.Single(parsed.Chapters);
        Assert.Equal("#1 \\ the start", chapter.Title);
        Assert.Equal(["Dr. Who: Part [2]", "Narrator"], parsed.SpeakerLabels);
        var items = chapter.Items;
        Assert.Equal(4, items.Count);
        Assert.Equal("[length: 3s] is not a tag, \\[nor] this.", items[0].Text);
        Assert.Null(items[0].DurationSec);
        Assert.Equal("a]b", items[0].ExportId);
        Assert.Equal("Dr. Who: Part [2]", items[0].SpeakerLabel);
        // Lines of a segment's text come back joined (as any paragraph's lines do), and still one segment.
        Assert.Equal("Two lines: [Bob] isn't a speaker Alice: nor is she.", items[1].Text);
        Assert.Equal("Narrator", items[1].SpeakerLabel);
        Assert.Equal("A note]\n[speaker: x] stays in it", items[2].Text);
        Assert.Equal("assets\\door [old].wav", items[3].ClipFile);
    }

    [Fact]
    public void BackslashesEscapeMarkersInWrittenText()
    {
        var parsed = TextImporter.Parse("""
            \Chapter 3 was the hardest to write.

            \[whisper] Is anyone there?

            Alice\: not a speaker marker, and \\ is one backslash.

            [clip: assets\door.wav]
            """, new ImportOptions { KnownSpeakerNames = ["Alice"] });

        var items = Assert.Single(parsed.Chapters).Items;
        Assert.Equal(["Chapter 3 was the hardest to write.", "[whisper] Is anyone there?",
            "Alice: not a speaker marker, and \\ is one backslash."], items.Take(3).Select(i => i.Text));
        Assert.All(items.Take(3), i => Assert.Null(i.SpeakerLabel));
        Assert.Empty(parsed.SpeakerLabels);
        // A backslash before a letter isn't an escape: paths keep theirs.
        Assert.Equal("assets\\door.wav", items[3].ClipFile);
    }

    [Fact]
    public void MergeKeepsIdsForExactAndFuzzyMatches()
    {
        var a = new TimelineItem { Text = "The wind howled across the moor.", Speaker = "n", SelectedTake = 2 };
        var b = new TimelineItem { Text = "Who's there?", Speaker = "n", SelectedTake = 1 };
        var c = new TimelineItem { Text = "Completely removed paragraph.", Speaker = "n" };
        var existing = new List<TimelineItem> { a, b, c };

        var imported = new List<TimelineItem>
        {
            new() { Text = "Brand new opening.", Speaker = "n" },
            new() { Text = "The wind howled across the moors.", Speaker = "n" }, // small edit → fuzzy
            new() { Text = "Who's there?", Speaker = "n" },                      // exact
        };
        var merged = ScriptMerger.Merge(existing, imported);
        Assert.Equal(3, merged.Count);
        Assert.NotEqual(a.Id, merged[0].Id);
        Assert.Equal(a.Id, merged[1].Id);
        Assert.Equal("The wind howled across the moors.", merged[1].Text);
        Assert.Equal(2, merged[1].SelectedTake);
        Assert.Equal(b.Id, merged[2].Id);
    }
}
