using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.App.ViewModels;
using LocalAudiobookStudio.App.Views;
using LocalAudiobookStudio.Engine.Export;

namespace LocalAudiobookStudio.App.Services;

/// <summary>Opens the app's windows and dialogs (keeps window creation out of the view models' logic).</summary>
public static class WindowService
{
    private static JobsWindow? _jobs;
    private static WorkersDialog? _workers;
    private static OneShotWindow? _oneShot;

    private static Window? Owner => Dialogs.Owner;

    private static async Task<T?> ShowDialog<T>(Window dialog)
    {
        var owner = Owner;
        if (owner == null)
        {
            dialog.Show();
            return default;
        }
        return await dialog.ShowDialog<T?>(owner);
    }

    public static async Task<bool> ShowImportAsync(ImportViewModel vm) =>
        await ShowDialog<bool>(new ImportDialog { DataContext = vm });

    /// <summary>The Import audio dialog (modal). True when the recording was added to the project.</summary>
    public static async Task<bool> ShowImportAudioAsync(ImportAudioViewModel vm) =>
        await ShowDialog<bool>(new ImportAudioDialog { DataContext = vm });

    public static Task ShowExportAsync(ExportViewModel vm) => ShowDialog<object>(new ExportDialog { DataContext = vm });

    /// <param name="speaker">The speaker to show first (e.g. the one double-clicked); null shows the first.</param>
    public static Task ShowSpeakersAsync(ProjectSession session, SpeakerViewModel? speaker = null)
    {
        var main = (Owner?.DataContext as MainWindowViewModel) ?? (Dialogs.Owner?.DataContext as MainWindowViewModel);
        var vm = new SpeakersDialogViewModel(session, main, main?.AssetFiles);
        if (speaker != null && session.Speakers.Contains(speaker)) vm.Selected = speaker;
        return ShowDialog<object>(new SpeakersDialog { DataContext = vm });
    }

    /// <summary>
    /// Opens the generation inspector for a take (non-modal, one window each): its kept fused generation, with the
    /// segment's part highlighted, else the take's own audio and transcript. Returns false when there is nothing to
    /// show (the record is gone, deleted or kept by another copy of the project, and no transcript).
    /// </summary>
    public static bool ShowGenerationInspector(ProjectSession session, ItemViewModel item, Core.Model.TakeInfo take)
    {
        if ((Owner?.DataContext ?? Dialogs.Owner?.DataContext) is not MainWindowViewModel main) return false;
        GenerationInspectorViewModel vm;
        if (take.Fusion?.Record is { } id && File.Exists(Path.Combine(session.Paths.FusionRecordDir(id), Core.Model.FusionRecord.RecordFile)))
            vm = new GenerationInspectorViewModel(main, session, id, item.Id);
        else if (File.Exists(session.Paths.TakeTranscript(item.Id, take.Take)))
            vm = new GenerationInspectorViewModel(main, session, item, take);
        else return false;

        var window = new GenerationInspectorWindow { DataContext = vm };
        if (Owner is { } owner) window.Show(owner);
        else window.Show();
        return true;
    }

    public static void ShowJobs(MainWindowViewModel main)
    {
        if (_jobs != null)
        {
            _jobs.Activate();
            return;
        }
        _jobs = new JobsWindow { DataContext = new JobsViewModel(main) };
        _jobs.Closed += (_, _) => _jobs = null;
        _jobs.Show();
    }

    /// <summary>The One shot window (one at a time, not modal), on the tab of <paramref name="kind"/>.</summary>
    public static void ShowOneShot(OneShotKind kind)
    {
        if (_oneShot != null)
        {
            (_oneShot.DataContext as OneShotViewModel)?.Show(kind);
            _oneShot.Activate();
            return;
        }
        var main = (Owner?.DataContext as MainWindowViewModel) ?? (Dialogs.Owner?.DataContext as MainWindowViewModel);
        var vm = new OneShotViewModel(main);
        vm.Show(kind);
        _oneShot = new OneShotWindow { DataContext = vm };
        _oneShot.Closed += (_, _) => _oneShot = null;
        _oneShot.Show();
    }

    public static async Task ShowWorkersAsync(string? installWorker = null)
    {
        if (_workers != null)
        {
            _workers.Activate();
            return;
        }
        var vm = new WorkersDialogViewModel();
        _workers = new WorkersDialog { DataContext = vm };
        _workers.Closed += (_, _) => _workers = null;
        if (installWorker != null) _workers.Opened += async (_, _) => await vm.InstallWorkerAsync(installWorker);
        await ShowDialog<object>(_workers);
    }

    public static async Task ShowSettingsAsync() =>
        await ShowDialog<bool>(new SettingsDialog { DataContext = new AppSettingsViewModel() });

    public static async Task ShowProjectPropertiesAsync(ProjectSession session) =>
        await ShowDialog<bool>(new ProjectPropertiesDialog { DataContext = new ProjectPropertiesViewModel(session) });

    /// <summary>Options for packing a project; null when cancelled.</summary>
    public static async Task<PackOptions?> ShowPackOptionsAsync()
    {
        var selectedOnly = new CheckBox { Content = "Selected takes only (smaller file)", IsChecked = false };
        var includeExport = new CheckBox { Content = "Include the export folder", IsChecked = false };
        PackOptions? result = null;
        var window = new Window
        {
            Title = "Pack project",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var ok = new Button { Content = "Pack…", IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            result = new PackOptions { SelectedTakesOnly = selectedOnly.IsChecked == true, IncludeExport = includeExport.IsChecked == true };
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Creates one .abproj file (a zip) for sharing or backup. The .cache folder is never included.", TextWrapping = TextWrapping.Wrap },
                selectedOnly,
                includeExport,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        await ShowDialog<object>(window);
        return result;
    }

    /// <summary>Runs a cancellable task with a progress dialog. Returns true on success.</summary>
    public static async Task<bool> RunWithProgressAsync(string title, Func<IProgress<(double fraction, string text)>, CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
        var label = new TextBlock { Text = "", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = { bar, label, cancel } },
        };
        cancel.Click += (_, _) => cts.Cancel();
        var progress = new Progress<(double fraction, string text)>(p => { bar.Value = p.fraction; label.Text = p.text; });

        Exception? error = null;
        var owner = Owner;
        window.Opened += async (_, _) =>
        {
            try { await Task.Run(() => work(progress, cts.Token)); }
            catch (Exception ex) { error = ex; }
            window.Close();
        };
        if (owner != null) await window.ShowDialog(owner);
        if (error is OperationCanceledException) return false;
        if (error != null)
        {
            await Dialogs.ShowError(title, error.Message);
            return false;
        }
        return true;
    }

    public static Task ShowAboutAsync() => ShowDialog<object>(new AboutWindow { DataContext = new AboutViewModel() });

    public static Task ShowSystemCheckAsync() => ShowDialog<object>(new SystemCheckWindow { DataContext = new SystemCheckViewModel() });
}
