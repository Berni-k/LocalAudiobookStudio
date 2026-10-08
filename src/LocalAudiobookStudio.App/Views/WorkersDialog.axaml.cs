using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Views;

public partial class WorkersDialog : Window
{
    public WorkersDialog() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyLog(object? sender, RoutedEventArgs e) => await Log.CopyAllAsync();
}
