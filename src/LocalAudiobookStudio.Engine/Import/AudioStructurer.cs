using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Engine;

/// <summary>How a recording's words become chapters, segments and pauses (<see cref="AudioStructurer"/>).</summary>
public sealed class AudioStructureOptions
{
    // --- chapters ---

    /// <summary>Start a chapter where the file says so (m4b chapters, cue points).</summary>
    public bool ChaptersFromMetadata { get; set; } = true;

    /// <summary>Start a chapter at a spoken heading ("Chapter twelve"), matched by <see cref="ChapterRegex"/>.</summary>
    public bool ChaptersFromHeadings { get; set; } = true;

    /// <summary>Start a chapter after a pause longer than <see cref="ChapterPauseSec"/>.</summary>
    public bool ChaptersFromPauses { get; set; }

    public double ChapterPauseSec { get; set; } = 3;

    public string ChapterRegex { get; set; } = ImportOptions.DefaultChapterRegex;

    /// <summary>Headings longer than this are text.</summary>
    public int MaxHeadingChars { get; set; } = 120;

    /// <summary>A spoken heading counts only after this much silence.</summary>
    public int HeadingPauseMs { get; set; } = 1500;

    /// <summary>Make a segment of the heading (else the chapter just has the title).</summary>
    public bool SpeakHeadings { get; set; } = true;

    public string DefaultChapterTitle { get; set; } = "Chapter 1";

    // --- segments and pauses ---

    public int MaxSegmentChars { get; set; } = 1500;

    /// <summary>Per speaker label (null: the default speaker), the limit of that speaker's worker.</summary>
    public Func<string?, int?>? MaxCharsForSpeaker { get; set; }

    /// <summary>A pause this long ends a sentence even without punctuation.</summary>
    public int SentencePauseMs { get; set; } = 700;

    /// <summary>Sentences aren't joined into one segment across a pause this long.</summary>
    public int JoinPauseMs { get; set; } = 1000;

    /// <summary>A gap of at least this much between segments becomes a Pause item of the measured length; shorter ones are left to the default spacing.</summary>
    public int InsertPauseMs { get; set; } = 600;
}

/// <summary>A chapter the file itself declares.</summary>
public sealed record SourceChapter(string Title, int StartMs);

/// <summary>Where a segment is in the recording, and the words it holds (their times are the recording's).</summary>
public sealed record SegmentSpan(int StartMs, int EndMs, IReadOnlyList<TranscriptWord> Words);

/// <summary>A voice found in the recording: how long it speaks, and a stretch of it to listen to.</summary>
public sealed record SpeakerStats(string Label, long SpeechMs, int ReferenceStartMs, int ReferenceEndMs);

public sealed class AudioStructure
{
    /// <summary>Chapters of segments and pauses, in the form the text import produces.</summary>
    public ImportResult Result { get; } = new();

    /// <summary>For every spoken segment: where it is in the recording.</summary>
    public Dictionary<ImportedItem, SegmentSpan> Spans { get; } = new(ReferenceEqualityComparer.Instance);

    public List<SpeakerStats> Speakers { get; } = new();

    public int PauseCount => Result.Chapters.Sum(c => c.Items.Count(i => i.Kind == ItemKind.Pause));
    public int SpokenCount => Spans.Count;
    public long AudioMs { get; set; }
}

/// <summary>
/// Turns the words a recording was heard to say into chapters, segments and pauses, using the words' times: sentences end at
/// punctuation or a pause, a change of voice ends a segment, and a gap between two segments long enough becomes a Pause item
/// of that length. Pure: it works on the word list, so it runs again at once when an option changes.
/// </summary>
public static class AudioStructurer
{
    /// <summary>Speaker labels: "Speaker 1", "Speaker 2" … in the order the voices first speak.</summary>
    public static string SpeakerLabel(int number) => $"Speaker {number}";

    private sealed record Sentence(List<TranscriptWord> Words, string Text, string? Label)
    {
        public int Start => Words[0].StartMs;
        public int End => Words[^1].EndMs;
    }

