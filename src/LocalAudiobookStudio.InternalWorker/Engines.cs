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
        // CTC forced alignment (MMS-300M-FA exported to ONNX): finds when each word of a given text is spoken, on the CPU.
        ["ctc-align"] = c => new CtcAlignEngine(c),
        // Speaker-embedding networks (3D-Speaker, WeSpeaker) exported to ONNX: how alike voices are, on the CPU.
        ["speaker-embed"] = c => new SpeakerEmbedEngine(c),
        // Speaker diarization (pyannote community-1 exported to ONNX): who speaks when, on the CPU.
        ["pyannote"] = c => new PyannoteEngine(c),
    };

    public static IEngine Create(EngineContext context) =>
        Factories.TryGetValue(context.Options.Engine, out var create)
            ? create(context)
            : throw new ArgumentException($"unknown engine '{context.Options.Engine}' (known: {string.Join(", ", Factories.Keys)})");
}
