using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalAudiobookStudio.App.Controls;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

/// <summary>
/// Code-behind for view-only concerns: keyboard shortcuts, syncing the ListBox selection,
/// drag-and-drop, context menus, panel sizes and window placement.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly DataFormat<string> ItemsFormat = DataFormat.CreateStringApplicationFormat("las-items");
    private static readonly DataFormat<string> ChapterFormat = DataFormat.CreateStringApplicationFormat("las-chapter");
    private static readonly DataFormat<string> AssetFormat = DataFormat.CreateStringApplicationFormat("las-asset");
    private static readonly DataFormat<string> SpeakerFormat = DataFormat.CreateStringApplicationFormat("las-speaker");

    private bool _syncingSelection;
    private bool _closingConfirmed;
    private ItemViewModel? _pressedItem;
    private Point? _dragStart;
    private bool _promptingSave;
    private Border? _activeGrip;
    private double _gripStartX, _gripStartWidth;
    private ChapterViewModel? _chapterDragStart;
    private Point? _chapterDragPoint;
    private AssetViewModel? _assetDragStart;
    private Point? _assetDragPoint;
    private SpeakerViewModel? _speakerDragStart;
    private Point? _speakerDragPoint;
    private ListBoxItem? _speakerDropRow;

    public MainWindow()
    {
        InitializeComponent();
        MainWindowViewModel.ApplyTextFont();
        RestoreBounds();

        Stack.SelectionChanged += (_, _) => PushSelection();
        Stack.AddHandler(PointerPressedEvent, OnStackPointerPressed, RoutingStrategies.Tunnel);
        Stack.AddHandler(PointerMovedEvent, OnStackPointerMoved, RoutingStrategies.Tunnel);
        Stack.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
        Stack.AddHandler(TappedEvent, OnStackTapped);
        Stack.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Stack.AddHandler(GotFocusEvent, OnEditorGotFocus);
        Stack.AddHandler(LostFocusEvent, OnEditorLostFocus);
        Stack.ContextRequested += OnStackContextRequested;
        Stack.AddHandler(DragDrop.DragOverEvent, OnStackDragOver);
        Stack.AddHandler(DragDrop.DropEvent, OnStackDrop);
        Stack.AddHandler(DragDrop.DragLeaveEvent, (_, _) => { DropIndicator.IsVisible = false; SetSpeakerDropRow(null); });

        ChapterList.AddHandler(DoubleTappedEvent, OnChapterDoubleTapped);
        // Only user picks move the playhead to the chapter start (automatic switches while
        // playing change the selection too, but don't raise these events).
        ChapterList.AddHandler(TappedEvent, (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is ChapterViewModel ch) ViewModel?.GoToChapter(ch);
        });
        ChapterList.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown &&
                ViewModel?.SelectedChapter is { } ch)
                ViewModel.GoToChapter(ch);
        });
        ChapterList.AddHandler(PointerPressedEvent, OnChapterPointerPressed, RoutingStrategies.Tunnel);
        ChapterList.AddHandler(PointerMovedEvent, OnChapterPointerMoved, RoutingStrategies.Tunnel);
        ChapterList.AddHandler(PointerReleasedEvent, (_, _) => _chapterDragPoint = null, RoutingStrategies.Tunnel);
        ChapterList.ContextRequested += OnChapterContextRequested;
        ChapterList.AddHandler(DragDrop.DragOverEvent, OnChapterDragOver);
        ChapterList.AddHandler(DragDrop.DropEvent, OnChapterDrop);

        SearchBox.KeyDown += OnSearchKeyDown;
        Scrubber.SeekRequested += ms => ViewModel?.ScrubTimeline(ms);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        AssignSpeakerMenu.SubmenuOpened += (_, _) => BuildSpeakerMenu(AssignSpeakerMenu);
        // A placeholder child makes the submenu arrow appear before it is filled.
        AssignSpeakerMenu.Items.Add(new MenuItem { Header = "(no speakers)" });
        RecentMenu.Items.Add(new MenuItem { Header = "(none)" });
        RecentMenu.SubmenuOpened += (_, _) => BuildRecentMenu();

        DataContextChanged += OnDataContextChanged;
        LeftSplitter.DragCompleted += (_, _) => SavePanelSizes();
        RightSplitter.DragCompleted += (_, _) => SavePanelSizes();
        SectionSplitter1.DragCompleted += (_, _) => SavePanelSizes();
        SectionSplitter2.DragCompleted += (_, _) => SavePanelSizes();

        // Double-click a speaker: open the Speakers window on that speaker.
        SpeakerList.AddHandler(DoubleTappedEvent, (_, e) =>
            ViewModel?.ShowSpeakersCommand.Execute((e.Source as StyledElement)?.DataContext as SpeakerViewModel));
        SpeakerList.ContextRequested += OnSpeakerContextRequested;
        SpeakerList.AddHandler(PointerPressedEvent, OnSpeakerPointerPressed, RoutingStrategies.Tunnel);
        SpeakerList.AddHandler(PointerMovedEvent, OnSpeakerPointerMoved, RoutingStrategies.Tunnel);
        SpeakerList.AddHandler(PointerReleasedEvent, (_, _) => _speakerDragPoint = null, RoutingStrategies.Tunnel);

        AssetList.AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (!IsOnButton(e.Source) && (e.Source as StyledElement)?.DataContext is AssetViewModel { IsAudio: true } a) ViewModel?.InsertAssetAtSelection(a);
        });
        // A click on a folder row expands or collapses it.
        AssetList.AddHandler(TappedEvent, (_, e) =>
        {
            if (!IsOnButton(e.Source) && (e.Source as StyledElement)?.DataContext is AssetViewModel { IsFolder: true } f)
                ViewModel?.ToggleAssetFolderCommand.Execute(f);
        });
        AssetList.AddHandler(PointerPressedEvent, OnAssetPointerPressed, RoutingStrategies.Tunnel);
        AssetList.AddHandler(PointerMovedEvent, OnAssetPointerMoved, RoutingStrategies.Tunnel);
        AssetList.AddHandler(PointerReleasedEvent, (_, _) => _assetDragPoint = null, RoutingStrategies.Tunnel);
        AssetList.ContextRequested += OnAssetContextRequested;
        AssetsSection.AddHandler(DragDrop.DragOverEvent, OnAssetsDragOver);
        AssetsSection.AddHandler(DragDrop.DropEvent, OnAssetsDrop);

        foreach (var grip in new[] { SpeakerGrip, AudioGrip })
        {
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += (_, e) => { _activeGrip = null; e.Pointer.Capture(null); };
            grip.PointerCaptureLost += (_, _) => _activeGrip = null;
        }
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.ScrollIntoViewRequested += ScrollIntoView;
        vm.SelectItemsRequested += SelectItems;
        vm.BeginEditRequested += item => { SelectItems([item]); ScrollIntoView(item); FocusEditor(item); };
        // Posted: the filter box has only just been opened and isn't laid out yet.
        vm.FocusSearchRequested += () => Dispatcher.UIThread.Post(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Loaded);
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyPanels();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.ShowSpeakersPanel):
            case nameof(MainWindowViewModel.ShowChapters):
            case nameof(MainWindowViewModel.ShowAssets):
            case nameof(MainWindowViewModel.ShowInspector):
                ApplyPanels();
                break;
            case nameof(MainWindowViewModel.StackItems):
                // New chapter shown: start at the top and clear the old selection.
                Dispatcher.UIThread.Post(() =>
                {
                    if (ViewModel?.StackItems is { Count: > 0 } items && Stack.SelectedItems?.Count == 0)
                        Stack.ScrollIntoView(items[0]);
                    PushSelection();
                });
                break;
        }
    }

    // ================================================================== selection

    private void PushSelection()
    {
        if (_syncingSelection || ViewModel == null) return;
        var selected = Stack.SelectedItems?.OfType<ItemViewModel>().ToList() ?? [];
        ViewModel.UpdateSelection(selected);
    }

    private void SelectItems(IReadOnlyList<ItemViewModel> items)
    {
        if (Stack.SelectedItems == null) return;
        // One batched change with auto-scroll off: adding items one by one made the list scroll to each
        // new selection in turn, crawling down to the bottom on Ctrl+A. Scrolling is done explicitly
        // (ScrollIntoViewRequested) where it is wanted.
        var autoScroll = Stack.AutoScrollToSelectedItem;
        Stack.AutoScrollToSelectedItem = false;
        _syncingSelection = true;
        Stack.Selection.BeginBatchUpdate();
        try
        {
            Stack.Selection.Clear();
            var wanted = items.ToHashSet();
            var source = ViewModel?.StackItems;
            if (source != null)
                for (int idx = 0; idx < source.Count; idx++)
                    if (wanted.Contains(source[idx])) Stack.Selection.Select(idx);
        }
        finally
        {
            Stack.Selection.EndBatchUpdate();
            _syncingSelection = false;
            Dispatcher.UIThread.Post(() => Stack.AutoScrollToSelectedItem = autoScroll, DispatcherPriority.Background);
        }
        PushSelection();
        if (IsTextInputFocused()) return;
        // Keyboard focus: the row for a single selection; the list itself for several (focusing the last
        // row would bring it into view).
        if (items.Count == 1 && Stack.ContainerFromItem(items[0]) is ListBoxItem container) container.Focus();
        else if (items.Count > 1) Stack.Focus();
    }

    private void ScrollIntoView(ItemViewModel item)
    {
        Stack.ScrollIntoView(item);
    }

    private bool IsTextInputFocused() => FocusManager?.GetFocusedElement() is TextBox;

    // ================================================================== keyboard

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null || IsTextInputFocused()) return;
        // Arrow keys in the chapter list navigate chapters, not segments.
        if (e.Key is Key.Up or Key.Down && FocusManager?.GetFocusedElement() is Visual focused &&
            (ReferenceEquals(focused, ChapterList) || focused.FindAncestorOfType<ListBox>() == ChapterList))
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        switch (e.Key)
        {
            case Key.Space when ctrl:
                vm.PlaySelectedOnlyCommand.Execute(null);
                break;
            case Key.Space when !alt:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left when alt:
                vm.PreviousItemCommand.Execute(null);
                break;
            case Key.Right when alt:
                vm.NextItemCommand.Execute(null);
                break;
            case Key.Up when !ctrl && !alt && vm.HasProject:
                vm.MoveSelection(-1, shift);
                break;
            case Key.Down when !ctrl && !alt && vm.HasProject:
                vm.MoveSelection(+1, shift);
                break;
            case Key.Enter when ctrl:
                vm.InsertTextBelowCommand.Execute(null);
                break;
            case Key.Enter when !alt:
                vm.EditTextCommand.Execute(null);
                break;
            case Key.Delete:
                vm.DeleteCommand.Execute(null);
                break;
            case Key.R when !ctrl && !alt:
                vm.RegenerateCommand.Execute(null);
                break;
            case Key.G when !ctrl && !alt:
                vm.GenerateStaleChapterCommand.Execute(null);
                break;
            case Key.L when !ctrl && !alt:
                vm.ToggleLockCommand.Execute(null);
                break;
            case Key.B when !ctrl && !alt:
                vm.ToggleBackgroundCommand.Execute(null);
                break;
            case Key.OemOpenBrackets when !ctrl && !alt:
                vm.PreviousTakeCommand.Execute(null);
                break;
            case Key.OemCloseBrackets when !ctrl && !alt:
                vm.NextTakeCommand.Execute(null);
                break;
            case >= Key.D1 and <= Key.D9 when !ctrl && !alt:
                vm.AssignSpeakerByPosition(e.Key - Key.D0);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when !ctrl && !alt:
                vm.AssignSpeakerByPosition(e.Key - Key.NumPad0);
                break;
            case Key.A when ctrl:
                vm.SelectAllCommand.Execute(null);
                break;
            case Key.C when ctrl:
                vm.CopyTextCommand.Execute(null);
                break;
            case Key.Escape when vm.IsPlaying:
                vm.StopPlaybackCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Keys inside the inline row editor.</summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Name: "RowEditor" } box || box.DataContext is not ItemViewModel item) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Escape)
        {
            item.CancelEdit();
            FocusRow(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ctrl && shift)
        {
            ViewModel?.SplitAt(item, box.CaretIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ctrl)
        {
            item.CommitEdit();
            FocusRow(item);
            e.Handled = true;
        }
    }

    /// <summary>Clicking into a row's text selects that row.</summary>
    private void OnEditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is TextBox { Name: "RowEditor", DataContext: ItemViewModel item } && ViewModel is { } vm && !vm.SelectedItems.Contains(item))
            SelectItems([item]);
    }

    /// <summary>
    /// Leaving a row's text with unsaved changes asks whether to save them, or saves them when the "Ask before
    /// saving changed text" setting is off.
    /// </summary>
    private async void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { Name: "RowEditor", DataContext: ItemViewModel { IsDirty: false } clean })
        {
            // Nothing to save: back to the transcript text (if the row has one).
            clean.EndTextEdit();
            return;
        }
        if (_promptingSave || e.Source is not TextBox { Name: "RowEditor" } box || box.DataContext is not ItemViewModel { IsDirty: true } item) return;
        // Let focus settle: the text box's own context menu or switching to another app shouldn't prompt.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (box.IsFocused || box.ContextFlyout?.IsOpen == true || !IsActive || !item.IsDirty) return;
        if (!AppHost.Settings.Ui.ConfirmTextEdits)
        {
            item.CommitEdit();
            return;
        }

        _promptingSave = true;
        try
        {
            var choice = await Dialogs.Choose("Unsaved text", "Save the changes to this segment's text?", "Save", "Discard", "Keep editing");
            if (choice == "Save") item.CommitEdit();
            else if (choice == "Discard") item.CancelEdit();
            else FocusEditor(item);
        }
        finally
        {
            _promptingSave = false;
        }
    }

    /// <summary>Puts the caret at the end of an item's text box (scrolling it into view first is the caller's job).</summary>
    private void FocusEditor(ItemViewModel item, int? caret = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Stack.ContainerFromItem(item) is not Control c) return;
            var box = c.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == "RowEditor");
            if (box == null) return;
            box.Focus();
            box.CaretIndex = Math.Min(caret ?? int.MaxValue, box.Text?.Length ?? 0);
        }, DispatcherPriority.Background);
    }

    private void FocusRow(ItemViewModel item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Stack.ContainerFromItem(item) is ListBoxItem c) c.Focus();
            else Stack.Focus();
        });
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.FindMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Stack.Focus();
            e.Handled = true;
        }
    }

    // ================================================================== clicks

    private static ItemViewModel? ItemFromSource(object? source) =>
        (source as StyledElement)?.DataContext as ItemViewModel;

    private void OnStackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedItem = ItemFromSource(e.Source);
        // Dragging starts from the time column (the drag handle).
        var handle = (e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true);
        _dragStart = handle?.Name == "DragHandle" && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
    }

    /// <summary>Clicking a row's speaker chip opens a menu to reassign that row's speaker.</summary>
    private void OnStackTapped(object? sender, TappedEventArgs e)
    {
        // Clicking a transcribed row's text edits the original text, with the caret where the click was.
        var source = e.Source as Visual;
        var view = source?.FindAncestorOfType<TranscriptView>(includeSelf: true)
                   ?? (source?.FindAncestorOfType<Border>(includeSelf: true) is { Name: "TranscriptBox", Child: TranscriptView inner } ? inner : null);
        if (view is { DataContext: ItemViewModel row })
        {
            var caret = view.SourceIndexAt(e.GetPosition(view));
            SelectItems([row]);
            row.IsEditingText = true;
            FocusEditor(row, caret);
            e.Handled = true;
            return;
        }
        var chip = (e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true);
        while (chip != null && chip.Name != "SpeakerChip") chip = chip.FindAncestorOfType<Border>();
        if (chip?.DataContext is not ItemViewModel item || ViewModel?.Session == null) return;
        SelectItems([item]);
        new ContextMenu { ItemsSource = SpeakerMenuItems(item.Speaker) }.Open(chip);
        e.Handled = true;
    }

    // ================================================================== column header grips

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        var cols = StackColumns.Shared;
        _activeGrip = grip;
        _gripStartX = e.GetPosition(this).X;
        _gripStartWidth = grip == SpeakerGrip ? cols.SpeakerWidth : cols.AudioWidth;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_activeGrip == null) return;
        var cols = StackColumns.Shared;
        var dx = e.GetPosition(this).X - _gripStartX;
        // Always leave the text column some room.
        var room = Stack.Bounds.Width - 200;
        if (_activeGrip == SpeakerGrip)
            cols.SpeakerWidth = Math.Clamp(_gripStartWidth + dx, StackColumns.MinSpeaker, Math.Max(StackColumns.MinSpeaker, Math.Min(StackColumns.MaxSpeaker, room - cols.AudioWidth)));
        else
            cols.AudioWidth = Math.Clamp(_gripStartWidth - dx, StackColumns.MinAudio, Math.Max(StackColumns.MinAudio, Math.Min(StackColumns.MaxAudio, room - cols.SpeakerWidth)));
    }

    // ================================================================== drag and drop: items

    private async void OnStackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || ViewModel is not { } vm || _pressedItem == null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - start.Y) < 5 && Math.Abs(p.X - start.X) < 5) return;
        _dragStart = null;
        if (vm.IsFiltering) { vm.StatusText = "Clear the filter to reorder items."; return; }
        if (!vm.SelectedItems.Contains(_pressedItem)) SelectItems([_pressedItem]);
        if (vm.Session?.RefuseIfLocked(vm.SelectedItems) == true) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ItemsFormat, "items"));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        DropIndicator.IsVisible = false;
    }

    /// <summary>Insertion index in the current stack for a pointer position (before the row under the pointer's upper half).</summary>
    private (int index, double y)? DropTarget(DragEventArgs e)
    {
        var items = ViewModel?.StackItems;
        if (items == null) return null;
        var pos = e.GetPosition(Stack);
        ListBoxItem? best = null;
        foreach (var c in Stack.GetRealizedContainers().OfType<ListBoxItem>())
        {
            var top = c.TranslatePoint(new Point(0, 0), Stack)?.Y ?? 0;
            if (pos.Y >= top && pos.Y < top + c.Bounds.Height) { best = c; break; }
        }
        if (best?.DataContext is not ItemViewModel over)
            return (items.Count, Stack.Bounds.Height - 2);
        var y0 = best.TranslatePoint(new Point(0, 0), Stack)?.Y ?? 0;
        var after = pos.Y > y0 + best.Bounds.Height / 2;
        var idx = items.IndexOf(over) + (after ? 1 : 0);
        return (idx, after ? y0 + best.Bounds.Height : y0);
    }

    private void OnStackDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(SpeakerFormat))
        {
            // A speaker from the Speakers panel: highlight the speech row under the pointer, no insertion line.
            DropIndicator.IsVisible = false;
            var row = SpeakerDropRow(e);
            SetSpeakerDropRow(row);
            e.DragEffects = row != null ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }
        var isItems = e.DataTransfer.Contains(ItemsFormat);
        var isFiles = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(AssetFormat);
        if ((!isItems && !isFiles) || ViewModel?.SelectedChapter == null || ViewModel.IsFiltering)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = isItems ? DragDropEffects.Move : DragDropEffects.Copy;
        if (DropTarget(e) is { } t)
        {
            DropIndicator.IsVisible = true;
            DropIndicator.Width = Stack.Bounds.Width;
            var offset = Stack.TranslatePoint(new Point(0, 0), (Visual)DropIndicator.Parent!) ?? new Point();
            Canvas.SetLeft(DropIndicator, offset.X);
            Canvas.SetTop(DropIndicator, offset.Y + t.y - 1);
        }
    }

    private async void OnStackDrop(object? sender, DragEventArgs e)
    {
        DropIndicator.IsVisible = false;
        if (e.DataTransfer.Contains(SpeakerFormat))
        {
            if (SpeakerDropRow(e)?.DataContext is ItemViewModel target && _speakerDragStart is { } speaker)
                ViewModel?.AssignSpeakerByDrop(target, speaker);
            SetSpeakerDropRow(null);
            return;
        }
        if (ViewModel is not { SelectedChapter: { } chapter, Session: { } session } vm || DropTarget(e) is not { } t) return;
        if (e.DataTransfer.Contains(ItemsFormat))
        {
            session.MoveItems(vm.SelectedItems.ToList(), chapter, t.index);
        }
        else if (e.DataTransfer.Contains(AssetFormat) && _assetDragStart is { } asset)
        {
            vm.InsertAsset(asset, chapter, t.index);
        }
        else if (e.DataTransfer.TryGetFiles() is { } files)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>()
                .Where(p => Session.ProjectSession.AudioExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
            var txt = files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            if (paths.Count > 0) await vm.InsertClipFilesAsync(chapter, t.index, paths);
            else if (txt != null) await vm.ImportTextFileAsync(txt);
        }
    }

    // ================================================================== chapters

    private void OnChapterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is ChapterViewModel ch) ViewModel?.RenameChapterCommand.Execute(ch);
    }

    private void OnChapterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _chapterDragStart = (e.Source as StyledElement)?.DataContext as ChapterViewModel;
        _chapterDragPoint = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
    }

    private async void OnChapterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_chapterDragPoint is not { } start || _chapterDragStart == null) return;
        if (Math.Abs(e.GetPosition(this).Y - start.Y) < 6) return;
        _chapterDragPoint = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ChapterFormat, "chapter"));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnChapterDragOver(object? sender, DragEventArgs e)
    {
        var ok = e.DataTransfer.Contains(ItemsFormat) || e.DataTransfer.Contains(ChapterFormat);
        e.DragEffects = ok && (e.Source as StyledElement)?.DataContext is ChapterViewModel ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void OnChapterDrop(object? sender, DragEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is not ChapterViewModel target || ViewModel is not { Session: { } session } vm) return;
        if (e.DataTransfer.Contains(ItemsFormat)) vm.MoveSelectionToChapter(target);
        else if (e.DataTransfer.Contains(ChapterFormat) && _chapterDragStart is { } moving && !ReferenceEquals(moving, target))
            session.MoveChapter(moving, session.Chapters.IndexOf(target));
    }

    private void OnChapterContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is not ChapterViewModel ch || ViewModel is not { } vm) return;
        vm.SelectedChapter = ch;
        var clear = new MenuItem
        {
            Header = "Clear",
            ItemsSource = new object[]
            {
                Item("Unused takes…", () => vm.CleanUpChapterTakesCommand.Execute(ch)),
                Item("All takes…", () => vm.ClearChapterTakesCommand.Execute(ch)),
            },
        };
        var items = new List<object>
        {
            Item("Generate", () => vm.GenerateChapterCommand.Execute(ch), "G"),
            Item("Regenerate all…", () => vm.RegenerateChapterCommand.Execute(ch)),
            clear,
        };
        // Only when something needs attention: stale, failed, or a transcript that doesn't match.
        if (MainWindowViewModel.Warnings(ch).Count is > 0 and var warnings)
            items.Add(Item($"Jump to warning ({warnings})", () => vm.JumpToWarningCommand.Execute(ch)));
        items.AddRange(
        [
            new Separator(),
            Item("Rename…", () => vm.RenameChapterCommand.Execute(ch), "Double-click"),
            Item("Export chapter…", () => vm.ExportChapterCommand.Execute(ch)),
            new Separator(),
            Item("Add chapter after", () => vm.AddChapterCommand.Execute(null)),
            Item("Delete chapter…", () => vm.DeleteChapterCommand.Execute(ch)),
        ]);
        new ContextMenu { ItemsSource = items }.Open(ChapterList);
        e.Handled = true;
    }

    // ================================================================== speakers panel

    private void OnSpeakerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is not SpeakerViewModel sp || ViewModel is not { } vm) return;
        var assign = Item($"Assign to selected segments", () => vm.AssignSpeakerCommand.Execute(sp), sp.ShortcutText.Length > 0 ? sp.ShortcutText : null);
        assign.IsEnabled = vm.SelectedItems.Count > 0;
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                assign,
                Item("Show only this speaker", () => vm.SpeakerFilter = sp),
                new Separator(),
                Item("Edit speaker…", () => vm.ShowSpeakersCommand.Execute(sp), "Double-click"),
            },
        };
        menu.Open(SpeakerList);
        e.Handled = true;
    }

    /// <summary>Dragging a speaker onto a speech row assigns it (to the whole selection if the row is selected).</summary>
    private void OnSpeakerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _speakerDragStart = (e.Source as StyledElement)?.DataContext as SpeakerViewModel;
        _speakerDragPoint = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
    }

    private async void OnSpeakerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_speakerDragPoint is not { } start || _speakerDragStart == null || ViewModel?.HasProject != true) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - start.Y) < 5 && Math.Abs(p.X - start.X) < 5) return;
        _speakerDragPoint = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(SpeakerFormat, _speakerDragStart.Id));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        SetSpeakerDropRow(null);
    }

    /// <summary>The unlocked speech row under the pointer, or null.</summary>
    private static ListBoxItem? SpeakerDropRow(DragEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ItemViewModel { IsTts: true, IsLocked: false } } row ? row : null;

    private void SetSpeakerDropRow(ListBoxItem? row)
    {
        if (ReferenceEquals(row, _speakerDropRow)) return;
        _speakerDropRow?.Classes.Remove("speakerDrop");
        _speakerDropRow = row;
        row?.Classes.Add("speakerDrop");
    }

    // ================================================================== assets panel

    private void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _assetDragStart = (e.Source as StyledElement)?.DataContext as AssetViewModel is { IsAudio: true } a ? a : null;
        _assetDragPoint = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !IsOnButton(e.Source) ? e.GetPosition(this) : null;
    }

    /// <summary>True when the event came from a button (or its icon) inside a list row.</summary>
    private static bool IsOnButton(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null;

    /// <summary>Dragging an asset into the segment list inserts it as a clip there.</summary>
    private async void OnAssetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_assetDragPoint is not { } start || _assetDragStart == null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - start.Y) < 5 && Math.Abs(p.X - start.X) < 5) return;
        _assetDragPoint = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(AssetFormat, _assetDragStart.RelativePath));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        DropIndicator.IsVisible = false;
    }

    private void OnAssetContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { Session: not null } vm) return;
        var items = new List<object>();
        var a = (e.Source as StyledElement)?.DataContext as AssetViewModel;
        if (a is { IsFolder: true })
        {
            items.Add(Item("Add files to this folder…", async () => await vm.ImportAssetsIntoAsync(a)));
            items.Add(Item("New folder inside…", async () => await vm.NewAssetFolderAsync(a)));
            items.Add(Item("Open folder", () => vm.RevealAsset(a)));
            items.Add(new Separator());
            items.Add(Item("Delete folder", async () => await vm.DeleteAssetAsync(a)));
            items.Add(new Separator());
        }
        else if (a != null)
        {
            if (a.IsAudio) items.Add(Item("Insert below selection", () => vm.InsertAssetAtSelection(a), "Double-click"));
            items.Add(Item("Open containing folder", () => vm.RevealAsset(a)));
            items.Add(new Separator());
            items.Add(Item("Delete…", async () => await vm.DeleteAssetAsync(a)));
            items.Add(new Separator());
        }
        items.Add(Item("Add audio files or workflows…", () => vm.ImportAssetsCommand.Execute(null)));
        items.Add(Item("New folder…", async () => await vm.NewAssetFolderAsync(null)));
        items.Add(Item("Refresh", async () => await vm.RefreshAssetsAsync()));
        new ContextMenu { ItemsSource = items }.Open(AssetList);
        e.Handled = true;
    }

    private void OnAssetsDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && ViewModel?.HasProject == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnAssetsDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { HasProject: true } vm || e.DataTransfer.TryGetFiles() is not { } files) return;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>()
            .Where(MainWindowViewModel.IsAssetFile).ToList();
        // Dropped on a folder row, or on a file inside a folder: into that folder.
        var target = (e.Source as StyledElement)?.DataContext as AssetViewModel;
        await vm.ImportAssetFilesAsync(paths, target == null ? null : vm.AssetFolderOf(target));
    }

    // ================================================================== row context menu

    private void OnStackContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { Session: not null } vm || ItemFromSource(e.Source) is not { } item) return;
        if (!vm.SelectedItems.Contains(item)) SelectItems([item]);

        var items = new List<object>();
        var onWaveform = (e.Source as Visual)?.FindAncestorOfType<WaveformView>(includeSelf: true) != null;
        if (onWaveform && vm.SelectedItems.Count == 1 && item.HasTakes)
        {
            var takes = item.Takes;
            var current = item.SelectedTakeInfo;
            items.Add(Item("Play take", () => vm.PlayItemOnly(item), "Ctrl+Space"));
            var idx = takes.ToList().FindIndex(t => t.Take == current?.Take);
            if (idx > 0) items.Add(Item($"Compare with previous take (A/B: {takes[idx - 1].Take} → {takes[idx].Take})", () => vm.PlayAB(item, takes[idx - 1].Take, takes[idx].Take)));
            if (current != null)
            {
                items.Add(Item("Open containing folder", () => Dialogs.Reveal(vm.Session!.Takes.TakeAudio(item.Id, current.Take))));
                items.Add(Item("Export this take as WAV…", async () =>
                {
                    var target = await Dialogs.SaveFile("Export take", $"take-{current.Take:0000}.wav", "wav", new FileFilter("WAV", "*.wav"));
                    if (target != null) await vm.Session!.ExportTakeAsWavAsync(item.Id, current.Take, target);
                }));
            }
            items.Add(new Separator());
        }

        var speakerMenu = new MenuItem { Header = "Assign speaker" };
        BuildSpeakerMenu(speakerMenu);
        items.Add(speakerMenu);
        items.Add(Item("Generate / regenerate", () => vm.RegenerateCommand.Execute(null), "R"));

        // Transcript: listen to the take again, and take back an approval or an ignore.
        var selected = vm.SelectedItems.ToList();
        if (selected.Any(i => vm.Session!.Engine.TranscriberFor(i.Model) != null && i.Model.SelectedTake != null))
            items.Add(Item("Rerun transcript", () => vm.Session!.RerunTranscripts(selected)));
        if (selected.Any(i => i.Model.ApprovedTranscriptTake != null))
            items.Add(Item("Undo transcript approval", () => vm.Session!.ClearTranscriptApproval(selected)));
        if (selected.Any(i => i.Model.IgnoreTranscript == true))
            items.Add(Item("Stop ignoring transcript", () => vm.Session!.SetTranscriptIgnored(selected, false)));
        items.Add(new Separator());
        items.Add(Item("Set gain…", () => vm.SetGainCommand.Execute(null)));
        items.Add(Item("Reset gain", () => vm.ResetGainCommand.Execute(null)));
        if (vm.SelectedItems.Any(i => !i.IsPause))
            items.Add(Item(vm.SelectedItems.Where(i => !i.IsPause).All(i => i.IsBackground) ? "Make normal section" : "Make background",
                () => vm.ToggleBackgroundCommand.Execute(null), "B"));
        items.Add(Item(vm.SelectedItems.All(i => i.IsLocked) ? "Unlock" : "Lock", () => vm.ToggleLockCommand.Execute(null), "L"));
        items.Add(new Separator());
        items.Add(Item("Edit text", () => vm.EditTextCommand.Execute(null), "Enter"));
        items.Add(Item("Merge into one segment", () => vm.MergeCommand.Execute(null), "Ctrl+M"));
        items.Add(Item("Split at cursor…", () =>
        {
            vm.StatusText = "Edit the text (Enter), place the cursor, then press Ctrl+Shift+Enter to split.";
            vm.EditTextCommand.Execute(null);
        }, "Ctrl+Shift+Enter"));
        var above = new MenuItem { Header = "Insert above" };
        above.ItemsSource = new object[]
        {
            Item("Text segment", () => vm.InsertTextAboveCommand.Execute(null)),
            Item("Pause", () => vm.InsertPauseAboveCommand.Execute(null)),
            Item("Overlap (negative pause)", () => vm.InsertOverlapAboveCommand.Execute(null)),
            Item("Comment", () => vm.InsertCommentAboveCommand.Execute(null)),
            Item("Clip from file…", () => vm.InsertClipAboveCommand.Execute(null)),
        };
        var below = new MenuItem { Header = "Insert below" };
        below.ItemsSource = new object[]
        {
            Item("Text segment", () => vm.InsertTextBelowCommand.Execute(null), "Ctrl+Enter"),
            Item("Pause", () => vm.InsertPauseBelowCommand.Execute(null)),
            Item("Overlap (negative pause)", () => vm.InsertOverlapBelowCommand.Execute(null)),
            Item("Comment", () => vm.InsertCommentBelowCommand.Execute(null)),
            Item("Clip from file…", () => vm.InsertClipBelowCommand.Execute(null)),
        };
        items.Add(above);
        items.Add(below);
        items.Add(new Separator());
        items.Add(Item("Copy text", () => vm.CopyTextCommand.Execute(null), "Ctrl+C"));
        items.Add(Item("Delete", () => vm.DeleteCommand.Execute(null), "Delete"));

        var menu = new ContextMenu { ItemsSource = items };
        menu.Open((Control)(e.Source as Control ?? Stack));
        e.Handled = true;
    }

    private static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var mi = new MenuItem { Header = header };
        if (gesture != null)
        {
            try { mi.InputGesture = KeyGesture.Parse(gesture); }
            catch (ArgumentException) { /* display-only hint; ignore unparseable gestures */ }
        }
        mi.Click += (_, _) => action();
        return mi;
    }

    private void BuildSpeakerMenu(MenuItem parent)
    {
        var list = SpeakerMenuItems();
        parent.ItemsSource = null;
        parent.Items.Clear();
        parent.ItemsSource = list;
    }

    /// <summary>One entry per speaker (assigns to the selection), then "New speaker…". The current speaker is shown bold.</summary>
    private List<object> SpeakerMenuItems(SpeakerViewModel? current = null)
    {
        var vm = ViewModel;
        var list = new List<object>();
        if (vm?.Session != null)
        {
            foreach (var sp in vm.Session.Speakers)
            {
                var s = sp;
                var mi = Item(s.Name, () => vm.AssignSpeakerCommand.Execute(s), s.ShortcutText.Length > 0 ? s.ShortcutText : null);
                mi.Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = s.ColorBrush };
                if (ReferenceEquals(s, current)) mi.FontWeight = Avalonia.Media.FontWeight.Bold;
                list.Add(mi);
            }
            list.Add(new Separator());
            list.Add(Item("New speaker…", () => vm.NewSpeakerForSelectionCommand.Execute(null)));
        }
        else
        {
            list.Add(new MenuItem { Header = "(no project)", IsEnabled = false });
        }
        return list;
    }

    private void BuildRecentMenu()
    {
        var vm = ViewModel;
        var list = new List<object>();
        if (vm != null)
            foreach (var r in vm.RecentProjects)
            {
                var path = r;
                list.Add(Item(path, () => vm.OpenRecentCommand.Execute(path)));
            }
        if (list.Count == 0) list.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        RecentMenu.ItemsSource = null;
        RecentMenu.Items.Clear();
        RecentMenu.ItemsSource = list;
    }

    // ================================================================== panels and window placement

    private void ApplyPanels()
    {
        var ui = AppHost.Settings.Ui;
        var vm = ViewModel;
        if (vm == null) return;
        SavePanelSizes();
        var showLeft = vm.ShowSpeakersPanel || vm.ShowChapters || vm.ShowAssets;
        var cols = MainGrid.ColumnDefinitions;
        cols[0].Width = showLeft ? new GridLength(Math.Max(150, ui.ChapterPanelWidth)) : new GridLength(0);
        cols[1].Width = showLeft ? new GridLength(4) : new GridLength(0);
        cols[3].Width = vm.ShowInspector ? new GridLength(4) : new GridLength(0);
        cols[4].Width = vm.ShowInspector ? new GridLength(Math.Max(240, ui.InspectorWidth)) : new GridLength(0);
        LeftPanel.IsVisible = showLeft;
        InspectorPanel.IsVisible = vm.ShowInspector;
        ArrangeLeftSections(vm);
    }

    /// <summary>
    /// Stacks the visible left-panel sections with a splitter between neighbours. Chapters fills the
    /// remaining height; when it is hidden, the lowest visible section does.
    /// </summary>
    private void ArrangeLeftSections(MainWindowViewModel vm)
    {
        var ui = AppHost.Settings.Ui;
        var sections = new (Control control, bool show, double height)[]
        {
            (SpeakersSection, vm.ShowSpeakersPanel, ui.SpeakersPanelHeight),
            (ChaptersSection, vm.ShowChapters, 0),
            (AssetsSection, vm.ShowAssets, ui.AssetsPanelHeight),
        };
        var visible = sections.Where(s => s.show).ToList();
        var filler = vm.ShowChapters ? ChaptersSection : visible.LastOrDefault().control;
        var splitters = new[] { SectionSplitter1, SectionSplitter2 };

        LeftGrid.RowDefinitions.Clear();
        foreach (var s in sections) s.control.IsVisible = s.show;
        foreach (var sp in splitters) sp.IsVisible = false;
        for (int i = 0; i < visible.Count; i++)
        {
            if (i > 0)
            {
                LeftGrid.RowDefinitions.Add(new RowDefinition(4, GridUnitType.Pixel));
                splitters[i - 1].IsVisible = true;
                Grid.SetRow(splitters[i - 1], LeftGrid.RowDefinitions.Count - 1);
            }
            var (control, _, height) = visible[i];
            LeftGrid.RowDefinitions.Add(ReferenceEquals(control, filler)
                ? new RowDefinition(1, GridUnitType.Star) { MinHeight = 60 }
                : new RowDefinition(Math.Max(60, height), GridUnitType.Pixel) { MinHeight = 60 });
            Grid.SetRow(control, LeftGrid.RowDefinitions.Count - 1);
        }
    }

    private void SavePanelSizes()
    {
        var ui = AppHost.Settings.Ui;
        var cols = MainGrid.ColumnDefinitions;
        if (cols[0].ActualWidth > 0) ui.ChapterPanelWidth = cols[0].ActualWidth;
        if (cols[4].ActualWidth > 0) ui.InspectorWidth = cols[4].ActualWidth;
        // Only sections with a fixed height are stored; the filling one just takes what is left.
        foreach (var (section, save) in new (Control, Action<double>)[]
                 {
                     (SpeakersSection, h => ui.SpeakersPanelHeight = h),
                     (AssetsSection, h => ui.AssetsPanelHeight = h),
                 })
        {
            if (!section.IsVisible || section.Bounds.Height <= 0) continue;
            var row = Grid.GetRow(section);
            if (row < LeftGrid.RowDefinitions.Count && LeftGrid.RowDefinitions[row].Height.IsAbsolute) save(section.Bounds.Height);
        }
    }

    private void RestoreBounds()
    {
        var b = AppHost.Settings.Ui.WindowBounds;
        if (b == null || b.Width < 400 || b.Height < 300) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(b.X, b.Y);
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var maximized = WindowState == WindowState.Maximized;
        var ui = AppHost.Settings.Ui;
        if (maximized && ui.WindowBounds != null) { ui.WindowBounds.Maximized = true; return; }
        ui.WindowBounds = new Core.Settings.WindowBounds
        {
            X = Position.X, Y = Position.Y, Width = (int)Width, Height = (int)Height, Maximized = maximized,
        };
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || ViewModel is not { } vm) return;
        e.Cancel = true;
        _closingConfirmed = true;
        SaveBounds();
        SavePanelSizes();
        IsEnabled = false;
        Title = "Closing…";
        try { await vm.ShutdownAsync(); }
        catch (Exception ex) { AppHost.LogCrash(ex); }
        Close();
    }

    private async void OnShortcutsClick(object? sender, RoutedEventArgs e)
    {
        await Dialogs.ShowInfo("Keyboard shortcuts",
            "Space  play / pause from selected item\n" +
            "Ctrl+Space  play selected item only\n" +
            "Up / Down  previous / next item (Shift extends)\n" +
            "Alt+Left / Alt+Right  jump to previous / next item\n" +
            "R  regenerate selection\n" +
            "G  generate all stale in chapter (Ctrl+Shift+G: whole book)\n" +
            "[ / ]  previous / next take\n" +
            "1 … 9  assign speaker by list position\n" +
            "L  toggle lock (locked sections can't be changed)\n" +
            "B  toggle background (plays under the next section)\n" +
            "Enter  edit text · Ctrl+Enter save · Esc cancel · Ctrl+Shift+Enter split\n" +
            "Ctrl+Enter (not editing)  insert text segment below\n" +
            "Delete  delete selection\n" +
            "Ctrl+Z / Ctrl+Y  undo / redo\n" +
            "Ctrl+F  search · F3 / Enter next match\n" +
            "Ctrl+M  merge · Ctrl+J  jobs · Ctrl+I import · Ctrl+E export");
    }
}
