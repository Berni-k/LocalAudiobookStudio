using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>A speaker as shown in chips, menus and combo boxes. Wraps the <see cref="Speaker"/> model.</summary>
public sealed partial class SpeakerViewModel : ObservableObject
{
    public SpeakerViewModel(Speaker model) => Model = model;

    public Speaker Model { get; }
    public string Id => Model.Id;
    public string Name => string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.Id : Model.DisplayName;

    /// <summary>1-based position used for the 1–9 shortcuts.</summary>
    [ObservableProperty] private int _position;

    public IBrush ColorBrush => new SolidColorBrush(ParseColor(Model.Color));

    public string WorkerName => string.IsNullOrEmpty(Model.Worker) ? "Recorded" : AppHost.Catalog?.Get(Model.Worker)?.DisplayName ?? Model.Worker;

    /// <summary>No worker: this speaker's segments are recorded with the microphone instead of generated.</summary>
    public bool IsRecorded => string.IsNullOrEmpty(Model.Worker);

    public string ShortcutText => Position is >= 1 and <= 9 ? Position.ToString() : "";

    /// <summary>Call after the model changed (name, color, worker).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(WorkerName));
    }

    partial void OnPositionChanged(int value) => OnPropertyChanged(nameof(ShortcutText));

    public static Color ParseColor(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Color.TryParse(s, out var c) ? c : Color.Parse("#6FA8DC");

    public override string ToString() => Name;
}
