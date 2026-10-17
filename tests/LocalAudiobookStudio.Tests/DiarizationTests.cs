using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.InternalWorker;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Tests;

/// <summary>The arithmetic of the pyannote pipeline (apart from its networks) and pairing turns with heard words.</summary>
public class DiarizationTests
{
    private static float[] Vector(Random rng, float[] centre, float noise) =>
        centre.Select(c => c + (float)(rng.NextDouble() - 0.5) * noise).ToArray();

    private static float[] Unit(float[] v)
    {
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }

    [Fact]
    public void CentroidClusteringCutsWhereMergesAreFarApart()
    {
        var rng = new Random(1);
        float[] a = Enumerable.Range(0, 16).Select(i => i == 0 ? 1f : 0f).ToArray(), b = Enumerable.Range(0, 16).Select(i => i == 1 ? 1f : 0f).ToArray();
        var vectors = Enumerable.Range(0, 20).Select(_ => Unit(Vector(rng, a, 0.1f)))
            .Concat(Enumerable.Range(0, 20).Select(_ => Unit(Vector(rng, b, 0.1f)))).ToArray();

        var clusters = DiarizationMath.CentroidClusters(vectors, 0.6);
        Assert.Equal(2, clusters.Distinct().Count());
        Assert.Single(clusters.Take(20).Distinct());
        Assert.Single(clusters.Skip(20).Distinct());
        Assert.NotEqual(clusters[0], clusters[20]);

        // A threshold below every distance leaves each vector alone; one above all of them joins everything.
        Assert.Equal(40, DiarizationMath.CentroidClusters(vectors, 0.0001).Distinct().Count());
        Assert.Single(DiarizationMath.CentroidClusters(vectors, 3).Distinct());
        Assert.Equal([0], DiarizationMath.CentroidClusters([vectors[0]], 0.6));

        // Too many to group at once: every other one is grouped and the rest join the nearest cluster, so the result is the same.
        var capped = DiarizationMath.CentroidClusters(vectors, 0.6, maxVectors: 20);
        Assert.Equal(40, capped.Length);
        Assert.Single(capped.Take(20).Distinct());
        Assert.Single(capped.Skip(20).Distinct());
        Assert.NotEqual(capped[0], capped[20]);
    }

    /// <summary>Points in a made-up 8-dimensional PLDA space: a fixed wobble, with the first dimension shifted to a centre per point.</summary>
    private static (double[][] X, double[] Phi) Toy(params double[] centres)
    {
        var phi = new[] { 1.0, 0.8, 0.6, 0.5, 0.4, 0.3, 0.2, 0.1 };
        var x = centres.Select((c, t) => Enumerable.Range(0, 8).Select(d => ((t * 7 + d * 13) % 17 - 8) / 8.0 + (d == 0 ? c : 0)).ToArray()).ToArray();
        return (x, phi);
    }

    private static string FirstAppearance(IEnumerable<string> labels)
    {
        var seen = new Dictionary<string, int>();
        return string.Concat(labels.Select(l => (char)('a' + (seen.TryGetValue(l, out var n) ? n : seen[l] = seen.Count))));
    }

    [Fact]
    public void CentroidClustersAreTheOnesScipyMakes()
    {
        // 60 unit vectors from a fixed formula, in 5 loose classes; the expected clusters come from scipy's
        // linkage(method="centroid") and fcluster(criterion="distance") (the labels written here in order of appearance).
        const int n = 60, dim = 6;
        var vectors = Enumerable.Range(0, n).Select(t => Unit(Enumerable.Range(0, dim)
            .Select(d => (float)(Math.Sin((t % 5) * (d + 1) * 0.9 + 0.3) + 0.35 * Math.Sin(t * (d + 2) * 0.77))).ToArray())).ToArray();

        string Mine(double threshold) => FirstAppearance(DiarizationMath.CentroidClusters(vectors, threshold).Select(l => l.ToString()));
        string Scipy(string letters) => FirstAppearance(letters.Select(c => c.ToString()));

        Assert.Equal(Scipy("ebdcaebdcaebdcaebdcaebdcaebdcaebdcaebdcaebdcaebdcaebdcaebdca"), Mine(0.9));
        Assert.Equal(Scipy("fbdcaebdcafbdcagbdcajbdcafbdcafbdcaebdcakbdcafbdcaibdcahbdca"), Mine(0.6));
    }

