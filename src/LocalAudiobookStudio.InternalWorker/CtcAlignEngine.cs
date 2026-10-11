using System.Text;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;
using Microsoft.ML.OnnxRuntime;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// CTC forced alignment on ONNX Runtime, CPU: MMS-300M-FA (the model of torchaudio's MMS_FA, exported to ONNX), the same
/// graph and alignment as workers/mms_fa/worker.py. It doesn't listen for words: it is given the text and finds when each
/// word is spoken. The model's frames (20 ms) give a probability for each letter or "blank"; the Viterbi path that spells
/// the text through them is the alignment. engineConfig models name the "model" (models.json id), its "file" and "vocab".
/// </summary>
internal sealed class CtcAlignEngine(EngineContext context) : EngineBase(context)
{
    private const int FrameSamples = 320;      // one output frame: 20 ms
    private const int ReceptiveField = 400;    // samples one frame looks at; a window of n samples gives (n - 400) / 320 + 1 frames
    private const double FrameSec = 0.02;
    private const int WindowSamples = 1500 * FrameSamples; // 30 s, a whole number of frames
    private const int ContextSamples = 100 * FrameSamples; // 2 s on each side that is run but not kept: a window's edges are the model's worst frames
    private const long MaxTrellis = 1_500_000_000;         // bytes of Viterbi back-pointers (about 8 minutes of speech)

    private InferenceSession? _session;
    private string _input = "input_values", _output = "logits";
    private Dictionary<char, int> _vocab = new();
    private int _tokens, _blank;

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

