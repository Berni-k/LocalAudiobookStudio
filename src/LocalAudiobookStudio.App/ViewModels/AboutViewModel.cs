using System.Text;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Core.Settings;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>A label and value in the About window (a version, a tool's path).</summary>
public sealed record AboutRow(string Label, string Value);

/// <summary>A folder or file in the About window, with a button that shows it in the file manager.</summary>
public sealed partial class AboutLocation(string label, string path)
{
    public string Label { get; } = label;
    public string Path { get; } = path;

    [RelayCommand]
    private void Open() => Dialogs.Reveal(Path);
}

/// <summary>Help → About: version, components, where things are, and the system check.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    public AboutViewModel()
    {
        Components =
        [
            new(".NET", Environment.Version.ToString()),
            new("Avalonia", VersionOf(typeof(Application))),
            new("NAudio", VersionOf(typeof(NAudio.Wave.WaveFormat))),
            new("ffmpeg", Ffmpeg.Locate(AppHost.Settings.FfmpegPath, AppHost.DataRoot.Root) ?? "not found"),
        ];
        Locations =
        [
            new("Data folder", AppHost.DataRoot.Root),
            new("Settings", SettingsStore.SettingsFile),
            new("Logs", AppHost.DataRoot.Logs),
            new("App folder", SettingsStore.AppDir),
        ];
        _ = LoadUvVersionAsync();
        _ = LoadHartsyVersionAsync();
    }

    /// <summary>"Version 0.1.0 (6a4c02e)": the number and the commit the build is from.</summary>
    public string Version { get; } = "Version " + AppVersion.Display +
                                     (SettingsStore.Mode == StorageMode.Portable ? " · portable" : "");

    public IReadOnlyList<AboutRow> Components { get; }
    public IReadOnlyList<AboutLocation> Locations { get; }
    [ObservableProperty] private string _uvVersion = "checking…";
    /// <summary>"2.0.0-alpha.245 · hartsy\las-hartsy-worker.exe": the library version and where the worker is.</summary>
    [ObservableProperty] private string _hartsyVersion = "checking…";

    /// <summary>"Copyright (c) 2026 Berni · MIT License" (the copyright from Directory.Build.props).</summary>
    public string Copyright { get; } =
        (typeof(AboutViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyCopyrightAttribute), false)
            .OfType<System.Reflection.AssemblyCopyrightAttribute>().FirstOrDefault()?.Copyright ?? "") + " · MIT License";

    private static string VersionOf(Type t) => t.Assembly.GetName().Version?.ToString(3) ?? "?";

    private async Task LoadUvVersionAsync() => UvVersion = await AppHost.Environments.GetUvVersionAsync() ?? "not found";

    private async Task LoadHartsyVersionAsync()
    {
        if (AppHost.Environments.HartsyWorkerPath is not { } path) { HartsyVersion = "not found"; return; }
        var version = await AppHost.Environments.GetHartsyVersionAsync() ?? "version unknown";
        HartsyVersion = $"{version} · {Path.GetRelativePath(SettingsStore.AppDir, path)}";
    }

    [RelayCommand]
    private Task SystemCheck() => WindowService.ShowSystemCheckAsync();

    /// <summary>Everything shown, as plain text (for a bug report).</summary>
    [RelayCommand]
    private async Task CopyInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Local Audiobook Studio, {Version} ({Copyright})");
        foreach (var c in Components) sb.AppendLine($"{c.Label}: {c.Value}");
        sb.AppendLine($"uv: {UvVersion}");
        sb.AppendLine($"HartsyInference: {HartsyVersion}");
        foreach (var l in Locations) sb.AppendLine($"{l.Label}: {l.Path}");
        if (Dialogs.Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(sb.ToString());
    }
}
