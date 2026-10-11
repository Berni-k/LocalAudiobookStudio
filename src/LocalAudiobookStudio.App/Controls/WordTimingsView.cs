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

    static WordBarsView() => AffectsRender<WordBarsView>(WordsProperty, DurationMsProperty, ViewStartMsProperty, ViewEndMsProperty);

    public IReadOnlyList<HeardWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    /// <summary>The waveform's visible range, so the bars follow its zoom.</summary>
    public double ViewStartMs { get => GetValue(ViewStartMsProperty); set => SetValue(ViewStartMsProperty, value); }
    public double ViewEndMs { get => GetValue(ViewEndMsProperty); set => SetValue(ViewEndMsProperty, value); }
    public ICommand? PlayWordCommand { get => GetValue(PlayWordCommandProperty); set => SetValue(PlayWordCommandProperty, value); }

    private static readonly IBrush Bar = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    private static readonly IBrush HoverBar = Brushes.White;
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
            ctx.FillRectangle(ReferenceEquals(word, _hover) ? HoverBar : Bar, BarRect(word), 2);
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
