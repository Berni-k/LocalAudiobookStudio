using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        (DataContext as AppSettingsViewModel)?.Apply();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
