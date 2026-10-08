using System.Collections.Concurrent;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>Analysis of an imported clip (kept in memory; rebuilt when a project opens).</summary>
public sealed record ClipInfo(string AbsolutePath, double DurationMs, int SampleRate, double? LoudnessLufs);

/// <summary>
/// In-memory index of every take (from the take-NNNN.json files) and every clip, so timeline math
/// and the UI never touch the disk. Loaded in the background when a project opens.
/// </summary>
public sealed class TakeIndex(ProjectPaths paths)
{
    private readonly ConcurrentDictionary<Guid, List<TakeInfo>> _takes = new();
    private readonly ConcurrentDictionary<string, ClipInfo> _clips = new(StringComparer.OrdinalIgnoreCase);

    public ProjectPaths Paths { get; } = paths;

    /// <summary>Scans audio/ for take metadata. Runs on a background thread.</summary>
    public void LoadAll(IEnumerable<Guid> itemIds)
    {
        foreach (var id in itemIds)
        {
            var takes = TakeStore.LoadTakes(Paths, id);
            if (takes.Count > 0) _takes[id] = takes;
        }
    }

    public IReadOnlyList<TakeInfo> Get(Guid itemId)
    {
        if (!_takes.TryGetValue(itemId, out var list)) return [];
        lock (list) return list.ToList();
    }

    public TakeInfo? Find(Guid itemId, int? take)
    {
        if (take == null || !_takes.TryGetValue(itemId, out var list)) return null;
        lock (list) return list.FirstOrDefault(t => t.Take == take);
    }

    public TakeInfo? Selected(TimelineItem item) => Find(item.Id, item.SelectedTake);

    public void Add(Guid itemId, TakeInfo info)
    {
        var list = _takes.GetOrAdd(itemId, _ => new List<TakeInfo>());
        lock (list)
        {
            list.RemoveAll(t => t.Take == info.Take);
            list.Add(info);
            list.Sort((a, b) => a.Take.CompareTo(b.Take));
        }
    }

    public void Remove(Guid itemId) => _takes.TryRemove(itemId, out _);

    public void Reload(Guid itemId)
    {
        var takes = TakeStore.LoadTakes(Paths, itemId);
        if (takes.Count > 0) _takes[itemId] = takes; else _takes.TryRemove(itemId, out _);
    }

    /// <summary>A take's audio file, in whatever format it is stored (WAV or Opus).</summary>
    public string TakeAudio(Guid itemId, int take) =>
        Find(itemId, take) is { } info ? Paths.TakeAudio(itemId, info) : Paths.TakeWav(itemId, take);
    public string TakePeaks(Guid itemId, int take) => Paths.TakePeaks(itemId, take);

    // ------------------------------------------------------------------ clips

    public ClipInfo? Clip(string? clipFile) =>
        clipFile != null && _clips.TryGetValue(clipFile, out var c) ? c : null;

    /// <summary>Analyzes a clip (duration, loudness) and builds its peaks if missing.</summary>
    public ClipInfo? AnalyzeClip(string clipFile)
    {
        var abs = Paths.Resolve(clipFile);
        if (!File.Exists(abs)) { _clips.TryRemove(clipFile, out _); return null; }
        try
        {
            var peaksPath = Paths.ClipPeaks(clipFile);
            var a = TakeAnalyzer.Analyze(abs, File.Exists(peaksPath) ? null : peaksPath);
            var info = new ClipInfo(abs, a.DurationMs, a.SampleRate, a.LoudnessLufs);
            _clips[clipFile] = info;
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Audio source of an item for the mixer, or null when it has no audio yet.</summary>
    public AudioSourceInfo? SourceOf(TimelineItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Tts:
                var t = Selected(item);
                if (t == null) return null;
                return new AudioSourceInfo(Paths.TakeAudio(item.Id, t), t.DurationMs, t.LoudnessLufs, t.TrimStartMs, t.TrimEndMs);
            case ItemKind.Clip:
                var c = Clip(item.ClipFile);
                return c == null ? null : new AudioSourceInfo(c.AbsolutePath, c.DurationMs, c.LoudnessLufs, 0, 0);
            default:
                return null;
        }
    }

    /// <summary>Peaks file path for an item's current audio.</summary>
    public string? PeaksPathOf(TimelineItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Tts:
                return item.SelectedTake is int n && Find(item.Id, n) != null ? TakePeaks(item.Id, n) : null;
            case ItemKind.Clip:
                return item.ClipFile != null ? Paths.ClipPeaks(item.ClipFile) : null;
            default:
                return null;
        }
    }

    /// <summary>The audio file matching <see cref="PeaksPathOf"/> (used to rebuild missing peaks).</summary>
    public string? WavPathOf(TimelineItem item) => SourceOf(item)?.Path;
}

/// <summary>Small LRU cache of loaded peaks. Missing peaks files are rebuilt from the WAV in the background.</summary>
public sealed class PeaksCache(int capacity = 400)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string key, Peaks peaks)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string key, Peaks peaks)> _lru = new();
    private readonly ConcurrentDictionary<string, Task<Peaks?>> _loading = new(StringComparer.OrdinalIgnoreCase);

    public Peaks? TryGet(string peaksPath)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(peaksPath, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.peaks;
        }
    }

    public void Invalidate(string peaksPath)
    {
        lock (_lock)
        {
            if (_map.Remove(peaksPath, out var node)) _lru.Remove(node);
        }
    }

    /// <summary>Forgets every loaded peaks file (after many take files were deleted at once).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    /// <summary>Loads on a background thread; rebuilds the peaks file from <paramref name="wavPath"/> if needed.</summary>
    public Task<Peaks?> LoadAsync(string peaksPath, string? wavPath)
    {
        var cached = TryGet(peaksPath);
        if (cached != null) return Task.FromResult<Peaks?>(cached);
        return _loading.GetOrAdd(peaksPath, key => Task.Run(() =>
        {
            try
            {
                Peaks? p = null;
                if (File.Exists(peaksPath))
                {
                    try { p = Peaks.Load(peaksPath); } catch { p = null; }
                }
                if (p == null && wavPath != null && File.Exists(wavPath))
                {
                    p = Peaks.FromWav(wavPath);
                    try { p.Save(peaksPath); } catch { /* read-only project: keep in memory */ }
                }
                if (p != null)
                {
                    lock (_lock)
                    {
                        if (_map.Remove(peaksPath, out var old)) _lru.Remove(old);
                        _map[peaksPath] = _lru.AddFirst((peaksPath, p));
                        while (_lru.Count > capacity)
                        {
                            _map.Remove(_lru.Last!.Value.key);
                            _lru.RemoveLast();
                        }
                    }
                }
                return p;
            }
            finally
            {
                _loading.TryRemove(peaksPath, out _);
            }
        }));
    }
}
