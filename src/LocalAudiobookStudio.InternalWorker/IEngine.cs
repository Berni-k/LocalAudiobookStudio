using System.Text.Json.Nodes;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// One engine of the internal worker: a kind of model and what runs it (whisper.cpp, ONNX Runtime). Registered by name
/// in <see cref="Engines"/>; a worker's manifest names it ("engine") and says which models it loads ("engineConfig").
/// Engines derive from <see cref="EngineBase"/>, which tracks requests for cancelling and writes generated audio.
/// </summary>
internal interface IEngine : IDisposable
{
    /// <summary>The install check: the engine's native libraries load. Prints to stderr; 0 when they do.</summary>
    int SelfTest();

    /// <summary>For hello, before anything is loaded: where this process runs ("cpu", "gpu 1 …").</summary>
    string DeviceDescription { get; }

    /// <summary>Gets the model from the app (needModel), sends prepared, loads it and sends ready.</summary>
    Task LoadAsync(string? id);

    /// <summary>A <c>transcribe</c> request: sends the transcript (text and timed words).</summary>
    Task TranscribeAsync(string? id, JsonObject msg);

    /// <summary>A <c>generate</c> request: writes the request's outPath and sends done.</summary>
    Task GenerateAsync(string? id, JsonObject msg);

    /// <summary>Cancels the running request (any request when <paramref name="id"/> is null).</summary>
    void Cancel(string? id);

    /// <summary>Errors after which the process should be restarted (the device may be in a bad state).</summary>
    bool IsFatal(Exception ex);
}
