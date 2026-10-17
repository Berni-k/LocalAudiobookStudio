namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// The arithmetic of pyannote.audio 4's speaker diarization pipeline (community-1), apart from the neural networks: how
/// the overlapping windows of the segmentation model are added up, how speakers are counted, the clustering of speaker
/// embeddings (agglomerative start, PLDA, VBx), and how clusters become speaker turns. A port of
/// pyannote/audio/core/inference.py (aggregate), pipelines/speaker_diarization.py, pipelines/clustering.py
/// (VBxClustering), utils/vbx.py and utils/signal.py (Binarize), all MIT licensed. Has no dependencies, so the tests
/// compile this file too.
/// </summary>
internal static class DiarizationMath
{
    /// <summary>numpy's <c>rint</c> (halves go to the even number), which pyannote's frame arithmetic relies on.</summary>
    private static int Rint(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    /// <summary>The frame whose middle is nearest <paramref name="t"/> (pyannote's <c>SlidingWindow.closest_frame</c>, starting at 0).</summary>
    public static int ClosestFrame(double t, double frameDuration, double frameStep) =>
        Rint((t - 0.5 * frameDuration) / frameStep);

    // ------------------------------------------------------------------ windows

    /// <summary>
    /// Adds up scores of overlapping chunks into one track of frames (<c>Inference.aggregate</c>, no Hamming window, no
    /// warm-up). <paramref name="scores"/>[chunk] holds frames × classes values in row-major order; NaN means missing.
    /// Averaged by how many chunks reached a frame, unless <paramref name="skipAverage"/> (then summed). Frames no chunk
    /// reached are 0. Returns frames × classes.
    /// </summary>
    public static float[] Aggregate(IReadOnlyList<float[]> scores, int framesPerChunk, int classes, double chunkDuration, double chunkStep,
        double frameDuration, double frameStep, bool skipAverage, out int frames)
    {
        var chunks = scores.Count;
        frames = ClosestFrame(chunkDuration + (chunks - 1) * chunkStep + 0.5 * frameDuration, frameDuration, frameStep) + 1;
        var sum = new float[frames * classes];
        var count = new float[frames * classes];
        for (var c = 0; c < chunks; c++)
        {
            var first = ClosestFrame(c * chunkStep + 0.5 * frameDuration, frameDuration, frameStep);
            var chunk = scores[c];
            for (var f = 0; f < framesPerChunk && first + f < frames; f++)
                for (var k = 0; k < classes; k++)
                {
                    var v = chunk[f * classes + k];
                    if (float.IsNaN(v)) continue;
                    sum[(first + f) * classes + k] += v;
                    count[(first + f) * classes + k] += 1;
                }
        }
        if (skipAverage) return sum; // missing frames are 0 already
        for (var i = 0; i < sum.Length; i++) sum[i] = count[i] == 0 ? 0 : sum[i] / Math.Max(count[i], 1e-12f);
        return sum;
    }

    /// <summary>
    /// The speakers active at each frame, counted from the chunks' binary activity: the per-chunk sums added up and
    /// rounded (half to even), as <c>speaker_diarization.speaker_count</c> does with no warm-up.
    /// </summary>
    public static int[] SpeakerCount(IReadOnlyList<float[]> binary, int framesPerChunk, int speakers, double chunkDuration, double chunkStep,
        double frameDuration, double frameStep)
    {
        var sums = binary.Select(chunk =>
        {
            var s = new float[framesPerChunk];
            for (var f = 0; f < framesPerChunk; f++)
                for (var k = 0; k < speakers; k++) s[f] += chunk[f * speakers + k];
            return s;
        }).ToList();
        var average = Aggregate(sums, framesPerChunk, 1, chunkDuration, chunkStep, frameDuration, frameStep, skipAverage: false, out _);
        return average.Select(v => Rint(v)).ToArray();
    }

    // ------------------------------------------------------------------ embeddings

    /// <summary>
    /// Which (chunk, speaker) embeddings take part in the clustering: those the speaker is the only one talking for at
    /// least <paramref name="minActiveRatio"/> of the chunk, with an embedding (<c>BaseClustering.filter_embeddings</c>).
    /// <paramref name="binary"/>[chunk] is frames × speakers.
    /// </summary>
    public static List<(int Chunk, int Speaker)> TrainingSet(IReadOnlyList<float[]> binary, int framesPerChunk, int speakers, float[][][] embeddings,
        double minActiveRatio)
    {
        var set = new List<(int, int)>();
        for (var c = 0; c < binary.Count; c++)
            for (var s = 0; s < speakers; s++)
            {
                if (embeddings[c][s] == null) continue;
                double clean = 0;
                for (var f = 0; f < framesPerChunk; f++)
                {
                    float total = 0;
                    for (var k = 0; k < speakers; k++) total += binary[c][f * speakers + k];
                    if (total == 1) clean += binary[c][f * speakers + s];
                }
                if (clean >= minActiveRatio * framesPerChunk) set.Add((c, s));
            }
        return set;
    }

    private static void L2Normalize(double[] v)
    {
        double norm = 0;
        foreach (var x in v) norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    /// <summary>The x-vector → PLDA space transformation VBx works in, from the numbers in community-1's PLDA files.</summary>
    internal sealed class Plda(double[] mean1, double[] mean2, double[][] lda, double[] mu, double[][] transform, double[] phi)
    {
        /// <summary>The between-class variances of the PLDA space (<c>PLDA.phi</c>), one per dimension.</summary>
        public double[] Phi { get; } = phi;

        public int Dimension => mean2.Length;

        /// <summary>An embedding in the PLDA space: centre, length-normalise, LDA, again, then the PLDA transform.</summary>
        public double[] Transform(float[] embedding)
        {
            var inDim = mean1.Length;
            var v = new double[inDim];
            for (var i = 0; i < inDim; i++) v[i] = embedding[i] - mean1[i];
            L2Normalize(v);
            var scale1 = Math.Sqrt(lda.Length);        // sqrt(lda.shape[0])
            var outDim = mean2.Length;
            var w = new double[outDim];
            for (var j = 0; j < outDim; j++)
            {
                double sum = 0;
                for (var i = 0; i < inDim; i++) sum += lda[i][j] * v[i] * scale1;
                w[j] = sum - mean2[j];
            }
            L2Normalize(w);
            var scale2 = Math.Sqrt(outDim);             // sqrt(lda.shape[1])
            for (var j = 0; j < outDim; j++) w[j] *= scale2;

            var result = new double[outDim];
            for (var j = 0; j < outDim; j++)
            {
                double sum = 0;
                for (var i = 0; i < outDim; i++) sum += (w[i] - mu[i]) * transform[j][i];
                result[j] = sum;
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ clustering

    /// <summary>
    /// Agglomerative clustering with centroid linkage on Euclidean distances (scipy's <c>linkage(method="centroid")</c>),
    /// cut where merges are no further apart than <paramref name="threshold"/> (<c>fcluster(criterion="distance")</c>: a
    /// cluster is a subtree none of whose merges is above it). Returns a cluster number per vector, from 0.
    /// </summary>
    public static int[] CentroidClusters(float[][] vectors, double threshold, int maxVectors = int.MaxValue)
    {
        if (vectors.Length > maxVectors)
        {
            // Every distance is kept, so a long recording is grouped from an evenly spaced part of its embeddings (the
            // windows overlap by 90%, so neighbours say nearly the same) and the rest join the nearest cluster.
            var stride = (vectors.Length + maxVectors - 1) / maxVectors;
            var picked = Enumerable.Range(0, vectors.Length).Where(i => i % stride == 0).ToArray();
            var sub = CentroidClusters(picked.Select(i => vectors[i]).ToArray(), threshold);
            var dim = vectors[0].Length;
            var count = sub.Max() + 1;
            var centroids = new double[count][];
            var sizes = new int[count];
            for (var k = 0; k < count; k++) centroids[k] = new double[dim];
            for (var j = 0; j < picked.Length; j++)
            {
                sizes[sub[j]]++;
                for (var x = 0; x < dim; x++) centroids[sub[j]][x] += vectors[picked[j]][x];
            }
            for (var k = 0; k < count; k++)
                for (var x = 0; x < dim; x++) centroids[k][x] /= sizes[k];
            var joined = new int[vectors.Length];
            Parallel.For(0, vectors.Length, i =>
            {
                var best = double.PositiveInfinity;
                for (var k = 0; k < count; k++)
                {
                    double sum = 0;
                    for (var x = 0; x < dim; x++) { var diff = vectors[i][x] - centroids[k][x]; sum += diff * diff; }
                    if (sum < best) { best = sum; joined[i] = k; }
                }
            });
            return joined;
        }

        var n = vectors.Length;
        if (n == 0) return [];
        if (n == 1) return [0];

        var d = new float[(long)n * n];
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                double sum = 0;
                var a = vectors[i];
                var b = vectors[j];
                for (var k = 0; k < a.Length; k++) { var diff = a[k] - b[k]; sum += diff * diff; }
                d[(long)i * n + j] = d[(long)j * n + i] = (float)Math.Sqrt(sum);
            }

        var size = new int[n];
        var active = new bool[n];
        var node = new int[n];                 // the dendrogram node a slot currently holds
        var nn = new int[n];
        var nnDistance = new float[n];
        for (var i = 0; i < n; i++) { size[i] = 1; active[i] = true; node[i] = i; }

        void Nearest(int i)
        {
            var best = float.PositiveInfinity;
            var arg = -1;
            for (var j = 0; j < n; j++)
            {
                if (j == i || !active[j]) continue;
                var v = d[(long)i * n + j];
                if (v < best) { best = v; arg = j; }
            }
            nn[i] = arg;
            nnDistance[i] = best;
        }
        for (var i = 0; i < n; i++) Nearest(i);

        // Merge m creates node n + m from nodes Left[m] and Right[m] at Height[m].
        var left = new int[n - 1];
        var right = new int[n - 1];
        var height = new double[n - 1];
        for (var step = 0; step < n - 1; step++)
        {
            var a = -1;
            var best = float.PositiveInfinity;
            for (var i = 0; i < n; i++)
                if (active[i] && nnDistance[i] < best) { best = nnDistance[i]; a = i; }
            var b = nn[a];
            if (b < a) (a, b) = (b, a);
            double dab = d[(long)a * n + b];
            double sa = size[a], sb = size[b];

            for (var k = 0; k < n; k++)
            {
                if (!active[k] || k == a || k == b) continue;
                double dka = d[(long)k * n + a], dkb = d[(long)k * n + b];
                var updated = Math.Sqrt(Math.Max(0, (sa * dka * dka + sb * dkb * dkb - sa * sb * dab * dab / (sa + sb)) / (sa + sb)));
                d[(long)a * n + k] = d[(long)k * n + a] = (float)updated;
            }
            left[step] = node[a];
            right[step] = node[b];
            height[step] = dab;
            node[a] = n + step;
            size[a] += size[b];
            active[b] = false;

            for (var k = 0; k < n; k++)
            {
                if (!active[k] || k == a) continue;
                if (nn[k] == a || nn[k] == b) Nearest(k);
                else if (d[(long)k * n + a] < nnDistance[k]) { nn[k] = a; nnDistance[k] = d[(long)k * n + a]; }
            }
            Nearest(a);
        }

        // A subtree is one cluster when its highest merge is within the threshold.
        var highest = new double[n - 1];
        double Highest(int nodeId) => nodeId < n ? 0 : highest[nodeId - n];
        for (var m = 0; m < n - 1; m++) highest[m] = Math.Max(height[m], Math.Max(Highest(left[m]), Highest(right[m])));

        var labels = new int[n];
        Array.Fill(labels, -1);
        var next = 0;
        var stack = new Stack<int>();
        stack.Push(2 * n - 2);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current < n) { labels[current] = next++; continue; }
            if (highest[current - n] <= threshold)
            {
                var cluster = next++;
                var inside = new Stack<int>();
                inside.Push(current);
                while (inside.Count > 0)
                {
                    var x = inside.Pop();
                    if (x < n) labels[x] = cluster;
                    else { inside.Push(left[x - n]); inside.Push(right[x - n]); }
                }
                continue;
            }
            stack.Push(right[current - n]);
            stack.Push(left[current - n]);
        }
        // Numbered in the order the vectors first appear.
        var renumber = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
            if (!renumber.ContainsKey(labels[i])) renumber[labels[i]] = renumber.Count;
        return labels.Select(l => renumber[l]).ToArray();
    }

    /// <summary>
    /// VBx (Landini et al.): Bayesian HMM-free clustering of x-vectors in the PLDA space, started from the agglomerative
    /// clusters. <paramref name="initial"/> are the starting responsibilities (frames × speakers). Returns the final
    /// responsibilities and speaker priors (<c>vbx.VBx</c> with <c>cluster_vbx</c>'s settings).
    /// </summary>
    public static (double[][] Gamma, double[] Pi) Vbx(double[][] x, double[] phi, double fa, double fb, double[][] initial, int maxIterations, double epsilon)
    {
        var frames = x.Length;
        var speakers = initial[0].Length;
        var dim = phi.Length;
        var gamma = initial.Select(r => (double[])r.Clone()).ToArray();
        var pi = Enumerable.Repeat(1.0 / speakers, speakers).ToArray();

        var constant = new double[frames];   // per-frame constant term, (23)
        for (var t = 0; t < frames; t++)
        {
            double sq = 0;
            for (var d = 0; d < dim; d++) sq += x[t][d] * x[t][d];
            constant[t] = -0.5 * (sq + dim * Math.Log(2 * Math.PI));
        }
        var rho = new double[frames][];       // (18)
        for (var t = 0; t < frames; t++)
        {
            rho[t] = new double[dim];
            for (var d = 0; d < dim; d++) rho[t][d] = x[t][d] * Math.Sqrt(phi[d]);
        }

        var invL = new double[speakers][];
        var alpha = new double[speakers][];
        var logP = new double[frames][];
        for (var t = 0; t < frames; t++) logP[t] = new double[speakers];
        double previous = double.NaN;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            for (var s = 0; s < speakers; s++)
            {
                double total = 0;
                for (var t = 0; t < frames; t++) total += gamma[t][s];
                invL[s] = new double[dim];
                alpha[s] = new double[dim];
                for (var d = 0; d < dim; d++) invL[s][d] = 1.0 / (1 + fa / fb * total * phi[d]);                  // (17)
                for (var t = 0; t < frames; t++)
                    for (var d = 0; d < dim; d++) alpha[s][d] += gamma[t][s] * rho[t][d];
                for (var d = 0; d < dim; d++) alpha[s][d] = fa / fb * invL[s][d] * alpha[s][d];                    // (16)
            }

            var penalty = new double[speakers];
            for (var s = 0; s < speakers; s++)
                for (var d = 0; d < dim; d++) penalty[s] += (invL[s][d] + alpha[s][d] * alpha[s][d]) * phi[d];

            var logPi = pi.Select(p => Math.Log(p + 1e-8)).ToArray();
            double logLikelihood = 0;
            for (var t = 0; t < frames; t++)
            {
                var max = double.NegativeInfinity;
                for (var s = 0; s < speakers; s++)
                {
                    double dot = 0;
                    for (var d = 0; d < dim; d++) dot += rho[t][d] * alpha[s][d];
                    logP[t][s] = fa * (dot - 0.5 * penalty[s] + constant[t]);                                      // (23)
                    max = Math.Max(max, logP[t][s] + logPi[s]);
                }
                double sum = 0;
                for (var s = 0; s < speakers; s++) sum += Math.Exp(logP[t][s] + logPi[s] - max);
                var logPx = max + Math.Log(sum);
                logLikelihood += logPx;
                for (var s = 0; s < speakers; s++) gamma[t][s] = Math.Exp(logP[t][s] + logPi[s] - logPx);
            }
            var newPi = new double[speakers];
            for (var t = 0; t < frames; t++)
                for (var s = 0; s < speakers; s++) newPi[s] += gamma[t][s];
            var piSum = newPi.Sum();
            for (var s = 0; s < speakers; s++) pi[s] = newPi[s] / piSum;

            double regulariser = 0;
            for (var s = 0; s < speakers; s++)
                for (var d = 0; d < dim; d++) regulariser += Math.Log(invL[s][d]) - invL[s][d] - alpha[s][d] * alpha[s][d] + 1;
            var elbo = logLikelihood + fb * 0.5 * regulariser;                                                     // (25)
            if (iteration > 0 && elbo - previous < epsilon) break;
            previous = elbo;
        }
        return (gamma, pi);
    }

    /// <summary>softmax(one-hot(cluster) × <paramref name="smoothing"/>) per row: where VBx starts from the agglomerative clusters.</summary>
    public static double[][] SmoothedStart(int[] clusters, int count, double smoothing)
    {
        var start = new double[clusters.Length][];
        var high = Math.Exp(smoothing);
        for (var t = 0; t < clusters.Length; t++)
        {
            start[t] = new double[count];
            var total = (count - 1) + high;
            for (var s = 0; s < count; s++) start[t][s] = (s == clusters[t] ? high : 1.0) / total;
        }
        return start;
    }

    /// <summary>
    /// k-means on unit-length vectors with a fixed start (the farthest-point rule), for when the number of speakers is
    /// given and VBx found another number (pyannote uses scikit-learn's with a fixed seed; this is its stand-in).
    /// </summary>
    public static int[] KMeans(float[][] vectors, int clusters)
    {
        var n = vectors.Length;
        clusters = Math.Min(clusters, n);
        double Dist(float[] a, double[] b)
        {
            double sum = 0;
            for (var i = 0; i < a.Length; i++) { var diff = a[i] - b[i]; sum += diff * diff; }
            return sum;
        }
        var centers = new List<double[]> { vectors[0].Select(v => (double)v).ToArray() };
        while (centers.Count < clusters)
        {
            var far = Enumerable.Range(0, n).MaxBy(i => centers.Min(c => Dist(vectors[i], c)));
            centers.Add(vectors[far].Select(v => (double)v).ToArray());
        }
        var labels = new int[n];
        for (var round = 0; round < 100; round++)
        {
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                var best = Enumerable.Range(0, clusters).MinBy(k => Dist(vectors[i], centers[k]));
                if (best != labels[i] || round == 0) { changed |= best != labels[i]; labels[i] = best; }
            }
            for (var k = 0; k < clusters; k++)
            {
                var members = Enumerable.Range(0, n).Where(i => labels[i] == k).ToList();
                if (members.Count == 0) continue;
                for (var d = 0; d < centers[k].Length; d++) centers[k][d] = members.Average(i => (double)vectors[i][d]);
            }
            if (!changed && round > 0) break;
        }
        return labels;
    }

