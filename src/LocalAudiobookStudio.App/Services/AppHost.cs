using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.Services;

/// <summary>
/// The app's one <see cref="StudioHost"/> (settings, logging, worker catalog, environments, devices and
/// the scheduler), plus the worker log kept for the Jobs panel.
/// </summary>
public static class AppHost
{
    private static readonly AppSettings PreStartSettings = new();

    /// <summary>Null until <see cref="Initialize"/> (the setup window runs before it).</summary>
    public static StudioHost? Host { get; private set; }

    public static AppSettings Settings => Host?.Settings ?? PreStartSettings;
    public static ILoggerFactory LoggerFactory => Host?.LoggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    public static ILogger Log => Host?.Log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public static DataRootPaths DataRoot => Host?.DataRoot ?? new(SettingsStore.DefaultDataRoot);
    public static WorkerCatalog Catalog => Host!.Catalog;
    public static EnvironmentManager Environments => Host!.Environments;
    public static GenerationScheduler Scheduler => Host!.Scheduler;
    public static LocalAudiobookStudio.Workers.Models.ModelStore Models => Host!.Models;
    public static IReadOnlyList<GpuInfo> Gpus => Host?.Gpus ?? [];
    public static string? LogFilePath => Host?.LogFilePath;

    /// <summary>Worker log lines (all workers), kept for the Jobs panel.</summary>
    public static WorkerLogBuffer WorkerLogs { get; } = new(2000);

    public static void Initialize()
    {
        Host = StudioHost.Start();
        Host.WorkerLogLine += WorkerLogs.Add;
    }

    public static void SetWorkerSettings(string workerId, JsonObject values) => Host!.SetWorkerSettings(workerId, values);
    public static string? RuntimeOf(string workerId) => Host?.RuntimeOf(workerId);
    public static void SaveSettings() => Host?.SaveSettings();
    public static WorkerHosting HostingOf(string workerId) => Host!.HostingOf(workerId);
    public static string? WhyWorkerCannotRun(string workerId) => Host!.WhyWorkerCannotRun(workerId);
    public static string DeviceName(DeviceSettings s) => Host!.DeviceName(s);
    public static void ApplyDeviceSettings() => Host!.ApplyDeviceSettings();
    public static Task DetectGpusAsync(bool addMissing) => Host!.DetectGpusAsync(addMissing);
    public static void LogCrash(Exception? ex) => StudioHost.LogCrash(Log, DataRoot.Logs, ex);
    public static Task ShutdownAsync() => Host?.ShutdownAsync() ?? Task.CompletedTask;
}

/// <summary>A thread-safe ring buffer of log lines with a change notification.</summary>
public sealed class WorkerLogBuffer(int capacity)
{
    private readonly Queue<string> _lines = new();
    public event Action<string>? LineAdded;

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
        }
        LineAdded?.Invoke(line);
    }

    public string[] Snapshot()
    {
        lock (_lines) return _lines.ToArray();
    }
}
