using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;
using Microsoft.ML.OnnxRuntime;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// NeMo TDT transducers exported to ONNX (Parakeet TDT, as istupakov's exports for onnx-asr) on ONNX Runtime, CPU: the
/// same graphs and decoding as workers/parakeet/worker.py, without Python. engineConfig entries name the "model" and its
/// "preprocessor" (mel features), "encoder", "decoder" (decoder/joint network) and "vocab" files. The greedy TDT loop
/// keeps each token's predicted duration, which gives each word its end.
/// </summary>
internal sealed class TdtEngine(EngineContext context) : EngineBase(context)
{
    private const double FrameSec = 0.08; // one encoder frame: 10 ms features, subsampled 8x
    private const double MaxPieceSec = 30, MinPieceSec = 20;
    private const int MaxTokensPerStep = 10;

    private InferenceSession? _preprocessor, _encoder, _decoder;
    private Dictionary<int, string> _vocab = new();
    private int _blank;

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
        string File(string name) => Path.Combine(folder, EngineContext.Require(entry, name));
        _preprocessor = new InferenceSession(File("preprocessor"), options);
        _encoder = new InferenceSession(File("encoder"), options);
        _decoder = new InferenceSession(File("decoder"), options);
        // vocab.txt: "<token> <id>" per line; ▁ marks a word start.
        _vocab = System.IO.File.ReadLines(File("vocab"))
            .Select(l => l.Split(' '))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => int.Parse(parts[1]), parts => parts[0].Replace('▁', ' '));
        _blank = _vocab.First(kv => kv.Value == "<blk>").Key;
        Protocol.Log($"{key} loaded on ONNX Runtime (CPU)");
        Protocol.Send("ready", id);
    }

    public override Task TranscribeAsync(string? id, JsonObject msg) =>
        // A transducer only writes what it hears: no silence check, which could only lose real words.
        TranscribeInPiecesAsync(id, msg, MinPieceSec, MaxPieceSec, dropSilentWords: false, (piece, offsetMs, ct) =>
            Task.FromResult<(IEnumerable<HeardWord>, string?)>((Recognize(piece, offsetMs, ct), null)));

    /// <summary>Words of one piece, with times in the whole file.</summary>
    private List<HeardWord> Recognize(float[] audio, long offsetMs, CancellationToken ct)
    {
        if (_preprocessor == null || _encoder == null) throw new InvalidOperationException("the model isn't loaded");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var runOptions = new RunOptions();
        using var waveforms = OrtValue.CreateTensorValueFromMemory(audio, [1, audio.Length]);
        using var waveformLengths = OrtValue.CreateTensorValueFromMemory(new[] { (long)audio.Length }, [1]);
        using var features = _preprocessor.Run(runOptions, ["waveforms", "waveforms_lens"], [waveforms, waveformLengths], ["features", "features_lens"]);
        using var encoded = _encoder.Run(runOptions, ["audio_signal", "length"], [features[0], features[1]], ["outputs", "encoded_lengths"]);

        var shape = encoded[0].GetTensorTypeAndShape().Shape; // [1, D, T]
        var dim = (int)shape[1];
        var frames = (int)shape[2];
        var frameCount = (int)encoded[1].GetTensorDataAsSpan<long>()[0];
        var encodeMs = clock.ElapsedMilliseconds;
        var tokens = Decode(encoded[0].GetTensorDataAsSpan<float>().ToArray(), dim, frames, Math.Min(frameCount, frames), ct);
        Protocol.Log($"{audio.Length / (double)AudioPieces.SampleRate:0.0} s of audio: encoder {encodeMs} ms, decoder {clock.ElapsedMilliseconds - encodeMs} ms ({tokens.Count} tokens)");
        return Words(tokens, offsetMs, audio.Length * 1000L / AudioPieces.SampleRate);
    }

    /// <summary>
    /// Greedy TDT decoding (as onnx-asr does it), keeping each token's duration: (token, frame, frames, logprob).
    /// One decoder run per step: its inputs are tensors over arrays allocated once and refilled in place.
    /// </summary>
    private List<(int Token, int Frame, int Frames, double LogProb)> Decode(float[] encoder, int dim, int frames, int frameCount, CancellationToken ct)
    {
        var decoder = _decoder!;
        var meta = decoder.InputMetadata;
        var shape1 = meta["input_states_1"].Dimensions;
        var shape2 = meta["input_states_2"].Dimensions;
        var state1 = new float[shape1[0] * shape1[2]];
        var state2 = new float[shape2[0] * shape2[2]];
        var frame = new float[dim];
        var targetsAreLong = meta["targets"].ElementType == typeof(long);
        var targetLong = new long[1];
        var targetInt = new int[1];
        var vocabSize = _vocab.Count;

        using var runOptions = new RunOptions();
        using var frameValue = OrtValue.CreateTensorValueFromMemory(frame, [1, dim, 1]);
        using var targetValue = targetsAreLong
            ? OrtValue.CreateTensorValueFromMemory(targetLong, [1, 1])
            : OrtValue.CreateTensorValueFromMemory(targetInt, [1, 1]);
        using var lengthValue = targetsAreLong
            ? OrtValue.CreateTensorValueFromMemory(new[] { 1L }, [1])
            : OrtValue.CreateTensorValueFromMemory(new[] { 1 }, [1]);
        using var state1Value = OrtValue.CreateTensorValueFromMemory(state1, [shape1[0], 1, shape1[2]]);
        using var state2Value = OrtValue.CreateTensorValueFromMemory(state2, [shape2[0], 1, shape2[2]]);
        string[] inputNames = ["encoder_outputs", "targets", "target_length", "input_states_1", "input_states_2"];
        OrtValue[] inputs = [frameValue, targetValue, lengthValue, state1Value, state2Value];
        string[] outputNames = ["outputs", "output_states_1", "output_states_2"];

        var emitted = new List<(int, int, int, double)>();
        int t = 0, perStep = 0;
        while (t < frameCount)
        {
            ct.ThrowIfCancellationRequested();
            // The encoder's output is [1, D, T]: frame t is every T-th value from t.
            for (var d = 0; d < dim; d++) frame[d] = encoder[d * frames + t];
            var previous = emitted.Count > 0 ? emitted[^1].Item1 : _blank;
            targetLong[0] = previous;
            targetInt[0] = previous;
            using var result = decoder.Run(runOptions, inputNames, inputs, outputNames);
            var logits = result[0].GetTensorDataAsSpan<float>();
            var token = ArgMax(logits, 0, vocabSize);
            var step = ArgMax(logits, vocabSize, logits.Length) - vocabSize;
            if (token != _blank)
            {
                // The state moves on only with an emitted token.
                result[1].GetTensorDataAsSpan<float>().CopyTo(state1);
                result[2].GetTensorDataAsSpan<float>().CopyTo(state2);
                emitted.Add((token, t, step, LogSoftmax(logits, vocabSize, token)));
                perStep++;
            }
            if (step > 0)
            {
                t += step;
                perStep = 0;
            }
            else if (token == _blank || perStep == MaxTokensPerStep)
            {
                t++;
                perStep = 0;
            }
        }
        return emitted;
    }

    /// <summary>Tokens joined into words: a token starting with a space begins a word; punctuation stays on its word.</summary>
    private List<HeardWord> Words(List<(int Token, int Frame, int Frames, double LogProb)> tokens, long offsetMs, long pieceMs)
    {
        var words = new List<(string Text, double Start, double End, List<double> LogProbs)>();
        foreach (var (token, frame, frames, logProb) in tokens)
        {
            var piece = _vocab[token];
            var start = frame * FrameSec;
            var end = Math.Min((frame + Math.Max(frames, 1)) * FrameSec, pieceMs / 1000.0);
            if (words.Count == 0 || piece.StartsWith(' '))
                words.Add((piece, start, end, [logProb]));
            else
            {
                var w = words[^1];
                w.LogProbs.Add(logProb);
                words[^1] = (w.Text + piece, w.Start, Math.Max(w.End, end), w.LogProbs);
            }
        }
        // The geometric mean of the tokens' probabilities: low when the model was unsure of any part.
        return words.Where(w => w.Text.Trim().Length > 0)
            .Select(w => new HeardWord(w.Text.Trim(), offsetMs + (long)Math.Round(w.Start * 1000), offsetMs + (long)Math.Round(w.End * 1000),
                Math.Exp(w.LogProbs.Average())))
            .ToList();
    }

    private static int ArgMax(ReadOnlySpan<float> values, int from, int to)
    {
        var best = from;
        for (var i = from + 1; i < to; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }

    private static double LogSoftmax(ReadOnlySpan<float> logits, int count, int index)
    {
        double max = logits[0];
        for (var i = 1; i < count; i++) max = Math.Max(max, logits[i]);
        double sum = 0;
        for (var i = 0; i < count; i++) sum += Math.Exp(logits[i] - max);
        return logits[index] - max - Math.Log(sum);
    }

    public override void Dispose()
    {
        _preprocessor?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
    }
}
