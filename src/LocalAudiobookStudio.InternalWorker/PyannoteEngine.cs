using System.Globalization;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;
using Microsoft.ML.OnnxRuntime;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// Speaker diarization (who speaks when) with pyannote's community-1 pipeline on ONNX Runtime, CPU: a powerset
/// segmentation network looks at 10 s windows every second and says which of up to three local speakers talk in each
/// frame; a WeSpeaker ResNet34 turns each local speaker of each window into an embedding (weighted by when they talk);
/// the embeddings are clustered (agglomerative start, PLDA, VBx) into the recording's speakers; and the windows are put
/// back together into speaker turns. The arithmetic is <see cref="DiarizationMath"/>. It answers <c>diarize</c> with the
/// turns as segments without text. The models are converted copies of pyannote's (CC-BY-4.0): the segmentation and
/// embedding networks as ONNX, the filterbank features computed here (<see cref="Fbank"/>), and the PLDA and clustering
/// settings in a JSON file. engineConfig names the models.json id and the three files.
/// </summary>
internal sealed class PyannoteEngine(EngineContext context) : EngineBase(context)
{
    private sealed record Setup(
        InferenceSession Segmentation, InferenceSession Embedding, int ChunkSamples, int StepSamples, int Frames, int Speakers,
        double FrameDuration, double FrameStep, double ChunkDuration, double ChunkStep, int[][] Powerset,
        int FbankFrames, int PoolFrames, int MinSamples, double Threshold, double Fa, double Fb, double MinActiveRatio,
        int MaxIterations, double InitSmoothing, double Epsilon, DiarizationMath.Plda Plda);

