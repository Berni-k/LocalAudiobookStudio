using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Core.Transcripts;

/// <summary>A word a transcription worker heard, with where it is in the audio.</summary>
public sealed record HeardWord(string Text, int StartMs, int EndMs, double? Confidence = null);

public enum WordMatch
{
    /// <summary>Heard as written.</summary>
    Match,

    /// <summary>Heard almost as written (a name spelled differently, a missing accent): not counted as an error.</summary>
    Close,

    /// <summary>Something else was heard in its place.</summary>
    Substituted,

    /// <summary>In the text, but not heard.</summary>
    Missing,

    /// <summary>Heard, but not in the text.</summary>
    Extra,
}

/// <summary>
/// One word of the comparison. <see cref="TextStart"/> and <see cref="TextLength"/> locate an expected word in the
/// original text (for highlighting it); extra words have none. Missing words get the time of the gap they belong in.
/// </summary>
public sealed record WordComparison(
    WordMatch Kind, string? Expected, string? Heard, int StartMs, int EndMs, int TextStart, int TextLength, double? Confidence)
{
    public bool IsError => Kind is WordMatch.Substituted or WordMatch.Missing or WordMatch.Extra;
}

/// <summary>
/// Compares the text a take should contain with what a transcription worker heard, word by word: which words are
/// missing, extra or different, the word error rate, and the time of every word of the text (from the heard word it
/// lines up with), so the words can be followed during playback.
/// </summary>
/// <remarks>
/// Both sides are reduced to words of letters and digits (case, punctuation and accents don't count; hyphenated
/// words are separate words), then lined up with the fewest edits. English numbers written as words count as their
/// digits ("forty-two" is "42"); in other languages, digits on one side and words on the other count as different.
/// </remarks>
public sealed partial class TranscriptCheck
{
    private TranscriptCheck(IReadOnlyList<WordComparison> words, int expectedCount)
    {
        Words = words;
        ExpectedCount = expectedCount;
    }

    /// <summary>The words in order: expected words with the heard word they line up with, extra words between them.</summary>
    public IReadOnlyList<WordComparison> Words { get; }

    /// <summary>Words in the text.</summary>
    public int ExpectedCount { get; }

    public int Substitutions => Words.Count(w => w.Kind == WordMatch.Substituted);
    public int Missing => Words.Count(w => w.Kind == WordMatch.Missing);
    public int Extra => Words.Count(w => w.Kind == WordMatch.Extra);
    public int Errors => Substitutions + Missing + Extra;

    /// <summary>Errors per word of the text (can exceed 1 when much more was heard than written).</summary>
    public double WordErrorRate => ExpectedCount == 0 ? (Extra > 0 ? 1 : 0) : (double)Errors / ExpectedCount;

    public static TranscriptCheck Compare(string expectedText, IReadOnlyList<HeardWord> heard)
    {
        var expected = MergeNumbers(Tokenize(expectedText).ToList(), expectedText);
        var said = MergeNumbers(SplitHeard(heard));
        var ops = Align(expected.Select(t => t.Key).ToList(), said.Select(h => h.Key).ToList());

        var words = new List<WordComparison>(ops.Count);
        foreach (var (kind, e, h) in ops)
        {
            var ew = e >= 0 ? expected[e] : default;
            var hw = h >= 0 ? said[h] : null;
            words.Add(new WordComparison(kind, e >= 0 ? ew.Text : null, hw?.Text,
                hw?.StartMs ?? 0, hw?.EndMs ?? 0, e >= 0 ? ew.Start : -1, e >= 0 ? ew.Length : 0, hw?.Confidence));
        }
        FillMissingTimes(words);
        return new TranscriptCheck(words, expected.Count);
    }

    // ------------------------------------------------------------------ words

    /// <summary>A word of the text. <see cref="PauseBefore"/>: punctuation separates it from the word before.</summary>
    private readonly record struct TextWord(string Text, string Key, int Start, int Length, bool PauseBefore);

