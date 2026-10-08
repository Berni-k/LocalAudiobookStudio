using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// A small HSV color picker: a saturation/brightness square, a hue bar and a swatch.
/// <see cref="Color"/> ("#RRGGBB") is only written when a drag ends, so listeners aren't flooded
/// while dragging; the swatch previews the color live.
/// </summary>
public sealed class ColorPickerPanel : UserControl
{
    public static readonly StyledProperty<string?> ColorProperty =
        AvaloniaProperty.Register<ColorPickerPanel, string?>(nameof(Color), defaultBindingMode: BindingMode.TwoWay);

    public string? Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    private readonly SaturationValueArea _area = new() { Height = 150 };
    private readonly HueBar _hue = new() { Height = 14 };
    private readonly Border _swatch = new() { Height = 20, CornerRadius = new CornerRadius(4) };
    private double _h, _s, _v;
    private bool _syncing;

    public ColorPickerPanel()
    {
        Width = 240;
        Content = new StackPanel { Spacing = 10, Children = { _area, _hue, _swatch } };

        _area.Changed += (s, v, done) => { _s = s; _v = v; Preview(done); };
        _hue.Changed += (h, done) => { _h = h; _area.Hue = h; Preview(done); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty && !_syncing) LoadFrom(Color);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadFrom(Color);
    }

    private void LoadFrom(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !Avalonia.Media.Color.TryParse(hex, out var c)) c = Colors.Gray;
        var hsv = c.ToHsv();
        // Keep the hue for greys (where it is undefined) so the square doesn't jump to red.
        if (hsv.S > 0.001) _h = hsv.H;
        _s = hsv.S;
        _v = hsv.V;
        _area.Hue = _h;
        _area.Saturation = _s;
        _area.Value = _v;
        _hue.Hue = _h;
        Preview(commit: false);
    }

    private void Preview(bool commit)
    {
        var c = HsvColor.ToRgb(_h, _s, _v);
        _swatch.Background = new SolidColorBrush(c);
        if (commit) Commit($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void Commit(string hex)
    {
        if (string.Equals(hex, Color, StringComparison.OrdinalIgnoreCase)) return;
        _syncing = true;
        try { Color = hex; }
        finally { _syncing = false; }
    }
}
/// <summary>Saturation (x) and brightness (y) for one hue. Click or drag to pick.</summary>
public sealed class SaturationValueArea : Control
{
    public double Hue { get; set { field = value; InvalidateVisual(); } }
    public double Saturation { get; set { field = value; InvalidateVisual(); } }
    public double Value { get; set { field = value; InvalidateVisual(); } }

    /// <summary>(saturation, value, drag finished).</summary>
    public event Action<double, double, bool>? Changed;

    private bool _dragging;

    public SaturationValueArea()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(new SolidColorBrush(HsvColor.ToRgb(Hue, 1, 1)), r);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
        }, r);
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
        }, r);
        var p = new Point(Saturation * r.Width, (1 - Value) * r.Height);
        ctx.DrawEllipse(null, new Pen(Brushes.Black, 3), p, 6, 6);
        ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 6, 6);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this), false);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Pick(e.GetPosition(this), false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Pick(e.GetPosition(this), true);
    }

    private void Pick(Point p, bool done)
    {
        Saturation = Math.Clamp(p.X / Math.Max(1, Bounds.Width), 0, 1);
        Value = 1 - Math.Clamp(p.Y / Math.Max(1, Bounds.Height), 0, 1);
        Changed?.Invoke(Saturation, Value, done);
    }
}

/// <summary>Horizontal rainbow bar for the hue (0–360). Click or drag to pick.</summary>
public sealed class HueBar : Control
{
    public double Hue { get; set { field = value; InvalidateVisual(); } }

    /// <summary>(hue, drag finished).</summary>
    public event Action<double, bool>? Changed;

    private bool _dragging;

    public HueBar() => Cursor = new Cursor(StandardCursorType.Hand);

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        for (int i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(HsvColor.ToRgb(i * 60 % 360, 1, 1), i / 6.0));
        ctx.DrawRectangle(brush, null, r, 3, 3);
        var x = Hue / 360 * r.Width;
        var marker = new Rect(x - 3, -1, 6, r.Height + 2);
        ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), marker, 2, 2);
        ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), marker, 2, 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this).X, false);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Pick(e.GetPosition(this).X, false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Pick(e.GetPosition(this).X, true);
    }

    private void Pick(double x, bool done)
    {
        Hue = Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * 359.9;
        Changed?.Invoke(Hue, done);
    }
}