    /// <summary>
    /// Gives each of the chunk's local speakers its own cluster, the one whose centroid is most similar overall
    /// (scipy's <c>linear_sum_assignment(maximize=True)</c>): at most one local speaker per cluster, and with fewer
    /// clusters than speakers some go without (-2). Local speakers that are never active are excluded by the caller.
    /// </summary>
    public static int[] AssignDistinct(double[][] similarity)
    {
        var speakers = similarity.Length;
        var clusters = speakers == 0 ? 0 : similarity[0].Length;
        var assigned = Math.Min(speakers, clusters);
        var best = double.NegativeInfinity;
        var bestChoice = Enumerable.Repeat(-2, speakers).ToArray();
        var choice = Enumerable.Repeat(-2, speakers).ToArray();
        var used = new bool[clusters];

        void Search(int speaker, int count, double total)
        {
            if (speaker == speakers)
            {
                if (count == assigned && total > best) { best = total; bestChoice = (int[])choice.Clone(); }
                return;
            }
            // (Speakers still to place must be able to reach the required number of assignments.)
            if (count + (speakers - speaker) >= assigned)
            {
                choice[speaker] = -2;
                Search(speaker + 1, count, total);
            }
            if (count < assigned)
                for (var k = 0; k < clusters; k++)
                {
                    if (used[k]) continue;
                    used[k] = true;
                    choice[speaker] = k;
                    Search(speaker + 1, count + 1, total + similarity[speaker][k]);
                    used[k] = false;
                    choice[speaker] = -2;
                }
        }
        Search(0, 0, 0);
        return bestChoice;
    }

