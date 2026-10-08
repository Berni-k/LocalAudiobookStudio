using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.Alsa;

namespace LocalAudiobookStudio.App.Devices;

/// <summary>
/// Records the microphone to a mono 24-bit WAV (WASAPI shared mode on Windows, ALSA on Linux).
/// The capture runs on its own thread; <see cref="Level"/> and <see cref="ElapsedMs"/> can be polled
/// from the UI for a VU meter and a timer.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _inFormat;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private float[] _mono = [];
    private long _frames;
    private float _peak;
    private bool _disposed;

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: WASAPI usually captures 32-bit float in a WAVEFORMATEXTENSIBLE.</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private AudioRecorder(IWaveIn capture, string path)
    {
        _capture = capture;
        _inFormat = capture.WaveFormat;
        Path = path;
        _writer = new WaveFileWriter(path, new WaveFormat(_inFormat.SampleRate, 24, 1));
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Error = e.Exception.Message;
            _stopped.TrySetResult();
        };
    }

    /// <summary>The WAV being written.</summary>
    public string Path { get; }
    public int SampleRate => _inFormat.SampleRate;

    /// <summary>Set when the device failed while recording.</summary>
    public string? Error { get; private set; }

    public double ElapsedMs
    {
        get { lock (_lock) return _frames * 1000.0 / Math.Max(1, _inFormat.SampleRate); }
    }

    /// <summary>Peak level (0–1) since the last read; reading resets it, so poll it at the meter's rate.</summary>
    public float TakeLevel()
    {
        lock (_lock)
        {
            var p = _peak;
            _peak = 0;
            return p;
        }
    }

    /// <summary>Opens the input device and starts writing to <paramref name="path"/>.</summary>
    /// <exception cref="InvalidOperationException">No usable input device.</exception>
    public static AudioRecorder Start(string? deviceId, string path)
    {
        IWaveIn capture;
        if (OperatingSystem.IsWindows()) capture = CreateWasapi(deviceId);
        else if (OperatingSystem.IsLinux()) capture = CreateAlsa(deviceId);
        else throw new PlatformNotSupportedException("Recording is supported on Windows and Linux.");

        var r = new AudioRecorder(capture, path);
        try
        {
            capture.StartRecording();
        }
        catch
        {
            r.Dispose();
            throw;
        }
        return r;
    }

    // WasapiCapture is marked obsolete in NAudio 3 in favour of WasapiRecorder, but it shares IWaveIn with
    // AlsaIn (one code path for both platforms) and is the tested path, so it stays.
#pragma warning disable CS0618
    [SupportedOSPlatform("windows")]
    private static IWaveIn CreateWasapi(string? deviceId)
    {
        MMDevice? device = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                device = en.GetDevice(deviceId);
            }
            catch
            {
                // Unplugged or renamed: use the default microphone.
            }
        }
        try
        {
            device ??= WasapiCapture.GetDefaultCaptureDevice();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("No microphone found. Connect one, or pick an input device in Settings.", ex);
        }
        return new WasapiCapture(device);
    }
#pragma warning restore CS0618

    [SupportedOSPlatform("linux")]
    private static IWaveIn CreateAlsa(string? deviceId) =>
        string.IsNullOrEmpty(deviceId) ? new AlsaIn() : new AlsaIn(deviceId);

    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ListWasapi();
            if (OperatingSystem.IsLinux()) return ListAlsa();
        }
        catch
        {
            // No audio system: no devices.
        }
        return [];
    }

    [SupportedOSPlatform("linux")]
    private static IReadOnlyList<AudioDeviceInfo> ListAlsa() =>
        AlsaDeviceEnumerator.GetCaptureDevices()
            .Select(d => new AudioDeviceInfo(d.Name, string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description.Split('\n')[0]))
            .ToList();

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AudioDeviceInfo> ListWasapi()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName)).ToList();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var frames = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded));
        if (frames == 0) return;
        var peak = 0f;
        for (int i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(_mono[i]));
        lock (_lock)
        {
            if (_disposed) return;
            _writer.WriteSamples(_mono, 0, frames);
            _frames += frames;
            _peak = Math.Max(_peak, peak);
        }
    }

    /// <summary>Converts a captured buffer (float or 16/24/32-bit PCM, any channel count) to mono floats in <see cref="_mono"/>.</summary>
    private int ToMono(ReadOnlySpan<byte> data)
    {
        var ch = Math.Max(1, _inFormat.Channels);
        var bytesPerSample = _inFormat.BitsPerSample / 8;
        if (bytesPerSample == 0) return 0;
        var frames = data.Length / (bytesPerSample * ch);
        if (_mono.Length < frames) _mono = new float[frames];
        var isFloat = _inFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (_inFormat is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        for (int f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (int c = 0; c < ch; c++)
            {
                var o = (f * ch + c) * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(data.Slice(o, 4)),
                    4 => BitConverter.ToInt32(data.Slice(o, 4)) / 2147483648f,
                    3 => ((data[o] << 8) | (data[o + 1] << 16) | (data[o + 2] << 24)) / 2147483648f,
                    2 => BitConverter.ToInt16(data.Slice(o, 2)) / 32768f,
                    _ => (data[o] - 128) / 128f,
                };
            }
            _mono[f] = sum / ch;
        }
        return frames;
    }

    /// <summary>Stops capturing and closes the WAV. Returns the recorded length in ms.</summary>
    public async Task<double> StopAsync()
    {
        try { _capture.StopRecording(); } catch { _stopped.TrySetResult(); }
        await Task.WhenAny(_stopped.Task, Task.Delay(3000)).ConfigureAwait(false);
        var ms = ElapsedMs;
        Dispose();
        return ms;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
        try { _capture.Dispose(); } catch { }
    }
}
