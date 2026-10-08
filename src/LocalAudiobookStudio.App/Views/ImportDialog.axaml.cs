using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Views;

public partial class ImportDialog : Window
{
    public ImportDialog() => InitializeComponent();

    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
