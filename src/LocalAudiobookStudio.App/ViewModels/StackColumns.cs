using CommunityToolkit.Mvvm.ComponentModel;
using LocalAudiobookStudio.App.Services;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Widths of the segment stack's side columns, shared by the column header and every row so they
/// line up (the text column takes whatever is left), and what the rows show. Stored in the UI settings.
/// </summary>
public sealed partial class StackColumns : ObservableObject
{
    public const double MinSpeaker = 64, MaxSpeaker = 260;
    public const double MinAudio = 200, MaxAudio = 900;

    public static StackColumns Shared { get; } = new();

    private StackColumns()
    {
        var ui = AppHost.Settings.Ui;
        _speakerWidth = Math.Clamp(ui.StackSpeakerColumnWidth, MinSpeaker, MaxSpeaker);
        _audioWidth = Math.Clamp(ui.StackAudioColumnWidth, MinAudio, MaxAudio);
        _showNumbers = ui.ShowSegmentNumbers;
        _showTimes = ui.ShowSegmentTimes;
    }

    /// <summary>Rows show their segment number (Settings → Text).</summary>
    [ObservableProperty] private bool _showNumbers;

    /// <summary>Rows show their length and end time (Settings → Text).</summary>
    [ObservableProperty] private bool _showTimes;

    [ObservableProperty] private double _speakerWidth;
    [ObservableProperty] private double _audioWidth;

    partial void OnSpeakerWidthChanged(double value) => AppHost.Settings.Ui.StackSpeakerColumnWidth = value;
    partial void OnAudioWidthChanged(double value) => AppHost.Settings.Ui.StackAudioColumnWidth = value;
}
