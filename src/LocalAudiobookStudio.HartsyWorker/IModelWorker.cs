using System.Text.Json.Nodes;
using HartsyInference.Engine;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>One model behind the worker protocol: <see cref="SpeechWorker"/> or <see cref="MusicWorker"/>.</summary>
internal interface IModelWorker : IDisposable
{
    /// <summary>Handles "load": downloads (then sends "prepared"), loads, sends "ready".</summary>
    Task LoadAsync(string? id);

    /// <summary>Handles "generate": writes the WAV and sends "done".</summary>
    Task GenerateAsync(string? id, JsonObject msg);

    /// <summary>Cancels the running request (a "cancel" message, read on the stdin thread).</summary>
    void Cancel(string? id);
}

/// <summary>The device this process runs on, as the app set it up (like protocol.py's torch_device).</summary>
internal static class WorkerDevice
{
    public static bool OnCpu => Environment.GetEnvironmentVariable("LAS_DEVICE") == "cpu";

    /// <summary>CUDA_VISIBLE_DEVICES already narrows the process to its GPU, so ordinal 0 is that GPU.</summary>
    public static InferenceEngine CreateEngine() => OnCpu ? new InferenceEngine("cpu") : new InferenceEngine("cuda", 0);

    public static string Description()
    {
        if (OnCpu) return "cpu";
        var cvd = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        return cvd == null ? "cuda:0 (all GPUs visible)" : $"cuda:0 (GPU {cvd})";
    }

    /// <summary>Errors after which the process should be restarted (the device may be in a bad state).</summary>
    public static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or InsufficientExecutionStackException
        || ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || ex.GetType().Name.Contains("Cuda", StringComparison.Ordinal);
}
