using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Views;

/// <summary>The setup window. <see cref="Confirmed"/> is true after Continue; Quit or closing leaves it false.</summary>
public partial class SetupDialog : Window
{
    public SetupDialog() => InitializeComponent();

    public bool Confirmed { get; private set; }

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();
}
