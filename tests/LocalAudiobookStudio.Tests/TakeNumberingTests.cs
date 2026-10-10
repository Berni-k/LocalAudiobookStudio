using LocalAudiobookStudio.Core.IO;

namespace LocalAudiobookStudio.Tests;

/// <summary>Take numbers are never reused: take paths key caches (waveform peaks) for the whole session.</summary>
public sealed class TakeNumberingTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string _dir => _tmp.Path;

    public void Dispose() => _tmp.Dispose();

    private void WriteTake(int n)
    {
        foreach (var ext in new[] { ".wav", ".json", ".peaks" }) File.WriteAllText(Path.Combine(_dir, $"take-{n:0000}{ext}"), "");
    }

    private void DeleteTake(int n)
    {
        foreach (var ext in new[] { ".wav", ".json", ".peaks" }) File.Delete(Path.Combine(_dir, $"take-{n:0000}{ext}"));
    }

    [Fact]
    public void Numbers_count_up_from_one()
    {
        Assert.Equal(1, TakeStore.NextTakeNumber(_dir));
        WriteTake(1);
        Assert.Equal(2, TakeStore.NextTakeNumber(_dir));
    }

    [Fact]
    public void Deleting_the_newest_takes_does_not_free_their_numbers()
    {
        for (var n = 1; n <= 3; n++)
        {
            Assert.Equal(n, TakeStore.NextTakeNumber(_dir));
            WriteTake(n);
        }
        // "Clean up takes" removed 1 and 2, then the last take was deleted: the folder has no takes left.
        foreach (var n in new[] { 1, 2, 3 }) DeleteTake(n);
        Assert.Equal(4, TakeStore.NextTakeNumber(_dir));
    }

    [Fact]
    public void Take_files_count_even_without_the_marker()
    {
        WriteTake(7); // e.g. a take copied in by hand
        Assert.Equal(7, TakeStore.HighestTakeNumber(_dir));
        Assert.Equal(8, TakeStore.NextTakeNumber(_dir));
    }

    [Fact]
    public void A_transcript_file_counts_as_part_of_its_take()
    {
        File.WriteAllText(Path.Combine(_dir, "take-0005.transcript"), "");
        Assert.Equal(5, TakeStore.HighestTakeNumber(_dir));
    }

    [Fact]
    public void A_stored_transcript_round_trips()
    {
        var path = Path.Combine(_dir, "take-0001.transcript");
        new LocalAudiobookStudio.Core.Transcripts.StoredTranscript
        {
            Worker = "whisper",
            Language = "en",
            Words = [new("Hello", 100, 400, 0.9), new("world", 450, 900)],
        }.Save(path);

        var loaded = LocalAudiobookStudio.Core.Transcripts.StoredTranscript.TryLoad(path);
        Assert.NotNull(loaded);
        Assert.Equal("whisper", loaded.Worker);
        Assert.Equal(2, loaded.Words.Count);
        Assert.Equal(900, loaded.Words[1].EndMs);
        Assert.Null(LocalAudiobookStudio.Core.Transcripts.StoredTranscript.TryLoad(Path.Combine(_dir, "none.transcript")));
    }

    [Fact]
    public void A_transcript_verdict_lives_in_the_take_file()
    {
        var info = new LocalAudiobookStudio.Core.Model.TakeInfo { Take = 1, TranscriptErrors = 2, TranscriptWordCount = 9 };
        var back = LocalAudiobookStudio.Core.Serialization.CoreJson.Deserialize<LocalAudiobookStudio.Core.Model.TakeInfo>(
            LocalAudiobookStudio.Core.Serialization.CoreJson.Serialize(info));
        Assert.Equal(2, back.TranscriptErrors);
        Assert.Equal(9, back.TranscriptWordCount);
        Assert.Null(LocalAudiobookStudio.Core.Serialization.CoreJson.Deserialize<LocalAudiobookStudio.Core.Model.TakeInfo>("{\"take\":2}").TranscriptErrors);
    }

    [Fact]
    public void A_take_still_being_written_reserves_its_number()
    {
        WriteTake(1);
        File.WriteAllText(Path.Combine(_dir, "take-0003.wav.partial"), "");
        Assert.Equal(4, TakeStore.NextTakeNumber(_dir));
    }
}
