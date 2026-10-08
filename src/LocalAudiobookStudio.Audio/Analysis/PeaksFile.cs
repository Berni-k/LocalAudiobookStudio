using LocalAudiobookStudio.Audio.Decoding;

namespace LocalAudiobookStudio.Audio.Analysis;

/// <summary>
/// Precomputed waveform peaks: 16-bit min/max pairs per 256 source samples (mono mixdown).
/// The waveform control draws from these and never decodes audio.
/// </summary>
public sealed class Peaks
{
    public const int DefaultSamplesPerBin = 256;
    private const uint Magic = 0x5053414C; // "LASP"
    private const int Version = 1;

    public Peaks(int sampleRate, int samplesPerBin, long totalSamples, short[] min, short[] max)
    {
        SampleRate = sampleRate;
        SamplesPerBin = samplesPerBin;
        TotalSamples = totalSamples;
        Min = min;
        Max = max;
    }

    public int SampleRate { get; }
    public int SamplesPerBin { get; }
    public long TotalSamples { get; }
    public short[] Min { get; }
    public short[] Max { get; }
    public int BinCount => Min.Length;
    public double DurationMs => TotalSamples * 1000.0 / Math.Max(1, SampleRate);

    public static Peaks FromSamples(ReadOnlySpan<float> mono, int sampleRate, int samplesPerBin = DefaultSamplesPerBin)
    {
        var bins = (mono.Length + samplesPerBin - 1) / samplesPerBin;
        var min = new short[bins];
        var max = new short[bins];
        for (int b = 0; b < bins; b++)
        {
            var start = b * samplesPerBin;
            var end = Math.Min(mono.Length, start + samplesPerBin);
            float lo = 0, hi = 0;
            for (int i = start; i < end; i++)
            {
                var v = mono[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            min[b] = ToShort(lo);
            max[b] = ToShort(hi);
        }
        return new Peaks(sampleRate, samplesPerBin, mono.Length, min, max);
    }

    public static Peaks FromWav(string wavPath)
    {
        var buf = AudioDecoder.Decode(wavPath);
        return FromSamples(AudioDecoder.ToMono(buf), buf.SampleRate);
    }

    private static short ToShort(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32768, 32767);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(SampleRate);
            w.Write(SamplesPerBin);
            w.Write(BinCount);
            w.Write(TotalSamples);
            for (int i = 0; i < BinCount; i++)
            {
                w.Write(Min[i]);
                w.Write(Max[i]);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static Peaks Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a peaks file.");
        var version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"Unsupported peaks version {version}.");
        var rate = r.ReadInt32();
        var spb = r.ReadInt32();
        var bins = r.ReadInt32();
        var total = r.ReadInt64();
        var min = new short[bins];
        var max = new short[bins];
        for (int i = 0; i < bins; i++)
        {
            min[i] = r.ReadInt16();
            max[i] = r.ReadInt16();
        }
        return new Peaks(rate, spb, total, min, max);
    }

    /// <summary>
    /// Min/max per output column for the time range [startMs, endMs). Several bins are merged per
    /// column when zoomed out, which picks the level of detail from bin count and control width.
    /// </summary>
    public void GetColumns(double startMs, double endMs, Span<float> colMin, Span<float> colMax)
    {
        var columns = colMin.Length;
        colMin.Clear();
        colMax.Clear();
        if (columns == 0 || BinCount == 0 || endMs <= startMs) return;
        var binsPerMs = SampleRate / 1000.0 / SamplesPerBin;
        for (int c = 0; c < columns; c++)
        {
            var t0 = startMs + (endMs - startMs) * c / columns;
            var t1 = startMs + (endMs - startMs) * (c + 1) / columns;
            var b0 = (int)Math.Floor(t0 * binsPerMs);
            var b1 = Math.Max(b0 + 1, (int)Math.Ceiling(t1 * binsPerMs));
            b0 = Math.Clamp(b0, 0, BinCount);
            b1 = Math.Clamp(b1, 0, BinCount);
            short lo = 0, hi = 0;
            for (int b = b0; b < b1; b++)
            {
                if (Min[b] < lo) lo = Min[b];
                if (Max[b] > hi) hi = Max[b];
            }
            colMin[c] = lo / 32768f;
            colMax[c] = hi / 32768f;
        }
    }
}
