using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Engine;

/// <summary>How well one file fits the others in a group (see <see cref="SpeakerVerification.Summarize"/>).</summary>
/// <param name="Mean">Its mean similarity to the other files.</param>
/// <param name="Min">Its lowest similarity to another file.</param>
/// <param name="MinIndex">Which file that was against.</param>
/// <param name="Outlier">Well below the group: it sounds like another voice (or is much harder to judge).</param>
public sealed record FileFit(double Mean, double Min, int MinIndex, bool Outlier);

/// <summary>The fit of every file, and the numbers for the whole group (the ones to compare between models or settings).</summary>
/// <param name="PairMean">The mean of all pairs.</param>
/// <param name="PairMin">The lowest pair.</param>
/// <param name="PairSpread">The standard deviation of all pairs.</param>
public sealed record GroupFit(IReadOnlyList<FileFit> Files, double PairMean, double PairMin, double PairSpread);

/// <summary>How a take's SIM score stands against its speaker's thresholds.</summary>
public enum SimLevel
{
    /// <summary>At or above the warning threshold.</summary>
    Ok,

    /// <summary>Below the warning threshold: yellow.</summary>
    Warn,

    /// <summary>Below the fail threshold: red.</summary>
    Fail,
}

/// <summary>
/// Speaker verification with a worker tagged Speaker Verification: building its job and judging scores. A take is scored
/// against a reference clip and compared with its speaker's thresholds (<see cref="LevelOf"/>); files compared with each
/// other (<c>las compare</c>) are judged against the group (<see cref="IsOutlier"/>, <see cref="Summarize"/>). Embeddings
/// capture timbre and pitch, not rhythm; emotion, whispering, loudness and short clips lower them, and different models
/// don't agree, so a threshold only means something for the model it was set for.
/// </summary>
public static class SpeakerVerification
{
    /// <summary>A score below <paramref name="fail"/> is red, below <paramref name="warn"/> yellow.</summary>
    public static SimLevel LevelOf(double score, double warn, double fail) =>
        score < fail ? SimLevel.Fail : score < warn ? SimLevel.Warn : SimLevel.Ok;

    /// <summary>A file with less speech than this isn't marked: short clips score low whatever the voice.</summary>
    public const int MinSpeechMs = 2500;

    /// <summary>The other scores needed before one can be judged; with fewer there is no baseline.</summary>
    public const int MinBaseline = 4;

    /// <summary>How many robust standard deviations below the median a score may be.</summary>
    public const double Deviations = 3;

    /// <summary>The smallest spread assumed, so a very tight group doesn't flag takes that differ by hundredths.</summary>
    public const double MinSpread = 0.02;

    /// <summary>A job that compares <paramref name="files"/> (16 kHz WAVs made with <c>Transcription.PrepareAudio</c>).</summary>
    public static GenerationJob Job(WorkerManifest worker, IReadOnlyList<VoiceFile> files, JsonObject settings, string label,
        JobPriority priority, object? tag = null, long readingOrder = 0) =>
        new()
        {
            WorkerId = worker.Id,
            Priority = priority,
            ReadingOrder = readingOrder,
            Label = label,
            Tag = tag,
            CompareVoices = new VoiceCompareRequest { Files = files, Settings = settings },
        };

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// The score below which a score is an outlier among <paramref name="others"/>: the median minus a few robust standard
    /// deviations (the median absolute deviation, scaled), never less than <see cref="MinSpread"/> wide. Null when there
    /// are too few others for a baseline.
    /// </summary>
    public static double? LowerBound(IReadOnlyList<double> others)
    {
        if (others.Count < MinBaseline) return null;
        var median = Median(others);
        var mad = Median(others.Select(v => Math.Abs(v - median)).ToList());
        return median - Deviations * Math.Max(1.4826 * mad, MinSpread);
    }

    /// <summary>
    /// Whether <paramref name="score"/> is well below the speaker's other scores. Not for a take with less than
    /// <see cref="MinSpeechMs"/> of speech, nor without a baseline.
    /// </summary>
    public static bool IsOutlier(double score, int speechMs, IReadOnlyList<double> others) =>
        speechMs >= MinSpeechMs && LowerBound(others) is { } bound && score < bound;

    /// <summary>
    /// For files compared with each other (<c>las compare</c>): each file's mean and lowest similarity to the others and
    /// whether it is an outlier by the same rule as a take in the app (its mean against the others' means), then the
    /// mean, lowest and spread of all pairs. <paramref name="matrix"/> is square and symmetric; its diagonal is ignored.
    /// </summary>
    public static GroupFit Summarize(IReadOnlyList<IReadOnlyList<double>> matrix)
    {
        var n = matrix.Count;
        var means = new double[n];
        var fits = new List<(double Mean, double Min, int MinIndex)>();
        for (var i = 0; i < n; i++)
        {
            var others = Enumerable.Range(0, n).Where(j => j != i).ToList();
            var min = others.MinBy(j => matrix[i][j]);
            means[i] = others.Average(j => matrix[i][j]);
            fits.Add((means[i], matrix[i][min], min));
        }
        var files = fits.Select((f, i) =>
            new FileFit(f.Mean, f.Min, f.MinIndex, LowerBound(means.Where((_, j) => j != i).ToList()) is { } bound && f.Mean < bound)).ToList();

        var pairs = new List<double>();
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++) pairs.Add(matrix[i][j]);
        var pairMean = pairs.Average();
        var spread = Math.Sqrt(pairs.Sum(p => (p - pairMean) * (p - pairMean)) / pairs.Count);
        return new GroupFit(files, pairMean, pairs.Min(), spread);
    }
}
