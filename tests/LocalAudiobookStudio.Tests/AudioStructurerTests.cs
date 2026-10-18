using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Tests;

/// <summary>Chapters, segments and pauses made from a recording's word times (no audio, no workers).</summary>
public sealed class AudioStructurerTests
{
    /// <summary>
    /// Words from a script: each word 300 ms; "|1500" before a word is a pause of that many ms before it; "@S2" after a word
    /// gives it that voice.
    /// </summary>
    private static List<TranscriptWord> Words(string script)
    {
        var words = new List<TranscriptWord>();
        var t = 0;
        foreach (var token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = token;
            if (text.StartsWith('|')) { t += int.Parse(text[1..]); continue; }
            string? speaker = null;
            var at = text.IndexOf('@');
            if (at >= 0) { speaker = text[(at + 1)..]; text = text[..at]; }
            words.Add(new TranscriptWord(text, t, t + 300, null, speaker));
            t += 300;
        }
        return words;
    }

    private static IEnumerable<string?> Texts(ImportedChapter c) => c.Items.Select(i => i.Kind == ItemKind.Pause ? $"[pause {i.DurationMs}]" : i.Text);

    [Fact]
    public void Sentences_are_joined_into_a_segment_up_to_the_limit()
    {
        var words = Words("Alpha beta gamma delta. Epsilon zeta eta theta. Iota kappa lambda mu.");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions { MaxSegmentChars = 1000 });

        var chapter = Assert.Single(s.Result.Chapters);
        Assert.Equal(["Alpha beta gamma delta. Epsilon zeta eta theta. Iota kappa lambda mu."], Texts(chapter));
        Assert.Equal(0, s.Spans[chapter.Items[0]].StartMs);
        Assert.Equal(words[^1].EndMs, s.Spans[chapter.Items[0]].EndMs);

