using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.Tests;

public class TimelineTests
{
    private static readonly ProjectDefaults Defaults = new() { GapAfterMs = 400, ChapterGapMs = 2000 };

    /// <summary>A section (clip) of a fixed length; the length travels in DurationMs for the test's duration function.</summary>
    private static TimelineItem Sec(int ms) => new() { Kind = ItemKind.Clip, DurationMs = ms };

    private static TimelineItem Pause(int ms) => new() { Kind = ItemKind.Pause, DurationMs = ms };

    private static TimelineItem Bg(int ms, BackgroundLength? len = null, double? limit = null, bool loop = false) =>
        new() { Kind = ItemKind.Clip, DurationMs = ms, Background = true, BackgroundLength = len, BackgroundLimit = limit, BackgroundLoop = loop ? true : null };

    private static ItemDuration Dur(TimelineItem i) => new(i.DurationMs ?? 0, false);

    [Fact]
    public void PrefixSumWithDefaultAndChapterGaps()
    {
        var c1 = new Chapter { Items = { Sec(1000), Sec(2000) } };
        var c2 = new Chapter { Items = { Sec(500) } };
        var t = TimelineCalculator.Compute([c1, c2], Defaults, Dur);

        Assert.Equal(0, t.Items[0].StartMs);
        Assert.Equal(1400, t.Items[1].StartMs);        // 1000 + 400 default gap
        Assert.Equal(1400 + 2000 + 2000, t.Items[2].StartMs); // + chapter gap
        Assert.Equal(new[] { 0.0, 5400.0 }, t.ChapterStarts);
        Assert.Equal(5900, t.TotalMs);
    }

    [Fact]
    public void ItemGapOverridesAreIgnored()
    {
        var c = new Chapter { Items = { new TimelineItem { Kind = ItemKind.Clip, DurationMs = 1000, GapAfterMs = -800 }, Sec(1000) } };
        var t = TimelineCalculator.Compute([c], Defaults, Dur);
        Assert.Equal(1400, t.Items[1].StartMs);
    }

    [Fact]
    public void PauseReplacesTheGap()
    {
        var c = new Chapter { Items = { Sec(1000), Pause(1000), Sec(1000), Pause(0), Sec(1000) } };
        var t = TimelineCalculator.Compute([c], Defaults, Dur);
        Assert.Equal(1000, t.Items[1].StartMs);  // pause starts right after the section
        Assert.Equal(2000, t.Items[2].StartMs);  // exactly 1 s of silence, no default gap
        Assert.Equal(3000, t.Items[4].StartMs);  // a 0 ms pause: back to back
    }

    [Fact]
    public void NegativePauseOverlapsAndIsClamped()
    {
        var c = new Chapter { Items = { Sec(3000), Pause(-1000), Sec(1000), Pause(-5000), Sec(1000) } };
        var t = TimelineCalculator.Compute([c], Defaults, Dur);
        Assert.Equal(2000, t.Items[2].StartMs);  // overlaps the last second of the first section
        Assert.Equal(2000, t.Items[1].StartMs);  // the pause sits where the overlap starts, zero length
        Assert.Equal(0, t.Items[1].DurationMs);
        Assert.Equal(2000, t.Items[4].StartMs);  // never before the previous section's start
        Assert.Equal(3000, t.TotalMs);           // the first section still ends last
    }

    [Fact]
    public void OverlappingQueryFindsAllItemsInWindow()
    {
        var c = new Chapter { Items = { Sec(10000), Pause(-9000), Sec(1000), Sec(1000) } };
        var t = TimelineCalculator.Compute([c], Defaults, Dur);
        // Section 0 spans 0-10000, section 2 spans 1000-2000, section 3 spans 2400-3400.
        Assert.Equal(new[] { 0, 2 }, t.Overlapping(1500, 1600));
        Assert.Equal(new[] { 0, 3 }, t.Overlapping(2500, 2600));
        Assert.Equal(new[] { 0 }, t.Overlapping(9000, 9500));
        Assert.Empty(t.Overlapping(10000, 11000));
    }

