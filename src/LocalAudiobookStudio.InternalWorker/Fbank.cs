namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// Kaldi-style log mel filterbank features, as speaker-embedding models take them (kaldi-native-fbank, which
/// sherpa-onnx uses, and torchaudio.compliance.kaldi.fbank): 25 ms Povey windows every 10 ms, no padding at the edges
/// (so a clip gives (n - 400) / 160 + 1 frames), DC offset removed, pre-emphasis 0.97, a 512-point power spectrum, 80
/// triangular mel bins from 20 Hz to the Nyquist frequency, natural log. No dither, so the same audio always gives the
/// same features. Has no dependencies, so the tests compile this file too.
/// </summary>
internal static class Fbank
{
    public const int SampleRate = 16_000;
    public const int WindowSamples = 400;  // 25 ms
    public const int ShiftSamples = 160;   // 10 ms
    public const int FftSize = 512;        // the window padded to the next power of two
    public const int MelBins = 80;
    private const double LowHz = 20;
    private const double Preemphasis = 0.97;

    private static readonly float[] Window = BuildWindow();
    private static readonly float[] HammingWindow = BuildHammingWindow();
    private static readonly float[][] MelWeights = BuildMelBanks();
    private static readonly (double Cos, double Sin)[] Twiddles = BuildTwiddles();

    /// <summary>
    /// Features of 16 kHz mono <paramref name="samples"/>: [frames, 80]. They are used as they come, so scale the samples
    /// the way the model was trained (some want -1..1, WeSpeaker's the 16-bit integer range). Empty when the clip is
    /// shorter than one window.
    /// </summary>
    /// <param name="samples">16 kHz mono samples.</param>
    /// <param name="hamming">
    /// A Hamming window instead of the Povey one (WeSpeaker's own features, which pyannote's embedding model takes; the
    /// 3D-Speaker and sherpa-onnx exports use Povey).
    /// </param>
    public static float[][] Compute(ReadOnlySpan<float> samples, bool hamming = false)
    {
        var window = hamming ? HammingWindow : Window;
        if (samples.Length < WindowSamples) return [];
        var frames = (samples.Length - WindowSamples) / ShiftSamples + 1;
        var result = new float[frames][];
        var frame = new double[WindowSamples];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var power = new double[FftSize / 2];
        for (var f = 0; f < frames; f++)
        {
            var start = f * ShiftSamples;
            double mean = 0;
            for (var i = 0; i < WindowSamples; i++) { frame[i] = samples[start + i]; mean += frame[i]; }
            mean /= WindowSamples;
            for (var i = 0; i < WindowSamples; i++) frame[i] -= mean;
            for (var i = WindowSamples - 1; i > 0; i--) frame[i] -= Preemphasis * frame[i - 1];
            frame[0] -= Preemphasis * frame[0];

            for (var i = 0; i < FftSize; i++)
            {
                re[i] = i < WindowSamples ? frame[i] * window[i] : 0;
                im[i] = 0;
            }
            Fft(re, im);
            // The bin at the Nyquist frequency isn't part of the mel banks (as in Kaldi).
            for (var i = 0; i < power.Length; i++) power[i] = re[i] * re[i] + im[i] * im[i];

            var row = new float[MelBins];
            for (var m = 0; m < MelBins; m++)
            {
                double sum = 0;
                var weights = MelWeights[m];
                for (var i = 0; i < weights.Length; i++) sum += weights[i] * power[i];
                row[m] = (float)Math.Log(Math.Max(sum, 1.1920929e-07)); // float epsilon, as Kaldi floors it
            }
            result[f] = row;
        }
        return result;
    }

    /// <summary>Subtracts each feature's mean over the clip (the "global-mean" normalisation 3D-Speaker and WeSpeaker models use).</summary>
    public static void SubtractMean(float[][] features)
    {
        if (features.Length == 0) return;
        for (var d = 0; d < MelBins; d++)
        {
            double mean = 0;
            foreach (var row in features) mean += row[d];
            mean /= features.Length;
            foreach (var row in features) row[d] = (float)(row[d] - mean);
        }
    }

    private static float[] BuildWindow()
    {
        // Povey: a Hann window raised to the power 0.85.
        var w = new float[WindowSamples];
        for (var i = 0; i < w.Length; i++)
            w[i] = (float)Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSamples - 1)), 0.85);
        return w;
    }

    private static float[] BuildHammingWindow()
    {
        var w = new float[WindowSamples];
        for (var i = 0; i < w.Length; i++)
            w[i] = (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (WindowSamples - 1)));
        return w;
    }

    private static double Mel(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);

    private static float[][] BuildMelBanks()
    {
        var bins = FftSize / 2;
        var binWidth = (double)SampleRate / FftSize;
        var melLow = Mel(LowHz);
        var melHigh = Mel(SampleRate / 2.0);
        var delta = (melHigh - melLow) / (MelBins + 1);
        var banks = new float[MelBins][];
        for (var b = 0; b < MelBins; b++)
        {
            var left = melLow + b * delta;
            var center = melLow + (b + 1) * delta;
            var right = melLow + (b + 2) * delta;
            var weights = new float[bins];
            for (var i = 0; i < bins; i++)
            {
                var mel = Mel(binWidth * i);
                if (mel <= left || mel >= right) continue;
                weights[i] = (float)(mel <= center ? (mel - left) / (center - left) : (right - mel) / (right - center));
            }
            banks[b] = weights;
        }
        return banks;
    }

    private static (double, double)[] BuildTwiddles()
    {
        var t = new (double, double)[FftSize / 2];
        for (var i = 0; i < t.Length; i++) t[i] = (Math.Cos(-2 * Math.PI * i / FftSize), Math.Sin(-2 * Math.PI * i / FftSize));
        return t;
    }

    /// <summary>In-place radix-2 FFT of <see cref="FftSize"/> points.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = FftSize;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var step = n / len;
            for (var i = 0; i < n; i += len)
                for (var k = 0; k < len / 2; k++)
                {
                    var (c, s) = Twiddles[k * step];
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * c - im[b] * s;
                    var ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}
