using System.Collections.Concurrent;
using LocalAudiobookStudio.Audio.Decoding;

namespace LocalAudiobookStudio.Audio.Mixing;

/// <summary>
/// LRU cache of decoded audio (stereo at the project rate), bounded by total size.
/// Decoding happens on background threads via <see cref="Prefetch"/>; <see cref="Get"/> decodes
/// synchronously only if a buffer is needed before its prefetch finished.
/// </summary>
public sealed class AudioBufferCache(int sampleRate, long maxBytes = 512L * 1024 * 1024)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = new();
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;

    private sealed record Entry(string Key, DecodedAudio Buffer, DateTime FileTimeUtc);

    public int SampleRate { get; } = sampleRate;

    /// <summary>Returns the decoded buffer, or null if the file is missing or unreadable.</summary>
    public DecodedAudio? Get(string path)
    {
        var stamp = Stamp(path);
        if (stamp == null) return null;
        lock (_lock)
        {
            if (_map.TryGetValue(path, out var node) && node.Value.FileTimeUtc == stamp)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Buffer;
            }
        }
        if (_inflight.TryGetValue(path, out var pending))
        {
            try { pending.Wait(TimeSpan.FromSeconds(5)); } catch { }
            lock (_lock)
                if (_map.TryGetValue(path, out var n2)) return n2.Value.Buffer;
        }
        return Load(path, stamp.Value);
    }

    /// <summary>True when the buffer is already decoded (never blocks).</summary>
    public bool Contains(string path)
    {
        lock (_lock) return _map.ContainsKey(path);
    }

    /// <summary>Starts decoding in the background if not cached.</summary>
    public void Prefetch(string path)
    {
        if (Contains(path) || _inflight.ContainsKey(path)) return;
        var t = new Task(() =>
        {
            try
            {
                var stamp = Stamp(path);
                if (stamp != null) Load(path, stamp.Value);
            }
            catch
            {
                // A failed prefetch just means Get() will try again.
            }
            finally
            {
                _inflight.TryRemove(path, out _);
            }
        });
        if (_inflight.TryAdd(path, t)) t.Start();
    }

    public void Invalidate(string path)
    {
        lock (_lock)
        {
            if (_map.Remove(path, out var node))
            {
                _lru.Remove(node);
                _bytes -= node.Value.Buffer.ByteSize;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    private DecodedAudio? Load(string path, DateTime stamp)
    {
        DecodedAudio buf;
        try { buf = AudioDecoder.DecodeStereo(path, SampleRate); }
        catch { return null; }

        lock (_lock)
        {
            if (_map.Remove(path, out var old))
            {
                _lru.Remove(old);
                _bytes -= old.Value.Buffer.ByteSize;
            }
            var node = _lru.AddFirst(new Entry(path, buf, stamp));
            _map[path] = node;
            _bytes += buf.ByteSize;
            while (_bytes > maxBytes && _lru.Count > 1)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Buffer.ByteSize;
            }
        }
        return buf;
    }

    private static DateTime? Stamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.LastWriteTimeUtc : null;
        }
        catch
        {
            return null;
        }
    }
}