    [Fact]
    public void VbxGivesTheNumbersPyannotesImplementationGives()
    {
        // The expected values come from pyannote.audio 4.0.7's cluster_vbx on exactly this data (maxIters 20, init smoothing 7).
        var (x, phi) = Toy(Enumerable.Repeat(-4.0, 20).Concat(Enumerable.Repeat(4.0, 20)).ToArray());
        var two = Enumerable.Range(0, 40).Select(i => i < 20 ? 0 : 1).ToArray();

        // Weak regularisation (pyannote's Fa and Fb): nearly no separation, the starting clusters barely move.
        var (gamma, pi) = DiarizationMath.Vbx(x, phi, 0.07, 0.8, DiarizationMath.SmoothedStart(two, 2, 7.0), 20, 1e-4);
        Near([0.503895059, 0.496104941], pi);
        Near([0.50582991, 0.49417009], gamma[0]);
        Near([0.502676406, 0.497323594], gamma[39]);
        Near([0.502525113, 0.497474887], gamma[20]);

        // Strong: the two voices are told apart cleanly.
        (gamma, pi) = DiarizationMath.Vbx(x, phi, 1.0, 1.0, DiarizationMath.SmoothedStart(two, 2, 7.0), 20, 1e-4);
        Near([0.5, 0.5], pi);
        Near([1.0, 0.0], gamma[0]);
        Near([0.0, 1.0], gamma[39]);

        // One voice started as three clusters: the weight ends up in one of them.
        var (one, phi1) = Toy(Enumerable.Repeat(3.0, 30).ToArray());
        (gamma, pi) = DiarizationMath.Vbx(one, phi1, 1.0, 1.0, DiarizationMath.SmoothedStart(Enumerable.Range(0, 30).Select(i => i % 3).ToArray(), 3, 7.0), 20, 1e-4);
        Near([7e-9, 7.6e-8, 0.999999916], pi);
        Near([3.5e-8, 3.65e-7, 0.9999996], gamma[0]);
    }

