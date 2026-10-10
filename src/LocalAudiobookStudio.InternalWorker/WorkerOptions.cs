using System.Text.Json.Nodes;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>Command line and environment of one worker process.</summary>
internal sealed class WorkerOptions
{
    /// <summary>The app's worker id (for hello), e.g. "whisper".</summary>
    public string WorkerId { get; private set; } = "";

    public string ModelVersion { get; private set; } = "";

    /// <summary>The engine (<see cref="Engines"/>): "whisper", "tdt".</summary>
    public string Engine { get; private set; } = "";

    /// <summary>The engine's folder in the GPU runtime (its downloaded GPU builds), or null for the CPU builds inside the exe.</summary>
    public string? GpuFolder { get; private set; }

    public bool SelfTest { get; private set; }

    public static WorkerOptions Parse(string[] args)
    {
        var o = new WorkerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--worker": o.WorkerId = Next(); break;
                case "--model-version": o.ModelVersion = Next(); break;
                case "--engine": o.Engine = Next().ToLowerInvariant(); break;
                case "--gpu-folder": o.GpuFolder = Next(); break;
                case "--selftest": o.SelfTest = true; break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (o.Engine.Length == 0) throw new ArgumentException("--engine is required");
        return o;
    }

    /// <summary>This worker's machine settings from the Workers window (LAS_WORKER_SETTINGS), with the defaults filled in.</summary>
    public static JsonObject Settings() => JsonFromEnvironment("LAS_WORKER_SETTINGS");

    /// <summary>The manifest's engineConfig (LAS_ENGINE_CONFIG): which models.json id and files each model choice loads.</summary>
    public static JsonObject EngineConfig() => JsonFromEnvironment("LAS_ENGINE_CONFIG");

    private static JsonObject JsonFromEnvironment(string variable)
    {
        try
        {
            return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } raw && JsonNode.Parse(raw) is JsonObject o
                ? o
                : new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"ignoring unparseable {variable}");
            return new JsonObject();
        }
    }
}