    private sealed record SaidWord(string Text, string Key, int StartMs, int EndMs, double? Confidence, bool PauseBefore);

    /// <summary>The words of a text with their place in it.</summary>
    private static IEnumerable<TextWord> Tokenize(string text)
    {
        var end = 0;
        foreach (Match m in WordPattern().Matches(text))
        {
            yield return new TextWord(m.Value, Key(m.Value), m.Index, m.Length, IsPause(text.AsSpan(end, m.Index - end)));
            end = m.Index + m.Length;
        }
    }

    /// <summary>Text between two words that keeps them apart: anything but spaces and hyphens ("twenty-one, forty-two").</summary>
    private static bool IsPause(ReadOnlySpan<char> between)
    {
        foreach (var c in between)
            if (!char.IsWhiteSpace(c) && c != '-') return true;
        return false;
    }

    /// <summary>
    /// Heard words split like the text ("forty-two" is two words); the parts share the word's time in proportion to
    /// their length. Words that are only punctuation are dropped.
    /// </summary>
    private static List<SaidWord> SplitHeard(IReadOnlyList<HeardWord> heard)
    {
        var list = new List<SaidWord>();
        var trailing = "";  // what followed the last part of the previous heard word ("21," leaves ",")
        foreach (var w in heard)
        {
            var parts = WordPattern().Matches(w.Text).ToList();
            var total = parts.Sum(p => p.Length);
            var at = 0;
            var end = 0;
            foreach (var p in parts)
            {
                var pause = IsPause(end == 0 ? trailing + w.Text[..p.Index] : w.Text.AsSpan(end, p.Index - end));
                var startMs = w.StartMs + (int)Math.Round((w.EndMs - w.StartMs) * (double)at / total);
                at += p.Length;
                var endMs = w.StartMs + (int)Math.Round((w.EndMs - w.StartMs) * (double)at / total);
                list.Add(new SaidWord(p.Value, Key(p.Value), startMs, endMs, w.Confidence, pause));
                end = p.Index + p.Length;
            }
            trailing = parts.Count > 0 ? w.Text[end..] : trailing + w.Text;
        }
        return list;
    }

    /// <summary>Number words in the text become one word keyed by its digits, spanning their place in the text.</summary>
    private static List<TextWord> MergeNumbers(List<TextWord> words, string text)
    {
        foreach (var (start, count, digits) in Enumerable.Reverse(EnglishNumbers.Find(words.Select(w => w.Key).ToList(), words.Select(w => w.PauseBefore).ToList())))
        {
            var first = words[start];
            var last = words[start + count - 1];
            var length = last.Start + last.Length - first.Start;
            words[start] = new TextWord(text.Substring(first.Start, length), digits, first.Start, length, first.PauseBefore);
            words.RemoveRange(start + 1, count - 1);
        }
        return words;
    }

    /// <summary>Heard number words become one word keyed by its digits, spanning their time.</summary>
    private static List<SaidWord> MergeNumbers(List<SaidWord> words)
    {
        foreach (var (start, count, digits) in Enumerable.Reverse(EnglishNumbers.Find(words.Select(w => w.Key).ToList(), words.Select(w => w.PauseBefore).ToList())))
        {
            var run = words.GetRange(start, count);
            words[start] = new SaidWord(string.Join(' ', run.Select(w => w.Text)), digits, run[0].StartMs, run[^1].EndMs,
                run.Min(w => w.Confidence), run[0].PauseBefore);
            words.RemoveRange(start + 1, count - 1);
        }
        return words;
    }

