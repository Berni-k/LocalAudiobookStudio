using LocalAudiobookStudio.Audio.Decoding;

namespace LocalAudiobookStudio.Audio.Analysis;

public sealed record AudioAnalysis(int DurationMs, int SampleRate, int Channels, double? LoudnessLufs, int TrimStartMs, int TrimEndMs);

/// <summary>
/// How silence at the ends of a take is found: anything quieter than <see cref="ThresholdDb"/> counts as silence, and
/// <see cref="PaddingMs"/> of it is kept before the first and after the last sound, so soft consonants and breaths
/// aren't clipped. Set in the app's settings.
/// </summary>
public sealed record SilenceTrim(double ThresholdDb = SilenceTrim.DefaultThresholdDb, double PaddingMs = SilenceTrim.DefaultPaddingMs)
{
    public const double DefaultThresholdDb = -50.0;
    public const double DefaultPaddingMs = 100.0;
    public static readonly SilenceTrim Default = new();
}

/// <summary>Runs once after a take is generated (or a clip imported): loudness, silence trims and peaks.</summary>
public static class TakeAnalyzer
{
    public const double MinSilenceMs = 30.0;

    public static AudioAnalysis Analyze(string wavPath, string? peaksPath, SilenceTrim? trim = null)
    {
        var buf = AudioDecoder.Decode(wavPath);
        var mono = AudioDecoder.ToMono(buf);
        if (peaksPath != null) Peaks.FromSamples(mono, buf.SampleRate).Save(peaksPath);
        var (lead, trail) = DetectSilence(mono, buf.SampleRate, trim);
        return new AudioAnalysis(
            (int)Math.Round(buf.DurationMs),
            buf.SampleRate,
            buf.Channels,
            LoudnessMeter.IntegratedLoudness(buf),
            (int)Math.Round(lead),
            (int)Math.Round(trail));
    }

    /// <summary>
    /// Leading and trailing silence in ms, less the padding. Silence shorter than 30 ms is ignored.
    /// A fully silent clip returns (0, 0) so it is never trimmed to nothing.
    /// </summary>
    public static (double leadMs, double trailMs) DetectSilence(ReadOnlySpan<float> mono, int sampleRate, SilenceTrim? trim = null)
    {
        trim ??= SilenceTrim.Default;
        var threshold = (float)Math.Pow(10, trim.ThresholdDb / 20.0);
        int first = -1, last = -1;
        for (int i = 0; i < mono.Length; i++)
            if (Math.Abs(mono[i]) > threshold) { first = i; break; }
        if (first < 0) return (0, 0);
        for (int i = mono.Length - 1; i >= 0; i--)
            if (Math.Abs(mono[i]) > threshold) { last = i; break; }

        var msPerSample = 1000.0 / sampleRate;
        var lead = first * msPerSample;
        var trail = (mono.Length - 1 - last) * msPerSample;
        var padding = Math.Max(0, trim.PaddingMs);
        lead = lead >= MinSilenceMs ? Math.Max(0, lead - padding) : 0;
        trail = trail >= MinSilenceMs ? Math.Max(0, trail - padding) : 0;
        return (lead, trail);
    }
}
