using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>Where a time is on overlays drawn over a <see cref="WaveformView"/>, which may be zoomed.</summary>
internal static class TimeAxis
{
    /// <summary>
    /// The x of <paramref name="ms"/> in a control <paramref name="width"/> wide showing <paramref name="from"/>–
    /// <paramref name="to"/>; before the waveform has published its range (<paramref name="to"/> not past
    /// <paramref name="from"/>), the whole file.
    /// </summary>
    public static double X(double ms, double from, double to, double durationMs, double width)
    {
        if (to <= from) (from, to) = (0, durationMs);
        return (ms - from) / Math.Max(1e-9, to - from) * width;
    }
}

/// <summary>
/// One stretch of a fused generation's audio: a segment's take as it was cut out, or where the worker said a context
/// line is. <see cref="IsCurrent"/>: the take the window was opened from.
/// </summary>
public sealed record FusionMarker(double StartMs, double EndMs, string Label, bool IsSegment, bool IsCurrent);

/// <summary>
/// Drawn over a <see cref="WaveformView"/> showing a whole file: a band per marker with its edges and label. Takes no
/// clicks, so the waveform under it still plays from where it is clicked.
/// </summary>
public sealed class FusionMarkersView : Control
{
    public static readonly StyledProperty<IReadOnlyList<FusionMarker>?> MarkersProperty =
        AvaloniaProperty.Register<FusionMarkersView, IReadOnlyList<FusionMarker>?>(nameof(Markers));
    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<FusionMarkersView, double>(nameof(DurationMs));
    public static readonly StyledProperty<double> ViewStartMsProperty =
        AvaloniaProperty.Register<FusionMarkersView, double>(nameof(ViewStartMs));
    public static readonly StyledProperty<double> ViewEndMsProperty =
        AvaloniaProperty.Register<FusionMarkersView, double>(nameof(ViewEndMs));

    static FusionMarkersView() =>
        AffectsRender<FusionMarkersView>(MarkersProperty, DurationMsProperty, ViewStartMsProperty, ViewEndMsProperty);

    public FusionMarkersView() => IsHitTestVisible = false;

    public IReadOnlyList<FusionMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    /// <summary>The waveform's visible range (<see cref="WaveformView.ViewStartMs"/>), so the bands follow its zoom.</summary>
    public double ViewStartMs { get => GetValue(ViewStartMsProperty); set => SetValue(ViewStartMsProperty, value); }
    public double ViewEndMs { get => GetValue(ViewEndMsProperty); set => SetValue(ViewEndMsProperty, value); }

    private static readonly IBrush SegmentFill = new SolidColorBrush(Color.FromArgb(40, 64, 150, 255));
    private static readonly IBrush CurrentFill = new SolidColorBrush(Color.FromArgb(80, 64, 150, 255));
    private static readonly IBrush ContextFill = new SolidColorBrush(Color.FromArgb(30, 160, 160, 160));
    private static readonly IPen SegmentEdge = new Pen(new SolidColorBrush(Color.FromRgb(64, 150, 255)), 1.5);
    private static readonly IPen ContextEdge = new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));

    /// <summary>The height of the word bars along the bottom of the inspector's waveform.</summary>
    private const double WordBarsHeight = 12;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Markers == null || DurationMs <= 0 || w < 2) return;
        double X(double ms) => Math.Clamp(TimeAxis.X(ms, ViewStartMs, ViewEndMs, DurationMs, w), -1, w + 1);
        // Context first, so the segments' edges are on top.
        foreach (var m in Markers.OrderBy(m => m.IsSegment))
        {
            var (x0, x1) = (X(m.StartMs), X(m.EndMs));
            ctx.FillRectangle(m.IsCurrent ? CurrentFill : m.IsSegment ? SegmentFill : ContextFill, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
            var edge = m.IsSegment ? SegmentEdge : ContextEdge;
            ctx.DrawLine(edge, new Point(x0, 0), new Point(x0, h));
            ctx.DrawLine(edge, new Point(x1, 0), new Point(x1, h));
            var text = new FormattedText(m.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White)
            {
                MaxTextWidth = Math.Max(1, x1 - x0 - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            if (x1 - x0 < 16) continue;
            // Context labels sit at the bottom, above the strip of word bars (WordBarsView).
            var y = m.IsSegment ? 1 : h - text.Height - 3 - WordBarsHeight;
            ctx.FillRectangle(LabelBack, new Rect(x0 + 1, y, text.Width + 6, text.Height + 2), 2);
            ctx.DrawText(text, new Point(x0 + 4, y + 1));
        }
    }
}
