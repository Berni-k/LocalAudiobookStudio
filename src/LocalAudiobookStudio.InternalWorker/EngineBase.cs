using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>What an engine is started with: the command line, the worker settings and the manifest's engineConfig.</summary>
internal sealed record EngineContext(WorkerOptions Options, JsonObject Settings, JsonObject Config)
{
    /// <summary>
    /// The model entry the worker settings choose: engineConfig.models[settings[engineConfig.modelSetting]], else the
    /// first entry. Its "model" is the models.json id to ask the app for; the other keys are the engine's (file names…).
    /// </summary>
    public (string Key, JsonObject Entry) Model()
    {
        var models = Config["models"] as JsonObject
                     ?? throw new InvalidOperationException("the manifest's engineConfig has no \"models\"");
        var key = (string?)Config["modelSetting"] is { } setting ? (string?)Settings[setting] : null;
        if (key != null && models[key] is JsonObject chosen) return (key, chosen);
        if (key != null) throw new ArgumentException($"unknown model choice '{key}' (engineConfig has {string.Join(", ", models.Select(m => m.Key))})");
        var first = models.First();
        return (first.Key, first.Value as JsonObject ?? throw new InvalidOperationException($"engineConfig model '{first.Key}' isn't an object"));
    }

    /// <summary>A string of the chosen model entry ("file", "encoder"…).</summary>
    public static string Require(JsonObject entry, string key) =>
        (string?)entry[key] ?? throw new InvalidOperationException($"the engineConfig model entry has no \"{key}\"");
}

/// <summary>
/// What every engine shares: the request being handled (so cancel reaches it), the default answer to requests an engine
/// doesn't do, recognising long audio piece by piece, and writing generated audio the way protocol.py does.
/// </summary>
internal abstract class EngineBase(EngineContext context) : IEngine
{
    protected EngineContext Context { get; } = context;

    private readonly Lock _lock = new();
    private (string? Id, CancellationTokenSource Cts)? _current;

    public abstract int SelfTest();
    public virtual string DeviceDescription => "cpu";
    public abstract Task LoadAsync(string? id);

    public virtual Task TranscribeAsync(string? id, JsonObject msg) =>
        throw new NotSupportedException($"the {Context.Options.Engine} engine doesn't transcribe audio");

    public virtual Task GenerateAsync(string? id, JsonObject msg) =>
        throw new NotSupportedException($"the {Context.Options.Engine} engine doesn't generate audio");

    public void Cancel(string? id)
    {
        lock (_lock)
            if (_current is { } c && (id == null || id == c.Id)) c.Cts.Cancel();
    }

    public virtual bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or InsufficientExecutionStackException
        || ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks <paramref name="id"/> as the request being handled until the returned scope ends; its token is cancelled by cancel.</summary>
    protected RequestScope Begin(string? id)
    {
        var cts = new CancellationTokenSource();
        lock (_lock) _current = (id, cts);
        return new RequestScope(this, cts);
    }

    protected sealed class RequestScope(EngineBase owner, CancellationTokenSource cts) : IDisposable
    {
        public CancellationToken Token => cts.Token;

        public void Dispose()
        {
            lock (owner._lock) owner._current = null;
            cts.Dispose();
        }
    }

    /// <summary>The request's "language" setting (a code like "en"), or null for Auto.</summary>
    protected static string? LanguageOf(JsonObject msg) =>
        (string?)msg["settings"]?["language"] is { Length: > 0 } l && !l.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? l : null;

    /// <summary>
    /// The usual transcribe request: reads the 16 kHz WAV, cuts it at quiet points into pieces of at most
    /// <paramref name="maxPieceSec"/> (some models do badly on long input), hears each with
    /// <paramref name="recognize"/> (piece samples, its offset in ms → words in the file's time), finishes the words
    /// (<see cref="TranscriptWords"/>; <paramref name="dropSilentWords"/> for models that invent text over silence) and
    /// sends the transcript.
    /// </summary>
    protected async Task TranscribeInPiecesAsync(string? id, JsonObject msg, double minPieceSec, double maxPieceSec, bool dropSilentWords,
        Func<float[], long, CancellationToken, Task<(IEnumerable<HeardWord> Words, string? Language)>> recognize)
    {
        var path = (string?)msg["audioPath"] ?? throw new ArgumentException("transcribe request has no audioPath");
        var language = LanguageOf(msg);
        using var request = Begin(id);
        var audio = AudioPieces.ReadWav(path);
        var heard = new List<HeardWord>();
        string? detected = null;
        if (audio.Length >= AudioPieces.SampleRate / 10)
        {
            var pieces = AudioPieces.QuietPieces(audio, minPieceSec, maxPieceSec);
            for (var i = 0; i < pieces.Count; i++)
            {
                request.Token.ThrowIfCancellationRequested();
                Protocol.Progress(id, (double)i / pieces.Count, pieces.Count == 1 ? "Listening" : $"Listening (part {i + 1} of {pieces.Count})");
                var (start, end) = pieces[i];
                var (words, lang) = await recognize(audio[start..end], start * 1000L / AudioPieces.SampleRate, request.Token);
                heard.AddRange(words);
                detected ??= lang;
            }
        }
        var finished = TranscriptWords.Finish(heard, audio, dropSilentWords);
        Protocol.Progress(id, 1);
        Protocol.Send("transcript", id,
            ("text", string.Join(" ", finished.Select(w => (string?)w["text"]))),
            ("words", new JsonArray([.. finished])),
            ("language", language ?? detected));
    }

    /// <summary>
    /// Writes generated audio for a generate request like protocol.py: a 16-bit WAV at outPath + ".partial", renamed
    /// when complete, then done (with the seed used).
    /// </summary>
    protected static void SendGenerated(string? id, JsonObject msg, ReadOnlySpan<float> samples, int sampleRate, int channels, long seed)
    {
        var outPath = (string?)msg["outPath"] ?? throw new ArgumentException("generate request has no outPath");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var partial = outPath + ".partial";
        try
        {
            WriteWav(partial, samples, sampleRate, channels);
            File.Move(partial, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch { /* best effort */ }
            throw;
        }
        Protocol.Send("done", id, ("path", outPath), ("durationMs", (long)Math.Round(samples.Length / channels * 1000.0 / sampleRate)),
            ("seed", seed), ("sampleRate", sampleRate));
    }

    private static void WriteWav(string path, ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var s in samples) writer.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
    }

    public virtual void Dispose()
    {
    }
}
