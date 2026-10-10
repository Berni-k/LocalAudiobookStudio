using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// Whisper models in ggml format on whisper.cpp: answers <c>transcribe</c> with the words heard and their times. The
/// model is one file from the app (needModel); engineConfig entries give its "model" id, "file" and "dtwHeads" (the
/// alignment-heads preset for word times: LargeV3Turbo, LargeV3, Small…). Word times come from DTW over those heads,
/// like the PyTorch runtime.
/// </summary>
/// <remarks>
/// Like the PyTorch runtime (workers/whisper/worker.py): long takes are cut at quiet points into pieces of at most
/// 20 s, each piece is recognised without the text of the one before, and the take's text is never given as a prompt.
/// </remarks>
internal sealed class WhisperEngine : EngineBase
{
    private const double MaxPieceSec = 20, MinPieceSec = 10;

    private WhisperFactory? _factory;
    private bool _gpu;

    public WhisperEngine(EngineContext context) : base(context) =>
        WhisperBackend.Configure(context.Options.GpuFolder, (string?)context.Settings["backend"]);

    public override string DeviceDescription => WhisperBackend.DeviceDescription();

    public override int SelfTest()
    {
        try
        {
            var library = WhisperBackend.Load();
            Console.Error.WriteLine($"whisper.cpp loaded: {WhisperBackend.Label(library)}");
            Console.Error.WriteLine(WhisperFactory.GetRuntimeInfo()?.Trim());
            Console.Error.WriteLine("selftest OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public override async Task LoadAsync(string? id)
    {
        var (key, entry) = Context.Model();
        var heads = Enum.TryParse<WhisperAlignmentHeadsPreset>((string?)entry["dtwHeads"] ?? "", ignoreCase: true, out var preset)
            ? preset
            : throw new InvalidOperationException($"engineConfig model '{key}' needs \"dtwHeads\" (one of {string.Join(", ", Enum.GetNames<WhisperAlignmentHeadsPreset>())})");

        Protocol.Progress(id, 0.02, "Loading whisper.cpp");
        var library = WhisperBackend.Load();
        var folder = await Protocol.NeedModelAsync(id, new ModelNeed(EngineContext.Require(entry, "model")), CancellationToken.None);
        Protocol.Send("prepared", id);

        Protocol.Progress(id, 0.3, $"Loading Whisper {key} ({WhisperBackend.Label(library)})");
        _gpu = WhisperBackend.UsesGpu(library);
        _factory = WhisperFactory.FromPath(Path.Combine(folder, EngineContext.Require(entry, "file")), new WhisperFactoryOptions
        {
            UseGpu = _gpu,
            GpuDevice = _gpu ? WhisperBackend.GpuDevice(library) : 0,
            UseDtwTimeStamps = true,
            HeadsPreset = heads,
        });
        Protocol.Log($"Whisper {key} loaded on whisper.cpp ({WhisperBackend.Label(library)}{(_gpu ? $", device {WhisperBackend.GpuDevice(library)}" : "")})");
        Protocol.Send("ready", id);
    }

    public override Task TranscribeAsync(string? id, JsonObject msg)
    {
        var factory = _factory ?? throw new InvalidOperationException("the model isn't loaded");
        var language = (string?)msg["language"] is { Length: > 0 } l ? l : null;
        var threads = int.TryParse(Environment.GetEnvironmentVariable("LAS_CPU_THREADS"), out var t) && t > 0
            ? t
            : Math.Max(1, Environment.ProcessorCount / 2);
        // Whisper invents text over silence now and then: words heard in silence are dropped.
        return TranscribeInPiecesAsync(id, msg, MinPieceSec, MaxPieceSec, dropSilentWords: true, async (piece, offsetMs, ct) =>
        {
            var builder = factory.CreateBuilder()
                .WithLanguage(language ?? "auto")
                .WithTokenTimestamps()
                .WithProbabilities()
                .WithNoContext()
                .WithThreads(threads);
            // Whisper encodes 30 s of audio (1500 frames) whatever the piece's length. On the CPU, encoding only the
            // piece (plus a margin) makes a short take several times faster; a GPU is fast enough without the risk.
            if (!_gpu) builder = builder.WithAudioContextSize(Math.Min(1500, (int)Math.Ceiling(piece.Length / 320.0) + 64));
            await using var processor = builder.Build();
            var words = new List<HeardWord>();
            string? detected = null;
            await foreach (var segment in processor.ProcessAsync(piece, ct))
            {
                detected ??= segment.Language is { Length: > 0 } sl ? sl : null;
                words.AddRange(Words(segment, offsetMs));
            }
            return (words, detected);
        });
    }

    /// <summary>
    /// A segment's tokens joined into words: a token starting with a space begins a word, punctuation stays on its
    /// word, and whisper.cpp's special tokens ([_BEG_], [_TT_150]) are skipped. A word starts at its first token's DTW
    /// time and ends where the next word starts; the segment's last word has an open end (found in the audio).
    /// </summary>
    private static IEnumerable<HeardWord> Words(SegmentData segment, long offsetMs)
    {
        var parts = new List<(string Text, long StartMs, List<float> Probabilities)>();
        foreach (var token in segment.Tokens ?? [])
        {
            var text = token.Text ?? "";
            if (text.Length == 0 || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
            // DTW timestamps (and whisper.cpp's own t0) are in centiseconds from the start of the piece.
            var timeMs = offsetMs + 10 * (token.DtwTimestamp >= 0 ? token.DtwTimestamp : token.Start);
            if (parts.Count == 0 || text.StartsWith(' '))
                parts.Add((text, timeMs, [token.Probability]));
            else
            {
                var last = parts[^1];
                last.Probabilities.Add(token.Probability);
                parts[^1] = (last.Text + text, last.StartMs, last.Probabilities);
            }
        }
        // whisper.cpp marks non-speech in brackets ("[BLANK_AUDIO]", "(music)"): not words of any text.
        parts.RemoveAll(p => p.Text.Trim() is var w && (w.Length == 0 || (w[0] == '[' && w[^1] == ']') || (w[0] == '(' && w[^1] == ')')));
        for (var i = 0; i < parts.Count; i++)
        {
            var last = i + 1 == parts.Count;
            var start = parts[i].StartMs;
            // The geometric mean of the tokens' probabilities: low when whisper.cpp was unsure of any part.
            var confidence = Math.Exp(parts[i].Probabilities.Average(p => Math.Log(Math.Max(p, 1e-6f))));
            yield return new HeardWord(parts[i].Text.Trim(), start, last ? start : Math.Max(start, parts[i + 1].StartMs), confidence, OpenEnd: last);
        }
    }

    public override void Dispose() => _factory?.Dispose();
}
