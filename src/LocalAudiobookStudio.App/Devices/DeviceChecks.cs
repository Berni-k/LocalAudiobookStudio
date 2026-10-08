namespace LocalAudiobookStudio.App.Devices;

/// <summary>The system check's audio entry (it needs the device code, which is the app's).</summary>
public static class DeviceChecks
{
    /// <summary>
    /// Playback and recording devices. On Linux they go through ALSA (NAudio.Alsa), which needs libasound.so.2:
    /// without it nothing plays, so that is a red X.
    /// </summary>
    public static SystemCheck Audio()
    {
        if (OperatingSystem.IsLinux() && !System.Runtime.InteropServices.NativeLibrary.TryLoad("libasound.so.2", out _))
            return new("Audio", CheckStatus.Missing,
                "ALSA (libasound.so.2) not found: no playback or recording. Install it with your package manager (libasound2 / alsa-lib).");
        try
        {
            var outputs = AudioOutputFactory.ListDevices();
            var inputs = AudioRecorder.ListDevices();
            var mic = inputs.Count > 0 ? $"{inputs.Count} microphone(s)" : "no microphone (only needed for recording)";
            return outputs.Count > 0
                ? new("Audio", CheckStatus.Ok, $"{outputs.Count} output device(s), {mic}")
                : new("Audio", CheckStatus.Warning, $"No output device: playback won't work until one is connected. {char.ToUpper(mic[0])}{mic[1..]}.");
        }
        catch (Exception ex)
        {
            return new("Audio", CheckStatus.Warning, "Couldn't list audio devices: " + ex.Message);
        }
    }
}
