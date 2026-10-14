using System.Text;
using System.Text.Json.Nodes;
using HartsyInference.Audio.Streaming;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>A text-to-speech model from HartsyInference's speech catalog, behind the worker protocol.</summary>
internal sealed class SpeechWorker(WorkerOptions options) : IModelWorker
{
    private readonly object _gate = new();
    private InferenceEngine? _engine;
    /// <summary>Keeps the current variant resident; another variant replaces it (one model in VRAM at a time).</summary>
    private ISynthesizerLease? _lease;
    private ModelSpec? _spec;
    private string? _currentId;
    private CancellationTokenSource? _currentCts;

    public async Task LoadAsync(string? id)
    {
        Protocol.Progress(id, 0.02, "Starting HartsyInference");
        _engine = WorkerDevice.CreateEngine();
        // The model with default settings and no reference clip; other variants load on first use.
        var spec = Spec(ModelVariants.For(options.Model, new JsonObject(), hasReference: false));

        // Prepare stage: the app downloads the model and links it into HartsyInference's folders; the prefetch then
        // finds the files (and fetches only what isn't in models.json, like Kokoro's voices).
        await ModelLinks.EnsureAsync(id, ModelLinks.ForSpeech(spec.Requested), CancellationToken.None);
        Protocol.Progress(id, 0.05, $"Checking {spec.Requested} model files");
        var prefetch = await _engine.ModelPrefetch.PrefetchAsync(spec,
            new Progress<HartsyInference.Audio.Cache.AudioFetchProgress>(p => Protocol.Progress(id, 0.1, p.ToString())));
        // Without a declared file list the model downloads while it loads, so other processes of this worker
        // may only start once it has loaded (they would download the same files at the same time).
        var prepared = prefetch.Supported;
        if (prepared) Protocol.Send("prepared", id);
        else Protocol.Log($"prefetch: {prefetch.Message}");

        Protocol.Progress(id, 0.3, "Loading the model");
        await UseAsync(spec, CancellationToken.None);
        if (!prepared) Protocol.Send("prepared", id);
        Protocol.Send("ready", id);
    }

    private static ModelSpec Spec(string token) => new() { Requested = token, Modality = Modality.Speech };

    /// <summary>Makes <paramref name="spec"/> the resident model, releasing the previous variant first.</summary>
    private async Task UseAsync(ModelSpec spec, CancellationToken ct)
    {
        if (_spec?.Requested == spec.Requested && _lease != null) return;
        if (_lease != null)
        {
            Protocol.Log($"switching model: {_spec?.Requested} -> {spec.Requested}");
            _lease.Dispose();
            _lease = null;
            _engine!.FreeMemory();
        }
        _lease = await _engine!.Speech.OpenSynthesizerAsync(spec, ct);
        _spec = spec;
    }

    public void Cancel(string? id)
    {
        lock (_gate)
            if (_currentCts != null && (id == null || id == _currentId)) _currentCts.Cancel();
    }