    // ------------------------------------------------------------------ turns

    /// <summary>
    /// The discrete diarization: at each frame, the <paramref name="count"/> clusters with the most activity are on
    /// (<c>SpeakerDiarizationMixin.to_diarization</c>). <paramref name="clustered"/>[chunk] is frames × clusters with NaN
    /// where the chunk has no speaker in that cluster. Returns frames × clusters of 0 and 1.
    /// </summary>
    public static float[] Discrete(IReadOnlyList<float[]> clustered, int framesPerChunk, int clusters, int[] count, double chunkDuration, double chunkStep,
        double frameDuration, double frameStep, out int frames)
    {
        var activations = Aggregate(clustered, framesPerChunk, clusters, chunkDuration, chunkStep, frameDuration, frameStep, skipAverage: true, out frames);
        var binary = new float[frames * clusters];
        var order = new int[clusters];
        for (var t = 0; t < frames; t++)
        {
            var n = t < count.Length ? count[t] : 0;
            if (n == 0) continue;
            for (var k = 0; k < clusters; k++) order[k] = k;
            var row = t * clusters;
            Array.Sort(order, (a, b) =>
            {
                var c = activations[row + b].CompareTo(activations[row + a]); // most active first
                return c != 0 ? c : a.CompareTo(b);
            });
            for (var i = 0; i < Math.Min(n, clusters); i++) binary[row + order[i]] = 1;
        }
        return binary;
    }

    /// <summary>
    /// Speaker turns out of the discrete diarization (<c>Binarize(onset=offset=0.5)</c> per speaker): a turn starts at the
    /// middle of its first active frame and ends at the middle of the first frame after it that is off. Sorted by start.
    /// </summary>
    public static List<(double Start, double End, int Speaker)> Turns(float[] discrete, int frames, int speakers, double frameDuration, double frameStep)
    {
        var turns = new List<(double, double, int)>();
        if (frames == 0) return turns;
        double Middle(int frame) => frame * frameStep + frameDuration / 2;
        for (var k = 0; k < speakers; k++)
        {
            var start = Middle(0);
            var on = discrete[k] > 0.5f;
            var last = Middle(0);
            for (var t = 1; t < frames; t++)
            {
                var time = Middle(t);
                last = time;
                var y = discrete[t * speakers + k];
                if (on)
                {
                    if (y < 0.5f) { turns.Add((start, time, k)); start = time; on = false; }
                }
                else if (y > 0.5f) { start = time; on = true; }
            }
            if (on) turns.Add((start, last, k));
        }
        return [.. turns.OrderBy(t => t.Item1).ThenBy(t => t.Item3)];
    }
}
