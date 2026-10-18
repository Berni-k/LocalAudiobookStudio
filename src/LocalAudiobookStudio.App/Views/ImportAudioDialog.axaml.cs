using Avalonia.Controls;
using LocalAudiobookStudio.App.ViewModels;

namespace LocalAudiobookStudio.App.Views;

public partial class ImportAudioDialog : Window
{
    private ImportAudioViewModel? _vm;

    public ImportAudioDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.CloseRequested -= OnCloseRequested;
            _vm = DataContext as ImportAudioViewModel;
            if (_vm != null) _vm.CloseRequested += OnCloseRequested;
        };
    }

    private void OnCloseRequested(bool imported) => Close(imported);

    // Closing the window any way (the X, Escape) aborts whatever is running.
    protected override void OnClosed(EventArgs e)
    {
        if (_vm != null) _vm.CloseRequested -= OnCloseRequested;
        _vm?.Dispose();
        base.OnClosed(e);
    }
}
