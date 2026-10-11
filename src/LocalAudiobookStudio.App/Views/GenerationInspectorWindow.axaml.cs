using Avalonia.Controls;

namespace LocalAudiobookStudio.App.Views;

public partial class GenerationInspectorWindow : Window
{
    public GenerationInspectorWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
