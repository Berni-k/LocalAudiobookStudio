using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Tests;

public class TextImporterTests
{
    private static ImportResult Parse(string text, Action<ImportOptions>? configure = null)
    {
        var o = new ImportOptions();
        configure?.Invoke(o);
        return TextImporter.Parse(text, o);
    }

    [Fact]
    public void SplitsOnBlankLinesAndJoinsSingleNewlines()
    {
        var r = Parse("First line\nstill first.\r\n\r\nSecond paragraph.\n\n\n\nThird.");
        var items = r.Chapters.Single().Items;
        Assert.Equal(3, items.Count);
        Assert.Equal("First line still first.", items[0].Text);
        Assert.Equal("Second paragraph.", items[1].Text);
        Assert.Equal("Third.", items[2].Text);
    }

    [Fact]
    public void LineModeTreatsEveryLineAsParagraph()
    {
        var r = Parse("One.\nTwo.\nThree.", o => o.EveryLineIsParagraph = true);
        Assert.Equal(3, r.Chapters.Single().Items.Count);
    }

    [Fact]
    public void DetectsChapterHeadingsAndSpeaksThem()
    {
        var r = Parse("Intro text.\n\nChapter 1: The Storm\n\nThe wind howled.\n\n# Part Two\n\nMore.");
        Assert.Equal(3, r.Chapters.Count);
        Assert.Equal("Chapter 1", r.Chapters[0].Title); // text before the first heading
        Assert.Equal("Chapter 1: The Storm", r.Chapters[1].Title);
        Assert.True(r.Chapters[1].Items[0].IsHeading);
        Assert.Equal("Chapter 1: The Storm", r.Chapters[1].Items[0].Text);
        Assert.Equal("Part Two", r.Chapters[2].Title);
    }

    [Fact]
    public void HeadingsCanBeSilent()
    {
        var r = Parse("Chapter 1\n\nText.", o => o.SpeakHeadings = false);
        Assert.Single(r.Chapters);
        Assert.Single(r.Chapters[0].Items);
        Assert.Equal("Text.", r.Chapters[0].Items[0].Text);
    }

    [Fact]
    public void ParagraphStartingWithPartIsNotAHeadingWhenMultiLine()
    {
        var r = Parse("Part of the reason\nhe left was the rain.");
        Assert.Single(r.Chapters);
        Assert.Equal("Part of the reason he left was the rain.", r.Chapters[0].Items[0].Text);
    }

    [Fact]
    public void VibeVoiceMarkers()
    {
        var r = Parse("Speaker 1: Hello there.\nSpeaker 2: Hi!\nspeaker  1: Again.");
        var items = r.Chapters.Single().Items;
        Assert.Equal(3, items.Count);
        Assert.Equal("Speaker 1", items[0].SpeakerLabel);
        Assert.Equal("Hello there.", items[0].Text);
        Assert.Equal("Speaker 2", items[1].SpeakerLabel);
        Assert.Equal("Speaker 1", items[2].SpeakerLabel);
        Assert.Equal(new[] { "Speaker 1", "Speaker 2" }, r.SpeakerLabels);
    }

    [Fact]
    public void BracketTags()
    {
        var r = Parse("[Alice] Hello there.\n\n[Bob]   Hi.");
        var items = r.Chapters.Single().Items;
        Assert.Equal("Alice", items[0].SpeakerLabel);
        Assert.Equal("Hello there.", items[0].Text);
        Assert.Equal("Bob", items[1].SpeakerLabel);
        Assert.Equal("Hi.", items[1].Text);
    }

    [Fact]
    public void NamePrefixOnlyForKnownSpeakers()
    {
        var r = Parse("Alice: Hello there.\n\nNote: this is narration.", o => o.KnownSpeakerNames = ["alice"]);
        var items = r.Chapters.Single().Items;
        Assert.Equal("alice", items[0].SpeakerLabel);
        Assert.Equal("Hello there.", items[0].Text);
        Assert.Null(items[1].SpeakerLabel);
        Assert.Equal("Note: this is narration.", items[1].Text);
    }

    [Fact]
    public void BlockTagAppliesUntilNextBlockTag()
    {
        var r = Parse("[speaker: Alice]\n\nOne.\n\nTwo.\n\n[speaker: Bob]\nThree.\n\n[speaker: default]\n\nFour.");
        var items = r.Chapters.Single().Items;
        Assert.Equal(new[] { "Alice", "Alice", "Bob", null }, items.Select(i => i.SpeakerLabel));
        Assert.Equal("Three.", items[2].Text);
    }

    [Fact]
    public void InlineMarkerOverridesBlockSpeakerForThatSegmentOnly()
    {
        var r = Parse("[speaker: Alice]\n\n[Bob] Hey.\n\nBack to Alice.");
        var items = r.Chapters.Single().Items;
        Assert.Equal("Bob", items[0].SpeakerLabel);
        Assert.Equal("Alice", items[1].SpeakerLabel);
    }

    [Fact]
    public void LengthsAndPauses()
    {
        var r = Parse("Text.\n\n[length: 4s] [Sounds] distant thunder\n\n[pause 1.5s]\n\n[pause 800ms]\n\n[length 2 min]\n[Music] calm strings\n\n[Sounds] rain");
        var items = r.Chapters.Single().Items;
        // A sound is a segment like any other; only its speaker's worker makes it one.
        Assert.Equal(ItemKind.Tts, items[1].Kind);
        Assert.Equal("distant thunder", items[1].Text);
        Assert.Equal("Sounds", items[1].SpeakerLabel);
        Assert.Equal(4.0, items[1].DurationSec);
        Assert.Equal(ItemKind.Pause, items[2].Kind);
        Assert.Equal(1500, items[2].DurationMs);
        Assert.Equal(800, items[3].DurationMs);
        Assert.Equal(120.0, items[4].DurationSec);
        Assert.Null(items[5].DurationSec);
    }