    private Setup? _setup;

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
        using var request = Begin(id);
        await EnsureLoadedAsync(id, request.Token);
        Protocol.Send("prepared", id);
        Protocol.Send("ready", id);
    }

    private async Task<Setup> EnsureLoadedAsync(string? id, CancellationToken ct)
    {
        if (_setup != null) return _setup;
        var (_, entry) = Context.Model();
        Protocol.Progress(id, 0.01, "Getting the diarization models");
        var folder = await Protocol.NeedModelAsync(id, new ModelNeed(EngineContext.Require(entry, "model")), ct);

        Protocol.Progress(id, 0.1, "Loading on ONNX Runtime (CPU)");
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, EngineContext.Require(entry, "plda"))))!;
        var seg = config["segmentation"]!;
        var emb = config["embedding"]!;
        var clu = config["clustering"]!;
        var plda = config["plda"]!;

        double[] Vector(JsonNode? n) => n!.AsArray().Select(v => (double)v!).ToArray();
        double[][] Matrix(JsonNode? n)
        {
            var shape = n!["shape"]!.AsArray().Select(v => (int)v!).ToArray();
            var data = Vector(n["data"]);
            return Enumerable.Range(0, shape[0]).Select(r => data[(r * shape[1])..((r + 1) * shape[1])]).ToArray();
        }
        var model = new DiarizationMath.Plda(Vector(plda["mean1"]), Vector(plda["mean2"]), Matrix(plda["lda"]), Vector(plda["mu"]),
            Matrix(plda["transform"]), Vector(plda["phi"]));

        // One thread per call; the engine runs many calls at once (see Concurrency).
        var options = new SessionOptions { IntraOpNumThreads = 1 };
        var segmentation = new InferenceSession(Path.Combine(folder, EngineContext.Require(entry, "segmentation")), options);
        var embedding = new InferenceSession(Path.Combine(folder, EngineContext.Require(entry, "embedding")), options);

        var sampleRate = (int)config["sample_rate"]!;
        if (sampleRate != AudioPieces.SampleRate) throw new InvalidOperationException($"the models want {sampleRate} Hz audio");
        _setup = new Setup(segmentation, embedding, (int)seg["window_samples"]!, (int)seg["step_samples"]!, (int)seg["num_frames"]!,
            (int)seg["num_speakers"]!, (double)seg["frame_duration"]!, (double)seg["frame_step"]!, (double)seg["chunk_duration"]!,
            (double)seg["chunk_step"]!,
            seg["powerset"]!.AsArray().Select(row => row!.AsArray().Select(v => (int)v!).ToArray()).ToArray(),
            (int)emb["fbank_frames"]!, (int)emb["pool_frames"]!, (int)emb["min_num_samples"]!,
            (double)clu["threshold"]!, (double)clu["fa"]!, (double)clu["fb"]!, (double)clu["min_active_ratio"]!,
            (int)clu["max_iters"]!, (double)clu["init_smoothing"]!, (double)clu["epsilon"]!, model);
        Protocol.Log($"pyannote community-1 loaded on ONNX Runtime (CPU): {_setup.Speakers} local speakers per {_setup.ChunkDuration:0} s window");
        return _setup;
    }

    public override async Task ListenAsync(string? id, JsonObject msg, ListenMode mode)
    {
        if (mode != ListenMode.Diarize)
            throw new NotSupportedException("the pyannote engine tells voices apart (diarize); it doesn't transcribe or align");
        var path = (string?)msg["audioPath"] ?? throw new ArgumentException("the request has no audioPath");
        using var request = Begin(id);
        var setup = await EnsureLoadedAsync(id, request.Token);
        var settings = msg["settings"] as JsonObject;
        var audio = AudioPieces.ReadWav(path);

        // The app's hint of how many voices there are (a fused generation knows) counts when the settings don't say.
        var hint = msg["speakerCount"] is { } h && int.TryParse(h.ToString(), CultureInfo.InvariantCulture, out var hinted) ? hinted : 0;
        var turns = Diarize(setup, audio, settings, hint, id, request.Token);
        Protocol.Progress(id, 1);
        Protocol.Send("transcript", id,
            ("text", ""),
            ("words", new JsonArray()),
            ("segments", new JsonArray([.. turns.Select(t => (JsonNode?)new JsonObject
            {
                ["text"] = "",
                ["startMs"] = (int)Math.Round(t.Start * 1000),
                ["endMs"] = (int)Math.Round(t.End * 1000),
                ["speaker"] = t.Speaker,
            })])),
            ("language", null));
    }

    /// <summary>Most embeddings the agglomerative step groups at once (it keeps every pair's distance: 6000 take 150 MB).</summary>
    private const int MaxAgglomerative = 6000;

    /// <summary>How many network runs go at once: the cores the app allows (LAS_CPU_THREADS), else all of them.</summary>
    private static int Concurrency =>
        int.TryParse(Environment.GetEnvironmentVariable("LAS_CPU_THREADS"), out var threads) && threads > 0 ? threads : Environment.ProcessorCount;

    private static int Int(JsonObject? settings, string key) =>
        settings?[key] is { } n && int.TryParse(n.ToString(), CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static bool Bool(JsonObject? settings, string key, bool fallback) =>
        settings?[key] is { } n && bool.TryParse(n.ToString(), out var v) ? v : fallback;

    private List<(double Start, double End, string Speaker)> Diarize(Setup s, float[] audio, JsonObject? settings, int hintedSpeakers, string? id, CancellationToken ct)
    {
        var overlap = Bool(settings, "overlap", false);
        int numSpeakers = Int(settings, "numSpeakers"), minSpeakers = Int(settings, "minSpeakers"), maxSpeakers = Int(settings, "maxSpeakers");
        if (numSpeakers == 0 && minSpeakers == 0 && maxSpeakers == 0) numSpeakers = hintedSpeakers;
        if (numSpeakers > 0) minSpeakers = maxSpeakers = numSpeakers;
        if (minSpeakers <= 0) minSpeakers = 1;
        if (maxSpeakers <= 0) maxSpeakers = int.MaxValue;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        string Lap(string what)
        {
            var text = $"{what} {clock.Elapsed.TotalSeconds:0.0} s";
            clock.Restart();
            return text;
        }

        // ---- Chunks: windows of 10 s every second; the last one padded with silence.
        var total = audio.Length;
        var complete = total >= s.ChunkSamples ? (total - s.ChunkSamples) / s.StepSamples + 1 : 0;
        var hasLast = total < s.ChunkSamples || (total - s.ChunkSamples) % s.StepSamples > 0;
        var chunks = complete + (hasLast ? 1 : 0);
        float[] Chunk(int c)
        {
            var window = new float[s.ChunkSamples];
            var start = c * s.StepSamples;
            if (start < total) Array.Copy(audio, start, window, 0, Math.Min(s.ChunkSamples, total - start));
            return window;
        }

        // ---- Segmentation: which local speakers talk in each frame (hard, from the powerset class).
        var binaryChunks = new float[chunks][];
        const int Batch = 4;
        var batches = (chunks + Batch - 1) / Batch;
        var segmented = 0;
        Parallel.For(0, batches, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, batch =>
        {
            var first = batch * Batch;
            Protocol.Progress(id, 0.05 + 0.35 * Interlocked.Increment(ref segmented) / batches, "Finding who talks when");
            var count = Math.Min(Batch, chunks - first);
            var input = new float[count * s.ChunkSamples];
            for (var b = 0; b < count; b++) Chunk(first + b).CopyTo(input, b * s.ChunkSamples);
            using var tensor = OrtValue.CreateTensorValueFromMemory(input, [count, 1, s.ChunkSamples]);
            using var output = s.Segmentation.Run(new RunOptions(), [s.Segmentation.InputNames[0]], [tensor], [s.Segmentation.OutputNames[0]]);
            var logProbs = output[0].GetTensorDataAsSpan<float>();
            var classes = s.Powerset.Length;
            for (var b = 0; b < count; b++)
            {
                var chunk = new float[s.Frames * s.Speakers];
                for (var f = 0; f < s.Frames; f++)
                {
                    var row = logProbs.Slice((b * s.Frames + f) * classes, classes);
                    var best = 0;
                    for (var k = 1; k < classes; k++) if (row[k] > row[best]) best = k;
                    for (var sp = 0; sp < s.Speakers; sp++) chunk[f * s.Speakers + sp] = s.Powerset[best][sp];
                }
                binaryChunks[first + b] = chunk;
            }
        });
        var binary = binaryChunks.ToList();

        var segmentationLap = Lap("segmentation");
        var counts = DiarizationMath.SpeakerCount(binary, s.Frames, s.Speakers, s.ChunkDuration, s.ChunkStep, s.FrameDuration, s.FrameStep);
        Protocol.Log($"diarize: {chunks} windows, {counts.Length} frames, at most {(counts.Length == 0 ? 0 : counts.Max())} voices at once, " +
                     $"{binary.Sum(b => b.Count(v => v > 0))} active frame-slots");
        if (counts.Length == 0 || counts.Max() == 0) return [];

        // ---- Embeddings: one per local speaker that is active in a chunk, weighted by when they talk (alone if they do enough).
        var minFrames = (int)Math.Ceiling(s.Frames * (double)s.MinSamples / s.ChunkSamples);
        var embeddings = new float[chunks][][];
        var jobs = new List<(int Chunk, int Speaker, float[] Weights)>();
        for (var c = 0; c < chunks; c++)
        {
            embeddings[c] = new float[s.Speakers][];
            for (var sp = 0; sp < s.Speakers; sp++)
            {
                var mask = new float[s.Frames];
                var clean = new float[s.Frames];
                float maskSum = 0, cleanSum = 0;
                for (var f = 0; f < s.Frames; f++)
                {
                    float others = 0;
                    for (var k = 0; k < s.Speakers; k++) others += binary[c][f * s.Speakers + k];
                    mask[f] = binary[c][f * s.Speakers + sp];
                    clean[f] = others < 2 ? mask[f] : 0;
                    maskSum += mask[f];
                    cleanSum += clean[f];
                }
                if (maskSum == 0) continue; // never active in this chunk: no embedding, no cluster
                jobs.Add((c, sp, cleanSum > minFrames ? clean : mask));
            }
        }

        // One window per task: its features once, then its (up to 3) voices in one run. The session runs one thread per call
        // and many calls at once, which keeps all cores busy; one call with many threads leaves most of them idle.
        var byChunk = jobs.GroupBy(j => j.Chunk).Select(g => g.ToArray()).ToArray();
        var embedded = 0;
        Parallel.ForEach(byChunk, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, group =>
        {
            var c = group[0].Chunk;
            var count = group.Length;
            // The window's features, in the 16-bit sample range with the mean taken off, as WeSpeaker wants them.
            var scaled = Chunk(c);
            for (var i = 0; i < scaled.Length; i++) scaled[i] *= 32768f;
            var rows = Fbank.Compute(scaled, hamming: true);
            Fbank.SubtractMean(rows);
            var fbank = new float[s.FbankFrames * Fbank.MelBins];
            for (var f = 0; f < Math.Min(rows.Length, s.FbankFrames); f++) rows[f].CopyTo(fbank, f * Fbank.MelBins);

            var features = new float[count * fbank.Length];
            var weights = new float[count * s.PoolFrames];
            // Nearest-neighbour resampling of the mask to the network's frame rate (torch's interpolate).
            var scale = (float)s.Frames / s.PoolFrames;
            for (var b = 0; b < count; b++)
            {
                fbank.CopyTo(features, b * fbank.Length);
                for (var i = 0; i < s.PoolFrames; i++)
                    weights[b * s.PoolFrames + i] = group[b].Weights[Math.Min((int)Math.Floor(i * scale), s.Frames - 1)];
            }
            using var featureTensor = OrtValue.CreateTensorValueFromMemory(features, [count, s.FbankFrames, Fbank.MelBins]);
            using var weightTensor = OrtValue.CreateTensorValueFromMemory(weights, [count, s.PoolFrames]);
            using var output = s.Embedding.Run(new RunOptions(), [.. s.Embedding.InputNames], [featureTensor, weightTensor], [s.Embedding.OutputNames[0]]);
            var result = output[0].GetTensorDataAsSpan<float>();
            var dim = result.Length / count;
            for (var b = 0; b < count; b++) embeddings[c][group[b].Speaker] = result.Slice(b * dim, dim).ToArray();
            Protocol.Progress(id, 0.4 + 0.45 * Interlocked.Increment(ref embedded) / byChunk.Length, "Telling the voices apart");
        });

        var embeddingLap = Lap("embeddings");

        // ---- Clustering.
        Protocol.Progress(id, 0.88, "Grouping the voices");
        var hard = ClusterSpeakers(s, binary, embeddings, minSpeakers, maxSpeakers, ct);
        Protocol.Log($"diarize: {jobs.Count} voice embeddings, {hard.SelectMany(r => r).Where(v => v >= 0).Distinct().Count()} clusters; " +
                     $"{segmentationLap}, {embeddingLap}, {Lap("clustering")}");

        // ---- Back to speaker turns.
        var clusters = hard.SelectMany(r => r).DefaultIfEmpty(-2).Max() + 1;
        if (clusters <= 0) return [];
        var clustered = new List<float[]>(chunks);
        for (var c = 0; c < chunks; c++)
        {
            var data = new float[s.Frames * clusters];
            Array.Fill(data, float.NaN);
            for (var k = 0; k < clusters; k++)
            {
                var members = Enumerable.Range(0, s.Speakers).Where(sp => hard[c][sp] == k).ToList();
                if (members.Count == 0) continue;
                for (var f = 0; f < s.Frames; f++)
                    data[f * clusters + k] = members.Max(sp => binary[c][f * s.Speakers + sp]);
            }
            clustered.Add(data);
        }
        var countUsed = overlap ? counts : counts.Select(v => Math.Min(v, 1)).ToArray();
        var discrete = DiarizationMath.Discrete(clustered, s.Frames, clusters, countUsed, s.ChunkDuration, s.ChunkStep, s.FrameDuration, s.FrameStep, out var frames);
        var turns = DiarizationMath.Turns(discrete, frames, clusters, s.FrameDuration, s.FrameStep);
        Protocol.Log($"diarize: {clusters} clusters, {frames} frames, {discrete.Count(v => v > 0.5f)} active, {turns.Count} turns; turns {Lap("built in")}");

        // Speakers are named in the order they first speak.
        var names = new Dictionary<int, string>();
        foreach (var t in turns)
            if (!names.ContainsKey(t.Speaker)) names[t.Speaker] = $"S{names.Count + 1}";
        return turns.Select(t => (t.Start, t.End, names[t.Speaker])).ToList();
    }

    /// <summary>Which cluster each local speaker of each chunk belongs to (-2: none, for speakers never active).</summary>
    private static int[][] ClusterSpeakers(Setup s, List<float[]> binary, float[][][] embeddings, int minSpeakers, int maxSpeakers, CancellationToken ct)
    {
        var chunks = binary.Count;
        var hard = Enumerable.Range(0, chunks).Select(_ => Enumerable.Repeat(0, s.Speakers).ToArray()).ToArray();
        var train = DiarizationMath.TrainingSet(binary, s.Frames, s.Speakers, embeddings, s.MinActiveRatio);

        if (train.Count >= 2)
        {
            var vectors = train.Select(t => embeddings[t.Chunk][t.Speaker]!).ToArray();
            var normed = vectors.Select(v =>
            {
                var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
                return v.Select(x => x / norm).ToArray();
            }).ToArray();

            ct.ThrowIfCancellationRequested();
            var ahc = DiarizationMath.CentroidClusters(normed, s.Threshold, MaxAgglomerative);
            var count = ahc.Max() + 1;
            var fea = vectors.Select(v => s.Plda.Transform(v)).ToArray();
            var (gamma, pi) = DiarizationMath.Vbx(fea, s.Plda.Phi, s.Fa, s.Fb, DiarizationMath.SmoothedStart(ahc, count, s.InitSmoothing), s.MaxIterations, s.Epsilon);

            var kept = Enumerable.Range(0, count).Where(k => pi[k] > 1e-7).ToList();
            var dim = vectors[0].Length;
            var centroids = new List<double[]>();
            foreach (var k in kept)
            {
                var centroid = new double[dim];
                double weight = 0;
                for (var i = 0; i < vectors.Length; i++)
                {
                    weight += gamma[i][k];
                    for (var d = 0; d < dim; d++) centroid[d] += gamma[i][k] * vectors[i][d];
                }
                for (var d = 0; d < dim; d++) centroid[d] /= weight;
                centroids.Add(centroid);
            }

            // A requested number of speakers VBx didn't find: k-means with that many.
            var constrained = true;
            var wanted = centroids.Count < minSpeakers ? minSpeakers : centroids.Count > maxSpeakers ? maxSpeakers : centroids.Count;
            if (wanted != centroids.Count)
            {
                var labels = DiarizationMath.KMeans(normed, wanted);
                centroids = Enumerable.Range(0, Math.Min(wanted, labels.Max() + 1)).Select(k =>
                {
                    var members = Enumerable.Range(0, vectors.Length).Where(i => labels[i] == k).ToList();
                    return Enumerable.Range(0, dim).Select(d => members.Average(i => (double)vectors[i][d])).ToArray();
                }).ToList();
                constrained = false;
            }

            // Every embedding goes to the most similar centroid (cosine), a local speaker to a cluster of its own per chunk.
            double Similarity(float[] e, double[] centroid)
            {
                double dot = 0, ne = 0, nc = 0;
                for (var d = 0; d < dim; d++) { dot += e[d] * centroid[d]; ne += e[d] * e[d]; nc += centroid[d] * centroid[d]; }
                return 2 - (1 - dot / (Math.Sqrt(ne) * Math.Sqrt(nc))); // 2 - cosine distance
            }
            var soft = new double[chunks][][];
            var floor = double.PositiveInfinity;
            for (var c = 0; c < chunks; c++)
            {
                soft[c] = new double[s.Speakers][];
                for (var sp = 0; sp < s.Speakers; sp++)
                {
                    soft[c][sp] = embeddings[c][sp] is { } e ? centroids.Select(cen => Similarity(e, cen)).ToArray() : new double[centroids.Count];
                    if (embeddings[c][sp] != null) floor = Math.Min(floor, soft[c][sp].Min());
                }
            }
            var inactive = double.IsInfinity(floor) ? 0 : floor - 1;
            for (var c = 0; c < chunks; c++)
            {
                for (var sp = 0; sp < s.Speakers; sp++)
                    if (embeddings[c][sp] == null) Array.Fill(soft[c][sp], inactive);
                hard[c] = constrained
                    ? DiarizationMath.AssignDistinct(soft[c])
                    : soft[c].Select(row => Array.IndexOf(row, row.Max())).ToArray();
            }
        }

        // Speakers never active belong nowhere.
        for (var c = 0; c < chunks; c++)
            for (var sp = 0; sp < s.Speakers; sp++)
                if (embeddings[c][sp] == null) hard[c][sp] = -2;
        return hard;
    }

    public override void Dispose()
    {
        _setup?.Segmentation.Dispose();
        _setup?.Embedding.Dispose();
    }
}
