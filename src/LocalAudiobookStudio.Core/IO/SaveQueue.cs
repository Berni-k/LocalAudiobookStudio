namespace LocalAudiobookStudio.Core.IO;

/// <summary>
/// Debounced, serialized background writer. Callers mark a file dirty with a producer that
/// creates its bytes; about 500 ms after the last change the producer runs and the file is
/// written atomically. All writes go through one task, so the disk is never thrashed and
/// two writes to the same file never race.
/// </summary>
public sealed class SaveQueue : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Func<byte[]>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce;
    private readonly Func<Func<byte[]>, Task<byte[]>> _snapshot;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private DateTime _lastChangeUtc;

    /// <param name="debounce">Quiet time before writing.</param>
    /// <param name="snapshotInvoker">
    /// Runs a producer where it is safe to read the model (the app passes a UI-thread invoker).
    /// Defaults to calling the producer on the save thread.
    /// </param>
    public SaveQueue(TimeSpan? debounce = null, Func<Func<byte[]>, Task<byte[]>>? snapshotInvoker = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _snapshot = snapshotInvoker ?? (p => Task.FromResult(p()));
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Raised on the save thread when a write fails.</summary>
    public event Action<string, Exception>? SaveFailed;

    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>Marks a file dirty. The latest producer for a path wins.</summary>
    public void Enqueue(string path, Func<byte[]> producer)
    {
        lock (_lock)
        {
            _pending[path] = producer;
            _lastChangeUtc = DateTime.UtcNow;
        }
        _signal.Release();
    }

    /// <summary>Writes everything pending now, without waiting for the debounce.</summary>
    public async Task FlushAsync()
    {
        List<KeyValuePair<string, Func<byte[]>>> batch;
        lock (_lock)
        {
            batch = _pending.ToList();
            _pending.Clear();
        }
        await WriteBatchAsync(batch).ConfigureAwait(false);
    }

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private async Task WriteBatchAsync(List<KeyValuePair<string, Func<byte[]>>> batch)
    {
        if (batch.Count == 0) return;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (path, producer) in batch)
            {
                try
                {
                    var bytes = await _snapshot(producer).ConfigureAwait(false);
                    AtomicFile.WriteAllBytes(path, bytes);
                }
                catch (Exception ex)
                {
                    SaveFailed?.Invoke(path, ex);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                // Drain extra signals; we only need to know "something changed".
                while (_signal.CurrentCount > 0) await _signal.WaitAsync(ct).ConfigureAwait(false);

                // Wait until the queue has been quiet for the debounce period.
                while (true)
                {
                    TimeSpan wait;
                    lock (_lock) wait = _lastChangeUtc + _debounce - DateTime.UtcNow;
                    if (wait <= TimeSpan.Zero) break;
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        await FlushAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
