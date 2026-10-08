using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.App.Controls;

public enum SegmentState
{
    Ready,
    Stale,
    Missing,
}

/// <summary>One segment on the scrub bar: its time span, generation state and speaker color.</summary>
public readonly record struct TimelineSegment(double StartMs, double EndMs, SegmentState State, Color? SpeakerColor);

/// <summary>
/// Scrub bar over a time range of the book (a chapter, or the whole book) with tick marks at
/// <see cref="Marks"/> (segment or chapter starts). Times are absolute book milliseconds;
/// dragging raises <see cref="SeekRequested"/>.
/// </summary>
public sealed class TimelineBar : Control
{
    public static readonly StyledProperty<double> RangeStartMsProperty =
        AvaloniaProperty.Register<TimelineBar, double>(nameof(RangeStartMs));
    public static readonly StyledProperty<double> RangeEndMsProperty =
        AvaloniaProperty.Register<TimelineBar, double>(nameof(RangeEndMs));
    public static readonly StyledProperty<double> PositionMsProperty =
        AvaloniaProperty.Register<TimelineBar, double>(nameof(PositionMs));
    public static readonly StyledProperty<double[]?> MarksProperty =
        AvaloniaProperty.Register<TimelineBar, double[]?>(nameof(Marks));
    public static readonly StyledProperty<IReadOnlyList<string>?> MarkTitlesProperty =
        AvaloniaProperty.Register<TimelineBar, IReadOnlyList<string>?>(nameof(MarkTitles));
    public static readonly StyledProperty<TimelineSegment[]?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineBar, TimelineSegment[]?>(nameof(Segments));
    public static readonly StyledProperty<bool> ShowSpeakersProperty =
        AvaloniaProperty.Register<TimelineBar, bool>(nameof(ShowSpeakers), true);

    static TimelineBar()
    {
        AffectsRender<TimelineBar>(RangeStartMsProperty, RangeEndMsProperty, PositionMsProperty, MarksProperty, SegmentsProperty, ShowSpeakersProperty);
    }

    public double RangeStartMs { get => GetValue(RangeStartMsProperty); set => SetValue(RangeStartMsProperty, value); }
    public double RangeEndMs { get => GetValue(RangeEndMsProperty); set => SetValue(RangeEndMsProperty, value); }
    public double PositionMs { get => GetValue(PositionMsProperty); set => SetValue(PositionMsProperty, value); }