    public async Task GenerateAsync(string? id, JsonObject msg)
    {
        if (_engine == null) throw new InvalidOperationException("generate before load");
        var finalPath = (string?)msg["outPath"] ?? throw new ArgumentException("generate request has no outPath");
        var text = (string?)msg["text"] ?? "";
        var settings = msg["settings"] as JsonObject ?? new JsonObject();
        var seed = Settings.Long(msg, "seed") ?? Settings.Long(settings, "seed") ?? Random.Shared.Next(1, int.MaxValue);

        var partial = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPath))!);
        using var cts = new CancellationTokenSource();
        lock (_gate) { _currentId = id; _currentCts = cts; }
        try
        {
            var reference = await ReferenceClipAsync(id, (string?)msg["voice"], settings, cts.Token);
            var request = BuildRequest(text, reference, settings, (int)(seed % int.MaxValue));
            var spec = Spec(ModelVariants.For(options.Model, settings, reference.Clip != null));
            if (_spec?.Requested != spec.Requested)
            {
                await ModelLinks.EnsureAsync(id, ModelLinks.ForSpeech(spec.Requested), cts.Token);
                Protocol.Progress(id, 0.02, $"Loading {spec.Requested}");
            }
            await UseAsync(spec, cts.Token);

            var samples = new List<float>();
            int sampleRate = 0, channels = 1;
            Protocol.Progress(id, 0.05, "Synthesizing");
            if (Streams(options.Model))
            {
                // The stream splits long text into sentences (Kokoro).
                var expectedSec = Math.Max(1.0, text.Length / 15.0); // rough: ~15 characters per second of speech
                await foreach (AudioChunk chunk in _engine.Speech.SynthesizeStreamAsync(spec, request, cts.Token))
                {
                    sampleRate = chunk.SampleRate;
                    channels = Math.Max(1, chunk.Channels);
                    samples.AddRange(chunk.Samples);
                    var seconds = samples.Count / (double)(sampleRate * channels);
                    Protocol.Progress(id, Math.Min(0.95, seconds / expectedSec), $"{seconds:0.0} s generated");
                }
            }
            else
            {
                var lease = _lease!;
                samples.AddRange(await Task.Run(() => lease.Synthesize(text, request, cts.Token), cts.Token));
                sampleRate = lease.SampleRate;
            }
            cts.Token.ThrowIfCancellationRequested();
            if (samples.Count == 0 || sampleRate <= 0) throw new InvalidOperationException("the model returned no audio");
            var durationMs = Wav.Write16(partial, samples, sampleRate, channels);
            File.Move(partial, finalPath, overwrite: true);
            Protocol.Send("done", id, ("path", finalPath), ("durationMs", durationMs), ("seed", seed), ("sampleRate", sampleRate));
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            throw;
        }
        finally
        {
            lock (_gate) { _currentId = null; _currentCts = null; }
        }
    }

    /// <summary>
    /// Whether the model's audio comes from HartsyInference's stream. The others synthesize the whole text in one go:
    /// the worker only needs the whole clip, and streaming is much slower for some (CosyVoice 2's flow runs again over a
    /// growing window for every piece: 30 s instead of 5 s for 8 s of speech on an RTX 3090).
    /// </summary>
    private static bool Streams(string model) => model.Equals("kokoro", StringComparison.OrdinalIgnoreCase);

    /// <summary>A reference clip (to clone) and/or a built-in voice id.</summary>
    private readonly record struct Reference(AudioClip? Clip, string? VoiceId);

    /// <summary>
    /// The request's voice is a reference clip (absolute path) or a built-in voice id; the "voice" setting is the
    /// preset chosen in the speaker's settings. A model with preset voice clips (see <see cref="ModelLinks.PresetVoices"/>)
    /// clones the preset's clip when the speaker has no clip of its own (VibeVoice: HartsyInference always needs a reference).
    /// </summary>
    private async Task<Reference> ReferenceClipAsync(string? id, string? voice, JsonObject settings, CancellationToken ct)
    {
        string? voiceId = Settings.String(settings, "voice");
        if (!string.IsNullOrWhiteSpace(voice))
        {
            if (File.Exists(voice)) return new Reference(new AudioClip { Data = await File.ReadAllBytesAsync(voice, ct) }, voiceId);
            if (Path.IsPathRooted(voice)) throw new FileNotFoundException($"voice reference clip not found: {voice}");
            voiceId = voice;
        }
        if (ModelLinks.PresetVoices(options.Model) is { } presets && voiceId != null)
        {
            if (voiceId.Length == 0 || !voiceId.Replace("-", "").Replace("_", "").All(char.IsLetterOrDigit))
                throw new ArgumentException($"invalid preset voice name: {voiceId}");
            var path = await Protocol.NeedFileAsync(id, presets, voiceId + ".wav", ct);
            return new Reference(new AudioClip { Data = await File.ReadAllBytesAsync(path, ct) }, null);
        }
        return new Reference(null, voiceId);
    }

    /// <summary>
    /// Maps the speaker's settings onto HartsyInference's request. The manifest's setting keys are named after
    /// <see cref="SpeechRequest"/>'s properties (camelCase), so one mapping serves every speech model; "steps"
    /// is accepted for <see cref="SpeechRequest.NfeStep"/> (the name our Python workers use).
    /// </summary>
    private static SpeechRequest BuildRequest(string text, Reference reference, JsonObject s, int seed) => new()
    {
        Text = text,
        Reference = reference.Clip,
        RefText = Settings.String(s, "refText") ?? "",
        Voice = reference.VoiceId,
        Speed = Settings.Double(s, "speed"),
        Exaggeration = Settings.Double(s, "exaggeration"),
        NfeStep = Settings.Int(s, "nfeStep") ?? Settings.Int(s, "steps"),
        CfgScale = Settings.Double(s, "cfgScale"),
        NormalizeLoudness = Settings.Bool(s, "normalizeLoudness"),
        SpeakerId = Settings.Int(s, "speakerId"),
        Temperature = Settings.Double(s, "temperature"),
        TopP = Settings.Double(s, "topP"),
        TopK = Settings.Int(s, "topK"),
        MaxTokens = Settings.Int(s, "maxTokens"),
        SpeakingRate = Settings.Double(s, "speakingRate"),
        PitchStd = Settings.Double(s, "pitchStd"),
        Instruction = Settings.String(s, "instruction"),
        Seed = seed,
    };

    public void Dispose()
    {
        _lease?.Dispose();
        _engine?.Dispose();
    }
}

/// <summary>Typed reads of JSON settings, whatever numeric type a value was stored as.</summary>
internal static class Settings
{
    public static double? Double(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null;
    }

    public static long? Long(JsonObject? o, string key) => Double(o, key) is double d ? (long)d : null;

    public static int? Int(JsonObject? o, string key) => Double(o, key) is double d ? (int)Math.Round(d) : null;

    public static bool? Bool(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static string? String(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

internal static class Wav
{
    /// <summary>Writes interleaved float samples as 16-bit PCM WAV; returns the duration in milliseconds.</summary>
    public static int Write16(string path, List<float> samples, int sampleRate, int channels)
    {
        using var w = new BinaryWriter(File.Create(path), Encoding.ASCII);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples)
            w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
        return (int)Math.Round(samples.Count / (double)channels * 1000.0 / sampleRate);
    }
}
