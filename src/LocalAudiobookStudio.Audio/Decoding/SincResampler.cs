namespace LocalAudiobookStudio.Audio.Decoding;

/// <summary>
/// Band-limited sinc resampling with a Hann-windowed kernel, the way <c>torchaudio.functional.resample</c> does it
/// (<c>sinc_interp_hann</c>, 6 zero crossings each side, cut-off at 0.99 of the lower Nyquist frequency). Speech models are
/// trained on audio resampled by libraries like that, and some (pyannote's segmentation) react to what a different filter
/// does to the quietest pauses, so audio meant for them is resampled with this one. Mono; the rates are reduced by their
/// greatest common divisor and one short kernel is built per output phase.
/// </summary>
public static class SincResampler
{
    public static float[] Resample(ReadOnlySpan<float> input, int originalRate, int newRate, int lowpassFilterWidth = 6, double rolloff = 0.99)
    {
        if (originalRate <= 0 || newRate <= 0) throw new ArgumentOutOfRangeException(nameof(originalRate), "sample rates must be positive");
        var divisor = Gcd(originalRate, newRate);
        int orig = originalRate / divisor, target = newRate / divisor;
        if (orig == target) return input.ToArray();
        var length = input.Length;
        var outputLength = (int)Math.Ceiling((double)target * length / orig);
        if (length == 0) return [];

        var baseFreq = Math.Min(orig, target) * rolloff;
        var width = (int)Math.Ceiling(lowpassFilterWidth * orig / baseFreq);
        var kernelLength = 2 * width + orig;
        var scale = baseFreq / orig;
        var kernels = new float[target][];
        for (var phase = 0; phase < target; phase++)
        {
            var kernel = new float[kernelLength];
            for (var j = 0; j < kernelLength; j++)
            {
                var t = (-(double)phase / target + (double)(-width + j) / orig) * baseFreq;
                t = Math.Clamp(t, -lowpassFilterWidth, lowpassFilterWidth);
                var window = Math.Cos(t * Math.PI / lowpassFilterWidth / 2);
                window *= window;
                t *= Math.PI;
                kernel[j] = (float)((t == 0 ? 1.0 : Math.Sin(t) / t) * window * scale);
            }
            kernels[phase] = kernel;
        }

        // The signal sits after `width` zeros and before width + orig zeros; one frame of `target` outputs per `orig` inputs.
        var frames = length / orig + 1;
        var output = new float[frames * target];
        var samples = input.ToArray();
        Parallel.For(0, (frames + 1023) / 1024, block =>
        {
            var end = Math.Min(frames, (block + 1) * 1024);
            for (var frame = block * 1024; frame < end; frame++)
            {
                var start = frame * orig - width; // index in the unpadded signal of the kernel's first tap
                for (var phase = 0; phase < target; phase++)
                {
                    var kernel = kernels[phase];
                    var from = Math.Max(0, -start);
                    var to = Math.Min(kernelLength, length - start);
                    double sum = 0;
                    for (var j = from; j < to; j++) sum += samples[start + j] * kernel[j];
                    output[frame * target + phase] = (float)sum;
                }
            }
        });
        return output.AsSpan(0, outputLength).ToArray();
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
