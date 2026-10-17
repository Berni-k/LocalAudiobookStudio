using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Tests;

/// <summary>Words timed by an aligner after a worker that tells voices apart heard the text: they keep the voice and block.</summary>
public class TranscriptBlocksTests
{
    private static TranscriptResult Heard(params TranscriptSegment[] blocks) =>
        new(string.Join(' ', blocks.Select(b => b.Text)),
            blocks.Select((b, i) => new TranscriptWord(b.Text, b.StartMs, b.EndMs, null, b.Speaker, i)).ToList(), "en", blocks);

    private static TranscriptResult Timed(params string[] words) =>
        new(string.Join(' ', words), words.Select((w, i) => new TranscriptWord(w, i * 300, i * 300 + 250)).ToList(), "en");

    [Fact]
    public void TimedWordsTakeTheVoiceAndBlockTheyCameFrom()
    {
        var heard = Heard(new("Hello there, friend.", 0, 1500, "S01"), new("Fine, thanks!", 2000, 3000, "S02"));
        Assert.True(heard.TimedByBlocks);

        // The aligner drops punctuation and keeps the order.
        var merged = Transcription.WithBlocksOf(Timed("Hello", "there", "friend", "Fine", "thanks"), heard);

        Assert.Equal([0, 0, 0, 1, 1], merged.Words.Select(w => w.Block));
        Assert.Equal(["S01", "S01", "S01", "S02", "S02"], merged.Words.Select(w => w.Speaker));
        Assert.Equal(2, merged.Segments!.Count);
        Assert.False(merged.TimedByBlocks); // single words now: cutting uses their times, not the blocks'
        Assert.Equal(0, merged.Words[0].StartMs); // the times are the aligner's
        // And they reach the stored form.
        Assert.Equal([0, 0, 0, 1, 1], Transcription.Heard(merged).Select(w => w.Block));
        Assert.Equal(["S01", "S02"], Transcription.Blocks(merged)!.Select(b => b.Speaker));
    }

    [Fact]
    public void AWordTheAlignerSplitOrDroppedStaysInItsNeighboursBlock()
    {
        var heard = Heard(new("The well-known thing", 0, 1500, "S01"), new("Done now", 2000, 3000, "S02"));

        // "well-known" came back as two words, and "thing" is missing: the next word still finds its own block.
        var merged = Transcription.WithBlocksOf(Timed("The", "well", "known", "Done", "now"), heard);

        Assert.Equal([0, 0, 0, 1, 1], merged.Words.Select(w => w.Block));
    }

    [Fact]
    public void WithoutBlocksNothingChanges()
    {
        var timed = Timed("one", "two");
        var plain = new TranscriptResult("one two", [new("one", 0, 100), new("two", 100, 200)], "en");
        Assert.Same(timed.Words, Transcription.WithBlocksOf(timed, plain).Words);
        Assert.Null(Transcription.Blocks(plain));
    }
}