    /// <summary>Lower case, without accents, typographic apostrophes as plain ones; "1,000" as "1000".</summary>
    private static string Key(string word)
    {
        if (char.IsDigit(word[0]) && word.All(c => char.IsDigit(c) || c is ',' or '.')) return word.Replace(",", "").Replace(".", "");
        var decomposed = word.Replace('’', '\'').ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // ------------------------------------------------------------------ alignment

    /// <summary>
    /// Lines the two word lists up with the fewest edits (Levenshtein over words). A close word costs less than a
    /// different one, so "Eowyn"/"Eowin" lines up as a pair rather than as a missing and an extra word. Between
    /// line-ups with as many edits, the one with the most identical words wins: "quick" missing and "fox" heard as
    /// "box" reads better than three words each heard as the next one.
    /// </summary>
    private static List<(WordMatch kind, int expected, int heard)> Align(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count;
        var cost = new double[n + 1, m + 1];
        for (int i = 0; i <= n; i++) cost[i, 0] = i;
        for (int j = 0; j <= m; j++) cost[0, j] = j;
        for (int i = 1; i <= n; i++)
        for (int j = 1; j <= m; j++)
            cost[i, j] = Math.Min(cost[i - 1, j - 1] + PairCost(a[i - 1], b[j - 1]),
                Math.Min(cost[i - 1, j], cost[i, j - 1]) + 1);

        var ops = new List<(WordMatch, int, int)>(Math.Max(n, m));
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && Same(cost[x, y], cost[x - 1, y - 1] + PairCost(a[x - 1], b[y - 1])))
            {
                var c = SubstitutionCost(a[x - 1], b[y - 1]);
                ops.Add((c == 0 ? WordMatch.Match : c < 1 ? WordMatch.Close : WordMatch.Substituted, x - 1, y - 1));
                x--; y--;
            }
            else if (x > 0 && Same(cost[x, y], cost[x - 1, y] + 1))
            {
                ops.Add((WordMatch.Missing, x - 1, -1));
                x--;
            }
            else
            {
                ops.Add((WordMatch.Extra, -1, y - 1));
                y--;
            }
        }
        ops.Reverse();
        return ops;
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

    /// <summary>
    /// A pair's cost in the line-up, with nudges too small to outweigh an edit: a bonus for identical words, and a
    /// different word pairs more readily with a similar one ("fox" heard as "box", not as "jumps").
    /// </summary>
    private static double PairCost(string a, string b) => SubstitutionCost(a, b) switch
    {
        0 => -MatchBonus,
        var c => c - Nudge * Similarity(a, b),
    };

    private const double MatchBonus = 1e-4, Nudge = 1e-6;

    private static double Similarity(string a, string b) => 1.0 - (double)CharDistance(a, b) / Math.Max(1, Math.Max(a.Length, b.Length));

    /// <summary>0 for the same word, 0.5 for a close one (most letters the same), 1 otherwise.</summary>
    private static double SubstitutionCost(string a, string b)
    {
        if (a == b) return 0;
        if (Math.Max(a.Length, b.Length) < 4) return 1;
        return Similarity(a, b) >= 0.75 ? 0.5 : 1;
    }

    /// <summary>Edits between two words, a swap of neighbouring letters counting as one ("metre"/"meter").</summary>
    private static int CharDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            d[i, j] = Math.Min(d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1), Math.Min(d[i - 1, j], d[i, j - 1]) + 1);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }

    /// <summary>A missing word gets the gap between the heard words around it, so highlighting can still pass over it.</summary>
    private static void FillMissingTimes(List<WordComparison> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Kind != WordMatch.Missing) continue;
            var before = words.Take(i).LastOrDefault(w => w.Kind != WordMatch.Missing);
            var after = words.Skip(i + 1).FirstOrDefault(w => w.Kind != WordMatch.Missing);
            var start = before?.EndMs ?? after?.StartMs ?? 0;
            var end = Math.Max(start, after?.StartMs ?? start);
            words[i] = words[i] with { StartMs = start, EndMs = end };
        }
    }

    /// <summary>
    /// Letters, marks and digits, with apostrophes inside a word ("don't", "l'homme"); numbers with thousands
    /// separators ("1,000", "1.000") are one word.
    /// </summary>
    [GeneratedRegex(@"\p{N}+(?:[,.]\p{N}{3})+(?![\p{L}\p{N}])|[\p{L}\p{M}\p{N}]+(?:['’][\p{L}\p{M}\p{N}]+)*")]
    private static partial Regex WordPattern();
}
