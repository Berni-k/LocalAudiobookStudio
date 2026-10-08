using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>A chapter in the left panel; owns the item view models shown in the segment stack.</summary>
public sealed partial class ChapterViewModel : ObservableObject
{
    public ChapterViewModel(ProjectSession session, Chapter model)
    {
        Session = session;
        Model = model;
        Items = new ObservableCollection<ItemViewModel>(model.Items.Select(i => new ItemViewModel(session, this, i)));
        Items.CollectionChanged += (_, _) => RenumberSegments();
        RenumberSegments();
    }

    /// <summary>
    /// Numbers the segments with audio 1, 2, 3… in reading order (pauses, comments and unknown segments get none):
    /// the numbers a per-segment export names them by.
    /// </summary>
    private void RenumberSegments()
    {
        var n = 0;
        foreach (var item in Items) item.SegmentNumber = item.HasAudio ? ++n : null;
    }

    public ProjectSession Session { get; }
    public Chapter Model { get; }
    public Guid Id => Model.Id;

    /// <summary>Items in reading order. Always kept in sync with <see cref="Chapter.Items"/> via the methods below.</summary>
    public ObservableCollection<ItemViewModel> Items { get; }

    public string Title => Model.Title;

    [ObservableProperty] private int _number;
    [ObservableProperty] private double _startMs;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _durationIsEstimate;
    [ObservableProperty] private int _finishedCount;
    [ObservableProperty] private int _staleCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isEditingTitle;

    public string DurationText => TimeFormat.Hms(DurationMs, DurationIsEstimate);
    public bool HasStale => StaleCount > 0;
    public bool HasFailed => FailedCount > 0;
    public string BadgeTooltip =>
        $"Duration: {DurationText}\n{FinishedCount} finished\n{StaleCount} stale\n{FailedCount} failed\n{MissingCount} not generated";

    partial void OnDurationMsChanged(double value) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(BadgeTooltip)); }
    partial void OnDurationIsEstimateChanged(bool value) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(BadgeTooltip)); }
    partial void OnFinishedCountChanged(int value) => OnPropertyChanged(nameof(BadgeTooltip));
    partial void OnStaleCountChanged(int value) { OnPropertyChanged(nameof(HasStale)); OnPropertyChanged(nameof(BadgeTooltip)); }
    partial void OnFailedCountChanged(int value) { OnPropertyChanged(nameof(HasFailed)); OnPropertyChanged(nameof(BadgeTooltip)); }
    partial void OnMissingCountChanged(int value) => OnPropertyChanged(nameof(BadgeTooltip));

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));

    public void RecountStatus()
    {
        int finished = 0, stale = 0, failed = 0, missing = 0;
        foreach (var i in Items)
        {
            switch (i.Status)
            {
                case ItemStatus.Ready: finished++; break;
                case ItemStatus.Stale: stale++; break;
                case ItemStatus.Failed: failed++; break;
                case ItemStatus.NotGenerated: missing++; break;
            }
        }
        FinishedCount = finished;
        StaleCount = stale;
        FailedCount = failed;
        MissingCount = missing;
    }

    // ------------------------------------------------------------------ structure (model + VM in sync)

    public void InsertItem(int index, ItemViewModel vm)
    {
        index = Math.Clamp(index, 0, Items.Count);
        vm.Chapter = this;
        Model.Items.Insert(index, vm.Model);
        Items.Insert(index, vm);
    }

    public int RemoveItem(ItemViewModel vm)
    {
        var index = Items.IndexOf(vm);
        if (index < 0) return -1;
        Items.RemoveAt(index);
        Model.Items.RemoveAt(index);
        return index;
    }

    public void MoveItem(int from, int to)
    {
        if (from == to) return;
        var m = Model.Items[from];
        Model.Items.RemoveAt(from);
        Model.Items.Insert(to, m);
        Items.Move(from, to);
    }

    public override string ToString() => Title;
}