    [Fact]
    public void BackgroundStartsWithTheNextSectionAndTakesNoTime()
    {
        var c = new Chapter { Items = { Sec(1000), Bg(5000), Sec(1000), Sec(1000) } };
        var t = TimelineCalculator.Compute([c], Defaults, Dur);
        Assert.Equal(1400, t.Items[1].StartMs);  // with the next section
        Assert.Equal(1400, t.Items[2].StartMs);  // which is not pushed back
        Assert.Equal(5000, t.Items[1].DurationMs);
        Assert.True(t.Items[1].IsBackground);
        Assert.Equal(2800, t.Items[3].StartMs);
        Assert.Equal(6400, t.TotalMs);           // the background outlasts the sections
    }

    [Fact]
    public void BackgroundLengthLimits()
    {
        // Sections at 0-1000, 1400-2400, 2800-3800.
        Assert.Equal(2000, Bg1(Bg(60000, BackgroundLength.Seconds, 2)));                    // seconds
        Assert.Equal(2400, Bg1(Bg(60000, BackgroundLength.Sections, 2)));                   // up to the end of the 2nd section
        Assert.Equal(3800, Bg1(Bg(60000, BackgroundLength.ChapterEnd)));                    // up to the chapter's last section
        Assert.Equal(500, Bg1(Bg(500, BackgroundLength.ChapterEnd)));                       // shorter audio stops at its end…
        Assert.Equal(3800, Bg1(Bg(500, BackgroundLength.ChapterEnd, loop: true)));          // …unless it loops

        static double Bg1(TimelineItem bg)
        {
            var c = new Chapter { Items = { bg, Sec(1000), Sec(1000), Sec(1000) } };
            return TimelineCalculator.Compute([c], Defaults, Dur).Items[0].DurationMs;
        }
    }

    [Fact]
    public void StartsStayInReadingOrder()
    {
        var c = new Chapter { Items = { Sec(1000), Bg(9000), Pause(1500), Pause(-3000), Sec(500), Bg(100) } };
        var t = TimelineCalculator.Compute([c], Defaults, Dur);
        for (int i = 1; i < t.Items.Length; i++) Assert.True(t.Items[i].StartMs >= t.Items[i - 1].StartMs);
        Assert.Equal(1500, t.Items[5].StartMs);  // trailing background starts where the last section ends
    }

    [Fact]
    public void EstimatesPropagate()
    {
        var c = new Chapter { Items = { Sec(1000), new TimelineItem { Kind = ItemKind.Tts, Text = new string('a', 150) }, Sec(100) } };
        var t = TimelineCalculator.Compute([c], Defaults, i =>
            i.Kind == ItemKind.Tts ? new ItemDuration(TimelineCalculator.EstimateSpeechMs(i.Text!.Length, null), true) : Dur(i));
        Assert.Equal(10000, t.Items[1].DurationMs); // 150 chars at 15 cps
        Assert.True(t.Items[1].IsEstimate);
        Assert.False(t.Items[1].StartIsEstimate);
        Assert.True(t.Items[2].StartIsEstimate);
        Assert.True(t.HasEstimates);
    }

    [Fact]
    public void TenThousandItemsIsFast()
    {
        var chapters = Enumerable.Range(0, 10).Select(_ => new Chapter { Items = Enumerable.Range(0, 1000).Select(_ => Sec(1000)).ToList() }).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = TimelineCalculator.Compute(chapters, Defaults, Dur);
        sw.Stop();
        Assert.Equal(10000, t.Items.Length);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Computing took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ItemAtAndChapterAt()
    {
        var c1 = new Chapter { Items = { Sec(1000), Sec(1000) } };
        var c2 = new Chapter { Items = { Sec(1000) } };
        var t = TimelineCalculator.Compute([c1, c2], Defaults, Dur);
        Assert.Equal(0, t.ItemAt(500));
        Assert.Equal(1, t.ItemAt(1500));
        Assert.Equal(0, t.ChapterAt(3000));
        Assert.Equal(1, t.ChapterAt(t.ChapterStarts[1] + 1));
    }

    [Fact]
    public void SpeakingRate()
    {
        var r = new SpeakingRateTracker();
        r.Add("n", 100, 5000);
        r.Add("n", 200, 5000);
        Assert.Equal(30.0, r.RateFor("n"));
        Assert.Null(r.RateFor("other"));
    }

    [Fact]
    public void TimeFormatting()
    {
        Assert.Equal("1:02:03", TimeFormat.Hms(3723000));
        Assert.Equal("~0:00:05", TimeFormat.Hms(5400, estimate: true));
        Assert.Equal("−1.5 s", TimeFormat.Gap(-1500));
    }
}
