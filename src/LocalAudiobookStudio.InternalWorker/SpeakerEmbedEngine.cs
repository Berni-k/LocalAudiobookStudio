using System.Globalization;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;
using Microsoft.ML.OnnxRuntime;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// Speaker verification on ONNX Runtime, CPU: a speaker-embedding network (3D-Speaker ERes2NetV2 and CAM++, WeSpeaker
/// ResNet34) turns a recording into one vector that stands for the voice, and the cosine of two vectors says how alike two
/// voices are. It answers <c>compareVoices</c>, not transcription. Features are Kaldi-style filterbanks (<see cref="Fbank"/>);
/// how a model wants its samples scaled and normalised is read from its ONNX metadata (the sherpa-onnx exports carry
/// <c>normalize_samples</c> and <c>feature_normalize_type</c>). engineConfig models name the "model" (models.json id)
/// and its "file".
/// Unlike the other engines, the model is chosen per request (the worker's "model" setting arrives with each
/// <c>compareVoices</c>, so a speaker's choice and <c>las compare --set model=…</c> work on a worker that is already
/// running), and is fetched and loaded when first asked for; these networks are small, so they stay loaded.
/// </summary>
internal sealed class SpeakerEmbedEngine(EngineContext context) : EngineBase(context)
{
    /// <summary>Clips longer than this are embedded in pieces and averaged, so a long take doesn't need a huge input.</summary>
    private const double PieceMaxSec = 20, PieceMinSec = 10, WholeUpToSec = 30;

    private sealed record Network(InferenceSession Session, string Input, string Output, bool Int16Samples, bool SubtractMean);

    private readonly Dictionary<string, Network> _networks = new();

    public override int SelfTest()
    {
        try
        {
            Console.Error.WriteLine($"ONNX Runtime {OrtEnv.Instance().GetVersionString()}, providers {string.Join(", ", OrtEnv.Instance().GetAvailableProviders())}");
            Console.Error.WriteLine("selftest OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ONNX Runtime could not be loaded: {ex.Message}" +
                                    (VcRuntime.Available ? "" : " The Microsoft Visual C++ Redistributable (x64) seems to be missing."));
            return 1;
        }
    }

    public override Task LoadAsync(string? id)
    {
        // Nothing to read yet: the model is the request's choice. Not downloading all three keeps the download small.
        Protocol.Send("prepared", id);
        Protocol.Send("ready", id);
        return Task.CompletedTask;
    }

    /// <summary>The model the request asks for (its "model" setting), else the worker setting, else the first one.</summary>
    private (string Key, JsonObject Entry) ModelOf(JsonObject msg)
    {
        var models = Context.Config["models"] as JsonObject ?? throw new InvalidOperationException("the manifest's engineConfig has no \"models\"");
        var setting = (string?)Context.Config["modelSetting"];
        if (setting != null && (string?)msg["settings"]?[setting] is { Length: > 0 } key)
            return models[key] is JsonObject chosen
                ? (key, chosen)
                : throw new ArgumentException($"unknown model choice '{key}' (engineConfig has {string.Join(", ", models.Select(m => m.Key))})");
        return Context.Model();
    }

