using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Protocol;
using NAudio.Wave;

namespace LocalAudiobookStudio.Tests;

/// <summary>Cutting a long recording into stretches an aligner takes, by the rough times a transcriber gave.</summary>
public sealed class AlignWindowsTests
{
    /// <summary>Words of 400 ms with 100 ms between them, except where <paramref name="gaps"/> says another gap before a word.</summary>
    private static List<TranscriptWord> Words(int count, Dictionary<int, int>? gaps = null, Dictionary<int, string>? texts = null)
    {
        var words = new List<TranscriptWord>();
        var t = 0;
        for (var i = 0; i < count; i++)
        {
            t += gaps != null && gaps.TryGetValue(i, out var gap) ? gap : i == 0 ? 0 : 100;
            words.Add(new TranscriptWord(texts != null && texts.TryGetValue(i, out var text) ? text : $"w{i}", t, t + 400));
            t += 400;
        }
        return words;
    }

    [Fact]
    public void A_recording_that_fits_is_one_window()
    {
        var windows = AlignWindows.Plan(Words(20), 60_000);

        var window = Assert.Single(windows);
        Assert.StartsWith("w0 w1", window.Text);
        Assert.EndsWith("w19", window.Text);
        Assert.Equal(0, window.StartMs);
    }

    [Fact]
    public void Windows_cover_every_word_once_in_order_and_none_is_too_long()
    {
        var words = Words(200); // 100 s
        var windows = AlignWindows.Plan(words, 20_000);

        Assert.True(windows.Count >= 5);
        Assert.Equal(string.Join(' ', words.Select(w => w.Text)), string.Join(' ', windows.Select(w => w.Text)));
        // (The padding around a window's words adds at most the pauses' halves, never more than 2 x PaddingMs.)
        Assert.All(windows, w => Assert.True(w.EndMs - w.StartMs <= 20_000 + 2 * AlignWindows.PaddingMs));
        Assert.Equal(windows.OrderBy(w => w.StartMs), windows);
    }

    [Fact]
    public void A_window_ends_at_the_longest_pause_in_reach()
    {
        // Word 14 (at 7.0 s) follows an 800 ms pause; word 18 a 400 ms one. A 10 s target looks from 6 s on.
        var words = Words(40, new() { [14] = 800, [18] = 400 });
        var windows = AlignWindows.Plan(words, 10_000);

        Assert.EndsWith("w13", windows[0].Text);
        Assert.StartsWith("w14", windows[1].Text);
    }

    [Fact]
    public void Without_a_pause_it_cuts_at_the_end_of_a_sentence()
    {
        var words = Words(40, texts: new() { [15] = "there.", [10] = "early." });
        var windows = AlignWindows.Plan(words, 10_000);

        // Gaps are all 100 ms (too short to count); the last sentence end in reach is word 15.
        Assert.EndsWith("there.", windows[0].Text);
        Assert.StartsWith("w16", windows[1].Text);
    }

    [Fact]
    public void A_word_longer_than_the_target_still_makes_progress()
    {
        var words = new List<TranscriptWord> { new("long", 0, 30_000), new("next", 30_100, 30_500) };

        var windows = AlignWindows.Plan(words, 10_000);

        Assert.Equal(["long", "next"], windows.Select(w => w.Text));
    }

    [Fact]
    public void Blocks_stand_in_for_words_when_there_are_no_word_times()
    {
        var heard = new TranscriptResult("a b", [], null, [new TranscriptSegment("first block", 0, 5000, "S1"), new TranscriptSegment("second", 5200, 9000, "S2")]);

        var units = AlignWindows.UnitsOf(heard);

        Assert.Equal(["first block", "second"], units.Select(u => u.Text));
        Assert.Empty(AlignWindows.UnitsOf(new TranscriptResult("text only", [], null)));
    }

    [Fact]
    public void Times_move_to_the_recordings_timeline()
    {
        var windows = new List<AlignWindow> { new("a b", 0, 2000), new("c", 10_000, 12_000) };
        IReadOnlyList<TranscriptWord>[] timed = [[new("a", 100, 500), new("b", 600, 900)], [new("c", 300, 700)]];

        var joined = AlignWindows.Join(windows, timed);

        Assert.Equal([100, 600, 10_300], joined.Select(w => w.StartMs));
        Assert.Equal([500, 900, 10_700], joined.Select(w => w.EndMs));
    }

    [Fact]
    public void A_slice_is_that_part_of_the_audio()
    {
        using var tmp = new TempDir();
        var source = Path.Combine(tmp.Path, "all.wav");
        var format = new WaveFormat(16_000, 16, 1);
        using (var writer = new WaveFileWriter(source, format))
            writer.Write(new byte[16_000 * 2 * 2], 0, 16_000 * 2 * 2); // 2 s of silence

        var slice = Path.Combine(tmp.Path, "part.wav");
        AlignWindows.Slice(source, 500, 1500, slice);
        using (var reader = new WaveFileReader(slice))
            Assert.Equal(1000, reader.TotalTime.TotalMilliseconds, 1);

        AlignWindows.Slice(source, 1500, 9000, slice); // past the end: cut to the file
        using (var reader = new WaveFileReader(slice))
            Assert.Equal(500, reader.TotalTime.TotalMilliseconds, 1);
    }
}
