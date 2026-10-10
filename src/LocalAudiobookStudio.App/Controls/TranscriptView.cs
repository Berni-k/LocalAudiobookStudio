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

    private readonly List<Spot> _spots = new();
    private Spot? _active;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    static TranscriptView()
    {
        SourceTextProperty.Changed.AddClassHandler<TranscriptView>((v, _) => v.Rebuild());
        CheckProperty.Changed.AddClassHandler<TranscriptView>((v, _) => v.Rebuild());
        PositionMsProperty.Changed.AddClassHandler<TranscriptView>((v, _) => v.UpdateHighlight());
    }

    private IBrush ErrorBrush => Resource("SemiColorDanger") ?? Brushes.IndianRed;
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(46, 160, 67), 0.6);

    private IBrush? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out var r) ? r as IBrush : null;

    private void Rebuild()
    {
        _spots.Clear();
        _active = null;
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        var text = SourceText;
        if (string.IsNullOrEmpty(text)) return;
        if (Check is not { } check)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var cursor = 0;
        foreach (var w in check.Words)
        {
            if (w.Kind == WordMatch.Extra)
            {
                Inlines.Add(Marker(w));
                continue;
            }
            if (w.TextStart < cursor || w.TextStart + w.TextLength > text.Length) continue;
            if (w.TextStart > cursor) Inlines.Add(new Run(text[cursor..w.TextStart]));
            var word = text.Substring(w.TextStart, w.TextLength);
            cursor = w.TextStart + w.TextLength;
            Inlines.Add(w.IsError ? ErrorWord(w, word) : PlainWord(w, word));
        }
        if (cursor < text.Length) Inlines.Add(new Run(text[cursor..]));
        UpdateHighlight();
    }

    private Inline PlainWord(WordComparison w, string word)
    {
        var run = new Run(word);
        AddSpot(w, on => run.Background = on ? HighlightBrush : null);
        return run;
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
