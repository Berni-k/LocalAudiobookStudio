using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalAudiobookStudio.Audio.Decoding;

/// <summary>Decoded audio held in memory as interleaved float samples.</summary>
public sealed class DecodedAudio(float[] samples, int sampleRate, int channels)
{
    public float[] Samples { get; } = samples;
    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public int Frames => Samples.Length / Math.Max(1, Channels);
    public double DurationMs => Frames * 1000.0 / SampleRate;
    public long ByteSize => Samples.LongLength * sizeof(float);
}

public sealed record WavInfo(int SampleRate, int Channels, long Frames)
{
    public double DurationMs => Frames * 1000.0 / Math.Max(1, SampleRate);
}

/// <summary>
/// Decoding (WAV, Opus, MP3), channel conversion and resampling. Other formats are converted with ffmpeg when they
/// are imported.
/// </summary>
public static class AudioDecoder
{
    /// <summary>The formats decoded here, so they can be kept as they are in a project.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".wav", OggOpus.Extension, ".mp3"];

    public static bool CanDecode(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Rate, channels and length. WAV reads the header; compressed files are decoded.</summary>
    public static WavInfo ReadInfo(string path)
    {
        if (!IsWav(path))
        {
            var buf = Decode(path);
            return new WavInfo(buf.SampleRate, buf.Channels, buf.Frames);
        }
        using var r = new WaveFileReader(path);
        return new WavInfo(r.WaveFormat.SampleRate, r.WaveFormat.Channels, r.SampleCount);
    }

    private static bool IsWav(string path) => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                                             || Path.GetExtension(path).Equals(".partial", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes a file at its native rate and channel count: WAV (PCM 8/16/24/32-bit or IEEE float), Opus
    /// (always 48 kHz) or MP3.
    /// </summary>
    public static DecodedAudio Decode(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        OggOpus.Extension => OggOpus.Read(path),
        ".mp3" => DecodeMp3(path),
        _ => DecodeWav(path),
    };

    /// <summary>MP3 with NLayer. Its encoder delay (about 25 ms of silence at the start) stays: it doesn't matter for clips.</summary>
    private static DecodedAudio DecodeMp3(string path)
    {
        using var mp3 = new NLayer.MpegFile(path);
        var channels = mp3.Channels;
        var output = new List<float>(mp3.Length > 0 ? (int)Math.Min(mp3.Length / sizeof(float), int.MaxValue) : 1 << 20);
        var buffer = new float[16384 * channels];
        int n;
        while ((n = mp3.ReadSamples(buffer, 0, buffer.Length)) > 0) output.AddRange(buffer.AsSpan(0, n));
        if (output.Count == 0) throw new InvalidDataException("The MP3 file has no audio.");
        return new DecodedAudio([.. output], mp3.SampleRate, channels);
    }

    private static DecodedAudio DecodeWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var total = checked((int)(reader.SampleCount * channels));
        var samples = new float[total];
        var offset = 0;
        while (offset < total)
        {
            var n = provider.Read(samples.AsSpan(offset, Math.Min(65536, total - offset)));
            if (n <= 0) break;
            offset += n;
        }
        if (offset < total) Array.Resize(ref samples, offset);
        return new DecodedAudio(samples, provider.WaveFormat.SampleRate, channels);
    }

    /// <summary>Decodes and converts to interleaved stereo at <paramref name="targetRate"/>.</summary>
    public static DecodedAudio DecodeStereo(string path, int targetRate)
    {
        var buf = Decode(path);
        var stereo = ToStereo(buf);
        return stereo.SampleRate == targetRate ? stereo : Resample(stereo, targetRate);
    }

    /// <summary>Mono is duplicated to both channels; more than two channels keep the first two.</summary>
    public static DecodedAudio ToStereo(DecodedAudio buf)
    {
        if (buf.Channels == 2) return buf;
        var frames = buf.Frames;
        var outp = new float[frames * 2];
        var ch = buf.Channels;
        var src = buf.Samples;
        for (int f = 0; f < frames; f++)
        {
            var l = src[f * ch];
            var r = ch > 1 ? src[f * ch + 1] : l;
            outp[f * 2] = l;
            outp[f * 2 + 1] = r;
        }
        return new DecodedAudio(outp, buf.SampleRate, 2);
    }

    /// <summary>Mixes all channels down to mono (average).</summary>
    public static float[] ToMono(DecodedAudio buf)
    {
        if (buf.Channels == 1) return buf.Samples;
        var frames = buf.Frames;
        var ch = buf.Channels;
        var outp = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += buf.Samples[f * ch + c];
            outp[f] = sum / ch;
        }
        return outp;
    }

    /// <summary>Writes audio as a 16-bit PCM WAV (exported takes, reference clips for the workers).</summary>
    public static void WriteWav(DecodedAudio audio, string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(audio.SampleRate, 16, audio.Channels));
        var pcm = new byte[audio.Samples.Length * 2];
        for (var i = 0; i < audio.Samples.Length; i++)
        {
            var s = (short)Math.Round(Math.Clamp(audio.Samples[i], -1f, 1f) * 32767f);
            pcm[2 * i] = (byte)s;
            pcm[2 * i + 1] = (byte)(s >> 8);
        }
        writer.Write(pcm, 0, pcm.Length);
    }

    public static DecodedAudio Resample(DecodedAudio buf, int targetRate)
    {
        var source = new ArraySampleProvider(buf.Samples, buf.SampleRate, buf.Channels);
        var resampler = new WdlResamplingSampleProvider(source, targetRate);
        var expectedFrames = (long)Math.Ceiling(buf.Frames * (double)targetRate / buf.SampleRate);
        var outp = new float[(expectedFrames + 1024) * buf.Channels];
        var offset = 0;
        while (true)
        {
            if (offset + 8192 > outp.Length) Array.Resize(ref outp, outp.Length + 65536);
            var n = resampler.Read(outp.AsSpan(offset, 8192));
            if (n <= 0) break;
            offset += n;
        }
        Array.Resize(ref outp, Math.Min(offset, (int)(expectedFrames * buf.Channels)));
        return new DecodedAudio(outp, targetRate, buf.Channels);
    }
}

/// <summary>An <see cref="ISampleProvider"/> reading from an in-memory array.</summary>
public sealed class ArraySampleProvider(float[] samples, int sampleRate, int channels) : ISampleProvider
{
    private int _pos;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    public int Read(Span<float> buffer)
    {
        var n = Math.Min(buffer.Length, samples.Length - _pos);
        if (n <= 0) return 0;
        samples.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }
}
