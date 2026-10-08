using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// One line of the system check (setup window, Help → System check) with its fix, if any: a download button
/// (ffmpeg, uv) with progress, or a command to copy (ffmpeg on Linux, which needs the package manager).
/// </summary>
public sealed partial class SystemCheckRow(SystemCheck check, Func<string> dataRoot, Func<CheckFixKind, string, Task> fixedAsync)
    : ObservableObject
{
    public SystemCheck Check { get; } = check;
    public CheckFix? Fix => Check.Fix;
    public bool HasDownload => Fix is { Kind: CheckFixKind.DownloadFfmpeg or CheckFixKind.DownloadUv };
    public bool HasCommand => Fix is { Kind: CheckFixKind.RunCommand, Command: not null };
    public string? Command => Fix?.Command;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isBusy;

    [ObservableProperty] private string? _progress;
    [ObservableProperty] private string? _error;

    public bool CanDownload => HasDownload && !IsBusy;

    [RelayCommand]
    private async Task Download()
    {
        if (Fix is not { } fix || IsBusy) return;
        IsBusy = true;
        Error = null;
        var progress = new Progress<string>(p => Progress = p);
        try
        {
            var root = dataRoot();
            var path = fix.Kind == CheckFixKind.DownloadUv
                ? await ToolInstaller.DownloadUvAsync(root, progress, CancellationToken.None)
                : await ToolInstaller.DownloadFfmpegAsync(root, progress, CancellationToken.None);
            Progress = null;
            AppHost.Log.LogInformation("Downloaded {Tool} to {Path}", fix.Kind, path);
            await fixedAsync(fix.Kind, path);
        }
        catch (Exception ex)
        {
            AppHost.Log.LogWarning(ex, "Download for {Check} failed", Check.Title);
            Progress = null;
            Error = "Download failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Copy()
    {
        if (Command != null && Dialogs.Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(Command);
    }
}
