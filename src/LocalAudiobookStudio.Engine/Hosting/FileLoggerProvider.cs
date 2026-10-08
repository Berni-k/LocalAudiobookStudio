using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Hosting;

/// <summary>A minimal file sink for Microsoft.Extensions.Logging: one log file per session.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly BlockingCollection<string> _queue = new(10000);
    private readonly Thread _thread;

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FilePath = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _thread = new Thread(Pump) { IsBackground = true, Name = "log writer" };
        _thread.Start();
    }

    public string FilePath { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void Pump()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { _writer.WriteLine(line); } catch { /* disk full etc. */ }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
        _writer.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Level(logLevel)}] {shortCategory}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            owner.Write(line);
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "   ",
        };
    }
}
