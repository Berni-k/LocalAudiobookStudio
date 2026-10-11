using LocalAudiobookStudio.Engine;

namespace LocalAudiobookStudio.Tests;

public class SpeakerVerificationTests
{
    private static readonly double[] Usual = [0.82, 0.85, 0.80, 0.84, 0.83, 0.81];

    [Fact]
    public void ReferencesAreTheMostRecentTakesOfOtherSegments()
    {
        var self = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var others = Enumerable.Range(0, 6).Select(i => new ReferenceCandidate(Guid.NewGuid(), 1, now.AddMinutes(-i))).ToList();
        var candidates = others.Append(new ReferenceCandidate(self, 3, now.AddMinutes(1))); // newest, but the take being judged

        var chosen = SpeakerVerification.ChooseReferences(candidates, self);

        Assert.Equal(SpeakerVerification.MaxReferenceTakes, chosen.Count);
        Assert.DoesNotContain(chosen, c => c.ItemId == self);
        Assert.Equal(others.Take(4).Select(c => c.ItemId), chosen.Select(c => c.ItemId));
    }

    [Fact]
    public void TheFirstTakeHasNothingToBeComparedWith()
    {
        var self = Guid.NewGuid();
        Assert.Empty(SpeakerVerification.ChooseReferences([new ReferenceCandidate(self, 1, DateTime.UtcNow)], self));
        Assert.Empty(SpeakerVerification.ChooseReferences([], self));
    }

    [Fact]
    public void ATakeWellBelowTheOthersIsFlagged()
    {
        Assert.True(SpeakerVerification.IsOutlier(0.55, 6000, Usual));
        Assert.False(SpeakerVerification.IsOutlier(0.79, 6000, Usual));
        Assert.False(SpeakerVerification.IsOutlier(0.95, 6000, Usual)); // better than usual is fine
    }

    [Fact]
    public void ATightGroupDoesNotFlagHundredthsOfDifference()
    {
        double[] identical = [0.90, 0.90, 0.90, 0.90, 0.90];
        Assert.False(SpeakerVerification.IsOutlier(0.88, 6000, identical));
        Assert.True(SpeakerVerification.IsOutlier(0.80, 6000, identical));
    }

    [Fact]
    public void ShortTakesAndTooFewScoresAreNeverFlagged()
    {
        Assert.False(SpeakerVerification.IsOutlier(0.30, SpeakerVerification.MinSpeechMs - 1, Usual));
        Assert.True(SpeakerVerification.IsOutlier(0.30, SpeakerVerification.MinSpeechMs, Usual));
        Assert.Null(SpeakerVerification.LowerBound(Usual.Take(SpeakerVerification.MinBaseline - 1).ToList()));
        Assert.False(SpeakerVerification.IsOutlier(0.30, 6000, Usual.Take(SpeakerVerification.MinBaseline - 1).ToList()));
        Assert.NotNull(SpeakerVerification.LowerBound(Usual.Take(SpeakerVerification.MinBaseline).ToList()));
    }

    [Fact]
    public void SummaryMarksTheOneFileThatSoundsLikeAnotherVoice()
    {
        // Five takes of one voice, and a sixth far from them.
        var n = 6;
        var m = new double[n][];
        for (var i = 0; i < n; i++)
        {
            m[i] = new double[n];
            for (var j = 0; j < n; j++) m[i][j] = i == j ? 1 : i == 5 || j == 5 ? 0.4 : 0.9 - 0.01 * ((i + j) % 3);
        }

        var fit = SpeakerVerification.Summarize(m);

        Assert.Equal([false, false, false, false, false, true], fit.Files.Select(f => f.Outlier));
        Assert.Equal(5, fit.Files[0].MinIndex);
        Assert.Equal(0.4, fit.Files[0].Min, 6);
        Assert.Equal(0.4, fit.PairMin, 6);
        Assert.InRange(fit.PairMean, 0.4, 0.9);
        Assert.True(fit.PairSpread > 0.1);
    }

    [Fact]
    public void SummaryOfAFewFilesMarksNoOutlier()
    {
        double[][] m = [[1, 0.9, 0.2], [0.9, 1, 0.2], [0.2, 0.2, 1]];
        Assert.All(SpeakerVerification.Summarize(m).Files, f => Assert.False(f.Outlier));
    }

    [Fact]
    public void MedianOfEvenAndOddCounts()
    {
        Assert.Equal(3, SpeakerVerification.Median([5, 1, 3]));
        Assert.Equal(2.5, SpeakerVerification.Median([4, 1, 2, 3]));
    }
}
