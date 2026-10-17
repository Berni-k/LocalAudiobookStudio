using System.Text.Json.Nodes;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// One engine of the internal worker: a kind of model and what runs it (whisper.cpp, ONNX Runtime). Registered by name
/// in <see cref="Engines"/>; a worker's manifest names it ("engine") and says which models it loads ("engineConfig").
/// Engines derive from <see cref="EngineBase"/>, which tracks requests for cancelling and writes generated audio.
/// </summary>
/// <summary>What a listening request asks for (the protocol's message type).</summary>
internal enum ListenMode
{
    /// <summary><c>transcribe</c>: the words heard, text only.</summary>
    Transcribe,

    /// <summary><c>align</c>: the times of a known text's words.</summary>
    Align,

    /// <summary><c>transcribeAlign</c>: the words heard with their times.</summary>
    TranscribeAlign,

    /// <summary><c>transcribeDiarize</c>: the text as timed blocks, each with the voice that said it.</summary>
    TranscribeDiarize,

    /// <summary><c>diarize</c>: who speaks when, as timed turns without text.</summary>
    Diarize,
}

internal interface IEngine : IDisposable
{
    /// <summary>The install check: the engine's native libraries load. Prints to stderr; 0 when they do.</summary>
    int SelfTest();

    /// <summary>For hello, before anything is loaded: where this process runs ("cpu", "gpu 1 …").</summary>
    string DeviceDescription { get; }

    /// <summary>Gets the model from the app (needModel), sends prepared, loads it and sends ready.</summary>
    Task LoadAsync(string? id);

    /// <summary>
    /// A <c>transcribe</c>, <c>align</c> or <c>transcribeAlign</c> request (<paramref name="mode"/>): sends the transcript
    /// (the text, and the timed words unless it is text only). An engine refuses a mode it can't do.
    /// </summary>
    Task ListenAsync(string? id, JsonObject msg, ListenMode mode);

    /// <summary>
    /// A <c>compareVoices</c> request: embeds each audio file and sends <c>voiceScores</c> (scores of the candidates against
    /// the references, the pairwise matrix, the speech in each file and the model that scored it).
    /// </summary>
    Task CompareVoicesAsync(string? id, JsonObject msg);

    /// <summary>A <c>generate</c> request: writes the request's outPath and sends done.</summary>
    Task GenerateAsync(string? id, JsonObject msg);

    /// <summary>Cancels the running request (any request when <paramref name="id"/> is null).</summary>
    void Cancel(string? id);

    /// <summary>Errors after which the process should be restarted (the device may be in a bad state).</summary>
    bool IsFatal(Exception ex);
}