    /// <summary>Absolute start times of the sections inside the range (sorted).</summary>
    public double[]? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }

    /// <summary>Optional title per mark, shown in the hover tooltip.</summary>
    public IReadOnlyList<string>? MarkTitles { get => GetValue(MarkTitlesProperty); set => SetValue(MarkTitlesProperty, value); }

    /// <summary>
    /// Segments drawn on the bar: the line is white when generated, yellow when stale and red when
    /// missing, with a band in the speaker's color underneath.
    /// </summary>
    public TimelineSegment[]? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    /// <summary>Draw the speaker color bands (off in book mode, where they'd be too dense).</summary>
    public bool ShowSpeakers { get => GetValue(ShowSpeakersProperty); set => SetValue(ShowSpeakersProperty, value); }

    // Brushes are created once: Render runs on every scrub frame.
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#3A3D42")).ToImmutable();
    private static readonly IBrush ReadyBrush = new SolidColorBrush(Color.Parse("#FFFFFF")).ToImmutable();
    private static readonly IBrush StaleBrush = new SolidColorBrush(Color.Parse("#FFD60A")).ToImmutable();
    private static readonly IBrush MissingBrush = new SolidColorBrush(Color.Parse("#FF3B30")).ToImmutable();
    private static readonly IBrush DenseTickBrush = new SolidColorBrush(Color.FromArgb(70, 0x9A, 0xA0, 0xA6)).ToImmutable();
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromArgb(160, 0x9A, 0xA0, 0xA6)).ToImmutable();


    /// <summary>Raised while dragging (and on click) with the target book time in ms.</summary>
    public event Action<double>? SeekRequested;

    private bool _dragging;
    private double _dragMs = -1;

    public TimelineBar()
    {
        Height = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private double Span => Math.Max(1, RangeEndMs - RangeStartMs);
    private double MsToX(double ms) => Math.Clamp((ms - RangeStartMs) / Span, 0, 1) * Bounds.Width;
    private double XToMs(double x) => RangeStartMs + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * Span;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        // A thin line: white where segments are fine, bright yellow (stale) or bright red
        // (not generated / failed). Right under it, each speaker's color fades down into the background.
        const double lineH = 2;
        var trackY = Math.Round(h * 0.3);
        ctx.FillRectangle(TrackBrush, new Rect(0, trackY, w, lineH));

        var pos = _dragging && _dragMs >= 0 ? _dragMs : PositionMs;
        var inRange = pos >= RangeStartMs - 1 && pos <= RangeEndMs + 1;
        var x = MsToX(pos);

        if (Segments is { Length: > 0 } segments)
        {
            var bandY = trackY + lineH;
            var bandH = h - bandY;
            foreach (var seg in segments)
            {
                if (seg.EndMs <= RangeStartMs || seg.StartMs >= RangeEndMs) continue;
                var x0 = MsToX(seg.StartMs);
                var x1 = Math.Max(x0 + 1, MsToX(seg.EndMs));
                var brush = seg.State switch
                {
                    SegmentState.Stale => StaleBrush,
                    SegmentState.Missing => MissingBrush,
                    _ => ReadyBrush,
                };
                ctx.FillRectangle(brush, new Rect(x0, trackY, x1 - x0, lineH));
                if (ShowSpeakers && seg.SpeakerColor is { } sc)
                    ctx.FillRectangle(FadeBrush(sc), new Rect(x0, bandY, x1 - x0, bandH));
            }
        }
        else
        {
            ctx.FillRectangle(ReadyBrush, new Rect(0, trackY, w, lineH));
        }

        if (Marks is { Length: > 1 } marks)
        {
            // Dense marks (many segments) are drawn fainter so the bar doesn't turn solid.
            var dense = marks.Length > w / 4;
            var tick = dense ? DenseTickBrush : TickBrush;
            var lastX = double.NegativeInfinity;
            // With the speaker band shown, ticks run down through it to the bottom so they divide the colors too.
            var tickBottom = ShowSpeakers && Segments is { Length: > 0 } ? h : trackY + lineH + 5;
            foreach (var m in marks)
            {
                if (m <= RangeStartMs || m >= RangeEndMs) continue;
                var tx = MsToX(m);
                if (tx - lastX < 2) continue; // never draw two ticks on the same pixel
                lastX = tx;
                ctx.FillRectangle(tick, new Rect(tx - 0.5, trackY - 5, 1, tickBottom - (trackY - 5)));
            }
        }
        // Play marker: a thick white vertical line across the whole bar.
        if (inRange) ctx.FillRectangle(Brushes.White, new Rect(Math.Round(x) - 1.5, 0, 3, h));
    }

    private readonly Dictionary<Color, IBrush> _fadeBrushes = new();

    /// <summary>Speaker color at the top fading to transparent at the bottom (cached per color).</summary>
    private IBrush FadeBrush(Color c)
    {
        if (_fadeBrushes.TryGetValue(c, out var b)) return b;
        b = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(170, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        _fadeBrushes[c] = b;
        return b;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragMs = XToMs(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        SeekRequested?.Invoke(_dragMs);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var ms = XToMs(e.GetPosition(this).X);
        if (_dragging)
        {
            _dragMs = ms;
            SeekRequested?.Invoke(ms);
            InvalidateVisual();
        }
        var tip = TimeFormat.Hms(ms - RangeStartMs);
        if (Marks is { Length: > 0 } marks && MarkTitles is { } titles)
        {
            var idx = Array.BinarySearch(marks, ms);
            if (idx < 0) idx = ~idx - 1;
            if (idx >= 0 && idx < titles.Count) tip += "  ·  " + titles[idx];
        }
        ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        _dragMs = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }
}
