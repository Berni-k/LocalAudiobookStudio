using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Views;

public partial class ExportDialog : Window
{
    public ExportDialog() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