    [Theory]
    [InlineData("[pause -500ms]", -500)]
    [InlineData("[pause -0.5s]", -500)]
    [InlineData("[pause: -1]", -1000)]
    [InlineData("[pause − 250 ms]", -250)] // typographic minus, as word processors write it
    public void NegativePausesOverlapTheNextItem(string marker, int ms)
    {
        var items = Parse($"One.\n\n{marker}\n\nTwo.").Chapters.Single().Items;
        Assert.Equal(ItemKind.Pause, items[1].Kind);
        Assert.Equal(ms, items[1].DurationMs);
    }

    [Fact]
    public void NegativePausesSurviveExportAndImport()
    {
        var project = new Project(new Core.IO.ProjectPaths(Path.GetTempPath()), new ProjectFile());
        project.Chapters.Add(new Chapter
        {
            Title = "Chapter 1",
            Items =
            [
                new TimelineItem { Kind = ItemKind.Tts, Text = "One.", Speaker = "narrator" },
                new TimelineItem { Kind = ItemKind.Pause, DurationMs = -500 },
                new TimelineItem { Kind = ItemKind.Tts, Text = "Two.", Speaker = "narrator" },
            ],
        });
        var items = Parse(ScriptExporter.Export(project)).Chapters.Single().Items;
        Assert.Contains(items, i => i.Kind == ItemKind.Pause && i.DurationMs == -500);
    }

    [Fact]
    public void MarkersInsideOneParagraphStartNewSegments()
    {
        var r = Parse("[Alice] Hello\nthere.\n[Bob] Hi.");
        var items = r.Chapters.Single().Items;
        Assert.Equal(2, items.Count);
        Assert.Equal("Hello there.", items[0].Text);
        Assert.Equal("Hi.", items[1].Text);
    }

    [Fact]
    public void LongSegmentsAreSplitAtSentenceBoundaries()
    {
        var sentence = "This is a sentence of moderate length. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 100));
        var r = Parse(text, o => o.MaxSegmentChars = 200);
        var items = r.Chapters.Single().Items;
        Assert.True(items.Count > 1);
        Assert.All(items, i => Assert.True(i.Text!.Length <= 200));
        Assert.All(items, i => Assert.EndsWith(".", i.Text));
        Assert.Equal(text.Trim(), string.Join(" ", items.Select(i => i.Text)));
    }

    [Fact]
    public void PerSpeakerLimitIsUsed()
    {
        var text = "[Alice] " + string.Concat(Enumerable.Repeat("Short one. ", 30));
        var r = Parse(text, o => o.MaxCharsForSpeaker = label => label == "Alice" ? 60 : null);
        Assert.All(r.Chapters.Single().Items, i => Assert.True(i.Text!.Length <= 60));
    }

    [Fact]
    public void ToChaptersMapsSpeakers()
    {
        var r = Parse("[Alice] Hi.\n\nNarration.");
        var chapters = TextImporter.ToChapters(r, label => label == null ? "narrator" : "alice");
        Assert.Equal("alice", chapters[0].Items[0].Speaker);
        Assert.Equal("narrator", chapters[0].Items[1].Speaker);
        Assert.NotEqual(chapters[0].Items[0].Id, chapters[0].Items[1].Id);
    }

    [Fact]
    public void LargeFileParsesQuickly()
    {
        var para = "It was a dark and stormy night; the rain fell in torrents, except at occasional intervals. ";
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < 40; c++)
        {
            sb.Append($"Chapter {c + 1}\n\n");
            for (int p = 0; p < 250; p++) sb.Append(para).Append(para).Append("\n\n");
        }
        Assert.True(sb.Length > 1_000_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = TextImporter.Parse(sb.ToString(), new ImportOptions());
        sw.Stop();
        Assert.Equal(40, r.Chapters.Count);
        Assert.True(sw.ElapsedMilliseconds < 10000, $"Parsing took {sw.ElapsedMilliseconds} ms");
    }
}

public class SentenceSplitterTests
{
    [Fact]
    public void KeepsAbbreviationsTogether()
    {
        var s = SentenceSplitter.SplitSentences("Mr. Smith arrived. He said \"Hello!\" Then left.");
        Assert.Equal(new[] { "Mr. Smith arrived.", "He said \"Hello!\"", "Then left." }, s);
    }

    [Fact]
    public void SplitsOverlongSentenceAtClausesThenWords()
    {
        var text = string.Join(", ", Enumerable.Repeat("word word word word", 20)) + ".";
        var parts = SentenceSplitter.SplitToLimit(text, 50);
        Assert.All(parts, p => Assert.True(p.Length <= 50));
    }
}

public class TextDecodingTests
{
    [Fact]
    public void DecodesUtf8WithAndWithoutBom()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Café");
        Assert.Equal("Café", TextDecoding.Decode(bytes));
        Assert.Equal("Café", TextDecoding.Decode([0xEF, 0xBB, 0xBF, .. bytes]));
    }

    [Fact]
    public void FallsBackForInvalidUtf8()
    {
        // "Café" in Windows-1252: é = 0xE9 is invalid as a lone UTF-8 byte.
        var text = TextDecoding.Decode([0x43, 0x61, 0x66, 0xE9]);
        Assert.StartsWith("Caf", text);
        Assert.Equal(4, text.Length);
    }
}
