using LocalAudiobookStudio.InternalWorker;

namespace LocalAudiobookStudio.Tests;

public class VoiceFeatureTests
{
    /// <summary>The signal the reference values were computed from: two sines, 0.1 s at 16 kHz, in the 16-bit range.</summary>
    private static float[] Signal() =>
        Enumerable.Range(0, 1600)
            .Select(n => (float)(3000 * Math.Sin(2 * Math.PI * 440 * n / 16000) + 1500 * Math.Sin(2 * Math.PI * 1250 * n / 16000 + 0.5)))
            .ToArray();

    // torchaudio.compliance.kaldi.fbank (80 bins, 25/10 ms, povey, dither 0, preemphasis 0.97, low 20 Hz, snip edges)
    // on Signal(), rows 0, 3 and 7, bins 0, 10, 40 and 79.
    // The reference is float32: bins with almost no energy (the top ones, here) are off by a few hundredths.
    private const double Tolerance = 0.05;

    private static readonly int[] Bins = [0, 10, 40, 79];
    private static readonly (int Row, double[] Values)[] Reference =
    [
        (0, [5.7992, 12.833, 5.4728, -5.1648]),
        (3, [7.4323, 12.8163, 5.2699, -6.3243]),
        (7, [7.3576, 12.8421, 5.4407, -5.6534]),
    ];

    [Fact]
    public void FbankMatchesTheKaldiReference()
    {
        var features = Fbank.Compute(Signal());
        Assert.Equal(8, features.Length);
        foreach (var (row, values) in Reference)
            for (var i = 0; i < Bins.Length; i++)
                Assert.InRange(features[row][Bins[i]], values[i] - Tolerance, values[i] + Tolerance);
    }

    [Fact]
    public void HammingFbankMatchesTorchaudioForPyannotesEmbeddingModel()
    {
        // torchaudio.compliance.kaldi.fbank(window_type="hamming", the rest as above) of two sines, 0.2 s at 16 kHz, in the
        // 16-bit range (what pyannote's WeSpeaker wrapper computes before the network), rows 0, 5 and 17, bins 0, 1, 10, 40, 79.
        var samples = Enumerable.Range(0, 3200)
            .Select(n => (float)((0.3 * Math.Sin(2 * Math.PI * 440 * n / 16000) + 0.2 * Math.Sin(2 * Math.PI * 1234 * n / 16000 + 0.5)) * 32768))
            .ToArray();
        var features = Fbank.Compute(samples, hamming: true);
        Assert.Equal(18, features.Length);
        int[] bins = [0, 1, 10, 40, 79];
        (int Row, double[] Values)[] expected =
        [
            (0, [12.3962, 12.627, 12.0056, 14.5558, 12.9175]),
            (5, [12.8946, 13.693, 12.9458, 14.054, 11.6032]),
            (17, [14.3295, 15.2534, 14.065, 14.3887, 12.1593]),
        ];
        foreach (var (row, values) in expected)
            for (var i = 0; i < bins.Length; i++)
                Assert.InRange(features[row][bins[i]], values[i] - Tolerance, values[i] + Tolerance);
    }

    [Fact]
    public void MeanSubtractionCentersEachFeature()
    {
        var features = Fbank.Compute(Signal());
        Fbank.SubtractMean(features);
        Assert.InRange(features[3][0], 0.305 - Tolerance, 0.305 + Tolerance);
        Assert.InRange(features[3][79], -0.4895 - Tolerance, -0.4895 + Tolerance);
        for (var d = 0; d < Fbank.MelBins; d++)
            Assert.InRange(features.Average(r => r[d]), -1e-4, 1e-4);
    }

    [Fact]
    public void AClipShorterThanOneWindowHasNoFrames()
    {
        Assert.Empty(Fbank.Compute(new float[Fbank.WindowSamples - 1]));
        Assert.Single(Fbank.Compute(new float[Fbank.WindowSamples]));
    }

    [Fact]
    public void CosineIsOneForTheSameDirectionAndMinusOneForTheOpposite()
    {
        Assert.Equal(1, VoiceMath.Cosine([1f, 2f, 3f], [2f, 4f, 6f]), 6);
        Assert.Equal(-1, VoiceMath.Cosine([1f, 2f, 3f], [-1f, -2f, -3f]), 6);
        Assert.Equal(0, VoiceMath.Cosine([1f, 0f], [0f, 1f]), 6);
        Assert.Equal(0, VoiceMath.Cosine([0f, 0f], [0f, 1f]));
    }

    [Fact]
    public void AverageGivesEveryReferenceTheSameWeightWhateverItsLength()
    {
        var mean = VoiceMath.Average([[10f, 0f], [0f, 1f]]);
        Assert.Equal(mean[0], mean[1], 6);
        Assert.Equal(1, VoiceMath.Cosine(mean, [1f, 1f]), 6);
    }

    [Fact]
    public void MatrixIsSymmetricWithOnesOnTheDiagonal()
    {
        var m = VoiceMath.Matrix([[1f, 0f], [1f, 1f], [0f, 1f]]);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, m[i][i]);
            for (var j = 0; j < 3; j++) Assert.Equal(m[i][j], m[j][i], 9);
        }
        Assert.Equal(Math.Sqrt(0.5), m[0][1], 6);
        Assert.Equal(0, m[0][2], 6);
    }
}
