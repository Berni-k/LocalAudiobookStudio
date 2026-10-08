using LocalAudiobookStudio.Core.Undo;

namespace LocalAudiobookStudio.Tests;

public class UndoStackTests
{
    [Fact]
    public void UndoRedo()
    {
        var value = 0;
        var s = new UndoStack();
        s.Execute(new DelegateCommand("set 1", () => value = 1, () => value = 0));
        s.Execute(new DelegateCommand("set 2", () => value = 2, () => value = 1));
        s.Undo();
        Assert.Equal(1, value);
        s.Undo();
        Assert.Equal(0, value);
        Assert.False(s.CanUndo);
        s.Redo();
        Assert.Equal(1, value);
        s.Execute(new DelegateCommand("set 5", () => value = 5, () => value = 1));
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void MergesConsecutiveEditsWithSameKey()
    {
        var value = 0;
        var s = new UndoStack();
        for (int i = 1; i <= 5; i++)
        {
            var old = value; var nv = i;
            s.Execute(new DelegateCommand("gain", () => value = nv, () => value = old), mergeKey: "gain:x");
        }
        Assert.Equal(5, value);
        s.Undo();
        Assert.Equal(0, value);
        Assert.False(s.CanUndo);
    }
}
