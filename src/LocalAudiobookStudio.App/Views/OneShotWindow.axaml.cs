using Avalonia.Controls;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

public partial class OneShotWindow : Window
{
    public OneShotWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as OneShotViewModel)?.Close();
        base.OnClosed(e);
    }
}
