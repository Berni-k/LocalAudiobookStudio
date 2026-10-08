using NAudio.Wave;

namespace LocalAudiobookStudio.Audio.Mixing;

/// <summary>
/// Mixes the timeline into stereo float at the project rate. For each read window it finds the
/// overlapping items (binary search), reads each one with trim, fades and gain applied, sums them,
/// and runs the master soft limiter. Playback and export both use this class, so what you hear
/// is exactly what gets exported. Seeking is instant because positions are computed.
/// </summary>
public sealed class TimelineMixer : ISampleProvider
{
    private readonly AudioBufferCache _cache;
    private readonly SoftLimiter _limiter;
    private readonly List<MixItem> _scratch = new();
    private readonly object _lock = new();
    private MixPlan _plan;
    private long _positionFrames;
    private long _endFrames;

    public TimelineMixer(MixPlan plan, AudioBufferCache cache, bool stopAtEnd = true)
    {
        _cache = cache;
        _plan = plan;
        SampleRate = cache.SampleRate;
        StopAtEnd = stopAtEnd;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        _limiter = new SoftLimiter(SampleRate);
        _endFrames = MsToFrames(plan.TotalMs);
    }

    public WaveFormat WaveFormat { get; }
    public int SampleRate { get; }

    /// <summary>When false, reads past the end return silence instead of ending the stream.</summary>
    public bool StopAtEnd { get; set; }

    /// <summary>Optional end point (ms); reading stops there (used for "play selected item only").</summary>
    public double? StopAtMs
    {
        get => _stopAtMs;
        set { lock (_lock) _stopAtMs = value; }
    }
    private double? _stopAtMs;

    public bool LimiterEnabled { get; set; } = true;

    /// <summary>How far ahead to decode audio in the background.</summary>
    public double PrefetchMs { get; set; } = 8000;

    public MixPlan Plan => _plan;

    /// <summary>Position of the next sample to be read (not what is audible; see PlaybackEngine).</summary>
    public long PositionFrames => Interlocked.Read(ref _positionFrames);

    public double PositionMs => PositionFrames * 1000.0 / SampleRate;

    /// <summary>Swaps in a new plan (after edits) without interrupting playback.</summary>
    public void SetPlan(MixPlan plan)
    {
        lock (_lock)
        {
            _plan = plan;
            _endFrames = MsToFrames(plan.TotalMs);
        }
    }

    public void Seek(double ms)
    {
        lock (_lock)
        {
            Interlocked.Exchange(ref _positionFrames, Math.Max(0, MsToFrames(ms)));
            _limiter.Reset();
        }
        Prefetch(ms);
    }

    /// <summary>Starts background decoding of items near <paramref name="fromMs"/>.</summary>
    public void Prefetch(double fromMs)
    {
        var list = new List<MixItem>();
        _plan.Overlapping(fromMs, fromMs + PrefetchMs, list);
        foreach (var it in list)
            if (!it.IsPlaceholder) _cache.Prefetch(it.Path);
    }

    private long MsToFrames(double ms) => (long)Math.Round(ms * SampleRate / 1000.0);

    public int Read(Span<float> buffer)
    {
        lock (_lock)
        {
            var frames = buffer.Length / 2;
            var start = _positionFrames;
            var end = _endFrames;
            if (_stopAtMs is double stop) end = Math.Min(end, MsToFrames(stop));
            if (StopAtEnd)
            {
                if (start >= end) return 0;
                frames = (int)Math.Min(frames, end - start);
            }
            var outp = buffer[..(frames * 2)];
            outp.Clear();

            var fromMs = start * 1000.0 / SampleRate;
            var toMs = (start + frames) * 1000.0 / SampleRate;
            _scratch.Clear();
            _plan.Overlapping(fromMs, toMs, _scratch);
            foreach (var item in _scratch) MixItemInto(item, start, outp);

            if (LimiterEnabled) _limiter.Process(outp);
            _positionFrames = start + frames;

            // Keep decoding ahead of the playhead roughly twice a second.
            if ((start / (SampleRate / 2)) != (_positionFrames / (SampleRate / 2))) Prefetch(toMs);
            return frames * 2;
        }
    }

    /// <summary>Level of placeholder static (peaks about −24 dBFS): clearly noticeable as a missing part, still below speech.</summary>
    private const float PlaceholderLevel = 0.065f;

