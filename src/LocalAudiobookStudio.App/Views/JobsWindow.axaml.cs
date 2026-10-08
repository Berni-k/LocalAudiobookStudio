using Avalonia.Controls;

namespace LocalAudiobookStudio.App.Views;

public partial class JobsWindow : Window
{
    public JobsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnCopyLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Log.CopyAllAsync();
}
