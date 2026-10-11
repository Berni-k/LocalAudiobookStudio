using System.Globalization;
using System.Text;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.Import;

/// <summary>
/// Writes a project back to plain text in the bracket-tag format, so it can be edited
/// externally and re-imported with <see cref="TextImporter"/>.
/// </summary>
public static class ScriptExporter
{
    public static string Export(Project project, IEnumerable<Chapter>? chapters = null)
    {
        var sb = new StringBuilder();
        foreach (var chapter in chapters ?? project.Chapters)
        {
            if (sb.Length > 0) sb.Append('\n');
            var title = Escape(chapter.Title);
            sb.Append("# ").Append(title.StartsWith('#') ? "\\" + title : title).Append("\n\n");
            AppendItems(sb, project, chapter, skipSpokenHeading: true);
        }
        return sb.ToString();
    }

    public static void AppendItems(StringBuilder sb, Project project, Chapter chapter, bool skipSpokenHeading)
    {
        for (int i = 0; i < chapter.Items.Count; i++)
        {
            var item = chapter.Items[i];
            // The heading line recreates the spoken heading on import, so don't write it twice.
            if (skipSpokenHeading && i == 0 && item.Kind == ItemKind.Tts && item.Text == chapter.Title) continue;
            sb.Append(FormatItem(project, item)).Append("\n\n");
        }
    }

    public static string FormatItem(Project project, TimelineItem item) =>
        (string.IsNullOrWhiteSpace(item.ExportId) ? "" : $"[id: {Escape(item.ExportId, "]")}]\n")
        + (item.Kind == ItemKind.Tts && item.DurationSec is double sec ? string.Create(CultureInfo.InvariantCulture, $"[length: {sec:0.##}s]\n") : "")
        + FormatContent(project, item);

    private static string FormatContent(Project project, TimelineItem item) => item.Kind switch
    {
        ItemKind.Tts => $"[{Escape(SpeakerLabel(project, item.Speaker), "[]:")}] {EscapeSpeech(item.Text ?? "")}",
        ItemKind.Pause => string.Create(CultureInfo.InvariantCulture, $"[pause {item.DurationMs ?? 0}ms]"),
        ItemKind.Clip => $"[clip: {Escape(item.ClipFile ?? "", "]")}]",
        ItemKind.Comment => $"[comment: {Escape(item.Text ?? "", "[]")}]",
        ItemKind.Unknown => $"[comment: unknown segment {Escape(item.Raw?.ToJsonString() ?? "", "[]")}]",
        _ => ""
    };

    /// <summary>
    /// <paramref name="s"/> as the importer reads it back unchanged: a backslash before every backslash and before each
    /// of the <paramref name="special"/> characters (see <see cref="TextImporter"/>).
    /// </summary>
    private static string Escape(string s, string special = "")
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\\' || special.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A segment's text after its speaker tag. There, only an id or a length would be read as a tag ("[length: 4s]" as
    /// text): its bracket is escaped. A later line starts a line of its own, where "[" or a colon ("Name: text") could
    /// be read as a marker, and a blank line would end the paragraph: those lines are escaped as plain text.
    /// </summary>
    private static string EscapeSpeech(string text)
    {
        var lines = Escape(text).Split('\n');
        if (TextImporter.StartsWithSectionTag(lines[0])) lines[0] = "\\" + lines[0];
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.Length == 0) lines[i] = "\\";
            else if (!line.StartsWith('\\') && (line.StartsWith('[') || line.Contains(':'))) lines[i] = "\\" + line;
        }
        return string.Join('\n', lines);
    }

    private static string SpeakerLabel(Project project, string? speakerId)
    {
        var sp = project.FindSpeaker(speakerId);
        return sp == null ? speakerId ?? project.Defaults.Speaker : (string.IsNullOrWhiteSpace(sp.DisplayName) ? sp.Id : sp.DisplayName);
    }
}

/// <summary>
/// Merges re-imported items into an existing chapter, preserving ids (and therefore takes, gain,
/// gaps and trims) for items whose text is unchanged (exact match) or nearly unchanged (fuzzy match).
/// </summary>
public static class ScriptMerger
{
    public const double FuzzyThreshold = 0.8;

