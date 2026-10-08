using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Views;

public partial class SystemCheckWindow : Window
{
    public SystemCheckWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
