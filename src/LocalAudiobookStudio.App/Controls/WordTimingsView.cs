using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LocalAudiobookStudio.Core.Transcripts;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// Where a transcription heard its words, drawn behind a <see cref="WaveformView"/> showing the whole file: a gentle
/// grey over each word, and grey lines at its start and end. For judging how well the words were timed.
/// </summary>
public sealed class WordShadingView : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeardWord>?> WordsProperty =
        AvaloniaProperty.Register<WordShadingView, IReadOnlyList<HeardWord>?>(nameof(Words));
    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<WordShadingView, double>(nameof(DurationMs));
    public static readonly StyledProperty<double> ViewStartMsProperty =
        AvaloniaProperty.Register<WordShadingView, double>(nameof(ViewStartMs));
    public static readonly StyledProperty<double> ViewEndMsProperty =
        AvaloniaProperty.Register<WordShadingView, double>(nameof(ViewEndMs));

    static WordShadingView() =>
        AffectsRender<WordShadingView>(WordsProperty, DurationMsProperty, ViewStartMsProperty, ViewEndMsProperty);

    public WordShadingView() => IsHitTestVisible = false;

    public IReadOnlyList<HeardWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    /// <summary>The waveform's visible range, so the words follow its zoom.</summary>
    public double ViewStartMs { get => GetValue(ViewStartMsProperty); set => SetValue(ViewStartMsProperty, value); }
    public double ViewEndMs { get => GetValue(ViewEndMsProperty); set => SetValue(ViewEndMsProperty, value); }

    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(28, 200, 200, 200));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(110, 170, 170, 170)), 1);

    public override void Render(DrawingContext ctx)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (Words == null || DurationMs <= 0 || w < 2) return;
        foreach (var word in Words)
        {
            var (x0, x1) = (TimeAxis.X(word.StartMs, ViewStartMs, ViewEndMs, DurationMs, w), TimeAxis.X(word.EndMs, ViewStartMs, ViewEndMs, DurationMs, w));
            if (x1 < 0 || x0 > w) continue;
            ctx.FillRectangle(Fill, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
            ctx.DrawLine(Edge, new Point(x0, 0), new Point(x0, h));
            ctx.DrawLine(Edge, new Point(x1, 0), new Point(x1, h));
        }
    }
}

/// <summary>
/// The heard words written just above their bars (<see cref="WordBarsView"/>), each only when it fits within its bar's
/// width; red for a word that doesn't match the text. Takes no clicks: the waveform under it still plays from there.
/// </summary>
public sealed class WordLabelsView : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeardWord>?> WordsProperty =
        AvaloniaProperty.Register<WordLabelsView, IReadOnlyList<HeardWord>?>(nameof(Words));
    public static readonly StyledProperty<IReadOnlySet<HeardWord>?> WrongWordsProperty =
        AvaloniaProperty.Register<WordLabelsView, IReadOnlySet<HeardWord>?>(nameof(WrongWords));
    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<WordLabelsView, double>(nameof(DurationMs));
    public static readonly StyledProperty<double> ViewStartMsProperty =
        AvaloniaProperty.Register<WordLabelsView, double>(nameof(ViewStartMs));
    public static readonly StyledProperty<double> ViewEndMsProperty =
        AvaloniaProperty.Register<WordLabelsView, double>(nameof(ViewEndMs));

    static WordLabelsView() =>
        AffectsRender<WordLabelsView>(WordsProperty, WrongWordsProperty, DurationMsProperty, ViewStartMsProperty, ViewEndMsProperty);

    public WordLabelsView() => IsHitTestVisible = false;

    public IReadOnlyList<HeardWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public IReadOnlySet<HeardWord>? WrongWords { get => GetValue(WrongWordsProperty); set => SetValue(WrongWordsProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }
    public double ViewStartMs { get => GetValue(ViewStartMsProperty); set => SetValue(ViewStartMsProperty, value); }
    public double ViewEndMs { get => GetValue(ViewEndMsProperty); set => SetValue(ViewEndMsProperty, value); }

    private static readonly IBrush Back = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush WrongText = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60));

    public override void Render(DrawingContext ctx)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (Words == null || DurationMs <= 0 || w < 2) return;
        foreach (var word in Words)
        {
            var (x0, x1) = (TimeAxis.X(word.StartMs, ViewStartMs, ViewEndMs, DurationMs, w), TimeAxis.X(word.EndMs, ViewStartMs, ViewEndMs, DurationMs, w));
            if (x1 < 0 || x0 > w) continue;
            var text = new FormattedText(word.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10, WrongWords?.Contains(word) == true ? WrongText : Brushes.White);
            // Only when it fits within the bar (which is a pixel narrower on each side).
            if (text.Width + 4 > x1 - x0 - 2) continue;
            var x = x0 + (x1 - x0 - text.Width) / 2;
            var y = h - text.Height - 1;
            ctx.FillRectangle(Back, new Rect(x - 2, y, text.Width + 4, text.Height), 2);
            ctx.DrawText(text, new Point(x, y));
        }
    }
}