    private async Task<Network> NetworkAsync(string? id, string key, JsonObject entry, CancellationToken ct)
    {
        if (_networks.TryGetValue(key, out var loaded)) return loaded;
        Protocol.Progress(id, 0.01, $"Getting {key}");
        var folder = await Protocol.NeedModelAsync(id, new ModelNeed(EngineContext.Require(entry, "model")), ct);

        Protocol.Progress(id, 0.05, $"Loading {key} on ONNX Runtime (CPU)");
        var options = new SessionOptions();
        if (int.TryParse(Environment.GetEnvironmentVariable("LAS_CPU_THREADS"), out var threads) && threads > 0)
            options.IntraOpNumThreads = threads;
        var session = new InferenceSession(Path.Combine(folder, EngineContext.Require(entry, "file")), options);

        // The export says what the network was trained on. A model without that metadata (WeSpeaker's own upload) says in
        // engineConfig (int16Samples, meanNormalize).
        var meta = session.ModelMetadata.CustomMetadataMap;
        if (meta.TryGetValue("sample_rate", out var rate) && rate != AudioPieces.SampleRate.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException($"{key} wants {rate} Hz audio, not {AudioPieces.SampleRate} Hz");
        var int16 = meta.TryGetValue("normalize_samples", out var normalize) ? normalize == "0" : (bool?)entry["int16Samples"] ?? false;
        var mean = meta.TryGetValue("feature_normalize_type", out var featureNorm) ? featureNorm == "global-mean" : (bool?)entry["meanNormalize"] ?? false;
        var network = new Network(session, session.InputMetadata.Keys.First(), session.OutputMetadata.Keys.First(), int16, mean);
        Protocol.Log($"{key} loaded on ONNX Runtime (CPU); samples {(int16 ? "in the 16-bit range" : "-1 to 1")}, " +
                     $"{(mean ? "mean-normalised features" : "plain features")}, embedding {(meta.TryGetValue("output_dim", out var dim) ? dim : "?")}");
        return _networks[key] = network;
    }

    public override async Task CompareVoicesAsync(string? id, JsonObject msg)
    {
        var files = (msg["files"] as JsonArray ?? throw new ArgumentException("the request has no files list")).Select(n =>
        {
            var o = n as JsonObject ?? throw new ArgumentException("a file entry isn't an object");
            return (Path: (string?)o["path"] ?? throw new ArgumentException("a file entry has no path"),
                    IsReference: string.Equals((string?)o["role"], "reference", StringComparison.OrdinalIgnoreCase));
        }).ToList();
        if (files.Count < 2) throw new ArgumentException("comparing voices takes at least two audio files");
        using var request = Begin(id);

        var (key, entry) = ModelOf(msg);
        var network = await NetworkAsync(id, key, entry, request.Token);

        // Each distinct file is embedded once, however many times the request lists it.
        var embedded = new Dictionary<string, (float[] Embedding, int SpeechMs)>(StringComparer.OrdinalIgnoreCase);
        var unique = files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < unique.Count; i++)
        {
            request.Token.ThrowIfCancellationRequested();
            Protocol.Progress(id, 0.1 + 0.9 * i / unique.Count, unique.Count == 1 ? "Listening" : $"Listening (file {i + 1} of {unique.Count})");
            embedded[unique[i]] = Embed(network, AudioPieces.ReadWav(unique[i]), request.Token);
        }

        var embeddings = files.Select(f => embedded[f.Path].Embedding).ToList();
        var matrix = VoiceMath.Matrix(embeddings);
        var references = files.Where(f => f.IsReference).Select(f => embedded[f.Path].Embedding).ToList();
        // With several references a candidate is compared with their average; with none there are no scores, only the matrix.
        var scores = new JsonArray();
        if (references.Count > 0)
        {
            var average = VoiceMath.Average(references);
            foreach (var f in files.Where(f => !f.IsReference))
                scores.Add(Round(VoiceMath.Cosine(embedded[f.Path].Embedding, average)));
        }

        if (Environment.GetEnvironmentVariable("LAS_DEBUG_EMBEDDINGS") == "1")
            foreach (var f in unique)
                Protocol.Log($"embedding {key} {Path.GetFileName(f)}: " + string.Join(" ", embedded[f].Embedding.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));

        Protocol.Progress(id, 1);
        Protocol.Send("voiceScores", id,
            ("scores", scores),
            ("matrix", new JsonArray([.. matrix.Select(row => (JsonNode?)new JsonArray([.. row.Select(v => Round(v))]))])),
            ("speechMs", new JsonArray([.. files.Select(f => (JsonNode?)JsonValue.Create(embedded[f.Path].SpeechMs))])),
            ("model", key));
    }

    private static JsonNode? Round(double v) => JsonValue.Create(Math.Round(v, 4));

    /// <summary>
    /// The voice of a clip: the network's embedding of the whole clip, or for a long one the average of its pieces (cut at
    /// quiet points). And how much of it is speech, in ms: frames within 30 dB of the clip's speech level.
    /// </summary>
    private static (float[] Embedding, int SpeechMs) Embed(Network network, float[] audio, CancellationToken ct)
    {
        var levels = AudioPieces.FrameLevels(audio);
        var speech = AudioPieces.SpeechLevel(levels);
        var speechMs = speech <= 0 ? 0 : levels.Count(l => l >= 0.03f * speech) * 20;

        var pieces = audio.Length > WholeUpToSec * AudioPieces.SampleRate
            ? AudioPieces.QuietPieces(audio, PieceMinSec, PieceMaxSec)
            : [(0, audio.Length)];
        var embeddings = new List<float[]>();
        foreach (var (start, end) in pieces)
        {
            ct.ThrowIfCancellationRequested();
            embeddings.Add(EmbedPiece(network, audio.AsSpan(start, end - start)));
        }
        return (embeddings.Count == 1 ? embeddings[0] : VoiceMath.Average(embeddings), speechMs);
    }

    private static float[] EmbedPiece(Network network, ReadOnlySpan<float> samples)
    {
        var scaled = new float[Math.Max(samples.Length, Fbank.WindowSamples)]; // shorter than a window: padded with silence
        for (var i = 0; i < samples.Length; i++) scaled[i] = network.Int16Samples ? samples[i] * 32768f : samples[i];
        var features = Fbank.Compute(scaled);
        if (network.SubtractMean) Fbank.SubtractMean(features);

        var flat = new float[features.Length * Fbank.MelBins];
        for (var f = 0; f < features.Length; f++) features[f].CopyTo(flat, f * Fbank.MelBins);
        using var input = OrtValue.CreateTensorValueFromMemory(flat, [1, features.Length, Fbank.MelBins]);
        using var output = network.Session.Run(new RunOptions(), [network.Input], [input], [network.Output]);
        return output[0].GetTensorDataAsSpan<float>().ToArray();
    }

    public override void Dispose()
    {
        foreach (var n in _networks.Values) n.Session.Dispose();
    }
}
