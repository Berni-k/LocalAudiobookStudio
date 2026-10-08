using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.Timeline;

/// <summary>Duration of one item and whether it is an estimate (not generated yet).</summary>
public readonly record struct ItemDuration(double Ms, bool IsEstimate);

/// <summary>Computed placement of one item on the book timeline.</summary>
public readonly record struct ItemTiming(
    Guid ItemId,
    int ChapterIndex,
    int ItemIndex,
    double StartMs,
    double DurationMs,
    double GapAfterMs,
    bool IsEstimate,
    bool StartIsEstimate,
    bool IsBackground = false)
{
    public double EndMs => StartMs + DurationMs;
}

/// <summary>Timeline positions for the whole book. Positions are always computed, never stored.</summary>
public sealed class TimelineLayout
{
    public static readonly TimelineLayout Empty = new([], [], 0, 0, false);

    public TimelineLayout(ItemTiming[] items, double[] chapterStarts, double totalMs, long version, bool hasEstimates)
    {
        Items = items;
        ChapterStarts = chapterStarts;
        TotalMs = totalMs;
        Version = version;
        HasEstimates = hasEstimates;
        _index = new Dictionary<Guid, int>(items.Length);
        _maxEnd = new double[items.Length];
        double maxEnd = double.MinValue;
        for (int i = 0; i < items.Length; i++)
        {
            _index[items[i].ItemId] = i;
            maxEnd = Math.Max(maxEnd, items[i].EndMs);
            _maxEnd[i] = maxEnd;
        }
    }

    private readonly Dictionary<Guid, int> _index;
    private readonly double[] _maxEnd;

    /// <summary>All items in reading order (chapter order, then item order). Start times never decrease.</summary>
    public ItemTiming[] Items { get; }
    public double[] ChapterStarts { get; }
    public double TotalMs { get; }
    public long Version { get; }
    public bool HasEstimates { get; }

    public ItemTiming? Find(Guid itemId) => _index.TryGetValue(itemId, out var i) ? Items[i] : null;
    public int IndexOf(Guid itemId) => _index.TryGetValue(itemId, out var i) ? i : -1;

    /// <summary>Indices of items that overlap [fromMs, toMs). Uses binary search over the running max end.</summary>
    public IEnumerable<int> Overlapping(double fromMs, double toMs)
    {
        // First index whose running max end is after fromMs: nothing earlier can overlap.
        int lo = 0, hi = Items.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_maxEnd[mid] > fromMs) hi = mid; else lo = mid + 1;
        }
        for (int i = lo; i < Items.Length; i++)
        {
            var it = Items[i];
            if (it.StartMs >= toMs) yield break;
            if (it.EndMs > fromMs) yield return i;
        }
    }

    /// <summary>The item playing at <paramref name="ms"/> (the latest-starting one when several overlap).</summary>
    public int ItemAt(double ms)
    {
        int lo = 0, hi = Items.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Items[mid].StartMs <= ms) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    public int ChapterAt(double ms)
    {
        var idx = Array.BinarySearch(ChapterStarts, ms);
        if (idx < 0) idx = ~idx - 1;
        return Math.Clamp(idx, 0, Math.Max(0, ChapterStarts.Length - 1));
    }
}

public static class TimelineCalculator
{
    public const double FallbackCharsPerSecond = 15.0;

    /// <summary>
    /// Prefix-sum placement of the book. Rules:
    /// <list type="bullet">
    /// <item>Sections (speaker segments, clips) follow each other with the project's default
    /// gap, or the chapter gap after a chapter's last section. No gap after the book's last section.</item>
    /// <item>A pause replaces the gap at its position: its length is the whole spacing (0 = back to
    /// back). A negative pause makes the neighbours overlap; the next section never starts before
    /// the previous one.</item>
    /// <item>A comment takes no time and changes nothing: the book is placed as if it weren't there.</item>
    /// <item>A background item starts together with the next section (or with the pause before it)
    /// and takes no timeline time.
    /// Its length is its own, a number of seconds, the next N sections, or up to the chapter end
    /// (optionally looping to fill it).</item>
    /// </list>
    /// For pauses, <paramref name="durationOf"/> returns the signed pause length.
    /// </summary>
    public static TimelineLayout Compute(
        IReadOnlyList<Chapter> chapters,
        ProjectDefaults defaults,
        Func<TimelineItem, ItemDuration> durationOf,
        long version = 0)
    {
        var total = chapters.Sum(c => c.Items.Count);
        var timings = new ItemTiming[total];
        var chapterStarts = new double[chapters.Count];
        var isSection = new bool[total];
        var lastSectionEnd = new double[chapters.Count];
        var backgrounds = new List<(int slot, int chapter, int index, TimelineItem item, ItemDuration d, double start, bool startEstimate)>();
        double cursor = 0;
        bool anyEstimate = false;
        int n = 0;

        for (int c = 0; c < chapters.Count; c++)
        {
            chapterStarts[c] = cursor;
            var items = chapters[c].Items;
            var pending = new List<(int slot, int index, TimelineItem item, ItemDuration d)>();
            // lastStart: start of the latest placed section or pause; nothing may start before it.
            double lastStart = cursor, endNoGap = cursor;
            bool endedWithPause = false;
            lastSectionEnd[c] = cursor;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var d = durationOf(item);
                var slot = n++;

                if (item.IsBackground)
                {
                    // Placed once the next section's start is known.
                    pending.Add((slot, i, item, d));
                    continue;
                }

                if (item.TakesNoTime)
                {
                    timings[slot] = new ItemTiming(item.Id, c, i, cursor, 0, 0, false, anyEstimate);
                    continue;
                }

                if (item.Kind == ItemKind.Pause)
                {
                    var len = d.Ms;
                    double start, dur;
                    if (len >= 0)
                    {
                        start = cursor;
                        dur = len;
                        cursor += len;
                    }
                    else
                    {
                        cursor = Math.Max(lastStart, cursor + len);
                        start = cursor;
                        dur = 0;
                    }
                    // Backgrounds just before a pause start with it (so starts stay in reading order).
                    foreach (var p in pending) backgrounds.Add((p.slot, c, p.index, p.item, p.d, start, anyEstimate));
                    pending.Clear();
                    timings[slot] = new ItemTiming(item.Id, c, i, start, dur, 0, false, anyEstimate);
                    lastStart = start;
                    endNoGap = cursor;
                    endedWithPause = true;
                    continue;
                }

                // A section.
                var sectionStart = cursor;
                foreach (var p in pending) backgrounds.Add((p.slot, c, p.index, p.item, p.d, sectionStart, anyEstimate));
                pending.Clear();

                var length = Math.Max(0, d.Ms);
                var next = NextPlaced(items, i + 1);
                var lastOfBook = next < 0 && c == chapters.Count - 1;
                double gap = next < 0 ? (lastOfBook ? 0 : defaults.ChapterGapMs)
                    : items[next].Kind == ItemKind.Pause ? 0 : defaults.GapAfterMs;

                timings[slot] = new ItemTiming(item.Id, c, i, sectionStart, length, gap, d.IsEstimate, anyEstimate);
                isSection[slot] = true;
                anyEstimate |= d.IsEstimate;
                lastStart = sectionStart;
                endNoGap = sectionStart + length;
                lastSectionEnd[c] = endNoGap;
                cursor = endNoGap + gap;
                endedWithPause = false;
            }

            // A chapter ending in a pause still gets the chapter gap before the next chapter.
            if (endedWithPause && c < chapters.Count - 1) cursor += defaults.ChapterGapMs;
            // Backgrounds after the chapter's last section start where that section ends.
            foreach (var p in pending) backgrounds.Add((p.slot, c, p.index, p.item, p.d, Math.Max(lastStart, endNoGap), anyEstimate));
        }