/// <summary>
/// A white bar per heard word along the bottom of a whole-file waveform: hover one to see the word,
/// click it to play just that word (<see cref="PlayWordCommand"/>, with the <see cref="HeardWord"/>).
/// </summary>
public sealed class WordBarsView : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeardWord>?> WordsProperty =
        AvaloniaProperty.Register<WordBarsView, IReadOnlyList<HeardWord>?>(nameof(Words));
    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<WordBarsView, double>(nameof(DurationMs));
    public static readonly StyledProperty<ICommand?> PlayWordCommandProperty =
        AvaloniaProperty.Register<WordBarsView, ICommand?>(nameof(PlayWordCommand));
    public static readonly StyledProperty<double> ViewStartMsProperty =
        AvaloniaProperty.Register<WordBarsView, double>(nameof(ViewStartMs));
    public static readonly StyledProperty<double> ViewEndMsProperty =
        AvaloniaProperty.Register<WordBarsView, double>(nameof(ViewEndMs));
    public static readonly StyledProperty<IReadOnlySet<HeardWord>?> WrongWordsProperty =
        AvaloniaProperty.Register<WordBarsView, IReadOnlySet<HeardWord>?>(nameof(WrongWords));

    static WordBarsView() =>
        AffectsRender<WordBarsView>(WordsProperty, DurationMsProperty, ViewStartMsProperty, ViewEndMsProperty, WrongWordsProperty);

    /// <summary>Words that don't match the text: drawn red, as the transcript marks them.</summary>
    public IReadOnlySet<HeardWord>? WrongWords { get => GetValue(WrongWordsProperty); set => SetValue(WrongWordsProperty, value); }

    public IReadOnlyList<HeardWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    /// <summary>The waveform's visible range, so the bars follow its zoom.</summary>
    public double ViewStartMs { get => GetValue(ViewStartMsProperty); set => SetValue(ViewStartMsProperty, value); }
    public double ViewEndMs { get => GetValue(ViewEndMsProperty); set => SetValue(ViewEndMsProperty, value); }
    public ICommand? PlayWordCommand { get => GetValue(PlayWordCommandProperty); set => SetValue(PlayWordCommandProperty, value); }

    private static readonly IBrush Bar = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    private static readonly IBrush HoverBar = Brushes.White;
    private static readonly IBrush WrongBar = new SolidColorBrush(Color.FromArgb(220, 0xFF, 0x3B, 0x30));
    private static readonly IBrush WrongHoverBar = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60));
    private HeardWord? _hover;

    public WordBarsView() => Cursor = new Cursor(StandardCursorType.Hand);

    private Rect BarRect(HeardWord word)
    {
        var w = Bounds.Width;
        var (x0, x1) = (TimeAxis.X(word.StartMs, ViewStartMs, ViewEndMs, DurationMs, w), TimeAxis.X(word.EndMs, ViewStartMs, ViewEndMs, DurationMs, w));
        // A pixel apart, so neighbouring words stay two bars.
        return new Rect(x0 + 1, 1, Math.Max(2, x1 - x0 - 2), Bounds.Height - 2);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Words == null || DurationMs <= 0 || Bounds.Width < 2) return;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit testing between the bars too
        foreach (var word in Words)
        {
            var wrong = WrongWords?.Contains(word) == true;
            var hover = ReferenceEquals(word, _hover);
            ctx.FillRectangle(wrong ? (hover ? WrongHoverBar : WrongBar) : hover ? HoverBar : Bar, BarRect(word), 2);
        }
    }

    private HeardWord? WordAt(Point p) =>
        Words?.FirstOrDefault(word => BarRect(word).Inflate(new Thickness(1, 4)).Contains(p));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var word = WordAt(e.GetPosition(this));
        if (ReferenceEquals(word, _hover)) return;
        _hover = word;
        ToolTip.SetTip(this, word?.Text);
        ToolTip.SetIsOpen(this, word != null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || WordAt(e.GetPosition(this)) is not { } word) return;
        if (PlayWordCommand?.CanExecute(word) == true) PlayWordCommand.Execute(word);
        e.Handled = true;
    }
}
