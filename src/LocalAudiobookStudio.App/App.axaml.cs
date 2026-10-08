using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.ViewModels;
using LocalAudiobookStudio.App.Views;
using LocalAudiobookStudio.Core.Settings;

namespace LocalAudiobookStudio.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DarkTitleBar.Register();
            ClickThroughToolTips.Register();
            var mode = SettingsStore.DetectMode();
            if (mode != null) SettingsStore.UseMode(mode.Value);
            var settings = mode != null ? SettingsStore.Load() : new AppSettings();
            if (mode != null && settings.FirstRunCompleted) ShowMainWindow(desktop, installWorker: null, openProject: null);
            else ShowSetup(desktop, mode, settings);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The setup window, before anything is written on a first start (so a portable copy leaves no trace in
    /// the user profile). The main window opens after Continue.
    /// </summary>
    private static void ShowSetup(IClassicDesktopStyleApplicationLifetime desktop, StorageMode? mode, AppSettings settings)
    {
        var vm = new SetupViewModel(mode, settings);
        var dialog = new SetupDialog { DataContext = vm };
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        dialog.Closed += (_, _) =>
        {
            if (!dialog.Confirmed)
            {
                desktop.Shutdown();
                return;
            }
            vm.Apply();
            ShowMainWindow(desktop, vm.InstallKokoro && vm.CanInstallKokoro ? "kokoro" : null, vm.OpenExample ? vm.ExampleProject : null);
            desktop.MainWindow!.Show();
        };
        desktop.MainWindow = dialog;
    }

    /// <param name="installWorker">A worker to install once the main window is up (setup's quick test), or null.</param>
    /// <param name="openProject">A project to open instead of the most recent one (setup's example project), or null.</param>
    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, string? installWorker, string? openProject)
    {
        AppHost.Initialize();
        var vm = new MainWindowViewModel();
        desktop.MainWindow = new MainWindow { DataContext = vm };
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var args = desktop.Args ?? [];
        desktop.MainWindow.Opened += async (_, _) => await vm.OnStartupAsync(args, installWorker, openProject);
    }
}
