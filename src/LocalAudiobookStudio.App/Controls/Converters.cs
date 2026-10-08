using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LocalAudiobookStudio.App.Controls;

/// <summary>Small value converters used from XAML via {x:Static ctl:Converters.Name}.</summary>
public static class Converters
{
    public static readonly IValueConverter BoolToBold =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>Comfortable reading line height: 1.45 × font size.</summary>
    public static readonly IValueConverter LineHeight =
        new FuncValueConverter<double, double>(size => Math.Round(size * 1.45));

    public static readonly IValueConverter IsZero =
        new FuncValueConverter<double, bool>(v => v <= 0.0001);

    public static readonly IValueConverter NotNull =
        new FuncValueConverter<object?, bool>(v => v != null);

    /// <summary>A chevron pointing right turned down for an expanded folder.</summary>
    public static readonly IValueConverter ExpandedAngle =
        new FuncValueConverter<bool, double>(expanded => expanded ? 90 : 0);

    /// <summary>Labels for the status filter dropdown.</summary>
    public static readonly IValueConverter StatusFilterLabel =
        new FuncValueConverter<ViewModels.StatusFilter, string>(f => f switch
        {
            ViewModels.StatusFilter.All => "All sections",
            ViewModels.StatusFilter.NotGenerated => "Not generated",
            _ => f.ToString(),
        });

    public static readonly IValueConverter ColorToBrush =
        new FuncValueConverter<string?, IBrush>(s => new SolidColorBrush(
            !string.IsNullOrWhiteSpace(s) && Color.TryParse(s, out var c) ? c : Colors.Gray));
}