    private sealed record Mark(int TimeMs, string? Title, int HeadingSentence, int Priority);

    private sealed record ChapterPlan(string Title, int First, int End, int Heading);

    public static AudioStructure Build(IReadOnlyList<TranscriptWord> words, IReadOnlyList<SourceChapter> sourceChapters, AudioStructureOptions o)
    {
        var structure = new AudioStructure { AudioMs = words.Count == 0 ? 0 : words[^1].EndMs };
        var sentences = Sentences(words, o);
        if (sentences.Count == 0) return structure;

        var labels = new List<string>();
        foreach (var s in sentences)
            if (s.Label != null && !labels.Contains(s.Label)) labels.Add(s.Label);
        structure.Result.SpeakerLabels.AddRange(labels);

        foreach (var c in ChaptersOf(sentences, sourceChapters, o))
        {
            var chapter = new ImportedChapter { Title = c.Title };
            structure.Result.Chapters.Add(chapter);
            var lastEnd = 0;

            void Add(ImportedItem item, int startMs, int endMs)
            {
                // The gap before it, when long enough, is a Pause item of that length (it replaces the default spacing).
                if (chapter.Items.Count > 0 && o.InsertPauseMs > 0 && startMs - lastEnd >= o.InsertPauseMs)
                    chapter.Items.Add(new ImportedItem { Kind = ItemKind.Pause, DurationMs = startMs - lastEnd });
                chapter.Items.Add(item);
                lastEnd = endMs;
            }

            var i = c.First;
            if (c.Heading >= 0)
            {
                var h = sentences[c.Heading];
                if (o.SpeakHeadings)
                {
                    var item = new ImportedItem { Kind = ItemKind.Tts, Text = h.Text, SpeakerLabel = h.Label, IsHeading = true };
                    structure.Spans[item] = new SegmentSpan(h.Start, h.End, h.Words);
                    Add(item, h.Start, h.End);
                }
                i = c.Heading + 1;
            }

            while (i < c.End)
            {
                // One segment: sentences of one voice, joined while they fit and no long pause intervenes.
                var group = new List<Sentence> { sentences[i] };
                var limit = Limit(o, sentences[i].Label);
                var chars = sentences[i].Text.Length;
                var j = i + 1;
                while (j < c.End && sentences[j].Label == sentences[i].Label
                       && sentences[j].Start - sentences[j - 1].End < o.JoinPauseMs
                       && chars + 1 + sentences[j].Text.Length <= limit)
                {
                    group.Add(sentences[j]);
                    chars += 1 + sentences[j].Text.Length;
                    j++;
                }
                i = j;

                var parts = group.Count == 1 ? SplitLong(group[0], limit) : [group.SelectMany(g => g.Words).ToList()];
                foreach (var part in parts)
                {
                    var text = Join(part);
                    if (text.Length == 0) continue;
                    var item = new ImportedItem { Kind = ItemKind.Tts, Text = text, SpeakerLabel = group[0].Label };
                    structure.Spans[item] = new SegmentSpan(part[0].StartMs, part[^1].EndMs, part);
                    Add(item, part[0].StartMs, part[^1].EndMs);
                }
            }
        }
        structure.Speakers.AddRange(SpeakersOf(sentences, labels));
        return structure;
    }

    private static int Limit(AudioStructureOptions o, string? label) =>
        Math.Max(50, Math.Min(o.MaxSegmentChars, o.MaxCharsForSpeaker?.Invoke(label) ?? int.MaxValue));

    private static string Join(IEnumerable<TranscriptWord> words) =>
        string.Join(' ', words.Select(w => w.Text.Trim()).Where(t => t.Length > 0));

    // ------------------------------------------------------------------ sentences

