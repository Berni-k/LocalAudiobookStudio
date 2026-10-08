using LocalAudiobookStudio.Audio.Decoding;

namespace LocalAudiobookStudio.Audio.Analysis;

/// <summary>
/// Integrated loudness per ITU-R BS.1770-4: K-weighting (high shelf + high pass), 400 ms blocks
/// with 75 % overlap, absolute gate at −70 LUFS and relative gate at −10 LU.
/// Clips shorter than one block fall back to RMS.
/// </summary>
public static class LoudnessMeter
{
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;

    /// <summary>Returns integrated loudness in LUFS, or null for digital silence.</summary>
    public static double? IntegratedLoudness(DecodedAudio buf)
    {
        var ch = buf.Channels;
        var frames = buf.Frames;
        if (frames == 0) return null;
        var rate = buf.SampleRate;
        var blockFrames = (int)Math.Round(0.4 * rate);

        if (frames < blockFrames) return RmsLoudness(buf);

        // K-weight each channel.
        var filtered = new double[ch][];
        for (int c = 0; c < ch; c++)
        {
            var x = new double[frames];
            for (int f = 0; f < frames; f++) x[f] = buf.Samples[f * ch + c];
            var shelf = Biquad.KWeightingShelf(rate);
            var hp = Biquad.KWeightingHighPass(rate);
            for (int f = 0; f < frames; f++) x[f] = hp.Process(shelf.Process(x[f]));
            filtered[c] = x;
        }

        // Prefix sums of squares for fast block energies.
        var step = blockFrames / 4;
        var blockCount = (frames - blockFrames) / step + 1;
        var blockPower = new double[blockCount];
        for (int c = 0; c < ch; c++)
        {
            var weight = ChannelWeight(c, ch);
            var prefix = new double[frames + 1];
            var x = filtered[c];
            for (int f = 0; f < frames; f++) prefix[f + 1] = prefix[f] + x[f] * x[f];
            for (int b = 0; b < blockCount; b++)
            {
                var s = b * step;
                blockPower[b] += weight * (prefix[s + blockFrames] - prefix[s]) / blockFrames;
            }
        }

        static double L(double power) => -0.691 + 10.0 * Math.Log10(power);

        // Absolute gate.
        double sum = 0; int n = 0;
        foreach (var p in blockPower)
            if (p > 0 && L(p) > AbsoluteGate) { sum += p; n++; }
        if (n == 0) return null;

        // Relative gate.
        var relative = L(sum / n) + RelativeGate;
        double sum2 = 0; int n2 = 0;
        foreach (var p in blockPower)
            if (p > 0 && L(p) > AbsoluteGate && L(p) > relative) { sum2 += p; n2++; }
        return n2 == 0 ? null : L(sum2 / n2);
    }

    /// <summary>Unweighted mean-square loudness estimate for very short clips.</summary>
    public static double? RmsLoudness(DecodedAudio buf)
    {
        double sum = 0;
        foreach (var v in buf.Samples) sum += (double)v * v;
        var frames = buf.Frames;
        if (frames == 0 || sum <= 0) return null;
        // Sum over channels of per-channel mean square, as BS.1770 does.
        var power = sum / frames;
        return -0.691 + 10.0 * Math.Log10(power);
    }

    /// <summary>BS.1770 channel weights: 1.0 for L, R, C; 1.41 for surrounds (channels 4+ in 5.1).</summary>
    private static double ChannelWeight(int c, int channels) => channels >= 5 && c >= 3 ? (c == 3 ? 0.0 : 1.41) : 1.0;

    /// <summary>
    /// Non-destructive normalization gain: (target − measured), clamped to ±20 dB so near-silent
    /// failures aren't boosted into noise.
    /// </summary>
    public static double AutoGainDb(double targetLufs, double? measuredLufs, double clampDb = 20.0) =>
        measuredLufs is double m ? Math.Clamp(targetLufs - m, -clampDb, clampDb) : 0.0;
}

/// <summary>Direct form I biquad in double precision.</summary>
internal sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
{
    private double _x1, _x2, _y1, _y2;

    public double Process(double x)
    {
        var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return y;
    }

    /// <summary>
    /// Stage 1 of K-weighting: +4 dB high shelf around 1.7 kHz. Bilinear design (as in libebur128)
    /// that reproduces the ITU 48 kHz coefficients exactly and adapts to any sample rate.
    /// </summary>
    public static Biquad KWeightingShelf(int fs)
    {
        const double G = 3.999843853973347, fc = 1681.974450955533, Q = 0.7071752369554196;
        var K = Math.Tan(Math.PI * fc / fs);
        var Vh = Math.Pow(10, G / 20.0);
        var Vb = Math.Pow(Vh, 0.4996667741545416);
        var a0 = 1 + K / Q + K * K;
        return new Biquad(
            (Vh + Vb * K / Q + K * K) / a0,
            2 * (K * K - Vh) / a0,
            (Vh - Vb * K / Q + K * K) / a0,
            2 * (K * K - 1) / a0,
            (1 - K / Q + K * K) / a0);
    }

    /// <summary>Stage 2 of K-weighting: high pass around 38 Hz (the "RLB" filter).</summary>
    public static Biquad KWeightingHighPass(int fs)
    {
        const double fc = 38.13547087602444, Q = 0.5003270373238773;
        var K = Math.Tan(Math.PI * fc / fs);
        var a0 = 1 + K / Q + K * K;
        return new Biquad(1, -2, 1, 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0);
    }
}
