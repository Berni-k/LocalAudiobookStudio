using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Engine.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Cli;

/// <summary>
/// One run of las: the global options, Ctrl+C, output (results on stdout, progress on stderr) and the
/// studio host, started on first use with the app's settings (read only) and data folder.
/// </summary>
public sealed class CliContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public CliContext(bool verbose, bool quiet, bool json, CancellationToken cancel)
    {
        Verbose = verbose;
        Quiet = quiet;
        Json = json;
        Cancel = cancel;
    }

    public bool Verbose { get; }
    public bool Quiet { get; }
    public bool Json { get; }
    public CancellationToken Cancel { get; }
    public StudioHost? Host { get; private set; }

    /// <summary>Starts the host (once): the app must have been set up on this machine.</summary>
    public async Task<StudioHost> StartHostAsync()
    {
        if (Host != null) return Host;
        var mode = SettingsStore.DetectMode()
                   ?? throw new SetupException("Local Audiobook Studio hasn't been set up on this machine. Start the app once and finish its setup.");
        SettingsStore.UseMode(mode);
        Host = StudioHost.Start(new StudioHostOptions
        {
            LogPrefix = "cli",
            ReadOnlySettings = true,
            ExtraLogProvider = Verbose ? new ConsoleLoggerProvider() : null,
        });
        if (Verbose) Host.WorkerLogLine += line => Console.Error.WriteLine(line);
        await Host.DetectGpusAsync(addMissing: false);
        return Host;
    }

    /// <summary>Progress and messages (stderr), unless --quiet.</summary>
    public void Info(string line)
    {
        if (!Quiet) Console.Error.WriteLine(line);
    }

    /// <summary>Details (stderr) only with --verbose.</summary>
    public void Detail(string line)
    {
        if (Verbose) Console.Error.WriteLine(line);
    }

    public static void Error(string line) => Console.Error.WriteLine(line);

    /// <summary>A result for scripts: text lines without --json.</summary>
    public void Out(string line)
    {
        if (!Json) Console.Out.WriteLine(line);
    }

    /// <summary>The run's JSON result (with --json).</summary>
    public void WriteJson(object value)
    {
        if (Json) Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    /// <summary>Writes log messages from Information up to stderr (--verbose).</summary>
    private sealed class ConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
        public void Dispose() { }

        private sealed class ConsoleLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var level = logLevel switch { LogLevel.Warning => "warn", LogLevel.Error => "error", LogLevel.Critical => "crit", _ => "info" };
                Console.Error.WriteLine($"{level}: {category}: {formatter(state, exception)}{(exception != null ? " — " + exception.Message : "")}");
            }
        }
    }
}
