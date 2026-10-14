namespace LocalAudiobookStudio.Cli;

/// <summary>
/// Where a command's output goes and its input comes from: this process's console, or a client of the las proxy
/// (<see cref="Proxy.ProxyServer"/>), whose console is in another process.
/// </summary>
public sealed class CliIo
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }

    /// <summary>Raw stdout, for audio written to it (tts -o -).</summary>
    public required Func<Stream> OpenStdout { get; init; }

    public bool IsInputRedirected { get; init; }

    /// <summary>All of stdin. Only read when a command needs it: a script's loop may be reading the same stdin.</summary>
    public required Func<string> ReadStdin { get; init; }

    public static CliIo ForConsole() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        OpenStdout = Console.OpenStandardOutput,
        IsInputRedirected = Console.IsInputRedirected,
        ReadStdin = () => Console.In.ReadToEnd(),
    };
}
