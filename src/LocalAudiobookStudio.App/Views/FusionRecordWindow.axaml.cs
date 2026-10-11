using Avalonia.Controls;

namespace LocalAudiobookStudio.App.Views;

public partial class FusionRecordWindow : Window
{
    public FusionRecordWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