        var small = AudioStructurer.Build(words, [], new AudioStructureOptions { MaxSegmentChars = 50 });
        Assert.Equal(["Alpha beta gamma delta. Epsilon zeta eta theta.", "Iota kappa lambda mu."], Texts(Assert.Single(small.Result.Chapters)));
    }

    [Fact]
    public void A_long_pause_becomes_a_pause_item_of_that_length_and_ends_the_segment()
    {
        var words = Words("Hello there. |1200 Back again. |300 Short gap.");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions());

        // 1200 ms: no joining across it (1000) and a Pause item (600+); 300 ms: joined, no pause item.
        Assert.Equal(["Hello there.", "[pause 1200]", "Back again. Short gap."], Texts(Assert.Single(s.Result.Chapters)));
        Assert.Equal(1, s.PauseCount);
    }

    [Fact]
    public void A_pause_without_punctuation_ends_a_sentence()
    {
        var words = Words("first part |800 second part");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions { InsertPauseMs = 5000 });

        Assert.Equal(["first part second part"], Texts(Assert.Single(s.Result.Chapters))); // two sentences, joined (800 < 1000)
        var apart = AudioStructurer.Build(words, [], new AudioStructureOptions { InsertPauseMs = 5000, JoinPauseMs = 500 });
        Assert.Equal(["first part", "second part"], Texts(Assert.Single(apart.Result.Chapters)));
    }

    [Fact]
    public void A_change_of_voice_ends_a_segment_and_voices_are_numbered_by_first_appearance()
    {
        var words = Words("Hi@B there.@B Hello@A you.@A Fine@B thanks.@B");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions());

        var items = Assert.Single(s.Result.Chapters).Items;
        Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], items.Select(i => i.SpeakerLabel));
        Assert.Equal(["Speaker 1", "Speaker 2"], s.Result.SpeakerLabels);
        Assert.Equal(["Speaker 1", "Speaker 2"], s.Speakers.Select(x => x.Label));
    }

    [Fact]
    public void Turns_taken_without_a_pause_or_a_full_stop_are_still_separate_segments()
    {
        // No punctuation and no gap between the two voices, as when people talk over each other's last words.
        var words = Words("so@A what@A do@A you@A think@A yeah@B i@B agree@B with@B that@B");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions());

        var items = Assert.Single(s.Result.Chapters).Items;
        Assert.Equal(["so what do you think", "yeah i agree with that"], items.Select(i => i.Text));
        Assert.Equal(["Speaker 1", "Speaker 2"], items.Select(i => i.SpeakerLabel));
    }

    [Fact]
    public void A_flicker_of_another_voice_inside_a_turn_does_not_split_it()
    {
        // One word of B inside A's turn (300 ms) is the diarizer flipping, not a turn.
        var words = Words("one@A two@A three@B four@A five@A six@A seven@A eight@B ten@A eleven@A");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions());

        var items = Assert.Single(s.Result.Chapters).Items;
        var item = Assert.Single(items);
        Assert.Equal("Speaker 1", item.SpeakerLabel);
        Assert.StartsWith("one two three four", item.Text);
    }

    [Fact]
    public void A_short_interjection_between_other_voices_is_its_own_segment()
    {
        var words = Words("we@A should@A go@A now@A yeah@B okay@A let's@A leave@A");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions());

        // (B's single word sits between two stretches of A: a flicker by the rule above.)
        Assert.Single(Assert.Single(s.Result.Chapters).Items);

        var words2 = Words("we@A should@A go@A now@A yeah@B okay@C let's@C leave@C");
        var s2 = AudioStructurer.Build(words2, [], new AudioStructureOptions());
        Assert.Equal(["we should go now", "yeah", "okay let's leave"], Assert.Single(s2.Result.Chapters).Items.Select(i => i.Text));
    }

    [Fact]
    public void A_spoken_heading_after_a_pause_starts_a_chapter()
    {
        var words = Words("Some intro text. |2000 Chapter two. |500 The story goes on. |2500 Chapter three. And more.");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions());

        Assert.Equal(["Chapter 1", "Chapter two", "Chapter three"], s.Result.Chapters.Select(c => c.Title));
        Assert.Equal("Chapter two.", s.Result.Chapters[1].Items[0].Text);
        Assert.True(s.Result.Chapters[1].Items[0].IsHeading);

        var silent = AudioStructurer.Build(words, [], new AudioStructureOptions { SpeakHeadings = false });
        Assert.Equal("The story goes on.", silent.Result.Chapters[1].Items[0].Text); // (no pause item: it would be the first in the chapter)
        Assert.DoesNotContain("Chapter two.", silent.Result.Chapters[1].Items.Select(i => i.Text));
    }

    [Fact]
    public void Chapters_come_from_the_file_and_a_heading_just_after_one_is_the_same_chapter()
    {
        var words = Words("Opening words here. |3000 Chapter one. It begins here. |4000 Another part follows.");
        // The file says a chapter starts at 3.2 s (the heading is spoken at about 3.9 s) and another at 12 s.
        var source = new SourceChapter[] { new("The Beginning", 3200), new("Later", 9000) };

        var s = AudioStructurer.Build(words, source, new AudioStructureOptions());

        Assert.Equal(["Chapter 1", "The Beginning", "Later"], s.Result.Chapters.Select(c => c.Title));

        var off = AudioStructurer.Build(words, source, new AudioStructureOptions { ChaptersFromMetadata = false, ChaptersFromHeadings = false });
        Assert.Single(off.Result.Chapters);
    }

    [Fact]
    public void Chapters_at_pauses_use_the_threshold_the_user_sets()
    {
        var words = Words("First block text. |4000 Second block text. |2000 Third block text.");

        var none = AudioStructurer.Build(words, [], new AudioStructureOptions());
        var three = AudioStructurer.Build(words, [], new AudioStructureOptions { ChaptersFromPauses = true, ChapterPauseSec = 3 });
        var one = AudioStructurer.Build(words, [], new AudioStructureOptions { ChaptersFromPauses = true, ChapterPauseSec = 1.5 });

        Assert.Single(none.Result.Chapters);
        Assert.Equal(["Chapter 1", "Chapter 2"], three.Result.Chapters.Select(c => c.Title));
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], one.Result.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void A_sentence_longer_than_the_limit_is_cut_at_commas()
    {
        var words = Words("alpha beta gamma delta, epsilon zeta eta theta iota kappa lambda mu nu.");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions { MaxSegmentChars = 55 });

        var texts = Texts(Assert.Single(s.Result.Chapters)).ToList();
        Assert.True(texts.Count >= 2);
        Assert.All(texts, t => Assert.True(t!.Length <= 55));
        Assert.Equal("alpha beta gamma delta, epsilon zeta eta theta iota kappa lambda mu nu.", string.Join(' ', texts));
    }

    [Fact]
    public void Each_speakers_limit_applies_and_a_reference_stretch_is_picked()
    {
        var words = Words("Long@A turn@A of@A words@A here.@A |100 Brief.@B");

        var s = AudioStructurer.Build(words, [], new AudioStructureOptions { MaxCharsForSpeaker = l => l == "Speaker 2" ? 3 : null });

        var a = s.Speakers.Single(x => x.Label == "Speaker 1");
        Assert.True(a.SpeechMs > 0);
        Assert.Equal(0, a.ReferenceStartMs);
        Assert.True(a.ReferenceEndMs - a.ReferenceStartMs is > 1000 and <= 15_000);
    }

    [Fact]
    public void Nothing_heard_is_nothing_imported()
    {
        var s = AudioStructurer.Build([], [], new AudioStructureOptions());

        Assert.Empty(s.Result.Chapters);
        Assert.Equal(0, s.SpokenCount);
    }
}
