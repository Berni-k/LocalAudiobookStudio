using Avalonia.Threading;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Devices;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Recording segments of speakers without a worker: one recording at a time, with the level and elapsed
/// time pushed to the row (VU meter and timer). Stopping saves the recording as a new take.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private AudioRecorder? _recorder;
    private ItemViewModel? _recordingItem;
    private int _recordingTake;
    private IDisposable? _recordTimer;

    public bool IsRecording => _recordingItem != null;

    /// <summary>The row's record button (and R): starts recording that segment, or stops if it is the one recording.</summary>
    public async void ToggleRecording(ItemViewModel item)
    {
        if (ReferenceEquals(_recordingItem, item))
        {
            await StopRecordingAsync();
            return;
        }
        await StopRecordingAsync(); // only one recording at a time
        StartRecording(item);
    }

    private void StartRecording(ItemViewModel item)
    {
        if (Session == null || !Session.IsRecorded(item.Model)) return;
        if (Session.RefuseIfLocked([item])) return;
        StopPlayback();
        var (take, path) = Session.NewRecordingPath(item);
        try
        {
            _recorder = AudioRecorder.Start(AppHost.Settings.AudioInputDevice, path);
        }
        catch (Exception ex)
        {
            AppHost.Log.LogWarning(ex, "Recording could not start");
            StatusText = "Recording could not start: " + ex.Message;
            try { File.Delete(path); } catch { }
            return;
        }
        _recordingItem = item;
        _recordingTake = take;
        item.IsRecording = true;
        item.RecordLevel = 0;
        item.RecordElapsedMs = 0;
        StatusText = "Recording… click the meter or the stop button (or press R) to finish.";
        _recordTimer = DispatcherTimer.Run(OnRecordTick, TimeSpan.FromMilliseconds(50));
        OnPropertyChanged(nameof(IsRecording));
    }

    private bool OnRecordTick()
    {
        if (_recorder is not { } r || _recordingItem is not { } item) return false;
        // Fast attack, slow release, like a VU meter.
        var level = r.TakeLevel();
        item.RecordLevel = Math.Max(level, item.RecordLevel * 0.85);
        item.RecordElapsedMs = r.ElapsedMs;
        if (r.Error != null)
        {
            StatusText = "Recording stopped: " + r.Error;
            _ = StopRecordingAsync();
            return false;
        }
        return true;
    }

    /// <summary>Stops the current recording (if any) and saves it as a take.</summary>
    public async Task StopRecordingAsync()
    {
        if (_recorder is not { } r || _recordingItem is not { } item || Session is not { } session) return;
        _recordTimer?.Dispose();
        _recordTimer = null;
        _recorder = null;
        _recordingItem = null;
        item.IsRecording = false;
        item.RecordLevel = 0;
        OnPropertyChanged(nameof(IsRecording));
        try
        {
            var ms = await r.StopAsync();
            await session.AddRecordedTakeAsync(item, _recordingTake, r.Path, ms);
        }
        catch (Exception ex)
        {
            AppHost.Log.LogError(ex, "Saving the recording failed");
            StatusText = "Saving the recording failed: " + ex.Message;
        }
    }
}