    private void MixItemInto(MixItem item, long windowStart, Span<float> outp)
    {
        if (item.IsPlaceholder)
        {
            MixPlaceholderInto(item, windowStart, outp);
            return;
        }
        var buf = _cache.Get(item.Path);
        if (buf == null) return;
        var src = buf.Samples;
        var srcFrames = buf.Frames;

        var itemStart = MsToFrames(item.StartMs);
        var itemLen = MsToFrames(item.DurationMs);
        var trim = MsToFrames(item.TrimStartMs);
        var fadeIn = MsToFrames(item.FadeInMs);
        var fadeOut = MsToFrames(item.FadeOutMs);
        var gain = item.GainLinear;
        var frames = outp.Length / 2;
        var loop = item.LoopMs > 0 ? Math.Max(1, MsToFrames(item.LoopMs)) : 0;

        var first = Math.Max(0, itemStart - windowStart);
        var last = Math.Min(frames, itemStart + itemLen - windowStart);
        for (long f = first; f < last; f++)
        {
            var rel = windowStart + f - itemStart;   // frames into the (trimmed) item
            var s = trim + (loop > 0 ? rel % loop : rel); // frame in the source file
            if (s < 0 || s >= srcFrames) continue;
            var g = gain;
            if (fadeIn > 0 && rel < fadeIn) g *= (float)rel / fadeIn;
            var fromEnd = itemLen - rel;
            if (fadeOut > 0 && fromEnd < fadeOut) g *= (float)fromEnd / fadeOut;
            outp[(int)(f * 2)] += src[s * 2] * g;
            outp[(int)(f * 2 + 1)] += src[s * 2 + 1] * g;
        }
    }

    /// <summary>
    /// Soft static for an item without audio. Each sample is a hash of its timeline frame, so the
    /// noise needs no state and sounds the same after seeking; averaging three neighbours takes the
    /// harsh top off.
    /// </summary>
    private void MixPlaceholderInto(MixItem item, long windowStart, Span<float> outp)
    {
        var itemStart = MsToFrames(item.StartMs);
        var itemLen = MsToFrames(item.DurationMs);
        var fadeIn = MsToFrames(item.FadeInMs);
        var fadeOut = MsToFrames(item.FadeOutMs);
        var frames = outp.Length / 2;

        var first = Math.Max(0, itemStart - windowStart);
        var last = Math.Min(frames, itemStart + itemLen - windowStart);
        for (long f = first; f < last; f++)
        {
            var abs = windowStart + f;
            var rel = abs - itemStart;
            var g = PlaceholderLevel;
            if (fadeIn > 0 && rel < fadeIn) g *= (float)rel / fadeIn;
            var fromEnd = itemLen - rel;
            if (fadeOut > 0 && fromEnd < fadeOut) g *= (float)fromEnd / fadeOut;
            for (int ch = 0; ch < 2; ch++)
            {
                var n = (Noise(abs, ch) + Noise(abs - 1, ch) + Noise(abs - 2, ch)) * (1f / 3f);
                outp[(int)(f * 2 + ch)] += n * g;
            }
        }
    }

    /// <summary>Deterministic white noise in [−1, 1] for a frame and channel.</summary>
    private static float Noise(long frame, int channel)
    {
        unchecked
        {
            var x = (uint)(frame * 2 + channel) * 0x9E3779B1u;
            x ^= x >> 15;
            x *= 0x2C1B3C6Du;
            x ^= x >> 12;
            x *= 0x297A2D39u;
            x ^= x >> 15;
            return x * (2f / uint.MaxValue) - 1f;
        }
    }
}

/// <summary>
/// Master-bus peak limiter with instant attack and smooth release, stereo-linked. Output never
/// exceeds the ceiling (−1 dBFS), so overlapping items never clip.
/// </summary>
public sealed class SoftLimiter
{
    private readonly float _ceiling;
    private readonly float _release;
    private float _gain = 1f;

    public SoftLimiter(int sampleRate, double ceilingDb = -1.0, double releaseMs = 120.0)
    {
        _ceiling = (float)Math.Pow(10, ceilingDb / 20.0);
        _release = (float)(1.0 - Math.Exp(-1.0 / (releaseMs / 1000.0 * sampleRate)));
    }

    public void Reset() => _gain = 1f;

    public void Process(Span<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            var peak = Math.Max(Math.Abs(stereo[i]), Math.Abs(stereo[i + 1]));
            var target = peak > _ceiling ? _ceiling / peak : 1f;
            _gain = target < _gain ? target : _gain + (target - _gain) * _release;
            stereo[i] *= _gain;
            stereo[i + 1] *= _gain;
        }
    }
}
