using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LocalAudiobookStudio.Core.Transcripts;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>
/// The stretches of speech a transcription worker that tells voices apart heard (<see cref="HeardBlock"/>), drawn as a thin
/// band over a whole-file waveform: one colour per voice, the voice's label inside when it fits. Words point into these
/// blocks, so this is where each timed word came from. Takes no clicks.
/// </summary>
public sealed class VoiceBlocksView : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeardBlock>?> BlocksProperty =
        AvaloniaProperty.Register<VoiceBlocksView, IReadOnlyList<HeardBlock>?>(nameof(Blocks));
    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<VoiceBlocksView, double>(nameof(DurationMs));
    public static readonly StyledProperty<double> ViewStartMsProperty =
        AvaloniaProperty.Register<VoiceBlocksView, double>(nameof(ViewStartMs));
    public static readonly StyledProperty<double> ViewEndMsProperty =
        AvaloniaProperty.Register<VoiceBlocksView, double>(nameof(ViewEndMs));

    static VoiceBlocksView() =>
        AffectsRender<VoiceBlocksView>(BlocksProperty, DurationMsProperty, ViewStartMsProperty, ViewEndMsProperty);

    public VoiceBlocksView() => IsHitTestVisible = false;

    public IReadOnlyList<HeardBlock>? Blocks { get => GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }
    public double DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    /// <summary>The waveform's visible range, so the blocks follow its zoom.</summary>
    public double ViewStartMs { get => GetValue(ViewStartMsProperty); set => SetValue(ViewStartMsProperty, value); }
    public double ViewEndMs { get => GetValue(ViewEndMsProperty); set => SetValue(ViewEndMsProperty, value); }

    /// <summary>One colour per voice, in the order the voices first speak (the labels are the worker's own: "S01", "S02").</summary>
    public static readonly Color[] VoiceColors =
    [
        Color.FromRgb(0xE0, 0x6C, 0x75), Color.FromRgb(0x61, 0xAF, 0xEF), Color.FromRgb(0x98, 0xC3, 0x79),
        Color.FromRgb(0xE5, 0xC0, 0x7B), Color.FromRgb(0xC6, 0x78, 0xDD), Color.FromRgb(0x56, 0xB6, 0xC2),
    ];

    public static Color ColorOf(IReadOnlyList<HeardBlock> blocks, string? speaker)
    {
        var voices = blocks.Select(b => b.Speaker).Where(s => s != null).Distinct().ToList();
        var index = speaker == null ? -1 : voices.IndexOf(speaker);
        return index < 0 ? Color.FromRgb(0x9A, 0x9A, 0x9A) : VoiceColors[index % VoiceColors.Length];
    }

    public override void Render(DrawingContext ctx)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (Blocks is not { Count: > 0 } blocks || DurationMs <= 0 || w < 2) return;
        foreach (var block in blocks)
        {
            var (x0, x1) = (TimeAxis.X(block.StartMs, ViewStartMs, ViewEndMs, DurationMs, w), TimeAxis.X(block.EndMs, ViewStartMs, ViewEndMs, DurationMs, w));
            if (x1 < 0 || x0 > w) continue;
            var color = ColorOf(blocks, block.Speaker);
            var rect = new Rect(x0 + 0.5, 0, Math.Max(1, x1 - x0 - 1), h);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B)), rect, 2);
            if (block.Speaker == null || x1 - x0 < 24) continue;
            var text = new FormattedText(block.Speaker, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White)
            {
                MaxTextWidth = Math.Max(1, x1 - x0 - 6),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            ctx.DrawText(text, new Point(Math.Max(x0, 0) + 4, (h - text.Height) / 2));
        }
    }
}
