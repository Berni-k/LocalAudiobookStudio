using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// Horizontal VU meter for recording: the bar shows <see cref="Level"/> (linear peak 0–1) on a dB scale
/// from −60 to 0 dB, green → yellow → red, with a peak marker that holds briefly and then falls.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    static LevelMeter() => AffectsRender<LevelMeter>(LevelProperty);

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private const double FloorDb = -60;
    private double _peak;
    private DateTime _peakAt;

    private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
    private static readonly LinearGradientBrush Bar = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#3CC36B"), 0),
            new GradientStop(Color.Parse("#3CC36B"), 0.70), // up to −18 dB
            new GradientStop(Color.Parse("#F5C542"), 0.85), // −9 dB
            new GradientStop(Color.Parse("#F04B4B"), 1),
        },
    };

    /// <summary>0–1 position for a linear level on the dB scale.</summary>
    private static double Fraction(double level)
    {
        if (level <= 0) return 0;
        var db = 20 * Math.Log10(level);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var r = new Rect(0, 0, w, h);
        ctx.DrawRectangle(Track, null, r, 2, 2);

        var f = Fraction(Level);
        if (f > 0)
            using (ctx.PushClip(new Rect(0, 0, w * f, h)))
                ctx.DrawRectangle(Bar, null, r, 2, 2); // the gradient spans the full width, so colours sit at fixed levels

        // Peak marker: holds for a second, then falls.
        var now = DateTime.UtcNow;
        if (f >= _peak) { _peak = f; _peakAt = now; }
        else if ((now - _peakAt).TotalSeconds > 1) _peak = Math.Max(f, _peak - 0.02);
        if (_peak > 0) ctx.FillRectangle(Brushes.White, new Rect(Math.Max(0, w * _peak - 2), 0, 2, h));
    }
}
