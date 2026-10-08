using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.Tests;

/// <summary>Segments of a kind from a newer version: kept as they are, shown raw, saved back unchanged.</summary>
public sealed class UnknownSegmentTests
{
    private const string Id = "6a0f3c1e-2b1d-4c1e-9a33-0c9a1b2c3d4e";
    private const string Future = $$"""
        { "id": "{{Id}}", "kind": "video", "file": "assets/intro.mp4", "volume": 0.8, "nested": { "a": [1, 2] }, "text": "x" }
        """;

    private static Chapter ChapterWith(string itemJson) =>
        CoreJson.Deserialize<Chapter>($$"""{ "title": "One", "items": [ { "kind": "tts", "text": "Hello." }, {{itemJson}} ] }""");

    [Fact]
    public void An_unknown_kind_loads_as_unknown_and_shows_its_raw_contents()
    {
        var item = ChapterWith(Future).Items[1];

        Assert.Equal(ItemKind.Unknown, item.Kind);
        Assert.Equal(Guid.Parse(Id), item.Id);
        Assert.Contains("\"kind\":\"video\"", item.DisplayText);
        Assert.False(item.HasAudio);
        Assert.False(item.IsGenerated);
    }

    [Fact]
    public void Saving_writes_the_same_contents_back()
    {
        var saved = JsonNode.Parse(CoreJson.Serialize(ChapterWith(Future)))!["items"]![1];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), saved));
    }

    [Fact]
    public void A_project_round_trip_keeps_it()
    {
        using var tmp = new TempDir();
        var p = ProjectStore.Create(Path.Combine(tmp.Path, "B"), "B");
        p.Chapters.Add(ChapterWith(Future));
        ProjectStore.SaveAll(p);

        var item = ProjectStore.Load(p.Paths.Root).Chapters.Single().Items[1];

        Assert.Equal(ItemKind.Unknown, item.Kind);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Future), item.Raw));
    }

    [Fact]
    public void Undo_snapshots_and_pasted_copies_keep_it()
    {
        var item = ChapterWith(Future).Items[1];

        var snapshot = item.Clone();
        var restored = new TimelineItem();
        restored.CopyFrom(snapshot);
        Assert.Equal(ItemKind.Unknown, restored.Kind);
        Assert.True(JsonNode.DeepEquals(item.Raw, restored.Raw));

        // A pasted copy is the same segment under its own id.
        var copy = item.CloneAsNew();
        var written = JsonNode.Parse(CoreJson.Serialize(copy))!.AsObject();
        Assert.Equal(copy.Id.ToString("D"), written["id"]!.GetValue<string>());
        Assert.Equal("video", written["kind"]!.GetValue<string>());
    }

    [Fact]
    public void It_takes_no_time_on_the_timeline()
    {
        var chapter = ChapterWith(Future);
        chapter.Items.Add(new TimelineItem { Text = "After.", Speaker = "narrator" });
        var defaults = new ProjectDefaults { GapAfterMs = 400 };

        var layout = TimelineCalculator.Compute([chapter], defaults, _ => new ItemDuration(1000, false));

        var timings = layout.Items.ToDictionary(t => t.ItemIndex);
        Assert.Equal(0, timings[1].DurationMs);
        // The next segment follows the first with one gap, as if the unknown one weren't there.
        Assert.Equal(timings[0].EndMs + 400, timings[2].StartMs);
    }
}
