using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LocalAudiobookStudio.Core.Transcripts;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// The text a take should contain, annotated with what a transcription worker heard: the word being played is
/// highlighted (from the heard word's timing), and words that didn't verify are marked in red with the reason as a
/// tooltip. A word that was heard but isn't in the text gets a small marker where it was heard.
/// </summary>
public sealed class TranscriptView : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty = AvaloniaProperty.Register<TranscriptView, string?>(nameof(SourceText));
    public static readonly StyledProperty<TranscriptCheck?> CheckProperty = AvaloniaProperty.Register<TranscriptView, TranscriptCheck?>(nameof(Check));

    /// <summary>The playback position in ms; negative when nothing plays.</summary>
    public static readonly StyledProperty<double> PositionMsProperty = AvaloniaProperty.Register<TranscriptView, double>(nameof(PositionMs), -1);

    /// <summary>The text the check was made against (not the text box, which may have changed since).</summary>
    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public TranscriptCheck? Check { get => GetValue(CheckProperty); set => SetValue(CheckProperty, value); }
    public double PositionMs { get => GetValue(PositionMsProperty); set => SetValue(PositionMsProperty, value); }

    /// <summary>A word that can be highlighted: its time and how to switch the highlight.</summary>
    private sealed record Spot(int StartMs, int EndMs, Action<bool> Highlight);

    /// <summary>Where a piece of the shown text came from in <see cref="SourceText"/> (for turning a click into a caret position).</summary>
    private readonly record struct Piece(int ShownStart, int ShownLength, int SourceStart);

    private readonly List<Spot> _spots = new();
    private readonly List<Piece> _pieces = new();
    private Spot? _active;
    private int _shown;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    static TranscriptView()
    {
        SourceTextProperty.Changed.AddClassHandler<TranscriptView>((v, _) => v.Rebuild());
        CheckProperty.Changed.AddClassHandler<TranscriptView>((v, _) => v.Rebuild());
        PositionMsProperty.Changed.AddClassHandler<TranscriptView>((v, _) => v.UpdateHighlight());
    }

    // Fixed colors: bright enough to stand out on both themes.
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(255, 59, 48));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(46, 160, 67), 0.6);

    private void Rebuild()
    {
        _spots.Clear();
        _pieces.Clear();
        _active = null;
        _shown = 0;
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        var text = SourceText;
        if (string.IsNullOrEmpty(text)) return;
        if (Check is not { } check)
        {
            AddRun(text, 0);
            return;
        }

        var cursor = 0;
        foreach (var w in check.Words)
        {
            if (w.Kind == WordMatch.Extra)
            {
                AddObject(Marker(w), cursor);
                continue;
            }
            if (w.TextStart < cursor || w.TextStart + w.TextLength > text.Length) continue;
            if (w.TextStart > cursor) AddRun(text[cursor..w.TextStart], cursor);
            var word = text.Substring(w.TextStart, w.TextLength);
            if (w.IsError) AddObject(ErrorWord(w, word), w.TextStart);
            else AddRun(word, w.TextStart, w);
            cursor = w.TextStart + w.TextLength;
        }
        if (cursor < text.Length) AddRun(text[cursor..], cursor);
        UpdateHighlight();
    }

    private void AddRun(string text, int sourceStart, WordComparison? word = null)
    {
        var run = new Run(text);
        if (word != null) AddSpot(word, on => run.Background = on ? HighlightBrush : null);
        Inlines!.Add(run);
        _pieces.Add(new Piece(_shown, text.Length, sourceStart));
        _shown += text.Length;
    }

    /// <summary>An element that takes the place of one character in the shown text.</summary>
    private void AddObject(Inline inline, int sourceStart)
    {
        Inlines!.Add(inline);
        _pieces.Add(new Piece(_shown, 1, sourceStart));
        _shown++;
    }

    /// <summary>The index in <see cref="SourceText"/> that a point in this control is over (for placing the caret when editing).</summary>
    public int SourceIndexAt(Point point)
    {
        var text = SourceText ?? "";
        if (_pieces.Count == 0) return text.Length;
        var hit = TextLayout.HitTestPoint(new Point(point.X - Padding.Left, point.Y - Padding.Top));
        var pos = hit.TextPosition;
        foreach (var p in _pieces)
        {
            if (pos < p.ShownStart || pos > p.ShownStart + p.ShownLength) continue;
            var offset = p.ShownLength == 1 ? 0 : pos - p.ShownStart;
            return Math.Clamp(p.SourceStart + offset, 0, text.Length);
        }
        return text.Length;
    }

    /// <summary>A word that didn't verify: red and underlined, with what went wrong on hover.</summary>
    private Inline ErrorWord(WordComparison w, string word)
    {
        var label = new TextBlock { Text = word, Foreground = ErrorBrush, TextDecorations = Avalonia.Media.TextDecorations.Underline };
        var box = new Border { Child = label, CornerRadius = new CornerRadius(2), Padding = new Thickness(0), Background = Brushes.Transparent };
        ToolTip.SetTip(box, w.Kind == WordMatch.Missing
            ? $"Not heard: “{w.Expected}”"
            : $"Heard “{w.Heard}” instead of “{w.Expected}”");
        AddSpot(w, on => box.Background = on ? HighlightBrush : Brushes.Transparent);
        return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    /// <summary>Where a word was heard that the text doesn't have.</summary>
    private Inline Marker(WordComparison w)
    {
        var label = new TextBlock { Text = "▾", Foreground = ErrorBrush, FontSize = FontSize * 0.9 };
        var box = new Border { Child = label, Background = Brushes.Transparent };
        ToolTip.SetTip(box, $"Heard “{w.Heard}”, which isn't in the text");
        return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    private void AddSpot(WordComparison w, Action<bool> highlight)
    {
        // A word without a duration (a missing one) can't be playing.
        if (w.EndMs > w.StartMs) _spots.Add(new Spot(w.StartMs, w.EndMs, highlight));
    }

    private void UpdateHighlight()
    {
        var pos = PositionMs;
        var now = pos < 0 ? null : _spots.FirstOrDefault(s => pos >= s.StartMs && pos < s.EndMs);
        if (ReferenceEquals(now, _active)) return;
        _active?.Highlight(false);
        now?.Highlight(true);
        _active = now;
    }
}