    /// <summary>Returns the new item list for the chapter. <paramref name="imported"/> items are consumed.</summary>
    public static List<TimelineItem> Merge(IReadOnlyList<TimelineItem> existing, IReadOnlyList<TimelineItem> imported)
    {
        var used = new bool[existing.Count];
        var result = new List<TimelineItem>(imported.Count);
        var matchIndex = new int?[imported.Count];

        // Pass 1: exact matches, preferring the nearest unused item in document order.
        var cursor = 0;
        for (int i = 0; i < imported.Count; i++)
        {
            var key = Key(imported[i]);
            var found = FindNearest(existing, used, cursor, e => Key(e) == key);
            if (found >= 0) { used[found] = true; matchIndex[i] = found; cursor = found + 1; }
        }

        // Pass 2: fuzzy matches for what's left, between the neighbouring exact matches.
        for (int i = 0; i < imported.Count; i++)
        {
            if (matchIndex[i] != null) continue;
            var imp = imported[i];
            if (imp.Kind != ItemKind.Tts) continue;
            var lo = 0; var hi = existing.Count;
            for (int k = i - 1; k >= 0; k--) if (matchIndex[k] is int a) { lo = a + 1; break; }
            for (int k = i + 1; k < imported.Count; k++) if (matchIndex[k] is int b) { hi = b; break; }

            var bestScore = FuzzyThreshold; var best = -1;
            for (int e = lo; e < hi; e++)
            {
                if (used[e] || existing[e].Kind != imp.Kind) continue;
                var score = Similarity(existing[e].DisplayText, imp.DisplayText);
                if (score >= bestScore) { bestScore = score; best = e; }
            }
            if (best >= 0) { used[best] = true; matchIndex[i] = best; }
        }

        for (int i = 0; i < imported.Count; i++)
        {
            var imp = imported[i];
            if (matchIndex[i] is not int e) { result.Add(imp); continue; }
            // Keep the existing item (id, takes, gain, gaps, trims) and apply the new content.
            var kept = existing[e];
            kept.ExportId = imp.ExportId;
            switch (imp.Kind)
            {
                case ItemKind.Tts: kept.Text = imp.Text; kept.Speaker = imp.Speaker; kept.DurationSec = imp.DurationSec; break;
                case ItemKind.Pause: kept.DurationMs = imp.DurationMs; break;
                case ItemKind.Clip: kept.ClipFile = imp.ClipFile; break;
                case ItemKind.Comment: kept.Text = imp.Text; break;
            }
            result.Add(kept);
        }
        return result;
    }

    private static string Key(TimelineItem i) => i.Kind + "\u0001" + i.DisplayText.Trim() + "\u0001" + i.Speaker;

    private static int FindNearest(IReadOnlyList<TimelineItem> list, bool[] used, int from, Func<TimelineItem, bool> match)
    {
        for (int i = from; i < list.Count; i++) if (!used[i] && match(list[i])) return i;
        for (int i = 0; i < Math.Min(from, list.Count); i++) if (!used[i] && match(list[i])) return i;
        return -1;
    }

    /// <summary>Dice coefficient over character bigrams: 1.0 for identical text, near 0 for unrelated text.</summary>
    public static double Similarity(string a, string b)
    {
        a = a.Trim().ToLowerInvariant();
        b = b.Trim().ToLowerInvariant();
        if (a == b) return 1.0;
        if (a.Length < 2 || b.Length < 2) return 0.0;

        var counts = new Dictionary<int, int>();
        for (int i = 0; i < a.Length - 1; i++)
        {
            var k = (a[i] << 16) | a[i + 1];
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        var intersection = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            var k = (b[i] << 16) | b[i + 1];
            if (counts.TryGetValue(k, out var c) && c > 0) { intersection++; counts[k] = c - 1; }
        }
        return 2.0 * intersection / (a.Length - 1 + b.Length - 1);
    }
}