        // Background lengths, now that every section is placed.
        foreach (var b in backgrounds)
        {
            var natural = Math.Max(0, b.d.Ms);
            var limit = b.item.BackgroundLimit ?? 0;
            double? span = b.item.EffectiveBackgroundLength switch
            {
                BackgroundLength.Seconds when limit > 0 => limit * 1000.0,
                BackgroundLength.Sections when limit >= 1 => EndOfNthSection(timings, isSection, b.slot, (int)limit) - b.start,
                BackgroundLength.ChapterEnd => lastSectionEnd[b.chapter] - b.start,
                _ => null,
            };
            var length = span is double s && s > 0
                ? (b.item.BackgroundLoop == true ? s : Math.Min(natural, s))
                : natural;
            timings[b.slot] = new ItemTiming(b.item.Id, b.chapter, b.index, b.start, length, 0, b.d.IsEstimate, b.startEstimate, IsBackground: true);
            anyEstimate |= b.d.IsEstimate;
        }

        double maxEnd = cursor;
        foreach (var t in timings) maxEnd = Math.Max(maxEnd, t.EndMs);
        return new TimelineLayout(timings, chapterStarts, maxEnd, version, anyEstimate);
    }

    /// <summary>The next item that takes part in the spacing: not a background item, comment or unknown segment.</summary>
    private static int NextPlaced(List<TimelineItem> items, int from)
    {
        for (int j = from; j < items.Count; j++)
            if (!items[j].IsBackground && !items[j].TakesNoTime) return j;
        return -1;
    }

    /// <summary>End of the <paramref name="count"/>-th section after <paramref name="slot"/> (or the last one there is).</summary>
    private static double EndOfNthSection(ItemTiming[] timings, bool[] isSection, int slot, int count)
    {
        double end = 0;
        for (int k = slot + 1, found = 0; k < timings.Length && found < count; k++)
        {
            if (!isSection[k]) continue;
            end = timings[k].EndMs;
            found++;
        }
        return end;
    }

    /// <summary>Estimated length of text for a speaker with the given measured rate (characters per second).</summary>
    public static double EstimateSpeechMs(int charCount, double? charsPerSecond)
    {
        var rate = charsPerSecond is > 1 ? charsPerSecond.Value : FallbackCharsPerSecond;
        return charCount / rate * 1000.0;
    }

    /// <summary>Trimmed length of a take or clip. Trims never make the length negative.</summary>
    public static double TrimmedMs(double rawMs, double trimStartMs, double trimEndMs) =>
        Math.Max(0, rawMs - Math.Max(0, trimStartMs) - Math.Max(0, trimEndMs));
}

/// <summary>Measures each speaker's average speaking rate across its generated takes.</summary>
public sealed class SpeakingRateTracker
{
    private readonly Dictionary<string, (double chars, double ms)> _totals = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => _totals.Clear();

    public void Add(string speakerId, int chars, double durationMs)
    {
        if (chars <= 0 || durationMs <= 0) return;
        var t = _totals.GetValueOrDefault(speakerId);
        _totals[speakerId] = (t.chars + chars, t.ms + durationMs);
    }

    /// <summary>Characters per second, or null when the speaker has no generated takes.</summary>
    public double? RateFor(string? speakerId)
    {
        if (speakerId == null || !_totals.TryGetValue(speakerId, out var t) || t.ms <= 0) return null;
        return t.chars / (t.ms / 1000.0);
    }
}
