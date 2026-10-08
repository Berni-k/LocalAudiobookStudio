using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.Alsa;

namespace LocalAudiobookStudio.App.Devices;

public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Platform audio output: WASAPI shared mode on Windows, ALSA on Linux.</summary>
public interface IAudioOutput : IDisposable
{
    void Init(ISampleProvider source);
    void Play();
    void Pause();
    void Stop();
    float Volume { get; set; }
    PlaybackState State { get; }

    /// <summary>Time between a sample being read from the source and it being heard.</summary>
    TimeSpan Latency { get; }

    /// <summary>Raised when playback stops (end of stream or device error).</summary>
    event EventHandler<StoppedEventArgs>? Stopped;
}

public static class AudioOutputFactory
{
    public const int LatencyMs = 100;

    public static IAudioOutput Create(string? deviceId)
    {
        if (OperatingSystem.IsWindows()) return new WasapiAudioOutput(deviceId);
        if (OperatingSystem.IsLinux()) return new AlsaAudioOutput(deviceId);
        throw new PlatformNotSupportedException("Audio output is supported on Windows and Linux.");
    }

    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WasapiAudioOutput.ListDevices();
            if (OperatingSystem.IsLinux()) return AlsaAudioOutput.ListDevices();
            return [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Common wrapper around an NAudio <see cref="IWavePlayer"/>.</summary>
public abstract class WavePlayerOutput : IAudioOutput
{
    protected IWavePlayer Player { get; }

    protected WavePlayerOutput(IWavePlayer player)
    {
        Player = player;
        Player.PlaybackStopped += (_, e) => Stopped?.Invoke(this, e);
    }

    public event EventHandler<StoppedEventArgs>? Stopped;

    public void Init(ISampleProvider source) => Player.Init(source);
    public void Play() => Player.Play();
    public void Pause() => Player.Pause();
    public void Stop() => Player.Stop();
    public PlaybackState State => Player.PlaybackState;

    /// <summary>Software volume is applied by the playback engine; this is kept for completeness.</summary>
    public float Volume { get; set; } = 1f;

    public TimeSpan Latency
    {
        get
        {
            if (Player is IWaveLatency l)
            {
                try { return State == PlaybackState.Playing ? l.CurrentLatency : l.AverageLatency; }
                catch { /* fall through */ }
            }
            return TimeSpan.FromMilliseconds(AudioOutputFactory.LatencyMs);
        }
    }

    public void Dispose()
    {
        try { Player.Stop(); } catch { }
        Player.Dispose();
        GC.SuppressFinalize(this);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioOutput : WavePlayerOutput
{
    public WasapiAudioOutput(string? deviceId) : base(CreatePlayer(deviceId)) { }

    private static IWavePlayer CreatePlayer(string? deviceId)
    {
        var builder = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(AudioOutputFactory.LatencyMs);
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                builder = builder.WithDevice(en.GetDevice(deviceId));
            }
            catch
            {
                // Device unplugged or renamed: fall back to the default device.
            }
        }
        return builder.Build();
    }

    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName)).ToList();
    }
}

[SupportedOSPlatform("linux")]
public sealed class AlsaAudioOutput : WavePlayerOutput
{
    public AlsaAudioOutput(string? deviceId)
        : base(string.IsNullOrEmpty(deviceId) ? new AlsaOut() : new AlsaOut(deviceId)) { }

    public static IReadOnlyList<AudioDeviceInfo> ListDevices() =>
        AlsaDeviceEnumerator.GetPlaybackDevices()
            .Select(d => new AudioDeviceInfo(d.Name, string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description.Split('\n')[0]))
            .ToList();
}
