using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Tests;

/// <summary>Importing a recording: the chapters a file declares, and how the structure becomes project items.</summary>
public sealed class AudioImportTests
{
    [Fact]
    public void Chapters_are_read_from_ffmpegs_metadata_in_any_timebase()
    {
        string[] lines =
        [
            ";FFMETADATA1", "title=The Book", "",
            "[CHAPTER]", "TIMEBASE=1/1000", "START=0", "END=60000", "title=Opening",
            "[CHAPTER]", "TIMEBASE=1/44100", "START=2646000", "END=5292000", @"title=Second\=Part",
            "[CHAPTER]", "START=125000", "END=130000",
            "[STREAM]", "title=ignored",
        ];

        var chapters = AudioSource.ParseChapters(lines);

        Assert.Equal([("Opening", 0), ("Second=Part", 60_000), ("", 125_000)], chapters.Select(c => (c.Title, c.StartMs)));
    }

    [Fact]
    public void A_file_without_chapters_has_none()
    {
        Assert.Empty(AudioSource.ParseChapters([";FFMETADATA1", "title=Just a title", "artist=Someone"]));
    }

    [Fact]
    public void Items_are_paired_with_where_they_are_in_the_recording()
    {
        var words = new List<TranscriptWord>();
        var t = 0;
        foreach (var w in "Hello there you. |2000 More words follow.".Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.StartsWith('|')) { t += int.Parse(w[1..]); continue; }
            words.Add(new TranscriptWord(w, t, t + 300));
            t += 300;
        }

        var structure = AudioStructurer.Build(words, [], new AudioStructureOptions());
        var imported = AudioImporter.ToChapters(structure, label => label ?? "narrator");

        var chapter = Assert.Single(imported.Chapters);
        // Segment, pause (2000 ms replaces the default gap), segment.
        Assert.Equal([ItemKind.Tts, ItemKind.Pause, ItemKind.Tts], chapter.Items.Select(i => i.Kind));
        Assert.Equal(2000, chapter.Items[1].DurationMs);
        Assert.Equal(2, imported.Segments.Count);
        Assert.Same(chapter.Items[0], imported.Segments[0].Item);
        Assert.Same(chapter.Items[2], imported.Segments[1].Item);
        Assert.Equal(0, imported.Segments[0].Span.StartMs);
        Assert.Equal(structure.Spans[structure.Result.Chapters[0].Items[2]], imported.Segments[1].Span);
        Assert.All(imported.Segments, s => Assert.Equal("narrator", s.Item.Speaker));
    }
}
