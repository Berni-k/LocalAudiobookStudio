namespace LocalAudiobookStudio.Audio.Mixing;

/// <summary>
/// One audible item on the timeline. Positions come from the timeline calculator; nothing here
/// modifies the audio file (trims, gain and fades are applied while mixing).
/// </summary>
public sealed record MixItem(
    Guid ItemId,
    double StartMs,
    double DurationMs,
    string Path,
    double TrimStartMs,
    double GainDb,
    double FadeInMs,
    double FadeOutMs,
    double LoopMs = 0)
{
    // LoopMs > 0: the (trimmed) source is this long and repeats to fill DurationMs (background loops).

    /// <summary>
    /// <see cref="Path"/> value for a placeholder: quiet static generated on the fly, standing in for
    /// audio that hasn't been generated yet (playback only, never exported).
    /// </summary>
    public const string PlaceholderNoise = "placeholder:noise";

    public bool IsPlaceholder => Path == PlaceholderNoise;
    public double EndMs => StartMs + DurationMs;
    public float GainLinear => (float)Math.Pow(10, GainDb / 20.0);
}

/// <summary>An immutable, start-sorted list of audible items with a fast overlap query.</summary>
public sealed class MixPlan
{
    public static readonly MixPlan Empty = new([], 0);

    private readonly MixItem[] _items;
    private readonly double[] _maxEnd;

    /// <param name="items">Audible items (pauses and missing audio are simply absent).</param>
    /// <param name="totalMs">Timeline length, including trailing silence and estimated items.</param>
    public MixPlan(IEnumerable<MixItem> items, double totalMs)
    {
        _items = items.Where(i => i.DurationMs > 0).OrderBy(i => i.StartMs).ToArray();
        _maxEnd = new double[_items.Length];
        double m = double.MinValue;
        for (int i = 0; i < _items.Length; i++)
        {
            m = Math.Max(m, _items[i].EndMs);
            _maxEnd[i] = m;
        }
        TotalMs = Math.Max(totalMs, _items.Length > 0 ? _maxEnd[^1] : 0);
    }

    public IReadOnlyList<MixItem> Items => _items;
    public double TotalMs { get; }

    /// <summary>Appends items overlapping [fromMs, toMs) to <paramref name="result"/>.</summary>
    public void Overlapping(double fromMs, double toMs, List<MixItem> result)
    {
        int lo = 0, hi = _items.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_maxEnd[mid] > fromMs) hi = mid; else lo = mid + 1;
        }
        for (int i = lo; i < _items.Length; i++)
        {
            var it = _items[i];
            if (it.StartMs >= toMs) break;
            if (it.EndMs > fromMs) result.Add(it);
        }
    }
}
