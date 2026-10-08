using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>Selection, batch operations on the selection, inserting items, and search/filter.</summary>
public sealed partial class MainWindowViewModel
{
    // ================================================================== selection

    /// <summary>The selected items in document order (set by the view from the ListBox).</summary>
    public IReadOnlyList<ItemViewModel> SelectedItems { get; private set; } = [];

    public bool HasSelection => SelectedItems.Count > 0;
    public bool HasSingleSelection => SelectedItems.Count == 1;
    public ItemViewModel? SelectedItem => SelectedItems.Count > 0 ? SelectedItems[0] : null;

    public void UpdateSelection(IEnumerable<ItemViewModel> items)
    {
        var list = items.ToList();
        if (Session != null)
            list = list.OrderBy(i => Session.ChapterIndex(i.Chapter)).ThenBy(i => i.Chapter.Items.IndexOf(i)).ToList();
        if (list.SequenceEqual(SelectedItems)) return;
        SelectedItems = list;
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(SelectedItem));
        Inspector.SetSelection(list);
        NotifySelectionCommands();
    }

    private void NotifySelectionCommands()
    {
        foreach (var c in new IRelayCommand[]
                 {
                     RegenerateCommand, DeleteCommand, CopyTextCommand, ToggleLockCommand, LockCommand, UnlockCommand,
                     MergeCommand, ResetGainCommand, SetGainCommand, ToggleBackgroundCommand, PlaySelectedOnlyCommand,
                     EditTextCommand, PreviousTakeCommand, NextTakeCommand, InsertTextBelowCommand, InsertTextAboveCommand, InsertSpeakerBelowCommand,
                     InsertPauseBelowCommand, InsertPauseAboveCommand, InsertCommentBelowCommand, InsertCommentAboveCommand, InsertOverlapBelowCommand, InsertOverlapAboveCommand,                     InsertClipBelowCommand, InsertClipAboveCommand, AssignSpeakerCommand, NewSpeakerForSelectionCommand,
                     SelectAllCommand,
                 })
            c.NotifyCanExecuteChanged();
    }

    private bool CanActOnSelection() => Session != null && SelectedItems.Count > 0;

    public void SelectAndReveal(ItemViewModel item)
    {
        if (!ReferenceEquals(item.Chapter, SelectedChapter) && !IsFiltering) SelectedChapter = item.Chapter;
        SelectItemsRequested?.Invoke([item]);
        ScrollIntoViewRequested?.Invoke(item);
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private void SelectAll()
    {
        if (StackItems != null) SelectItemsRequested?.Invoke(StackItems.ToList());
    }

    /// <summary>Up/Down: select the previous or next item in the stack.</summary>
    public void MoveSelection(int delta, bool extend)
    {
        var items = StackItems;
        if (items == null || items.Count == 0) return;
        var anchor = SelectedItems.Count == 0 ? -1 : items.IndexOf(delta < 0 ? SelectedItems[0] : SelectedItems[^1]);
        var next = Math.Clamp(anchor + delta, 0, items.Count - 1);
        if (anchor < 0) next = delta > 0 ? 0 : items.Count - 1;
        var target = items[next];
        var list = extend ? SelectedItems.Append(target).Distinct().ToList() : [target];
        SelectItemsRequested?.Invoke(list);
        ScrollIntoViewRequested?.Invoke(target);
    }

    // ================================================================== edits on the selection

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session?.Undo.Undo();
    private bool CanUndo() => Session?.Undo.CanUndo == true;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session?.Undo.Redo();
    private bool CanRedo() => Session?.Undo.CanRedo == true;

    public string UndoText => Session?.Undo.UndoDescription is { } d ? $"Undo {d}" : "Undo";
    public string RedoText => Session?.Undo.RedoDescription is { } d ? $"Redo {d}" : "Redo";

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Delete()
    {
        if (Session == null || SelectedItems.Count == 0) return;
        var items = SelectedItems.ToList();
        var stack = StackItems;
        var nextIdx = stack != null ? stack.IndexOf(items[^1]) + 1 : -1;
        var next = stack != null && nextIdx > 0 && nextIdx < stack.Count ? stack[nextIdx] : null;
        Session.DeleteItems(items);
        StatusText = items.Count == 1 ? "Deleted 1 item (Ctrl+Z to undo)" : $"Deleted {items.Count} items (Ctrl+Z to undo)";
        if (next != null) SelectItemsRequested?.Invoke([next]);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task CopyText()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, SelectedItems.Select(i => i.DisplayText));
        var clipboard = Dialogs.Owner?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);
        StatusText = "Copied text of " + SelectedItems.Count + " item(s)";
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void ToggleLock() => Session?.ToggleLock(SelectedItems);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Lock() => Session?.SetLocked(SelectedItems, true);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Unlock() => Session?.SetLocked(SelectedItems, false);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Merge()
    {
        if (Session == null) return;
        if (!Session.Merge(SelectedItems)) StatusText = "Select two or more speech segments in one chapter to merge.";
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void ResetGain() => Session?.ResetGain(SelectedItems);

    /// <summary>Turns "background" on for the selection (or off, if all selected items already are).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void ToggleBackground()
    {
        var items = SelectedItems.Where(i => !i.IsPause).ToList();
        if (Session == null || items.Count == 0) return;
        Session.SetBackground(items, !items.All(i => i.IsBackground));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task SetGain()
    {
        if (Session == null) return;
        var initial = SelectedItems.Select(i => i.Model.GainDb).Distinct().Count() == 1 ? SelectedItems[0].Model.GainDb.ToString("0.#") : "";
        var s = await Dialogs.Prompt("Set gain", "Gain in dB (applied on top of loudness normalization):", initial);
        if (s != null && double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db))
            Session.SetGain(SelectedItems, db);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void EditText()
    {
        if (SelectedItem is { } item && item.IsTts) BeginEditRequested?.Invoke(item);
    }

    /// <summary>Splits the item being edited at the caret (called by the view with the caret index).</summary>
    public void SplitAt(ItemViewModel item, int caret)
    {
        if (Session == null) return;
        item.CommitEdit();
        var created = Session.Split(item, caret);
        if (created == null) StatusText = "Place the cursor inside the text to split.";
        else SelectAndReveal(created);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void PreviousTake() { if (SelectedItem is { } i) Session?.StepTake(i, -1); }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void NextTake() { if (SelectedItem is { } i) Session?.StepTake(i, +1); }

    // ================================================================== speakers

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void AssignSpeaker(SpeakerViewModel? speaker)
    {
        if (Session == null || speaker == null) return;
        Session.SetSpeaker(SelectedItems, speaker.Id);
        StatusText = $"Assigned {speaker.Name} to {SelectedItems.Count(i => i.IsTts)} segment(s)";
    }

    /// <summary>A speaker dragged from the Speakers panel onto a row: that row, or the whole selection when the row is part of it.</summary>
    public void AssignSpeakerByDrop(ItemViewModel target, SpeakerViewModel speaker)
    {
        if (Session == null) return;
        List<ItemViewModel> items = SelectedItems.Contains(target) ? [.. SelectedItems] : [target];
        Session.SetSpeaker(items, speaker.Id);
        StatusText = $"Assigned {speaker.Name} to {items.Count(i => i.IsTts)} segment(s)";
    }

    /// <summary>1–9 shortcuts.</summary>
    public void AssignSpeakerByPosition(int position)
    {
        if (Session == null || position < 1 || position > Session.Speakers.Count) return;
        AssignSpeaker(Session.Speakers[position - 1]);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task NewSpeakerForSelection()
    {
        if (Session == null) return;
        var name = await Dialogs.Prompt("New speaker", "Name of the new speaker:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var worker = Session.Project.FindSpeaker(Session.Project.Defaults.Speaker)?.Worker ?? AppHost.Catalog.All.FirstOrDefault()?.Id ?? "kokoro";
        var sp = Session.AddSpeaker(name.Trim(), worker, Session.Speakers.Count);
        Session.SetSpeaker(SelectedItems, sp.Id);
        StatusText = $"Created speaker {sp.DisplayName}. Configure its voice in the Speakers dialog.";
    }

    // ================================================================== generation

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Regenerate()
    {
        if (Session == null) return;
        // R on one segment of a speaker without a worker starts or stops recording it.
        if (SelectedItems is [{ } only] && Session.IsRecorded(only.Model))
        {
            ToggleRecording(only);
            return;
        }
        // Explicitly regenerating a selection skips locked items too (spec: "skip locked").
        var n = Session.Generate(SelectedItems, SelectedItems.Count <= 5 ? JobPriority.High : JobPriority.Normal, skipLocked: true);
        StatusText = n == 0 ? "Nothing to generate (locked items, clips and pauses are skipped)." : $"Queued {n} item(s)";
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private void GenerateStaleChapter()
    {
        if (Session == null || SelectedChapter == null) return;
        var n = Session.GenerateStale([SelectedChapter]);
        StatusText = n == 0 ? "Chapter is up to date." : $"Queued {n} item(s) in {SelectedChapter.Title}";
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private void GenerateStaleBook()
    {
        if (Session == null) return;
        var n = Session.GenerateStale(Session.Chapters);
        StatusText = n == 0 ? "Book is up to date." : $"Queued {n} item(s)";
    }

    [RelayCommand]
    private void StopGeneration()
    {
        AppHost.Scheduler.CancelAll();
        StatusText = "Generation stopped.";
    }

    [RelayCommand]
    private void RetryFailed() => AppHost.Scheduler.RetryFailed();

    // ================================================================== insert

    private (ChapterViewModel chapter, int index)? InsertionPoint(bool below)
    {
        var ch = SelectedItem?.Chapter ?? SelectedChapter;
        if (ch == null) return null;
        if (SelectedItems.Count == 0) return (ch, below ? ch.Items.Count : 0);
        var idx = below ? ch.Items.IndexOf(SelectedItems[^1]) + 1 : ch.Items.IndexOf(SelectedItems[0]);
        return (ch, Math.Max(0, idx));
    }

    private void InsertAndSelect(bool below, TimelineItem model, bool edit)
    {
        if (Session == null || InsertionPoint(below) is not { } p) return;
        Session.InsertItems(p.chapter, p.index, [model]);
        if (Session.ItemVm(model.Id) is { } vm)
        {
            SelectAndReveal(vm);
            if (edit) BeginEditRequested?.Invoke(vm);
        }
    }

    private TimelineItem NewTextItem() => new()
    {
        Kind = ItemKind.Tts,
        Text = "",
        Speaker = SelectedItem?.Model.Speaker ?? Session?.Project.Defaults.Speaker,
    };

    private static TimelineItem NewPause() => new() { Kind = ItemKind.Pause, DurationMs = 1000 };

    private static TimelineItem NewComment() => new() { Kind = ItemKind.Comment, Text = "" };

    /// <summary>A negative pause: the sections around it overlap by this much.</summary>
    private static TimelineItem NewOverlap() => new() { Kind = ItemKind.Pause, DurationMs = -500 };

    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertTextBelow() => InsertAndSelect(true, NewTextItem(), true);
    /// <summary>The speaker dots on the "+ Speaker" button: a new text segment for that speaker.</summary>
    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private void InsertSpeakerBelow(SpeakerViewModel? speaker)
    {
        var item = NewTextItem();
        if (speaker != null) item.Speaker = speaker.Id;
        InsertAndSelect(true, item, true);
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertTextAbove()=> InsertAndSelect(false, NewTextItem(), true);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertPauseBelow() => InsertAndSelect(true, NewPause(), false);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertCommentBelow() => InsertAndSelect(true, NewComment(), true);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertCommentAbove() => InsertAndSelect(false, NewComment(), true);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertPauseAbove() => InsertAndSelect(false, NewPause(), false);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertOverlapBelow() => InsertAndSelect(true, NewOverlap(), false);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private void InsertOverlapAbove() => InsertAndSelect(false, NewOverlap(), false);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private Task InsertClipBelow() => InsertClipsAsync(true);
    [RelayCommand(CanExecute = nameof(CanUseProject))] private Task InsertClipAbove() => InsertClipsAsync(false);

    private async Task InsertClipsAsync(bool below)
    {
        if (Session == null) return;
        var files = await Dialogs.OpenFiles("Insert audio clip", true,
            new FileFilter("Audio", ProjectSession.AudioExtensions.Select(e => "*" + e).ToArray()));
        if (files.Length == 0 || InsertionPoint(below) is not { } p) return;
        await InsertClipFilesAsync(p.chapter, p.index, files);
    }

    /// <summary>Imports audio files into assets/ and inserts clip items (also used by drag-and-drop).</summary>
    public async Task InsertClipFilesAsync(ChapterViewModel chapter, int index, IReadOnlyList<string> files)
    {
        if (Session == null) return;
        var models = new List<TimelineItem>();
        foreach (var f in files)
        {
            try
            {
                StatusText = $"Importing {Path.GetFileName(f)}…";
                var rel = await Session.ImportClipFileAsync(f);
                models.Add(new TimelineItem { Kind = ItemKind.Clip, ClipFile = rel, Label = Path.GetFileNameWithoutExtension(f) });
            }
            catch (Exception ex)
            {
                await Dialogs.ShowError("Cannot import clip", $"{f}\n\n{ex.Message}");
            }
        }
        if (models.Count == 0) return;
        await RefreshAssetsAsync();
        Session.InsertItems(chapter, index, models, models.Count == 1 ? "Insert clip" : "Insert clips");
        StatusText = $"Inserted {models.Count} clip(s)";
        if (Session.ItemVm(models[0].Id) is { } vm) SelectAndReveal(vm);
    }

    // ================================================================== search and filter

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private SpeakerViewModel? _speakerFilter;
    [ObservableProperty] private StatusFilter _statusFilterValue = StatusFilter.All;

    /// <summary>The filter box under the magnifier button. Filters only apply while it is open; closing it
    /// shows everything again but keeps the values for next time.</summary>
    [ObservableProperty] private bool _isFilterPanelOpen;

    private ObservableCollection<ItemViewModel>? _filtered;

    public static IReadOnlyList<StatusFilter> StatusFilters { get; } = Enum.GetValues<StatusFilter>();

    public bool IsFiltering => IsFilterPanelOpen && (HasSearchText || SpeakerFilter != null || StatusFilterValue != StatusFilter.All);

    private bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>First entry of the speaker filter dropdown; picking it clears the speaker filter.</summary>
    public const string AllSpeakersOption = "All speakers";

    /// <summary>The speaker filter dropdown: "All speakers" followed by the project's speakers. One list kept in
    /// sync (never replaced), so "All speakers" is selectable from the start, before any project is open.</summary>
    public ObservableCollection<object> SpeakerFilterOptions { get; } = [AllSpeakersOption];

    public object SpeakerFilterChoice
    {
        get => (object?)SpeakerFilter ?? AllSpeakersOption;
        // Null comes from the dropdown losing its selection while the list is resynced; ignore it.
        set { if (value != null) SpeakerFilter = value as SpeakerViewModel; }
    }

    partial void OnSessionChanged(ProjectSession? oldValue, ProjectSession? newValue)
    {
        if (oldValue != null) oldValue.Speakers.CollectionChanged -= OnSpeakersCollectionChanged;
        if (newValue != null) newValue.Speakers.CollectionChanged += OnSpeakersCollectionChanged;
        SpeakerFilter = null;
        SyncSpeakerFilterOptions();
    }

    private void OnSpeakersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (SpeakerFilter != null && Session?.Speakers.Contains(SpeakerFilter) != true) SpeakerFilter = null;
        SyncSpeakerFilterOptions();
    }

    private void SyncSpeakerFilterOptions()
    {
        while (SpeakerFilterOptions.Count > 1) SpeakerFilterOptions.RemoveAt(SpeakerFilterOptions.Count - 1);
        foreach (var s in Session?.Speakers ?? []) SpeakerFilterOptions.Add(s);
        // Re-select in case the dropdown dropped its selection while items were removed.
        OnPropertyChanged(nameof(SpeakerFilterChoice));
    }

    /// <summary>What the segment stack shows: the chapter's items, or the filtered subset.</summary>
    public ObservableCollection<ItemViewModel>? StackItems => IsFiltering ? _filtered : SelectedChapter?.Items;

    partial void OnSearchTextChanged(string value) => RebuildFilter();
    partial void OnIsFilterPanelOpenChanged(bool value) => RebuildFilter();
    partial void OnSpeakerFilterChanged(SpeakerViewModel? value)
    {
        if (value != null) IsFilterPanelOpen = true;
        OnPropertyChanged(nameof(SpeakerFilterChoice));
        RebuildFilter();
    }
    partial void OnStatusFilterValueChanged(StatusFilter value) => RebuildFilter();

    partial void OnSelectedChapterChanged(ChapterViewModel? value)
    {
        if (IsFiltering) RebuildFilter();
        GenerateStaleChapterCommand.NotifyCanExecuteChanged();
        UpdateTimelineRange();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SpeakerFilter = null;
        StatusFilterValue = StatusFilter.All;
        SearchText = "";
    }

    private void RebuildFilter()
    {
        if (!IsFiltering || SelectedChapter == null)
        {
            _filtered = null;
            OnPropertyChanged(nameof(StackItems));
            OnPropertyChanged(nameof(IsFiltering));
            return;
        }
        _filtered = new ObservableCollection<ItemViewModel>(SelectedChapter.Items.Where(Matches));
        OnPropertyChanged(nameof(StackItems));
        OnPropertyChanged(nameof(IsFiltering));
        StatusText = $"{_filtered.Count} of {SelectedChapter.Items.Count} items match";
    }

    private bool Matches(ItemViewModel i)
    {
        if (SpeakerFilter != null && (!i.IsTts || !string.Equals(i.Model.Speaker, SpeakerFilter.Id, StringComparison.OrdinalIgnoreCase))) return false;
        if (StatusFilterValue != StatusFilter.All)
        {
            var ok = StatusFilterValue switch
            {
                StatusFilter.Stale => i.Status == ItemStatus.Stale,
                StatusFilter.Failed => i.Status == ItemStatus.Failed,
                StatusFilter.NotGenerated => i.Status == ItemStatus.NotGenerated,
                StatusFilter.Locked => i.IsLocked,
                _ => true,
            };
            if (!ok) return false;
        }
        if (HasSearchText)
            return i.DisplayText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
        return true;
    }

    [RelayCommand]
    private void Find()
    {
        IsFilterPanelOpen = true;
        FocusSearchRequested?.Invoke();
    }

    /// <summary>F3 / Edit → Find next.</summary>
    [RelayCommand]
    private void FindNext() => FindMatch(backwards: false);

    /// <summary>Enter in the search box: jump to the next match across all chapters (Shift+Enter: previous).</summary>
    public void FindMatch(bool backwards)
    {
        if (Session == null || string.IsNullOrWhiteSpace(SearchText)) return;
        var all = Session.AllItemVms().ToList();
        if (all.Count == 0) return;
        var start = SelectedItem != null ? all.IndexOf(SelectedItem) : -1;
        for (int k = 1; k <= all.Count; k++)
        {
            var idx = backwards ? ((start - k) % all.Count + all.Count) % all.Count : (start + k) % all.Count;
            var item = all[idx];
            if (!item.DisplayText.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (IsFiltering && !ReferenceEquals(item.Chapter, SelectedChapter)) SelectedChapter = item.Chapter;
            SelectAndReveal(item);
            StatusText = $"Match in chapter {item.Chapter.Number}: {item.Chapter.Title}";
            return;
        }
        StatusText = $"No matches for \"{SearchText}\"";
    }
}

public enum StatusFilter
{
    All,
    Stale,
    Failed,
    NotGenerated,
    Locked,
}
