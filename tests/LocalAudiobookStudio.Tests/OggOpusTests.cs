using LocalAudiobookStudio.Audio.Decoding;

namespace LocalAudiobookStudio.Tests;

/// <summary>Opus storage: a take must come back with its exact length and timing (trims and the timeline depend on it).</summary>
public sealed class OggOpusTests
{
    private static DecodedAudio Tone(int rate, int channels, double seconds, double hz = 440)
    {
        var frames = (int)(rate * seconds);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            // A short burst at 0.25 s marks the timing.
            var burst = i >= rate / 4 && i < rate / 4 + rate / 100 ? 0.4f : 0f;
            var v = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / rate)) + burst;
            for (var c = 0; c < channels; c++) samples[i * channels + c] = v;
        }
        return new DecodedAudio(samples, rate, channels);
    }

    [Theory]
    [InlineData(24000, 1, 2.3)]
    [InlineData(44100, 2, 1.01)]
    [InlineData(48000, 1, 0.5)]
    public void A_round_trip_keeps_length_channels_and_timing(int rate, int channels, double seconds)
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "take.opus");
        var original = Tone(rate, channels, seconds);

        OggOpus.Write(original, path);
        var decoded = AudioDecoder.Decode(path);

        Assert.Equal(48000, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.InRange(decoded.DurationMs, original.DurationMs - 0.1, original.DurationMs + 0.1);
        // The burst starts where it did: pre-skip removed, nothing shifted.
        var reference = AudioDecoder.Resample(original, 48000);
        Assert.InRange(Lag(reference, decoded), -2, 2);
        Assert.True(new FileInfo(path).Length < original.Samples.Length * 2 / 4, "compressed well below 16-bit PCM");
    }

    /// <summary>The offset (in frames) at which the decoded audio best matches the reference, within ±10 ms.</summary>
    private static int Lag(DecodedAudio reference, DecodedAudio decoded)
    {
        var best = 0;
        var bestScore = double.MinValue;
        var n = Math.Min(reference.Frames, decoded.Frames) - 960;
        for (var lag = -480; lag <= 480; lag++)
        {
            double score = 0;
            for (var i = 960; i < n; i += 3)
            {
                var j = i + lag;
                if (j < 0 || j >= decoded.Frames) continue;
                score += reference.Samples[i * reference.Channels] * decoded.Samples[j * decoded.Channels];
            }
            if (score > bestScore) (bestScore, best) = (score, lag);
        }
        return best;
    }

    [Fact]
    public void The_bit_rate_sets_the_size_and_keeps_the_length()
    {
        using var tmp = new TempDir();
        // Noise, so the encoder uses the bits it is given (a pure tone needs far fewer).
        var rng = new Random(1);
        var noise = new DecodedAudio(Enumerable.Range(0, 48000 * 3).Select(_ => (float)(rng.NextDouble() - 0.5) * 0.5f).ToArray(), 48000, 1);
        long SizeAt(int kbps)
        {
            var path = Path.Combine(tmp.Path, $"{kbps}.opus");
            OggOpus.Write(noise, path, kbps * 1000);
            Assert.Equal(noise.Frames, AudioDecoder.Decode(path).Frames);
            return new FileInfo(path).Length;
        }

        var (smaller, standard, high) = (SizeAt(48), SizeAt(64), SizeAt(96));

        Assert.True(smaller < standard && standard < high, $"{smaller} < {standard} < {high}");
        // About 3 s at 96 kbps is about 36 kB.
        Assert.InRange(high, 30_000, 42_000);
    }

    [Fact]
    public void A_file_that_is_not_opus_is_refused()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "fake.opus");
        File.WriteAllText(path, "not audio");
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(path));
    }
}
