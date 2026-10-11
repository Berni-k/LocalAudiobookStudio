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
        var language = LanguageOf(msg);
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
    /// word, and whisper.cpp's special tokens ([_BEG_], [_TT_150]) are skipped.
    /// </summary>
    /// <remarks>
    /// A token's DTW time is where it has been said: its end, not its start (measured against the audio: taken as a
    /// start, every word came out one word late). So a word ends at its last token's DTW time and starts where the word
    /// before it ended. The segment's first word has no word before it: its start is open, found in the audio (where the
    /// speech up to its end starts). Without DTW times, whisper.cpp's own t0/t1 are a token's start and end.
    /// </remarks>
    private static IEnumerable<HeardWord> Words(SegmentData segment, long offsetMs)
    {
        var parts = new List<(string Text, long StartMs, long EndMs, bool OpenStart, List<float> Probabilities)>();
        var segmentStartMs = offsetMs + (long)segment.Start.TotalMilliseconds;
        foreach (var token in segment.Tokens ?? [])
        {
            var text = token.Text ?? "";
            if (text.Length == 0 || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
            // DTW timestamps (and whisper.cpp's own t0/t1) are in centiseconds from the start of the piece.
            var dtw = token.DtwTimestamp >= 0;
            var endMs = offsetMs + 10 * (dtw ? token.DtwTimestamp : token.End);
            if (parts.Count == 0 || text.StartsWith(' '))
            {
                var startMs = !dtw ? offsetMs + 10 * token.Start : parts.Count > 0 ? parts[^1].EndMs : segmentStartMs;
                parts.Add((text, startMs, endMs, OpenStart: dtw && parts.Count == 0, [token.Probability]));
            }
            else
            {
                var last = parts[^1];
                last.Probabilities.Add(token.Probability);
                parts[^1] = (last.Text + text, last.StartMs, Math.Max(last.EndMs, endMs), last.OpenStart, last.Probabilities);
            }
        }
        // whisper.cpp marks non-speech in brackets ("[BLANK_AUDIO]", "(music)"): not words of any text.
        parts.RemoveAll(p => p.Text.Trim() is var w && (w.Length == 0 || (w[0] == '[' && w[^1] == ']') || (w[0] == '(' && w[^1] == ')')));
        foreach (var p in parts)
        {
            // The geometric mean of the tokens' probabilities: low when whisper.cpp was unsure of any part.
            var confidence = Math.Exp(p.Probabilities.Average(x => Math.Log(Math.Max(x, 1e-6f))));
            yield return new HeardWord(p.Text.Trim(), p.StartMs, Math.Max(p.StartMs, p.EndMs), confidence, OpenStart: p.OpenStart);
        }
    }

    public override void Dispose() => _factory?.Dispose();
}
