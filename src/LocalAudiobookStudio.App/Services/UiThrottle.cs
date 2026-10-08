using Avalonia.Threading;

namespace LocalAudiobookStudio.App.Services;

/// <summary>
/// Coalesces notifications from background threads into at most one UI-thread callback per
/// interval (about 30 Hz by default), so a flood of worker progress messages can't swamp the UI.
/// </summary>
public sealed class UiThrottle
{
    private readonly Action _action;
    private readonly TimeSpan _interval;
    private int _pending;
    private DateTime _lastRunUtc;

    public UiThrottle(Action action, TimeSpan? interval = null)
    {
        _action = action;
        _interval = interval ?? TimeSpan.FromMilliseconds(33);
    }

    /// <summary>Call from any thread.</summary>
    public void Trigger()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        var wait = _lastRunUtc + _interval - DateTime.UtcNow;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            if (wait <= TimeSpan.Zero) Run();
            else DispatcherTimer.RunOnce(Run, wait, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void Run()
    {
        Interlocked.Exchange(ref _pending, 0);
        _lastRunUtc = DateTime.UtcNow;
        _action();
    }
}
