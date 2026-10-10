using LocalAudiobookStudio.Core.Transcripts;

namespace LocalAudiobookStudio.Tests;

public class TranscriptCheckTests
{
    /// <summary>Heard words 300 ms apart, each 200 ms long.</summary>
    private static List<HeardWord> Heard(params string[] words) =>
        words.Select((w, i) => new HeardWord(w, i * 300, i * 300 + 200)).ToList();

    [Fact]
    public void CasePunctuationAndAccentsDontCount()
    {
        var c = TranscriptCheck.Compare("“Hello,” said Zoë. It’s late!", Heard("hello", "Said", "Zoe.", "it's", "late"));
        Assert.Equal(0, c.Errors);
        Assert.Equal(5, c.ExpectedCount);
        Assert.All(c.Words, w => Assert.Equal(WordMatch.Match, w.Kind));
    }

    [Fact]
    public void FindsMissingExtraAndDifferentWords()
    {
        var c = TranscriptCheck.Compare("the quick brown fox jumps", Heard("the", "brown", "box", "jumps", "jumps"));
        Assert.Equal(1, c.Missing);
        Assert.Equal(1, c.Substitutions);
        Assert.Equal(1, c.Extra);
        Assert.Equal(3.0 / 5, c.WordErrorRate, 6);
        Assert.Equal("quick", Assert.Single(c.Words, w => w.Kind == WordMatch.Missing).Expected);
        var sub = Assert.Single(c.Words, w => w.Kind == WordMatch.Substituted);
        Assert.Equal(("fox", "box"), (sub.Expected, sub.Heard));
    }

    [Fact]
    public void SimilarSpellingsAreCloseNotErrors()
    {
        var c = TranscriptCheck.Compare("Eowyn rode on", Heard("Eowin", "rode", "on"));
        Assert.Equal(0, c.Errors);
        Assert.Equal(WordMatch.Close, c.Words[0].Kind);
    }

    [Fact]
    public void ExpectedWordsGetTimesAndTextPositions()
    {
        const string text = "One two three";
        var c = TranscriptCheck.Compare(text, Heard("one", "three"));
        Assert.Equal((0, 200), (c.Words[0].StartMs, c.Words[0].EndMs));
        // "two" wasn't heard: it gets the gap between "one" and "three".
        Assert.Equal((WordMatch.Missing, 200, 300), (c.Words[1].Kind, c.Words[1].StartMs, c.Words[1].EndMs));
        Assert.Equal("three", text.Substring(c.Words[2].TextStart, c.Words[2].TextLength));
        Assert.Equal((300, 500), (c.Words[2].StartMs, c.Words[2].EndMs));
    }

    [Fact]
    public void HyphenatedHeardWordsSplitTheirTime()
    {
        var c = TranscriptCheck.Compare("a well known name", [new HeardWord("well-known", 1000, 1900)]);
        Assert.Equal(2, c.Missing);
        Assert.Equal((1000, 1400), (c.Words[1].StartMs, c.Words[1].EndMs));
        Assert.Equal((1400, 1900), (c.Words[2].StartMs, c.Words[2].EndMs));
    }

    [Theory]
    [InlineData("It was forty-two degrees.", "It was 42 degrees.")]
    [InlineData("Chapter one", "Chapter 1.")]
    [InlineData("one hundred and three steps", "103 steps")]
    [InlineData("in nineteen eighty-four", "in 1984")]
    [InlineData("two thousand and twenty people", "2,020 people")]
    [InlineData("It cost 1,000 coins", "It cost one thousand coins")]
    [InlineData("By chapter twenty-one, forty-two were left.", "By chapter 21, 42 were left.")]
    [InlineData("By chapter 21, 42 were left.", "By chapter twenty-one, forty-two were left.")]
    [InlineData("more than a metre wide", "more than a meter wide")]
    public void NumberWordsMatchDigits(string text, string heard)
    {
        var c = TranscriptCheck.Compare(text, Heard(heard.Split(' ')));
        Assert.Equal(0, c.Errors);
    }

    [Fact]
    public void A_number_word_run_highlights_as_one_word()
    {
        const string text = "It was forty-two degrees";
        var c = TranscriptCheck.Compare(text, Heard("It", "was", "42", "degrees"));
        var number = c.Words[2];
        Assert.Equal("forty-two", text.Substring(number.TextStart, number.TextLength));
        Assert.Equal((600, 800), (number.StartMs, number.EndMs));
        Assert.Equal(3, TranscriptCheck.Compare("one two three", Heard("1", "2", "3")).ExpectedCount);
    }

    [Fact]
    public void Two_written_words_heard_as_one_match_and_share_its_time()
    {
        const string text = "old rag mats here";
        var c = TranscriptCheck.Compare(text, [new HeardWord("old", 0, 200), new HeardWord("ragmats", 300, 1000), new HeardWord("here", 1100, 1300)]);
        Assert.Equal(0, c.Errors);
        Assert.Equal(4, c.ExpectedCount);
        Assert.Equal(("rag", 300, 600), (c.Words[1].Expected, c.Words[1].StartMs, c.Words[1].EndMs)); // 3 of 7 letters
        Assert.Equal(("mats", 600, 1000), (c.Words[2].Expected, c.Words[2].StartMs, c.Words[2].EndMs));
        Assert.Equal("mats", text.Substring(c.Words[2].TextStart, c.Words[2].TextLength));
    }

    [Fact]
    public void One_written_word_heard_as_two_matches_their_span()
    {
        var c = TranscriptCheck.Compare("a ragmats here", Heard("a", "rag", "mats", "here"));
        Assert.Equal(0, c.Errors);
        var w = Assert.Single(c.Words, w => w.Expected == "ragmats");
        Assert.Equal(("rag mats", 300, 800), (w.Heard, w.StartMs, w.EndMs));
    }

    [Fact]
    public void NothingHeardMeansEveryWordIsMissing()
    {
        var c = TranscriptCheck.Compare("all gone", []);
        Assert.Equal(2, c.Missing);
        Assert.Equal(1.0, c.WordErrorRate);
    }
}