    private static void Near(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) < 2e-6, $"[{i}] expected {expected[i]}, got {actual[i]}");
    }

    [Fact]
    public void WindowsAreAveragedOrSummedByFrame()
    {
        // Two chunks of 4 frames, one frame apart (step = one frame), one class: frames 1-3 are covered twice.
        const double fd = 1.0, fs = 1.0; // a frame is as long as it is spaced
        var chunks = new List<float[]> { new float[] { 1, 1, 1, 1 }, new float[] { 3, 3, 3, 3 } };
        var average = DiarizationMath.Aggregate(chunks, 4, 1, 4.0, 1.0, fd, fs, skipAverage: false, out var frames);
        Assert.Equal(6, frames); // the last frame is the one nearest the end of the last chunk; none reaches it, so it is 0
        Assert.Equal([1f, 2f, 2f, 2f, 3f, 0f], average);
        Assert.Equal([1f, 4f, 4f, 4f, 3f, 0f], DiarizationMath.Aggregate(chunks, 4, 1, 4.0, 1.0, fd, fs, skipAverage: true, out _));

        // Missing values (NaN) don't count, and frames nothing reached stay 0.
        chunks[0][0] = float.NaN;
        var missing = DiarizationMath.Aggregate(chunks, 4, 1, 4.0, 1.0, fd, fs, skipAverage: false, out _);
        Assert.Equal(0f, missing[0]);
    }

    [Fact]
    public void NumpyRoundingGoesToTheEvenNumber()
    {
        // pyannote's frame arithmetic uses numpy.rint: halves go to even (0.5 → 0, 1.5 → 2, 2.5 → 2).
        Assert.Equal(0, DiarizationMath.ClosestFrame(0.5, 0, 1));
        Assert.Equal(2, DiarizationMath.ClosestFrame(1.5, 0, 1));
        Assert.Equal(2, DiarizationMath.ClosestFrame(2.5, 0, 1));
        Assert.Equal(4, DiarizationMath.ClosestFrame(3.5, 0, 1));
    }

    [Fact]
    public void EachLocalSpeakerGetsAClusterOfItsOwn()
    {
        // Two local speakers both most like cluster 0: only one gets it, the other takes the next best.
        var assignment = DiarizationMath.AssignDistinct([[0.9, 0.5, 0.1], [0.8, 0.7, 0.0], [0.0, 0.0, 0.0]]);
        Assert.Equal(3, assignment.Distinct().Count());
        Assert.Equal(0, assignment[0]);
        Assert.Equal(1, assignment[1]);

        // With fewer clusters than speakers, as many speakers as clusters are placed; the best ones.
        var few = DiarizationMath.AssignDistinct([[0.9], [0.2], [0.5]]);
        Assert.Equal([0, -2, -2], few);
    }

    [Fact]
    public void TurnsStartAtTheMiddleOfTheFirstActiveFrameAndEndAtTheFirstInactiveOne()
    {
        // One speaker on for frames 2-4 of 8, another on for frame 7 (to the end).
        const int frames = 8;
        var discrete = new float[frames * 2];
        for (var t = 2; t <= 4; t++) discrete[t * 2] = 1;
        discrete[7 * 2 + 1] = 1;
        var turns = DiarizationMath.Turns(discrete, frames, 2, 0.5, 0.1);
        Assert.Equal(2, turns.Count);
        Assert.Equal((0.25 + 0.2, 0.25 + 0.5, 0), (turns[0].Start, turns[0].End, turns[0].Speaker));
        Assert.Equal(1, turns[1].Speaker);
        Assert.Equal(0.25 + 0.7, turns[1].Start, 9);
        Assert.Equal(0.25 + 0.7, turns[1].End, 9); // active only at the last frame: a turn of no length, as pyannote's
    }

    [Fact]
    public void TheBusiestClustersAreTheOnesCounted()
    {
        // Three frames, two clusters; the count says one voice, then two, then none.
        float[] chunk = [1, 0, 1, 1, 0, 0];
        var discrete = DiarizationMath.Discrete([chunk], 3, 2, [1, 2, 0], 3.0, 1.0, 1.0, 1.0, out var frames);
        Assert.Equal(4, frames); // (one frame past the chunk, which the count doesn't cover)
        Assert.Equal([1f, 0f, 1f, 1f, 0f, 0f, 0f, 0f], discrete);
    }

    [Fact]
    public void HeardWordsTakeTheVoiceOfTheTurnTheyFallIn()
    {
        var heard = new TranscriptResult("a b c d", [new("a", 0, 400), new("b", 500, 900), new("c", 2000, 2400), new("d", 5000, 5400)], "en");
        var turns = new TranscriptResult("", [], null,
        [
            new TranscriptSegment("", 0, 1000, "S01"),
            new TranscriptSegment("", 1800, 3000, "S02"),
            new TranscriptSegment("", 3000, 4000, "S03"),
        ]);

        var merged = Transcription.WithTurns(heard, turns);

        // "d" falls after every turn: it goes to the nearest, the last one.
        Assert.Equal(["S01", "S01", "S02", "S03"], merged.Words.Select(w => w.Speaker));
        Assert.Equal([0, 0, 1, 2], merged.Words.Select(w => w.Block));
        Assert.Equal(["a b", "c", "d"], merged.Segments!.Select(s => s.Text));
        Assert.Equal((0, 1000, "S01"), (merged.Segments[0].StartMs, merged.Segments[0].EndMs, merged.Segments[0].Speaker)); // the turn's own times
        Assert.Equal(heard.Words.Select(w => w.StartMs), merged.Words.Select(w => w.StartMs)); // the words' times stay theirs
    }

    [Fact]
    public void ATurnNobodySpokeInDropsOutAndBlocksAreNumberedAgain()
    {
        var heard = new TranscriptResult("x y", [new("x", 3100, 3400), new("y", 3500, 3900)], "en");
        var turns = new TranscriptResult("", [], null,
            [new TranscriptSegment("", 0, 1000, "S01"), new TranscriptSegment("", 3000, 4000, "S02")]);

        var merged = Transcription.WithTurns(heard, turns);

        Assert.Equal([0, 0], merged.Words.Select(w => w.Block));
        Assert.Equal(["S02"], merged.Segments!.Select(s => s.Speaker));
    }

    [Fact]
    public void WithoutTurnsTheWordsStayAsTheyAre()
    {
        var heard = new TranscriptResult("a", [new("a", 0, 100)], "en");
        Assert.Same(heard, Transcription.WithTurns(heard, new TranscriptResult("", [], null)));
    }
}
