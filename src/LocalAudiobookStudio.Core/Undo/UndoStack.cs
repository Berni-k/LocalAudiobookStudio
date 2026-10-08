namespace LocalAudiobookStudio.Core.Undo;

/// <summary>An edit that can be applied and reverted.</summary>
public interface IUndoableCommand
{
    string Description { get; }
    void Do();
    void Undo();
}

/// <summary>A command built from two delegates.</summary>
public sealed class DelegateCommand(string description, Action doAction, Action undoAction) : IUndoableCommand
{
    public string Description { get; } = description;
    public void Do() => doAction();
    public void Undo() => undoAction();
}

/// <summary>Several commands that undo and redo as one step.</summary>
public sealed class CompositeCommand(string description, IReadOnlyList<IUndoableCommand> commands) : IUndoableCommand
{
    public string Description { get; } = description;
    public IReadOnlyList<IUndoableCommand> Commands { get; } = commands;

    public void Do()
    {
        foreach (var c in Commands) c.Do();
    }

    public void Undo()
    {
        for (int i = Commands.Count - 1; i >= 0; i--) Commands[i].Undo();
    }
}

/// <summary>
/// Per-session undo/redo history. Every project edit goes through <see cref="Execute"/>.
/// Consecutive edits with the same merge key within a short window collapse into one step
/// (for example dragging a gain slider).
/// </summary>
public sealed class UndoStack
{
    private readonly List<Entry> _undo = new();
    private readonly List<IUndoableCommand> _redo = new();
    private readonly int _limit;

    private sealed record Entry(IUndoableCommand Command, string? MergeKey, DateTime TimeUtc);

    public UndoStack(int limit = 500) => _limit = limit;

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Command.Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    public static TimeSpan MergeWindow { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Applies the command and records it.</summary>
    public void Execute(IUndoableCommand command, string? mergeKey = null)
    {
        command.Do();
        Push(command, mergeKey);
    }

    /// <summary>Records a command whose effect has already been applied.</summary>
    public void Push(IUndoableCommand command, string? mergeKey = null)
    {
        var now = DateTime.UtcNow;
        if (mergeKey != null && _undo.Count > 0)
        {
            var top = _undo[^1];
            if (top.MergeKey == mergeKey && now - top.TimeUtc < MergeWindow)
            {
                // Keep the oldest "undo" and the newest "do".
                var merged = new DelegateCommand(command.Description, command.Do, top.Command.Undo);
                _undo[^1] = new Entry(merged, mergeKey, now);
                _redo.Clear();
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        _undo.Add(new Entry(command, mergeKey, now));
        if (_undo.Count > _limit) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        e.Command.Undo();
        _redo.Add(e.Command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var c = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        c.Do();
        _undo.Add(new Entry(c, null, DateTime.UtcNow));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
