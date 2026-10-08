using System.Reflection;

namespace LocalAudiobookStudio.Engine.Hosting;

/// <summary>
/// The app's version (Directory.Build.props, bumped by hand for a release) and the git commit it was built from.
/// The SDK stamps the commit into the informational version ("0.1.0+6a4c02e9f10…"); every project shares both.
/// </summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>"0.1.0".</summary>
    public static string Number { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>The short commit hash ("6a4c02e"), or null when built outside a git checkout.</summary>
    public static string? Commit { get; } =
        Informational.IndexOf('+') is var plus and >= 0 && Informational.Length > plus + 1
            ? Informational[(plus + 1)..][..Math.Min(7, Informational.Length - plus - 1)]
            : null;

    /// <summary>"0.1.0 (6a4c02e)", or just "0.1.0" without a commit.</summary>
    public static string Display => Commit != null ? $"{Number} ({Commit})" : Number;
}
