using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

public partial class OneShotWindow : Window
{
    public OneShotWindow()
    {
        InitializeComponent();
        // Handled too: a text box takes the drop itself and would otherwise swallow it.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The field under the pointer that takes files (marked with a Tag naming its role) and the files dragged onto it.</summary>
    private static (Control Field, string Role, List<string> Paths)? FieldAt(DragEventArgs e)
    {
        if (e.Source is not Visual source || e.DataTransfer.TryGetFiles() is not { } files) return null;
        var field = source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Tag is string);
        if (field?.Tag is not string role) return null;
        return (field, role, files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList());
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = FieldAt(e) is { } f && f.Field.DataContext is OneShotTabViewModel tab && tab.AcceptsDrop(f.Role, f.Paths)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (FieldAt(e) is not { } f || f.Field.DataContext is not OneShotTabViewModel tab || !tab.AcceptsDrop(f.Role, f.Paths)) return;
        try
        {
            tab.Drop(f.Role, f.Paths);
        }
        catch (Exception ex)
        {
            tab.Status = "Can't use the dropped file: " + ex.Message;
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as OneShotViewModel)?.Close();
        base.OnClosed(e);
    }
}
