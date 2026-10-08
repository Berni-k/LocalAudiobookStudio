using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// A log view (Workers window, Jobs window): <see cref="Lines"/> as dense monospace lines that follow the end
/// as lines arrive, unless lines are selected (so a selection being copied doesn't scroll away).
/// </summary>
public partial class LogView : UserControl
{
    public static readonly StyledProperty<IList<string>?> LinesProperty =
        AvaloniaProperty.Register<LogView, IList<string>?>(nameof(Lines));

    public LogView() => InitializeComponent();

    public IList<string>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LinesProperty) return;
        if (change.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OnLinesChanged;
        if (change.NewValue is INotifyCollectionChanged now) now.CollectionChanged += OnLinesChanged;
        List.ItemsSource = Lines;
    }

    private bool _scrollPending;

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending || List.SelectedItems?.Count > 0) return;
        // Once per burst (an install prints hundreds of lines at a time), after layout has the new items.
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (Lines is { Count: > 0 } lines) List.ScrollIntoView(lines.Count - 1);
        }, DispatcherPriority.Background);
    }

    /// <summary>Copies every line (the "Copy log" buttons).</summary>
    public Task CopyAllAsync() => SetClipboardAsync(Lines ?? []);

    private Task CopySelectedAsync()
    {
        var lines = Lines;
        var indexes = List.Selection.SelectedIndexes;
        if (lines == null || indexes.Count == 0) return CopyAllAsync();
        // In log order, whatever order the lines were clicked in.
        return SetClipboardAsync(indexes.Where(i => i >= 0 && i < lines.Count).Order().Select(i => lines[i]).ToList());
    }

    private async Task SetClipboardAsync(IEnumerable<string> lines)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl) return;
        if (e.Key == Key.C)
        {
            e.Handled = true;
            await CopySelectedAsync();
        }
        else if (e.Key == Key.A)
        {
            e.Handled = true;
            List.SelectAll();
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e) => await CopySelectedAsync();
    private async void OnCopyAll(object? sender, RoutedEventArgs e) => await CopyAllAsync();
    private void OnSelectAll(object? sender, RoutedEventArgs e) => List.SelectAll();
}
