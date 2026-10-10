using System.Globalization;

namespace LocalAudiobookStudio.Core.Transcripts;

/// <summary>
/// Finds English numbers written as words ("forty two", "one hundred and three", "nineteen eighty four") in a list of
/// lower-case words, so they compare equal to digits: a text says "forty-two" where the recogniser writes "42", or the
/// other way round. Years read in pairs ("twenty twenty four") become one number.
/// </summary>
internal static class EnglishNumbers
{
    private static readonly Dictionary<string, long> Small = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, long> Scales = new()
    {
        ["thousand"] = 1_000, ["million"] = 1_000_000, ["billion"] = 1_000_000_000,
    };

    /// <summary>
    /// Runs of number words: where each starts, how many words it spans, and its value in digits. A run never crosses
    /// a word with <paramref name="pauseBefore"/> set (punctuation before it: "twenty-one, forty-two" is two numbers).
    /// </summary>
    public static List<(int Start, int Count, string Digits)> Find(IReadOnlyList<string> words, IReadOnlyList<bool> pauseBefore)
    {
        var found = new List<(int, int, string)>();
        var i = 0;
        while (i < words.Count)
        {
            var (value, count, scaled) = ParseOne(words, pauseBefore, i);
            if (count == 0) { i++; continue; }
            // "nineteen eighty four", "twenty twenty": two numbers under a hundred read as one year.
            if (!scaled && value is >= 10 and < 100 && i + count < words.Count && !pauseBefore[i + count] &&
                ParseOne(words, pauseBefore, i + count) is { Count: > 0, Scaled: false } next && next.Value is >= 10 and < 100)
            {
                value = value * 100 + next.Value;
                count += next.Count;
            }
            found.Add((i, count, value.ToString(CultureInfo.InvariantCulture)));
            i += count;
        }
        return found;
    }

    /// <summary>One number starting at <paramref name="start"/>; Count is 0 when there is none.</summary>
    private static (long Value, int Count, bool Scaled) ParseOne(IReadOnlyList<string> words, IReadOnlyList<bool> pauseBefore, int start)
    {
        long total = 0, current = 0;
        var scaled = false;
        var j = start;
        for (; j < words.Count; j++)
        {
            if (j > start && pauseBefore[j]) break;
            var w = words[j];
            if (Small.TryGetValue(w, out var v))
            {
                // A unit may follow a round ten ("forty two"); anything else starts a new number ("nineteen eighty").
                var tail = current % 100;
                var fits = j == start || (tail == 0 && (current > 0 || total > 0)) || (tail >= 20 && tail % 10 == 0 && v < 10);
                if (!fits) break;
                current += v;
            }
            else if (w == "hundred" && current is > 0 and < 100)
            {
                current *= 100;
                scaled = true;
            }
            else if (Scales.TryGetValue(w, out var scale) && current > 0)
            {
                total += current * scale;
                current = 0;
                scaled = true;
            }
            else if (w == "and" && j > start && (current >= 100 || total > 0) && j + 1 < words.Count && !pauseBefore[j + 1] && Small.ContainsKey(words[j + 1]))
            {
                // "one hundred and three": the "and" belongs to the number.
            }
            else break;
        }
        return (total + current, j - start, scaled);
    }
}
