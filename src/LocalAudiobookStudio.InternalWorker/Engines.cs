namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// The internal worker's engines by name (a manifest's "engine"). Names say what kind of model an engine runs, so one
/// engine serves every model of that kind; the manifest's engineConfig says which files to load. Adding an engine: a
/// class deriving from <see cref="EngineBase"/>, a line here, and its name in InternalGpuRuntime.Engines (the app checks
/// manifests against that list).
/// </summary>
internal static class Engines
{
    private static readonly Dictionary<string, Func<EngineContext, IEngine>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Whisper models in ggml format on whisper.cpp (Whisper.net): CPU builds bundled, Vulkan/CUDA downloadable.
        ["whisper"] = c => new WhisperEngine(c),
        // NeMo TDT transducers exported to ONNX (Parakeet TDT): preprocessor, encoder and decoder/joint graphs, on the CPU.
        ["tdt"] = c => new TdtEngine(c),
    };

    public static IEngine Create(EngineContext context) =>
        Factories.TryGetValue(context.Options.Engine, out var create)
            ? create(context)
            : throw new ArgumentException($"unknown engine '{context.Options.Engine}' (known: {string.Join(", ", Factories.Keys)})");
}
