using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Core.Import;

/// <summary>Splits long text at sentence boundaries so each piece fits a model's input limit.</summary>
public static partial class SentenceSplitter
{
    // A sentence ends at . ! ? or … optionally followed by closing quotes/brackets, then whitespace.
    [GeneratedRegex(@"(?<=[.!?…]+[""'”’)\]]*)\s+")]
    private static partial Regex SentenceBoundary();

    // Weaker boundaries for sentences that are still too long.
    [GeneratedRegex(@"(?<=[,;:—–])\s+")]
    private static partial Regex ClauseBoundary();

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr.", "mrs.", "ms.", "dr.", "st.", "jr.", "sr.", "prof.", "vs.", "etc.", "e.g.", "i.e.", "mt.", "no."
    };

    public static List<string> SplitSentences(string text)
    {
        var raw = SentenceBoundary().Split(text.Trim());
        var result = new List<string>();
        foreach (var piece in raw)
        {
            if (piece.Length == 0) continue;
            // Re-join splits after common abbreviations ("Mr. Smith").
            if (result.Count > 0 && EndsWithAbbreviation(result[^1]))
                result[^1] = result[^1] + " " + piece;
            else
                result.Add(piece);
        }
        return result;
    }

    private static bool EndsWithAbbreviation(string s)
    {
        var lastSpace = s.LastIndexOf(' ');
        var word = lastSpace >= 0 ? s[(lastSpace + 1)..] : s;
        return Abbreviations.Contains(word);
    }

    /// <summary>
    /// Returns the text unchanged if it fits; otherwise packs whole sentences into chunks of at most
    /// <paramref name="maxChars"/>. A single sentence that is too long is split at clauses, then words.
    /// </summary>
    public static List<string> SplitToLimit(string text, int maxChars)
    {
        text = text.Trim();
        if (text.Length <= maxChars) return [text];

        var units = new List<string>();
        foreach (var sentence in SplitSentences(text))
        {
            if (sentence.Length <= maxChars) { units.Add(sentence); continue; }
            foreach (var clause in ClauseBoundary().Split(sentence))
            {
                if (clause.Length <= maxChars) { units.Add(clause); continue; }
                units.AddRange(SplitWords(clause, maxChars));
            }
        }

        var chunks = new List<string>();
        var current = "";
        foreach (var u in units)
        {
            if (current.Length == 0) current = u;
            else if (current.Length + 1 + u.Length <= maxChars) current += " " + u;
            else { chunks.Add(current); current = u; }
        }
        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }

    private static IEnumerable<string> SplitWords(string text, int maxChars)
    {
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > maxChars)
            {
                if (current.Length > 0) { yield return current; current = ""; }
                for (int i = 0; i < word.Length; i += maxChars)
                    yield return word.Substring(i, Math.Min(maxChars, word.Length - i));
                continue;
            }
            if (current.Length == 0) current = word;
            else if (current.Length + 1 + word.Length <= maxChars) current += " " + word;
            else { yield return current; current = word; }
        }
        if (current.Length > 0) yield return current;
    }
}
