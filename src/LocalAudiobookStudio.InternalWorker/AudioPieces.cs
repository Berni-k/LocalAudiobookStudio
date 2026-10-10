namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// The same audio handling as workers/common/audio.py: the 16 kHz mono WAV the app sends, cut into pieces at quiet
/// points, and 20 ms levels for telling speech from silence.
/// </summary>
internal static class AudioPieces
{
    public const int SampleRate = 16_000;
    private const int Hop = SampleRate / 50; // 20 ms frames

    /// <summary>The app's WAV (16-bit PCM, mono, 16 kHz) as float samples.</summary>
    public static float[] ReadWav(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
        int channels = 0, rate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
                if (format != 1) throw new InvalidDataException("expected PCM WAV");
            }
            else if (id == "data")
            {
                if (channels != 1 || bits != 16) throw new InvalidDataException("expected 16-bit mono WAV");
                if (rate != SampleRate) throw new InvalidDataException($"expected {SampleRate} Hz, got {rate} Hz");
                var bytes = reader.ReadBytes(size);
                var samples = new float[bytes.Length / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, 2 * i) / 32768f;
                return samples;
            }
            else
            {
                reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException("the WAV file has no audio");
    }

    /// <summary>
    /// (start, end) sample ranges of at most <paramref name="maxSec"/>, cut at the quietest 20 ms between
    /// <paramref name="minSec"/> and <paramref name="maxSec"/>, so each piece ends between words. No overlap.
    /// </summary>
    public static List<(int Start, int End)> QuietPieces(float[] audio, double minSec, double maxSec)
    {
        var pieces = new List<(int, int)>();
        var start = 0;
        while (audio.Length - start > maxSec * SampleRate)
        {
            var lo = start + (int)(minSec * SampleRate);
            var hi = start + (int)(maxSec * SampleRate);
            int quietest = lo;
            var least = double.MaxValue;
            for (var f = lo; f + Hop <= hi; f += Hop)
            {
                double energy = 0;
                for (var i = f; i < f + Hop; i++) energy += audio[i] * audio[i];
                if (energy < least) { least = energy; quietest = f; }
            }
            var cut = quietest + Hop / 2;
            pieces.Add((start, cut));
            start = cut;
        }
        pieces.Add((start, audio.Length));
        return pieces;
    }

    /// <summary>RMS of each 20 ms frame.</summary>
    public static float[] FrameLevels(float[] audio)
    {
        var levels = new float[Math.Max(1, audio.Length / Hop)];
        for (var f = 0; f < audio.Length / Hop; f++)
        {
            double sum = 0;
            for (var i = f * Hop; i < (f + 1) * Hop; i++) sum += audio[i] * audio[i];
            levels[f] = (float)Math.Sqrt(sum / Hop);
        }
        return levels;
    }

    /// <summary>The level below which only 5 % of the frames are louder: the take's speech level.</summary>
    public static float SpeechLevel(float[] levels)
    {
        var sorted = (float[])levels.Clone();
        Array.Sort(sorted);
        return sorted[(int)Math.Min(sorted.Length - 1, Math.Round(0.95 * (sorted.Length - 1)))];
    }

    /// <summary>
    /// Where speech that starts at <paramref name="startSec"/> stops: the first of 150 ms of frames about 30 dB below
    /// <paramref name="speech"/>, at the latest <paramref name="limitSec"/>. Low on purpose: at 16 kHz a word's final
    /// "s" or "z" keeps little of its energy (most of it is above 8 kHz), and it is still the word.
    /// </summary>
    public static double SpeechEnd(float[] levels, double startSec, double limitSec, float speech)
    {
        var first = Math.Max(0, (int)(startSec * 50));
        var last = Math.Min(levels.Length, (int)Math.Ceiling(limitSec * 50));
        var quiet = 0;
        for (var f = first; f < last; f++)
        {
            if (levels[f] >= 0.03f * speech) { quiet = 0; continue; }
            if (++quiet == 8) return Math.Max(startSec, (f - 7) / 50.0);
        }
        return limitSec;
    }

    /// <summary>The loudest 20 ms frame between two times.</summary>
    public static float PeakLevel(float[] levels, double startSec, double endSec)
    {
        var first = Math.Max(0, (int)(startSec * 50));
        var last = Math.Min(levels.Length, Math.Max((int)(endSec * 50) + 1, first + 1));
        float peak = 0;
        for (var f = first; f < last; f++) peak = Math.Max(peak, levels[f]);
        return peak;
    }
}
