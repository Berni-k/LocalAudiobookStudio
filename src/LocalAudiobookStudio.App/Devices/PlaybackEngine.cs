using LocalAudiobookStudio.Audio.Mixing;
using NAudio.Wave;

namespace LocalAudiobookStudio.App.Devices;

/// <summary>
/// Drives playback of a <see cref="TimelineMixer"/> through the platform output. The device stays
/// open while playing; when the mixer runs out, silence is fed and <see cref="Ended"/> is raised.
/// The audible position is derived from samples read minus the output latency, so the playhead
/// stays in sync with what is heard.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly AudioBufferCache _cache;
    private readonly Func<IAudioOutput> _outputFactory;
    private IAudioOutput? _output;
    private SwitchingProvider? _switch;
    private TimelineMixer? _mixer;
    private double _anchorMs;        // position at the last seek/play (the playhead never shows earlier)
    private double _pausedAtMs;
    private float _volume = 0.9f;
    private bool _endedRaised;

    public PlaybackEngine(AudioBufferCache cache, Func<IAudioOutput>? outputFactory = null)
    {
        _cache = cache;
        _outputFactory = outputFactory ?? (() => AudioOutputFactory.Create(DeviceId));
    }

    /// <summary>Output device id from settings (null = system default). Takes effect on the next play.</summary>
    public string? DeviceId { get; set; }

    public int SampleRate => _cache.SampleRate;
    public bool IsPlaying { get; private set; }

    /// <summary>What kind of playback is running (timeline, single item, scrub grain).</summary>
    public PlaybackMode Mode { get; private set; }

    /// <summary>Raised on an audio thread when the timeline (or the selected item) finished.</summary>
    public event Action? Ended;

    /// <summary>Raised when the output device fails.</summary>
    public event Action<Exception>? DeviceError;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_switch != null) _switch.Volume = _volume;
        }
    }

    public MixPlan? CurrentPlan => _mixer?.Plan;

    /// <summary>The audible position in ms.</summary>
    public double PositionMs
    {
        get
        {
            lock (_lock)
            {
                if (_mixer == null) return _pausedAtMs;
                if (!IsPlaying) return _pausedAtMs;
                var latency = _output?.Latency.TotalMilliseconds ?? 0;
                var pos = _mixer.PositionMs - latency;
                if (_switch is { SourceEnded: true }) pos = Math.Min(pos, _mixer.PositionMs);
                return Math.Max(_anchorMs, pos);
            }
        }
    }

    /// <summary>Plays <paramref name="plan"/> from <paramref name="fromMs"/>, optionally stopping at <paramref name="stopAtMs"/>.</summary>
    public void Play(MixPlan plan, double fromMs, double? stopAtMs = null, PlaybackMode mode = PlaybackMode.Timeline)
    {
        lock (_lock)
        {
            EnsureOutput();
            _mixer = new TimelineMixer(plan, _cache) { StopAtMs = stopAtMs };
            _mixer.Seek(fromMs);
            _anchorMs = fromMs;
            _pausedAtMs = fromMs;
            _endedRaised = false;
            Mode = mode;
            _switch!.SetSource(_mixer);
            if (_output!.State != PlaybackState.Playing) _output.Play();
            IsPlaying = true;
        }
    }

    /// <summary>Replaces the plan while playing (after an edit), keeping the position.</summary>
    public void UpdatePlan(MixPlan plan)
    {
        lock (_lock)
        {
            if (_mixer != null && Mode == PlaybackMode.Timeline) _mixer.SetPlan(plan);
        }
    }

    /// <summary>Changes where the current playback stops (null = end of the plan), without interrupting it.</summary>
    public void SetStopAt(double? ms)
    {
        lock (_lock)
        {
            if (_mixer == null) return;
            _mixer.StopAtMs = ms;
            _switch?.ResetEnded();
            _endedRaised = false;
        }
    }

    public void Seek(double ms)
    {
        lock (_lock)
        {
            _anchorMs = ms;
            _pausedAtMs = ms;
            _endedRaised = false;
            if (_mixer != null)
            {
                _mixer.Seek(ms);
                _switch?.ResetEnded();
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!IsPlaying) return;
            var pos = PositionMs;
            IsPlaying = false;
            _pausedAtMs = pos;
            _anchorMs = pos;
            _output?.Pause();
            // Rewind the mixer to what was actually heard, so resume continues from there.
            _mixer?.Seek(pos);
            _switch?.ResetEnded();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (IsPlaying || _mixer == null || _output == null) return;
            _endedRaised = false;
            _output.Play();
            IsPlaying = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsPlaying = false;
            _output?.Pause();
            _switch?.SetSource(null);
            _mixer = null;
            Mode = PlaybackMode.None;
        }
    }

    /// <summary>Audible scrubbing: plays a short grain of the plan at <paramref name="ms"/>.</summary>
    public void ScrubGrain(MixPlan plan, double ms, double grainMs = 70)
    {
        lock (_lock)
        {
            if (Mode == PlaybackMode.Scrub && _mixer != null && ReferenceEquals(_mixer.Plan, plan))
            {
                _mixer.StopAtMs = ms + grainMs;
                _mixer.Seek(ms);
                _switch!.ResetEnded();
                _endedRaised = true; // grains never raise Ended
                if (_output!.State != PlaybackState.Playing) _output.Play();
                IsPlaying = true;
                _anchorMs = ms;
                return;
            }
        }
        Play(plan, ms, ms + grainMs, PlaybackMode.Scrub);
        lock (_lock) _endedRaised = true;
    }

    private void EnsureOutput()
    {
        if (_output != null) return;
        _switch = new SwitchingProvider(SampleRate) { Volume = _volume };
        _switch.SourceFinished += OnSourceFinished;
        try
        {
            _output = _outputFactory();
            _output.Init(_switch);
            _output.Stopped += (_, e) =>
            {
                if (e.Exception != null) DeviceError?.Invoke(e.Exception);
            };
        }
        catch (Exception ex)
        {
            _output = null;
            DeviceError?.Invoke(ex);
            throw;
        }
    }

    private void OnSourceFinished()
    {
        bool raise;
        lock (_lock)
        {
            raise = !_endedRaised;
            _endedRaised = true;
        }
        if (!raise) return;
        // Let the tail (latency) play out, then report the end.
        var latency = _output?.Latency ?? TimeSpan.Zero;
        _ = Task.Delay(latency).ContinueWith(_ =>
        {
            lock (_lock)
            {
                if (_switch is not { SourceEnded: true }) return;
                _pausedAtMs = _mixer?.PositionMs ?? _pausedAtMs;
                IsPlaying = false;
                _output?.Pause();
            }
            Ended?.Invoke();
        });
    }

    /// <summary>Closes the device (for example after the output device setting changed).</summary>
    public void ResetDevice()
    {
        lock (_lock)
        {
            IsPlaying = false;
            _output?.Dispose();
            _output = null;
            _switch = null;
            _mixer = null;
        }
    }

    public void Dispose() => ResetDevice();
}

