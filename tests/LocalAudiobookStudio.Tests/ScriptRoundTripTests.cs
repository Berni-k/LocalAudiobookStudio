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
