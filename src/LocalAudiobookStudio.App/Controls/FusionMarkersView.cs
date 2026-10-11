using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LocalAudiobookStudio.App.Controls;

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

    static FusionMarkersView() => AffectsRender<FusionMarkersView>(MarkersProperty, DurationMsProperty);

    public FusionMarkersView() => IsHitTestVisible = false;

    public IReadOnlyList<FusionMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    private static readonly IBrush SegmentFill = new SolidColorBrush(Color.FromArgb(40, 64, 150, 255));
    private static readonly IBrush CurrentFill = new SolidColorBrush(Color.FromArgb(80, 64, 150, 255));
    private static readonly IBrush ContextFill = new SolidColorBrush(Color.FromArgb(30, 160, 160, 160));
    private static readonly IPen SegmentEdge = new Pen(new SolidColorBrush(Color.FromRgb(64, 150, 255)), 1.5);
    private static readonly IPen ContextEdge = new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Markers == null || DurationMs <= 0 || w < 2) return;
        double X(double ms) => Math.Clamp(ms / DurationMs, 0, 1) * w;
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
            ctx.FillRectangle(LabelBack, new Rect(x0 + 1, m.IsSegment ? 1 : h - text.Height - 3, text.Width + 6, text.Height + 2), 2);
            ctx.DrawText(text, new Point(x0 + 4, m.IsSegment ? 2 : h - text.Height - 2));
        }
    }
}
