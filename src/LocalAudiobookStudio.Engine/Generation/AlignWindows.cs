using LocalAudiobookStudio.Workers.Protocol;
using NAudio.Wave;

namespace LocalAudiobookStudio.Engine;

/// <summary>A stretch of a recording an aligner times on its own: its text, and where its audio starts and ends.</summary>
public sealed record AlignWindow(string Text, int StartMs, int EndMs);

/// <summary>
/// Cuts a long recording into stretches an aligner can take (a forced aligner holds the whole take in memory, so each has
/// a limit), using the rough times another worker already gave: the words a transcriber heard, or the blocks of one that
/// tells voices apart. Cuts go where the speaker pauses, preferably at the end of a sentence, so no word is split.
/// </summary>
public static class AlignWindows
{
    /// <summary>Audio kept beyond a window's first and last word (at most half the pause there), so the aligner has room around them.</summary>
    public const int PaddingMs = 300;

    /// <summary>A pause shorter than this is not a place to cut if a sentence end is near.</summary>
    private const int MinPauseMs = 120;

    /// <summary>A window ends no earlier than this share of the target length (else it cuts too eagerly at the first pause).</summary>
    private const double EarliestCut = 0.6;

    /// <summary>
    /// How much audio one aligner request holds: 60 % of what the aligner declares it takes (<see cref="Workers.Manifest.WorkerManifest.MaxAudioMinutes"/>),
    /// else of 8 minutes, the limit of the slowest we know (MMS).
    /// </summary>
    public static int TargetMs(Workers.Manifest.WorkerManifest aligner) =>
        (int)((aligner.MaxAudioMinutes is > 0 and var minutes ? minutes : 8) * 60_000 * 0.6);

    /// <summary>
    /// The units to cut by: the heard words when they carry times (or blocks standing in for them), else the transcript's
    /// blocks. Empty when there is no timing to go by.
    /// </summary>
    public static IReadOnlyList<TranscriptWord> UnitsOf(TranscriptResult heard)
    {
        if (heard.Words.Count > 0 && heard.Words[^1].EndMs > 0) return heard.Words;
        return heard.Segments?.Select(s => new TranscriptWord(s.Text, s.StartMs, s.EndMs, null, s.Speaker)).ToList() ?? [];
    }

    /// <summary>
    /// The windows for <paramref name="units"/> (in time order): one when it all fits in <paramref name="targetMs"/>, else the
    /// text cut at pauses so that none is longer, in order, together holding every unit once.
    /// </summary>
    public static List<AlignWindow> Plan(IReadOnlyList<TranscriptWord> units, int targetMs)
    {
        var windows = new List<AlignWindow>();
        var n = units.Count;
        if (n == 0) return windows;

        var start = 0;
        while (start < n)
        {
            // The last unit that still fits with the first one.
            var limit = start;
            while (limit < n && units[limit].EndMs - units[start].StartMs <= targetMs) limit++;
            var cut = limit >= n ? n : BestCut(units, start, Math.Max(limit, start + 1), targetMs); // (one unit alone may be too long)
            windows.Add(Window(units, start, cut));
            start = cut;
        }
        return windows;
    }

    /// <summary>Where to end the window that starts at <paramref name="start"/> and may take units up to (not including) <paramref name="limit"/>.</summary>
    private static int BestCut(IReadOnlyList<TranscriptWord> units, int start, int limit, int targetMs)
    {
        // Candidate cuts: after unit c-1, with the window at least EarliestCut of the target long.
        var earliest = limit;
        for (var c = start + 1; c <= limit; c++)
            if (units[c - 1].EndMs - units[start].StartMs >= targetMs * EarliestCut) { earliest = c; break; }

        int Gap(int c) => c < units.Count ? Math.Max(0, units[c].StartMs - units[c - 1].EndMs) : 0;
        bool EndsSentence(int c) => units[c - 1].Text.TrimEnd() is [.., '.' or '?' or '!' or '…'];

        var best = limit;
        var bestGap = -1;
        for (var c = earliest; c <= limit; c++)
        {
            var gap = Gap(c);
            if (gap > bestGap || (gap == bestGap && gap > 0)) { best = c; bestGap = gap; }
        }
        if (bestGap >= MinPauseMs) return best;

        // No real pause in reach: the last sentence end, else wherever the audio runs out.
        for (var c = limit; c >= earliest; c--)
            if (EndsSentence(c)) return c;
        return limit;
    }

    private static AlignWindow Window(IReadOnlyList<TranscriptWord> units, int first, int endExclusive)
    {
        var gapBefore = first > 0 ? Math.Max(0, units[first].StartMs - units[first - 1].EndMs) : PaddingMs * 2;
        var gapAfter = endExclusive < units.Count ? Math.Max(0, units[endExclusive].StartMs - units[endExclusive - 1].EndMs) : PaddingMs * 2;
        var from = Math.Max(0, units[first].StartMs - Math.Min(PaddingMs, gapBefore / 2));
        var to = units[endExclusive - 1].EndMs + Math.Min(PaddingMs, gapAfter / 2);
        var text = string.Join(' ', units.Skip(first).Take(endExclusive - first).Select(u => u.Text.Trim()).Where(t => t.Length > 0));
        return new AlignWindow(text, from, to);
    }

    /// <summary>Writes the part of a 16 kHz mono WAV from <paramref name="startMs"/> to <paramref name="endMs"/> (cut to the file) as a WAV of its own.</summary>
    public static void Slice(string source, int startMs, int endMs, string destination)
    {
        using var reader = new WaveFileReader(source);
        var format = reader.WaveFormat;
        long Bytes(int ms) => Math.Clamp((long)ms * format.AverageBytesPerSecond / 1000, 0, reader.Length) / format.BlockAlign * format.BlockAlign;
        var from = Bytes(startMs);
        var to = Math.Max(from, Bytes(endMs));
        reader.Position = from;
        using var writer = new WaveFileWriter(destination, format);
        var buffer = new byte[64 * 1024];
        for (var left = to - from; left > 0;)
        {
            var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) break;
            writer.Write(buffer, 0, read);
            left -= read;
        }
    }

    /// <summary>
    /// The words each window's aligner returned, one list per window in order, on the recording's own timeline: every time
    /// moves by where its window's audio started.
    /// </summary>
    public static List<TranscriptWord> Join(IReadOnlyList<AlignWindow> windows, IReadOnlyList<IReadOnlyList<TranscriptWord>> timed)
    {
        var words = new List<TranscriptWord>();
        for (var i = 0; i < windows.Count && i < timed.Count; i++)
            words.AddRange(timed[i].Select(w => w with { StartMs = w.StartMs + windows[i].StartMs, EndMs = w.EndMs + windows[i].StartMs }));
        return words;
    }
}