public enum PlaybackMode
{
    None,
    Timeline,
    /// <summary>The timeline mix from a point to the end of one segment (waveform click).</summary>
    Section,
    SingleItem,
    Scrub,
    Preview,
}

/// <summary>
/// Feeds the current source to the device and pads with silence when it ends, so the device
/// doesn't stop (which would add start-up latency to the next play).
/// </summary>
internal sealed class SwitchingProvider(int sampleRate) : ISampleProvider
{
    private readonly object _lock = new();
    private ISampleProvider? _source;
    private bool _ended;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    public float Volume { get; set; } = 1f;
    public bool SourceEnded { get { lock (_lock) return _ended || _source == null; } }
    public event Action? SourceFinished;

    public void SetSource(ISampleProvider? source)
    {
        lock (_lock)
        {
            _source = source;
            _ended = false;
        }
    }

    public void ResetEnded()
    {
        lock (_lock) _ended = false;
    }

    public int Read(Span<float> buffer)
    {
        int n = 0;
        bool finished = false;
        lock (_lock)
        {
            if (_source != null && !_ended)
            {
                n = _source.Read(buffer);
                if (n < buffer.Length)
                {
                    _ended = true;
                    finished = true;
                }
            }
        }
        if (n < buffer.Length) buffer[n..].Clear();
        var v = Volume;
        if (v != 1f)
            for (int i = 0; i < n; i++) buffer[i] *= v;
        if (finished) SourceFinished?.Invoke();
        return buffer.Length;
    }
}