    public override async Task LoadAsync(string? id)
    {
        var (key, entry) = Context.Model();
        Protocol.Progress(id, 0.02, "Loading the model");
        var folder = await Protocol.NeedModelAsync(id, new ModelNeed(EngineContext.Require(entry, "model")), CancellationToken.None);
        Protocol.Send("prepared", id);

        Protocol.Progress(id, 0.3, $"Loading {key} on ONNX Runtime (CPU)");
        var options = new SessionOptions();
        if (int.TryParse(Environment.GetEnvironmentVariable("LAS_CPU_THREADS"), out var threads) && threads > 0)
            options.IntraOpNumThreads = threads;
        _session = new InferenceSession(Path.Combine(folder, EngineContext.Require(entry, "file")), options);
        _input = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.Keys.First();

        // vocab.json: { "vocab": { "<blank>": 0, "a": 4, … }, "blankTokenId": 0 }. Only the single letters are spelled with.
        var json = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(folder, EngineContext.Require(entry, "vocab"))))!;
        var vocab = json["vocab"]!.AsObject();
        _vocab = vocab.Where(p => p.Key.Length == 1).ToDictionary(p => p.Key[0], p => (int)p.Value!);
        _blank = (int?)json["blankTokenId"] ?? 0;
        _tokens = vocab.Max(p => (int)p.Value!) + 1;
        Protocol.Log($"{key} loaded on ONNX Runtime (CPU)");
        Protocol.Send("ready", id);
    }

    public override Task ListenAsync(string? id, JsonObject msg, ListenMode mode)
    {
        if (mode != ListenMode.Align)
            throw new NotSupportedException("this worker doesn't hear words: it only aligns a known text (an align request)");
        var text = (string?)msg["text"];
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("this worker aligns a known text: the request has none");
        var path = (string?)msg["audioPath"] ?? throw new ArgumentException("the align request has no audioPath");
        using var request = Begin(id);
        var audio = AudioPieces.ReadWav(path);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var spelled = words.Select(Spell).ToList();
        var heard = new List<HeardWord>();

        if (spelled.All(s => s.Length == 0))
            throw new InvalidOperationException("The text has no letters this model can align (it knows a to z; numbers and other scripts have no sound for it).");
        if (audio.Length >= AudioPieces.SampleRate / 10)
        {
            var (emissions, frames) = Emissions(id, audio, request.Token);
            Protocol.Progress(id, 0.95, "Aligning");
            heard = Align(words, spelled, emissions, frames, request.Token);
        }

        var finished = TranscriptWords.Finish(heard, audio, dropSilent: false);
        Protocol.Progress(id, 1);
        Protocol.Send("transcript", id,
            ("text", string.Join(" ", finished.Select(w => (string?)w["text"]))),
            ("words", new JsonArray([.. finished])),
            ("language", null));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A word as the model's letters: accents dropped, lower case, and the few letters that are two sounds spelled out;
    /// anything else (digits, punctuation, other scripts) has no letter. Apostrophes stay inside a word only. Kept in step
    /// with <c>spell</c> in workers/mms_fa/worker.py.
    /// </summary>
    internal static string Spell(string word)
    {
        var sb = new StringBuilder();
        foreach (var raw in word)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z') sb.Append(c);
            else if (c is '\'' or '’' or '‘' or 'ʼ') sb.Append('\'');
            else if (Folded.TryGetValue(c, out var letters)) sb.Append(letters);
        }
        return sb.ToString().Trim('\'');
    }

    /// <summary>
    /// Letters with accents and the few that are spelled with two, as the a-z the model knows. A table rather than Unicode
    /// normalisation, which the invariant globalization this worker runs in doesn't do; the same table is in worker.py.
    /// </summary>
    private static readonly Dictionary<char, string> Folded = BuildFolded();

    private static Dictionary<char, string> BuildFolded()
    {
        var folded = new Dictionary<char, string>();
        foreach (var (letter, accented) in new[]
                 {
                     ("a", "àáâãäåāăą"), ("c", "çćĉċč"), ("d", "ďđð"), ("e", "èéêëēĕėęě"), ("g", "ĝğġģ"), ("h", "ĥ"),
                     ("i", "ìíîïĩīĭįı"), ("j", "ĵ"), ("k", "ķ"), ("l", "ĺļľł"), ("n", "ñńņň"), ("o", "òóôõöōŏőø"),
                     ("r", "ŕŗř"), ("s", "śŝşš"), ("t", "ţť"), ("u", "ùúûüũūŭůűų"), ("w", "ŵ"), ("y", "ýÿŷ"), ("z", "źżž"),
                     ("ss", "ß"), ("ae", "æ"), ("oe", "œ"), ("th", "þ"),
                 })
            foreach (var c in accented) folded[c] = letter;
        return folded;
    }

    /// <summary>
    /// Log-probabilities of every frame of the audio: [frames, tokens]. The model runs on windows of 30 s with 2 s of
    /// audio around each (not kept), because it attends over its whole input.
    /// </summary>
    private (float[] Emissions, int Frames) Emissions(string? id, float[] audio, CancellationToken ct)
    {
        if (_session == null) throw new InvalidOperationException("the model isn't loaded");
        var frames = audio.Length < ReceptiveField ? 0 : (audio.Length - ReceptiveField) / FrameSamples + 1;
        var result = new float[frames * _tokens];
        var windows = (audio.Length + WindowSamples - 1) / WindowSamples;
        using var runOptions = new RunOptions();
        for (var w = 0; w < windows; w++)
        {
            ct.ThrowIfCancellationRequested();
            Protocol.Progress(id, 0.05 + 0.9 * w / windows, windows == 1 ? "Listening" : $"Listening (part {w + 1} of {windows})");
            var keepStart = w * WindowSamples;
            var runStart = Math.Max(0, keepStart - ContextSamples);
            var runEnd = Math.Min(audio.Length, keepStart + WindowSamples + ContextSamples);
            if (runEnd - runStart < ReceptiveField) continue;

            // Zero mean and unit variance per run: what the model was trained with.
            var run = audio[runStart..runEnd];
            var mean = run.Average();
            var variance = run.Sum(v => (v - mean) * (v - mean)) / run.Length;
            var scale = 1 / Math.Sqrt(variance + 1e-7);
            for (var i = 0; i < run.Length; i++) run[i] = (float)((run[i] - mean) * scale);

            using var input = OrtValue.CreateTensorValueFromMemory(run, [1, run.Length]);
            using var output = _session.Run(runOptions, [_input], [input], [_output]);
            var shape = output[0].GetTensorTypeAndShape().Shape; // [1, frames, tokens]
            var runFrames = (int)shape[1];
            var logits = output[0].GetTensorDataAsSpan<float>();

            var firstGlobal = runStart / FrameSamples;
            var keepFrom = keepStart / FrameSamples;
            var keepTo = w == windows - 1 ? frames : Math.Min(frames, (keepStart + WindowSamples) / FrameSamples);
            for (var g = keepFrom; g < keepTo; g++)
            {
                var j = g - firstGlobal;
                if (j < 0 || j >= runFrames) continue;
                LogSoftmax(logits.Slice(j * _tokens, _tokens), result.AsSpan(g * _tokens, _tokens));
            }
        }
        return (result, frames);
    }

    private static void LogSoftmax(ReadOnlySpan<float> logits, Span<float> into)
    {
        var max = float.MinValue;
        foreach (var v in logits) max = Math.Max(max, v);
        double sum = 0;
        foreach (var v in logits) sum += Math.Exp(v - max);
        var log = (float)Math.Log(sum) + max;
        for (var i = 0; i < logits.Length; i++) into[i] = logits[i] - log;
    }

    /// <summary>
    /// The Viterbi path through the frames that spells the text (CTC: letters and blanks, a letter may last several
    /// frames, a blank must separate a letter from the same letter after it). A word starts at its first letter's first
    /// frame and ends after its last letter's last frame. A word without letters (a number, a dash) is placed where the
    /// word before it ends.
    /// </summary>
    private List<HeardWord> Align(string[] words, List<string> spelled, float[] lp, int frames, CancellationToken ct)
    {
        var tokens = new List<int>();
        var wordOf = new List<int>();
        for (var i = 0; i < words.Length; i++)
            foreach (var c in spelled[i])
                if (_vocab.TryGetValue(c, out var token)) { tokens.Add(token); wordOf.Add(i); }
        var n = tokens.Count;
        var states = 2 * n + 1; // blank, letter, blank, letter, … blank
        if ((long)frames * states > MaxTrellis)
            throw new InvalidOperationException("This take and its text are too long to align in one go.");
        if (frames < n) throw new InvalidOperationException("The audio is too short for the text.");

        var ext = new int[states];
        Array.Fill(ext, _blank);
        for (var i = 0; i < n; i++) ext[2 * i + 1] = tokens[i];
        var back = new byte[(long)frames * states];
        var previous = new float[states];
        var current = new float[states];
        Array.Fill(previous, float.NegativeInfinity);
        previous[0] = lp[_blank];
        previous[1] = lp[ext[1]];
        for (var t = 1; t < frames; t++)
        {
            ct.ThrowIfCancellationRequested();
            var row = t * _tokens;
            for (var s = 0; s < states; s++)
            {
                var best = previous[s];
                byte from = 0;
                if (s >= 1 && previous[s - 1] > best) { best = previous[s - 1]; from = 1; }
                // A letter can follow the letter before the blank between them, unless it's the same letter.
                if (s >= 2 && ext[s] != _blank && ext[s] != ext[s - 2] && previous[s - 2] > best) { best = previous[s - 2]; from = 2; }
                back[(long)t * states + s] = from;
                current[s] = best + lp[row + ext[s]];
            }
            (previous, current) = (current, previous);
        }
        var state = states - 1;
        if (states > 1 && previous[states - 2] > previous[states - 1]) state = states - 2;
        if (float.IsNegativeInfinity(previous[state])) throw new InvalidOperationException("The audio is too short for the text.");

        var first = new int[words.Length];
        var last = new int[words.Length];
        var letterLogProb = new double[n];
        Array.Fill(first, int.MaxValue);
        Array.Fill(last, -1);
        Array.Fill(letterLogProb, double.NegativeInfinity);
        for (var t = frames - 1; t >= 0; t--)
        {
            if ((state & 1) == 1)
            {
                var letter = state / 2;
                var w = wordOf[letter];
                first[w] = Math.Min(first[w], t);
                last[w] = Math.Max(last[w], t);
                letterLogProb[letter] = Math.Max(letterLogProb[letter], lp[t * _tokens + ext[state]]);
            }
            state -= back[(long)t * states + state];
        }

        var heard = new List<HeardWord>();
        long previousEnd = 0;
        var letterCount = new int[words.Length];
        foreach (var w in wordOf) letterCount[w]++;
        var letter0 = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var count = letterCount[i];
            if (count == 0)
            {
                heard.Add(new HeardWord(words[i], previousEnd, previousEnd, 0));
                continue;
            }
            // The geometric mean of the letters' best frames: low when the model hardly heard part of the word.
            var mean = Enumerable.Range(letter0, count).Average(l => letterLogProb[l]);
            letter0 += count;
            var start = (long)Math.Round(first[i] * FrameSec * 1000);
            var end = (long)Math.Round((last[i] + 1) * FrameSec * 1000);
            heard.Add(new HeardWord(words[i], start, end, Math.Exp(mean)));
            previousEnd = end;
        }
        return heard;
    }

    public override void Dispose() => _session?.Dispose();
}
