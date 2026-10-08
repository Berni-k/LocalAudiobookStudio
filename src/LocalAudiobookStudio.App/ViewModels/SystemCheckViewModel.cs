using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>Help → System check: the setup window's checks (GPU, driver, tools, audio, disk), run again on demand.</summary>
public sealed partial class SystemCheckViewModel : ObservableObject
{
    public SystemCheckViewModel() => _ = RunChecksAsync();

    public ObservableCollection<SystemCheckRow> Checks { get; } = new();
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _summary = "Checking…";

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        Summary = "Checking…";
        try
        {
            var dataRoot = AppHost.DataRoot.Root;
            var results = await SystemChecks.RunAsync(AppHost.Settings.FfmpegPath, dataRoot, Devices.DeviceChecks.Audio);
            Checks.Clear();
            foreach (var c in results.Append(SystemChecks.DiskCheck(dataRoot)))
                Checks.Add(new SystemCheckRow(c, () => AppHost.DataRoot.Root, OnFixedAsync));
            var missing = Checks.Count(c => c.Check.IsMissing);
            var warnings = Checks.Count(c => c.Check.IsWarning);
            Summary = missing + warnings == 0
                ? "Everything the app needs is in place."
                : string.Join(", ", new[] { Count(missing, "problem"), Count(warnings, "warning") }.Where(s => s != null)) + ".";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>After a download: a new uv is used from now on; the checks run again to show the result.</summary>
    private async Task OnFixedAsync(CheckFixKind kind, string path)
    {
        if (kind == CheckFixKind.DownloadUv) AppHost.Host?.Environments.UseUv(path);
        await RunChecksAsync();
    }

    private static string? Count(int n, string what) => n == 0 ? null : $"{n} {what}{(n == 1 ? "" : "s")}";

    /// <summary>The results as plain text, for a bug report.</summary>
    [RelayCommand]
    private async Task CopyReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Local Audiobook Studio {AppVersion.Display} system check");
        foreach (var c in Checks.Select(r => r.Check))
        {
            sb.AppendLine($"[{c.Status}] {c.Title}");
            foreach (var line in c.Lines) sb.AppendLine("    " + line);
        }
        if (Dialogs.Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(sb.ToString());
    }
}
