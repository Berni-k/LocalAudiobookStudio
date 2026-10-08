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
    public void A_take_still_being_written_reserves_its_number()
    {
        WriteTake(1);
        File.WriteAllText(Path.Combine(_dir, "take-0003.wav.partial"), "");
        Assert.Equal(4, TakeStore.NextTakeNumber(_dir));
    }
}
