using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>What a waveform reports back to its item (implemented by ItemViewModel).</summary>
public interface IWaveformHost
{
    /// <summary>Click: play from this point (ms from the trimmed start).</summary>
    void WaveformSeek(double relativeMs);

    /// <summary>Drag: audible scrubbing grain at this point.</summary>
    void WaveformScrub(double relativeMs);

    void WaveformScrubEnd();

    /// <summary>Trim handle released: new trims in ms.</summary>
    void WaveformTrim(double trimStartMs, double trimEndMs);
}

/// <summary>
/// Draws precomputed peaks with DrawingContext (never decodes audio). In a row it shows the trimmed
/// region; with <see cref="ShowFullRange"/> (inspector) it shows the whole file with trimmed parts
/// dimmed, trim lines that can be dragged, and Ctrl+wheel zoom. Click plays from a point and drag
/// scrubs. Rows can't trim: their handles were too easy to bump while clicking to play.
/// </summary>
public sealed class WaveformView : Control
{
    // Avalonia "styled properties" are like WPF dependency properties: bindable, and AffectsRender
    // below makes the control redraw when they change.
    public static readonly StyledProperty<Peaks?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, Peaks?>(nameof(Peaks));
    public static readonly StyledProperty<double> TrimStartMsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(TrimStartMs));
    public static readonly StyledProperty<double> TrimEndMsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(TrimEndMs));
    public static readonly StyledProperty<double> GainDbProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(GainDb));
    public static readonly StyledProperty<double> PlayheadMsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(PlayheadMs), -1);
    public static readonly StyledProperty<bool> ShowFullRangeProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowFullRange));
    public static readonly StyledProperty<bool> InteractiveProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(Interactive), true);
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IWaveformHost?> HostProperty =
        AvaloniaProperty.Register<WaveformView, IWaveformHost?>(nameof(Host));
    public static readonly StyledProperty<double> PlaceholderDurationMsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(PlaceholderDurationMs));

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, TrimStartMsProperty, TrimEndMsProperty, GainDbProperty,
            PlayheadMsProperty, ShowFullRangeProperty, FillProperty, PlaceholderDurationMsProperty);
        FocusableProperty.OverrideDefaultValue<WaveformView>(false);
    }

    public Peaks? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double TrimStartMs { get => GetValue(TrimStartMsProperty); set => SetValue(TrimStartMsProperty, value); }
    public double TrimEndMs { get => GetValue(TrimEndMsProperty); set => SetValue(TrimEndMsProperty, value); }
    public double GainDb { get => GetValue(GainDbProperty); set => SetValue(GainDbProperty, value); }
    public double PlayheadMs { get => GetValue(PlayheadMsProperty); set => SetValue(PlayheadMsProperty, value); }
    public bool ShowFullRange { get => GetValue(ShowFullRangeProperty); set => SetValue(ShowFullRangeProperty, value); }
    public bool Interactive { get => GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IWaveformHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }

    /// <summary>
    /// Length used when there are no peaks (audio not generated yet, estimated length): the playhead
    /// still moves across, and a click plays from that point (placeholder static).
    /// </summary>
    public double PlaceholderDurationMs { get => GetValue(PlaceholderDurationMsProperty); set => SetValue(PlaceholderDurationMsProperty, value); }

    private bool IsPlaceholder => Peaks == null && PlaceholderDurationMs > 0;
    private bool _placeholderPress;

    private const double HandleZone = 7;
    private static readonly IBrush PlayheadEdgeBrush = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));

    private enum DragMode { None, Pending, Scrub, TrimStart, TrimEnd }

    private DragMode _drag;
    private Point _pressPoint;
    private double _previewTrimStart = double.NaN;
    private double _previewTrimEnd = double.NaN;
    private bool _hover;
    private double _hoverX = -1;
    private double _zoom = 1;      // inspector only: 1 = whole file
    private double _scrollMs;      // inspector only: left edge when zoomed

    private float[] _min = [];
    private float[] _max = [];

    private double DurationMs => Peaks?.DurationMs ?? 0;
    private double EffTrimStart => double.IsNaN(_previewTrimStart) ? TrimStartMs : _previewTrimStart;
    private double EffTrimEnd => double.IsNaN(_previewTrimEnd) ? TrimEndMs : _previewTrimEnd;

    /// <summary>While dragging a trim handle, a row shows the whole file so the handle can follow the mouse.</summary>
    private bool FullView => ShowFullRange || _drag is DragMode.TrimStart or DragMode.TrimEnd;

    /// <summary>The file time range currently mapped to the control's width.</summary>
    private (double from, double to) ViewRange()
    {
        var d = DurationMs;
        if (d <= 0) return (0, 1);
        if (!FullView)
        {
            var from = Math.Clamp(EffTrimStart, 0, d);
            var to = Math.Clamp(d - EffTrimEnd, from + 1, d);
            return (from, to);
        }
        if (!ShowFullRange || _zoom <= 1) return (0, d);
        var span = d / _zoom;
        _scrollMs = Math.Clamp(_scrollMs, 0, d - span);
        return (_scrollMs, _scrollMs + span);
    }

    private double XToMs(double x)
    {
        var (from, to) = ViewRange();
        return from + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * (to - from);
    }

    private double MsToX(double ms)
    {
        var (from, to) = ViewRange();
        return (ms - from) / Math.Max(1e-9, to - from) * Bounds.Width;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < 2) return;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit testing

        var peaks = Peaks;
        if (peaks == null || peaks.BinCount == 0)
        {
            if (IsPlaceholder)
            {
                if (_hover && _hoverX >= 0 && Interactive)
                    ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), new Rect(_hoverX, 0, 1, h));
                if (PlayheadMs >= 0) DrawPlayhead(ctx, PlayheadMs / PlaceholderDurationMs * w, w, h);
            }
            return;
        }

        var cols = (int)Math.Ceiling(w);
        if (_min.Length != cols) { _min = new float[cols]; _max = new float[cols]; }
        var (from, to) = ViewRange();
        peaks.GetColumns(from, to, _min, _max);

        var scale = (float)Math.Clamp(Math.Pow(10, GainDb / 20.0), 0.05, 8.0);
        var mid = h / 2;
        var fill = Fill ?? new SolidColorBrush(Color.Parse("#7FA7C9"));
        var dim = new SolidColorBrush(Color.FromArgb(70, 127, 167, 201));
        var trimFrom = EffTrimStart;
        var trimTo = DurationMs - EffTrimEnd;

        // One vertical line per pixel column, drawn as rectangles (fast and crisp).
        for (int x = 0; x < cols; x++)
        {
            var lo = Math.Max(-1f, _min[x] * scale);
            var hi = Math.Min(1f, _max[x] * scale);
            var y1 = mid - hi * (mid - 1);
            var y2 = mid - lo * (mid - 1);
            if (y2 - y1 < 1) { y1 = mid - 0.5; y2 = mid + 0.5; }
            var t = from + (x + 0.5) / w * (to - from);
            var brush = FullView && (t < trimFrom || t > trimTo) ? dim : fill;
            ctx.FillRectangle(brush, new Rect(x, y1, 1, y2 - y1));
        }

        var handlePen = new Pen(new SolidColorBrush(Color.Parse("#DADCE0")), 2);

        // Trim markers in full view.
        if (FullView)
        {
            var shade = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            var xs = MsToX(trimFrom);
            var xe = MsToX(trimTo);
            if (xs > 0) ctx.FillRectangle(shade, new Rect(0, 0, Math.Min(w, xs), h));
            if (xe < w) ctx.FillRectangle(shade, new Rect(Math.Max(0, xe), 0, w - Math.Max(0, xe), h));
            ctx.DrawLine(handlePen, new Point(xs, 0), new Point(xs, h));
            ctx.DrawLine(handlePen, new Point(xe, 0), new Point(xe, h));
        }

        // Hover cursor line.
        if (_hover && _hoverX >= 0 && _drag == DragMode.None)
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), new Rect(_hoverX, 0, 1, h));

        // Playhead (relative to the trimmed start).
        if (PlayheadMs >= 0) DrawPlayhead(ctx, MsToX(trimFrom + PlayheadMs), w, h);
    }

    private static void DrawPlayhead(DrawingContext ctx, double x, double w, double h)
    {
        if (x < 0 || x > w) return;
        // Bold white line; the faint dark edge keeps it visible on light backgrounds too.
        ctx.FillRectangle(PlayheadEdgeBrush, new Rect(x - 2.5, 0, 5, h));
        ctx.FillRectangle(Brushes.White, new Rect(x - 1.5, 0, 3, h));
    }

    private double PlaceholderMsAt(double x) => Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * PlaceholderDurationMs;

    // ------------------------------------------------------------------ pointer

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        _hoverX = -1;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Interactive || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsPlaceholder)
        {
            // No audio to scrub or trim: a click just plays from that point.
            _placeholderPress = true;
            e.Handled = true;
            return;
        }
        if (Peaks == null) return;
        var props = e.GetCurrentPoint(this).Properties;
        var p = e.GetPosition(this);
        _pressPoint = p;

        if (IsOverStartHandle(p.X)) _drag = DragMode.TrimStart;
        else if (IsOverEndHandle(p.X)) _drag = DragMode.TrimEnd;
        else _drag = DragMode.Pending;

        if (_drag is DragMode.TrimStart or DragMode.TrimEnd)
        {
            _previewTrimStart = TrimStartMs;
            _previewTrimEnd = TrimEndMs;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    // Trimming only in the full view (inspector).
    private bool IsOverStartHandle(double x) => ShowFullRange && Math.Abs(x - MsToX(EffTrimStart)) <= HandleZone;
    private bool IsOverEndHandle(double x) => ShowFullRange && Math.Abs(x - MsToX(DurationMs - EffTrimEnd)) <= HandleZone;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _hoverX = p.X;
        if (IsPlaceholder && Interactive)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "~" + TimeFormat.Short(PlaceholderMsAt(p.X)) + " (not generated; plays placeholder static)");
            InvalidateVisual();
            return;
        }
        if (Peaks == null) return;

        switch (_drag)
        {
            case DragMode.None:
                if (Interactive)
                {
                    Cursor = IsOverStartHandle(p.X) || IsOverEndHandle(p.X) ? new Cursor(StandardCursorType.SizeWestEast) : new Cursor(StandardCursorType.Hand);
                    var rel = XToMs(p.X) - TrimStartMs;
                    ToolTip.SetTip(this, TimeFormat.Short(Math.Max(0, rel)));
                }
                break;
            case DragMode.Pending:
                if (Math.Abs(p.X - _pressPoint.X) > 3)
                {
                    _drag = DragMode.Scrub;
                    Host?.WaveformScrub(XToMs(p.X) - TrimStartMs);
                }
                break;
            case DragMode.Scrub:
                Host?.WaveformScrub(XToMs(p.X) - TrimStartMs);
                break;
            case DragMode.TrimStart:
            {
                var ms = Math.Clamp(XToMs(p.X), 0, DurationMs - EffTrimEnd - 20);
                _previewTrimStart = Math.Round(ms);
                ToolTip.SetTip(this, $"Trim start: {_previewTrimStart.ToString("0", CultureInfo.InvariantCulture)} ms");
                ToolTip.SetIsOpen(this, true);
                break;
            }
            case DragMode.TrimEnd:
            {
                var endMs = Math.Clamp(XToMs(p.X), EffTrimStart + 20, DurationMs);
                _previewTrimEnd = Math.Round(DurationMs - endMs);
                ToolTip.SetTip(this, $"Trim end: {_previewTrimEnd.ToString("0", CultureInfo.InvariantCulture)} ms");
                ToolTip.SetIsOpen(this, true);
                break;
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (_placeholderPress)
        {
            _placeholderPress = false;
            if (IsPlaceholder) Host?.WaveformSeek(PlaceholderMsAt(p.X));
            e.Handled = true;
            return;
        }
        var mode = _drag;
        // Read the trim preview before releasing capture: that raises OnPointerCaptureLost, which clears it.
        var s = _previewTrimStart;
        var en = _previewTrimEnd;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        switch (mode)
        {
            case DragMode.Pending:
                Host?.WaveformSeek(Math.Max(0, XToMs(p.X) - TrimStartMs));
                e.Handled = true;
                break;
            case DragMode.Scrub:
                Host?.WaveformScrubEnd();
                e.Handled = true;
                break;
            case DragMode.TrimStart:
            case DragMode.TrimEnd:
                _previewTrimStart = _previewTrimEnd = double.NaN;
                ToolTip.SetIsOpen(this, false);
                if (Math.Abs(s - TrimStartMs) > 0.5 || Math.Abs(en - TrimEndMs) > 0.5) Host?.WaveformTrim(s, en);
                e.Handled = true;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == DragMode.Scrub) Host?.WaveformScrubEnd();
        _drag = DragMode.None;
        _previewTrimStart = _previewTrimEnd = double.NaN;
        InvalidateVisual();
    }

    /// <summary>Ctrl+wheel zooms horizontally (inspector view only); plain wheel scrolls when zoomed.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!ShowFullRange || Peaks == null) { base.OnPointerWheelChanged(e); return; }
        var d = DurationMs;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var anchor = XToMs(e.GetPosition(this).X);
            var old = _zoom;
            _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 1, 200);
            // Keep the time under the mouse in place.
            var frac = e.GetPosition(this).X / Math.Max(1, Bounds.Width);
            _scrollMs = anchor - frac * (d / _zoom);
            if (old != _zoom) InvalidateVisual();
            e.Handled = true;
        }
        else if (_zoom > 1)
        {
            _scrollMs -= e.Delta.Y * (d / _zoom) * 0.1;
            InvalidateVisual();
            e.Handled = true;
        }
        else
        {
            base.OnPointerWheelChanged(e);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PeaksProperty)
        {
            _zoom = 1;
            _scrollMs = 0;
        }
    }
}
