using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.Import;

/// <summary>
/// Turns plain text into chapters and items. Recognized markers (any mix per file):
/// <c>Speaker 1: text</c>, <c>Name: text</c> (known names only), <c>[Name] text</c>,
/// <c>[speaker: Name]</c> block tags, <c>[pause 1.5s]</c> (negative, <c>[pause -500ms]</c>: the next item starts that
/// much earlier, overlapping), <c>[clip: path]</c>, <c>[id: name]</c> and <c>[length: 4s]</c> (for the next section:
/// on its own line or at the start of one, before or after a speaker tag), and
/// comments: <c>[comment: text]</c> (may span lines and paragraphs, up to a line ending in "]") or <c>[comment] text</c> (one line).
/// </summary>
public static partial class TextImporter
{
    [GeneratedRegex(@"^(speaker\s+\d+)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex VibeVoiceRegex();

    [GeneratedRegex(@"^\[\s*([^\[\]:]{1,60}?)\s*\]\s*(.*)$")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"^([^:\[\]\n]{1,40}?)\s*:\s*(.+)$")]
    private static partial Regex NamePrefixRegex();

    [GeneratedRegex(@"^\[\s*speaker\s*:\s*([^\]]+?)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    /// <summary>"[pause 1.5s]", "[pause 800ms]", "[pause: 2]" (seconds), and negative for an overlap: "[pause -500ms]"
    /// (also with the typographic minus "−" that word processors put in).</summary>
    [GeneratedRegex(@"^\[\s*pause\s*:?\s*([-−]?\s*\d+(?:\.\d+)?)\s*(ms|s|sec|seconds?)?\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex PauseRegex();

    [GeneratedRegex(@"^\[\s*clip\s*:\s*([^\]]+?)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex ClipRegex();

    /// <summary>"[id: guard_greeting]" at the start of a line; the rest of the line follows in group 2.</summary>
    [GeneratedRegex(@"^\[\s*id\s*:\s*([^\]]+?)\s*\]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex IdRegex();

    /// <summary>"[length: 4s]", "[length 2.5 seconds]", "[length: 1 min]" at the start of a line; the rest follows in group 3.</summary>
    [GeneratedRegex(@"^\[\s*length\s*:?\s*(\d+(?:\.\d+)?)\s*(ms|s|sec|secs|seconds?|min|mins|minutes?)?\s*\]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LengthRegex();

    /// <summary>
    /// "[comment: …]" from a line start to the first "]" that ends a line. It may span lines and paragraphs, but
    /// not past a line that starts with "[" (another tag), so a missing "]" can't swallow the rest of the book.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*\[[ \t]*comment[ \t]*:((?:(?!\n[ \t]*\[).)*?)\][ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CommentBlockRegex();

    /// <summary>"[comment] text", or a "[comment: text" left unclosed: the rest of the line.</summary>
    [GeneratedRegex(@"^[ \t]*\[[ \t]*comment[ \t]*[\]:][ \t]*(.*?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CommentLineRegex();

    /// <summary>Stands in for a comment while the text is split into paragraphs (comments may contain blank lines).</summary>
    private const char CommentMark = '\u0001';

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpacesRegex();

    public static ImportResult Parse(string text, ImportOptions options)
    {
        var headingRegex = new Regex(options.ChapterRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var known = new HashSet<string>(options.KnownSpeakerNames, StringComparer.OrdinalIgnoreCase);
        var result = new ImportResult();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Normalize line endings.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Comments become paragraphs of their own, marked by an index, before the text is split.
        var comments = new List<string>();
        string MarkComment(Match m)
        {
            var lines = m.Groups[1].Value.Split('\n').Select(l => l.TrimEnd());
            comments.Add(string.Join('\n', lines).Trim());
            return $"\n\n{CommentMark}{comments.Count - 1}{CommentMark}\n\n";
        }
        text = CommentBlockRegex().Replace(text, MarkComment);
        text = CommentLineRegex().Replace(text, MarkComment);

        // 2. Paragraphs are separated by blank lines (or every line, in line mode).
        IEnumerable<string> paragraphs = options.EveryLineIsParagraph
            ? text.Split('\n')
            : BlankLineRegex().Split(text);

        ImportedChapter? chapter = null;
        string? blockSpeaker = null;
        // From "[id: …]" and "[length: …]": go to the next section (every part, if a long paragraph is split).
        string? pendingId = null;
        double? pendingLength = null;

        // Comments before the first heading go into the first chapter (after its spoken heading).
        var leadingComments = new List<ImportedItem>();

        ImportedChapter CurrentChapter()
        {
            if (chapter == null) StartChapter(options.DefaultChapterTitle, spokenHeading: false);
            return chapter!;
        }

        void StartChapter(string title, bool spokenHeading)
        {
            chapter = new ImportedChapter { Title = title };
            result.Chapters.Add(chapter);
            if (spokenHeading) chapter.Items.Add(new ImportedItem { Kind = ItemKind.Tts, Text = title, IsHeading = true });
            chapter.Items.AddRange(leadingComments);
            leadingComments.Clear();
        }

        void AddSpeech(string body, string? label)
        {
            body = SpacesRegex().Replace(body, " ").Trim();
            if (body.Length == 0) return;
            if (label != null && labels.Add(label)) result.SpeakerLabels.Add(label);
            var max = options.MaxCharsForSpeaker?.Invoke(label) ?? options.MaxSegmentChars;
            foreach (var part in SentenceSplitter.SplitToLimit(body, Math.Max(50, max)))
                CurrentChapter().Items.Add(new ImportedItem
                {
                    Kind = ItemKind.Tts, Text = part, SpeakerLabel = label, ExportId = pendingId, DurationSec = pendingLength,
                });
            pendingId = null;
            pendingLength = null;
        }

        // "[length: 4s]" at the start of a line: remembered for the next section, the rest of the line stays.
        bool TakeLength(ref string line)
        {
            var m = LengthRegex().Match(line);
            if (!m.Success) return false;
            var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            pendingLength = unit == "ms" ? value / 1000.0 : unit.StartsWith('m') ? value * 60.0 : value;
            line = m.Groups[3].Value.Trim();
            return true;
        }

        // Any of the section tags at the start of a line, in any order.
        bool TakeSectionTags(ref string line)
        {
            var any = false;
            while (TakeId(ref line) || TakeLength(ref line)) any = true;
            return any;
        }

        // "[id: name]" at the start of a line: remembered for the next section, the rest of the line stays.
        bool TakeId(ref string line)
        {
            var m = IdRegex().Match(line);
            if (!m.Success) return false;
            pendingId = m.Groups[1].Value.Trim();
            line = m.Groups[2].Value.Trim();
            return true;
        }

        foreach (var rawPara in paragraphs)
        {
            var lines = rawPara.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;

            if (lines.Count == 1 && lines[0].Length > 2 && lines[0][0] == CommentMark && lines[0][^1] == CommentMark
                && int.TryParse(lines[0][1..^1], out var commentIndex))
            {
                var comment = new ImportedItem { Kind = ItemKind.Comment, Text = comments[commentIndex] };
                if (chapter == null) leadingComments.Add(comment);
                else chapter.Items.Add(comment);
                continue;
            }

            // A single-line paragraph may be a heading.
            if (lines.Count == 1 && IsHeading(lines[0], headingRegex, options, out var title))
            {
                StartChapter(title, options.SpeakHeadings);
                continue;
            }

            // Walk the lines; standalone tags and speaker markers start new items.
            var buffer = new StringBuilder();
            string? bufferSpeaker = blockSpeaker;

            void FlushBuffer()
            {
                if (buffer.Length > 0) AddSpeech(buffer.ToString(), bufferSpeaker);
                buffer.Clear();
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine;
                // An id or length starts a section of its own: the text before it is finished first.
                if (IdRegex().IsMatch(line) || LengthRegex().IsMatch(line))
                {
                    FlushBuffer();
                    TakeSectionTags(ref line);
                    if (line.Length == 0) continue;
                }

                if (TryParseStandaloneTag(line, out var tagItem, out var newBlockSpeaker))
                {
                    FlushBuffer();
                    if (newBlockSpeaker != null)
                    {
                        blockSpeaker = newBlockSpeaker.Length == 0 ? null : newBlockSpeaker;
                        if (blockSpeaker != null && labels.Add(blockSpeaker)) result.SpeakerLabels.Add(blockSpeaker);
                    }
                    else if (tagItem != null)
                    {
                        if (tagItem.Kind == ItemKind.Clip)
                        {
                            tagItem.ExportId = pendingId;
                            pendingId = null;
                        }
                        CurrentChapter().Items.Add(tagItem);
                    }
                    bufferSpeaker = blockSpeaker;
                    continue;
                }

                if (TryParseSpeakerMarker(line, known, out var label, out var body))
                {
                    FlushBuffer();
                    TakeSectionTags(ref body); // "[Alice] [id: …] text"
                    bufferSpeaker = label;
                    buffer.Append(body);
                    continue;
                }

                if (buffer.Length > 0) buffer.Append(' ');
                buffer.Append(line);
            }
            FlushBuffer();
        }

        // A text with nothing but comments still keeps them.
        if (leadingComments.Count > 0) CurrentChapter();
        return result;
    }

    private static bool IsHeading(string line, Regex headingRegex, ImportOptions options, out string title)
    {
        title = line;
        if (line.Length > options.MaxHeadingChars) return false;
        if (line.StartsWith('#'))
        {
            title = line.TrimStart('#').Trim();
            return title.Length > 0;
        }
        return headingRegex.IsMatch(line);
    }

    /// <summary>Block speaker tags, pause and clip tags that fill a whole line.</summary>
    private static bool TryParseStandaloneTag(string line, out ImportedItem? item, out string? blockSpeaker)
    {
        item = null;
        blockSpeaker = null;
        if (!line.StartsWith('[') || !line.EndsWith(']')) return false;

        var m = BlockTagRegex().Match(line);
        if (m.Success)
        {
            var name = m.Groups[1].Value.Trim();
            // "[speaker: default]" or "[speaker: -]" returns to the default speaker.
            blockSpeaker = name is "-" || name.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : name;
            return true;
        }

        m = PauseRegex().Match(line);
        if (m.Success)
        {
            var number = m.Groups[1].Value.Replace('−', '-').Replace(" ", "");
            var value = double.Parse(number, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            var ms = unit == "ms" ? value : value * 1000.0;
            item = new ImportedItem { Kind = ItemKind.Pause, DurationMs = (int)Math.Round(ms) };
            return true;
        }

        m = ClipRegex().Match(line);
        if (m.Success)
        {
            item = new ImportedItem { Kind = ItemKind.Clip, ClipFile = m.Groups[1].Value.Trim() };
            return true;
        }

        return false;
    }

    /// <summary>Speaker markers at the start of a line: "Speaker 1:", "[Name]" and known "Name:".</summary>
    private static bool TryParseSpeakerMarker(string line, HashSet<string> known, out string label, out string body)
    {
        label = "";
        body = "";

        var m = VibeVoiceRegex().Match(line);
        if (m.Success)
        {
            label = NormalizeSpeakerN(m.Groups[1].Value);
            body = m.Groups[2].Value;
            return true;
        }

        m = BracketTagRegex().Match(line);
        if (m.Success && !IsReservedTag(m.Groups[1].Value))
        {
            label = m.Groups[1].Value.Trim();
            body = m.Groups[2].Value;
            return true;
        }

        if (known.Count > 0)
        {
            m = NamePrefixRegex().Match(line);
            if (m.Success && known.Contains(m.Groups[1].Value.Trim()))
            {
                label = known.First(k => k.Equals(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase));
                body = m.Groups[2].Value;
                return true;
            }
        }
        return false;
    }

    private static bool IsReservedTag(string tag)
    {
        var t = tag.Trim().ToLowerInvariant();
        return t.StartsWith("pause") || t.StartsWith("speaker:") || t.StartsWith("clip") || t.StartsWith("comment")
               || t.StartsWith("id:") || t.StartsWith("length");
    }

    /// <summary>"speaker   1" → "Speaker 1".</summary>
    private static string NormalizeSpeakerN(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return "Speaker " + digits;
    }

    /// <summary>Converts parsed chapters into model chapters, resolving speaker labels to speaker ids.</summary>
    public static List<Chapter> ToChapters(ImportResult result, Func<string?, string> speakerIdForLabel)
    {
        var chapters = new List<Chapter>();
        foreach (var ic in result.Chapters)
        {
            var ch = new Chapter { Title = ic.Title };
            foreach (var ii in ic.Items)
            {
                var item = new TimelineItem { Kind = ii.Kind, ExportId = ii.ExportId };
                switch (ii.Kind)
                {
                    case ItemKind.Tts:
                        item.Text = ii.Text;
                        item.Speaker = speakerIdForLabel(ii.SpeakerLabel);
                        item.DurationSec = ii.DurationSec;
                        break;
                    case ItemKind.Pause:
                        item.DurationMs = ii.DurationMs;
                        break;
                    case ItemKind.Clip:
                        item.ClipFile = ii.ClipFile;
                        item.Label = Path.GetFileNameWithoutExtension(ii.ClipFile);
                        break;
                    case ItemKind.Comment:
                        item.Text = ii.Text;
                        break;
                }
                ch.Items.Add(item);
            }
            chapters.Add(ch);
        }
        return chapters;
    }
}
