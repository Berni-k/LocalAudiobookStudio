using LocalAudiobookStudio.Audio.Decoding;

namespace LocalAudiobookStudio.Tests;

public class SincResamplerTests
{
    private static float[] Signal(int rate, int length) =>
        Enumerable.Range(0, length)
            .Select(n => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * n / rate) + 0.25 * Math.Sin(2 * Math.PI * 3100 * n / rate + 0.3)))
            .ToArray();

    // torchaudio.functional.resample(x, rate, 16000) of the signal above (0.1 s), at some output samples.
    [Theory]
    [InlineData(24000, 2400, new[] { 0, 1, 2, 3, 100, 555, 1000, 1597, 1599 },
        new[] { 0.087398, 0.334356, 0.268938, 0.066016, -0.383431, 0.379489, -0.238892, -0.193343, -0.275321 })]
    [InlineData(44100, 4410, new[] { 0, 1, 2, 3, 100, 555, 1000, 1597, 1599 },
        new[] { 0.087271, 0.335566, 0.268349, 0.066278, -0.383441, 0.379479, -0.2389, -0.192847, -0.272622 })]
    [InlineData(48000, 4800, new[] { 0, 1, 2, 3, 100, 555, 1000, 1597, 1599 },
        new[] { 0.086862, 0.335666, 0.268307, 0.066297, -0.383439, 0.379482, -0.238901, -0.192821, -0.272476 })]
    public void MatchesTorchaudio(int rate, int length, int[] at, double[] expected)
    {
        var output = SincResampler.Resample(Signal(rate, length), rate, 16000);
        Assert.Equal(1600, output.Length);
        for (var i = 0; i < at.Length; i++)
            Assert.True(Math.Abs(output[at[i]] - expected[i]) < 2e-5, $"sample {at[i]} at {rate} Hz: expected {expected[i]}, got {output[at[i]]}");
    }

    [Fact]
    public void TheSameRateIsLeftAlone()
    {
        var signal = Signal(16000, 100);
        Assert.Equal(signal, SincResampler.Resample(signal, 16000, 16000));
        Assert.Empty(SincResampler.Resample([], 24000, 16000));
    }

    [Fact]
    public void ALengthThatDoesntDivideIsRoundedUp()
    {
        // ceil(16000 * 1001 / 24000) = ceil(667.33) = 668, as torchaudio's target length
        Assert.Equal(668, SincResampler.Resample(Signal(24000, 1001), 24000, 16000).Length);
    }
}