    private static List<Sentence> Sentences(IReadOnlyList<TranscriptWord> words, AudioStructureOptions o)
    {
        var voices = SmoothVoices(words);

        // Voices are numbered in the order they first speak.
        var numbers = new Dictionary<string, int>();
        foreach (var v in voices)
            if (v != null && !numbers.ContainsKey(v)) numbers[v] = numbers.Count + 1;

        var sentences = new List<Sentence>();
        var start = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var last = i == words.Count - 1;
            // A sentence ends at its punctuation, at a pause, and where the voice changes (people take turns without a pause,
            // and the transcript has no full stop where they do).
            if (!last && !EndsSentence(words[i].Text) && voices[i] == voices[i + 1] && words[i + 1].StartMs - words[i].EndMs < o.SentencePauseMs) continue;
            var part = words.Skip(start).Take(i - start + 1).ToList();
            var voice = voices[start];
            start = i + 1;
            var text = Join(part);
            if (text.Length == 0) continue;
            sentences.Add(new Sentence(part, text, voice == null ? null : SpeakerLabel(numbers[voice])));
        }
        return sentences;
    }

    /// <summary>The longest stretch (one or two words) of another voice inside a turn that is taken for the diarizer flipping.</summary>
    private const int FlickerMs = 450;

    /// <summary>
    /// The voice of each word, with a flicker removed: a word or two, under <see cref="FlickerMs"/>, of another voice between two
    /// stretches of the same voice are that voice's (a diarizer sometimes flips for a moment inside a turn). A real reply of
    /// two words takes longer, and an interjection ("Yeah.") between two other voices or at a turn's edge stays its own.
    /// </summary>
    private static string?[] SmoothVoices(IReadOnlyList<TranscriptWord> words)
    {
        var voices = words.Select(w => w.Speaker).ToArray();
        var i = 0;
        while (i < voices.Length)
        {
            var j = i;
            while (j + 1 < voices.Length && voices[j + 1] == voices[i]) j++;
            // The run i..j; it is a flicker when it is short and the runs on both sides are one voice.
            if (i > 0 && j + 1 < voices.Length && j - i + 1 <= 2 && words[j].EndMs - words[i].StartMs <= FlickerMs && voices[i - 1] == voices[j + 1])
                for (var k = i; k <= j; k++) voices[k] = voices[i - 1];
            i = j + 1;
        }
        return voices;
    }

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        while (t.Length > 0 && t[^1] is '"' or '\'' or ')' or ']' or '”' or '’' or '»') t = t[..^1];
        return t.Length > 0 && t[^1] is '.' or '?' or '!' or '…';
    }

    /// <summary>A sentence longer than a segment is cut into parts, at the commas nearest the limit when there are any.</summary>
    private static List<List<TranscriptWord>> SplitLong(Sentence s, int limit)
    {
        var parts = new List<List<TranscriptWord>>();
        if (s.Text.Length <= limit) return [s.Words];
        var from = 0;
        while (from < s.Words.Count)
        {
            var length = 0;
            var to = from;
            var best = -1;
            while (to < s.Words.Count && length + s.Words[to].Text.Length + 1 <= limit)
            {
                length += s.Words[to].Text.Length + 1;
                var t = s.Words[to].Text.TrimEnd();
                if (length > limit * 0.6 && (t.EndsWith(',') || t.EndsWith(';') || t.EndsWith(':'))) best = to + 1;
                to++;
            }
            if (to >= s.Words.Count) { parts.Add(s.Words.Skip(from).ToList()); break; }
            var cut = best > from ? best : Math.Max(to, from + 1);
            parts.Add(s.Words.Skip(from).Take(cut - from).ToList());
            from = cut;
        }
        return parts;
    }

    // ------------------------------------------------------------------ chapters

    private static List<ChapterPlan> ChaptersOf(List<Sentence> sentences, IReadOnlyList<SourceChapter> source, AudioStructureOptions o)
    {
        var marks = new List<Mark>();
        if (o.ChaptersFromMetadata)
            marks.AddRange(source.Select(c => new Mark(c.StartMs, string.IsNullOrWhiteSpace(c.Title) ? null : c.Title.Trim(), -1, 3)));
        if (o.ChaptersFromHeadings)
        {
            Regex regex;
            try { regex = new Regex(o.ChapterRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { regex = new Regex(ImportOptions.DefaultChapterRegex, RegexOptions.IgnoreCase); }
            for (var i = 0; i < sentences.Count; i++)
            {
                var quiet = i == 0 || sentences[i].Start - sentences[i - 1].End >= o.HeadingPauseMs;
                var title = sentences[i].Text.Trim().TrimEnd('.', ',', ';', ':', '!', '?', '…').Trim();
                if (quiet && title.Length > 0 && title.Length <= o.MaxHeadingChars && regex.IsMatch(title))
                    marks.Add(new Mark(sentences[i].Start, title, i, 2));
            }
        }
        if (o.ChaptersFromPauses)
            for (var i = 1; i < sentences.Count; i++)
                if (sentences[i].Start - sentences[i - 1].End >= o.ChapterPauseSec * 1000)
                    marks.Add(new Mark(sentences[i].Start, null, -1, 1));

        // Where each mark starts a chapter: its heading sentence, else the first sentence from (just before) its time.
        const int Slack = 300;
        var begins = new List<(int At, Mark Mark)>();
        foreach (var m in marks)
        {
            var at = m.HeadingSentence >= 0 ? m.HeadingSentence : sentences.FindIndex(s => s.Start >= m.TimeMs - Slack);
            if (at >= 0) begins.Add((at, m));
        }
        begins.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : b.Mark.Priority.CompareTo(a.Mark.Priority));

        // Marks at one place (a chapter atom, and the heading spoken just after it) are one chapter: the first sentence.
        const int Same = 2500;
        var starts = new List<(int At, string? Title, int Heading)>();
        foreach (var (at, mark) in begins)
        {
            if (starts.Count > 0 && sentences[at].Start - sentences[starts[^1].At].Start <= Same)
            {
                // The earlier mark's title wins (a chapter atom over the heading spoken just after it); the heading is
                // the chapter's heading segment when it is its very first sentence.
                var k = starts[^1];
                starts[^1] = (k.At, k.Title ?? mark.Title, k.Heading >= 0 ? k.Heading : mark.HeadingSentence == k.At ? k.At : -1);
            }
            else starts.Add((at, mark.Title, mark.HeadingSentence));
        }
        if (starts.Count == 0 || starts[0].At > 0) starts.Insert(0, (0, null, -1));

        var chapters = new List<ChapterPlan>();
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1].At : sentences.Count;
            var title = starts[k].Title ?? (k == 0 ? o.DefaultChapterTitle : $"Chapter {k + 1}");
            chapters.Add(new ChapterPlan(title, starts[k].At, end, starts[k].Heading));
        }
        return chapters;
    }

    // ------------------------------------------------------------------ speakers

    private static IEnumerable<SpeakerStats> SpeakersOf(List<Sentence> sentences, List<string> labels)
    {
        const int ReferenceMax = 15_000;
        foreach (var label in labels)
        {
            long speech = 0;
            int bestStart = 0, bestLength = 0;
            for (var i = 0; i < sentences.Count; i++)
            {
                if (sentences[i].Label != label) continue;
                // A turn: consecutive sentences of this voice with no long pause between them.
                var j = i;
                while (j + 1 < sentences.Count && sentences[j + 1].Label == label && sentences[j + 1].Start - sentences[j].End < 1000) j++;
                var turnStart = sentences[i].Start;
                var turnLength = Math.Min(ReferenceMax, sentences[j].End - turnStart);
                if (turnLength > bestLength) (bestStart, bestLength) = (turnStart, turnLength);
                for (var k = i; k <= j; k++) speech += sentences[k].Words.Sum(w => (long)(w.EndMs - w.StartMs));
                i = j;
            }
            yield return new SpeakerStats(label, speech, bestStart, bestStart + bestLength);
        }
    }
}
