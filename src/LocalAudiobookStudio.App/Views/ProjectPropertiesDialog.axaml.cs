using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

public partial class ProjectPropertiesDialog : Window
{
    public ProjectPropertiesDialog() => InitializeComponent();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        (DataContext as ProjectPropertiesViewModel)?.Apply();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
