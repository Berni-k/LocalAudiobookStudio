using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

public partial class SpeakersDialog : Window
{
    public SpeakersDialog() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SpeakersDialogViewModel)?.Close();
        base.OnClosed(e);
    }

    /// <summary>Reference clip menu: from a file, from the project's assets (current one checked), or none.
    /// Built on each click so it always lists the current assets.</summary>
    private void OnChooseVoice(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SpeakersDialogViewModel vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(new MenuItem { Header = "From file…", Command = vm.BrowseVoiceFileCommand });
        if (vm.Assets.Any(a => a.IsAudio))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "From assets", IsEnabled = false });
            foreach (var a in vm.Assets.Where(a => a.IsAudio))
            {
                menu.Items.Add(new MenuItem
                {
                    Header = a.DurationMs > 0 ? $"{a.DisplayPath}   ({a.DurationText})" : a.DisplayPath,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = string.Equals(vm.VoiceFile, a.RelativePath, StringComparison.OrdinalIgnoreCase),
                    Command = vm.UseAssetCommand,
                    CommandParameter = a,
                });
            }
        }
        if (vm.VoiceFile != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "No reference clip", Command = vm.ClearVoiceCommand });
        }
        menu.ShowAt(button);
    }
    /// <summary>Reference clip menu of speaker verification: from a file, from the project's assets (current one checked), or none.</summary>
    private void OnChooseSimReference(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not SpeakersDialogViewModel vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(new MenuItem { Header = "From file…", Command = vm.BrowseSimReferenceCommand });
        if (vm.Assets.Any(a => a.IsAudio))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "From assets", IsEnabled = false });
            foreach (var a in vm.Assets.Where(a => a.IsAudio))
            {
                menu.Items.Add(new MenuItem
                {
                    Header = a.DurationMs > 0 ? $"{a.DisplayPath}   ({a.DurationText})" : a.DisplayPath,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = string.Equals(vm.SimReferenceFile, a.RelativePath, StringComparison.OrdinalIgnoreCase),
                    Command = vm.UseSimAssetCommand,
                    CommandParameter = a,
                });
            }
        }
        if (vm.SimReferenceFile != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "No clip (use Generation's)", Command = vm.ClearSimReferenceCommand });
        }
        menu.ShowAt(button);
    }
}
