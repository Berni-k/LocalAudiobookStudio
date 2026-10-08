using System.Globalization;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Core.Timeline;

/// <summary>Guesses a segment's length when a worker needs one and the segment doesn't set it.</summary>
public static partial class LengthGuess
{
    /// <summary>The length asked of a worker that needs one when the segment sets none and its text names none.</summary>
    public const double DefaultSeconds = 5.0;

    /// <summary>
    /// A length named in a segment's text, such as "thunder, 4s", "rain for 2.5 seconds" or "1 min of crowd
    /// noise" (for workers that need a length: their text describes a sound). The last mention wins. Null when
    /// the text names no length. Anything over 10 minutes is ignored, which also skips years such as "1990s".
    /// </summary>
    public static double? NamedIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        double? found = null;
        foreach (Match m in LengthPattern().Matches(text))
        {
            if (!double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
                continue;
            var unit = m.Groups["u"].Value.ToLowerInvariant();
            var seconds = unit.StartsWith("ms") || unit.StartsWith("milli") ? n / 1000.0
                : unit.StartsWith('m') ? n * 60.0
                : n;
            if (seconds <= MaxSeconds) found = seconds;
        }
        return found;
    }

    /// <summary>Speech length from the text length and the speaker's measured rate.</summary>
    public static double FromText(string? text, double? charsPerSecond) =>
        TimelineCalculator.EstimateSpeechMs(text?.Length ?? 0, charsPerSecond) / 1000.0;

    private const double MaxSeconds = 600;

    [GeneratedRegex(@"(?<![\w.,])(?<n>\d+(?:[.,]\d+)?)\s*(?<u>ms|milliseconds?|s|secs?|seconds?|m|mins?|minutes?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LengthPattern();
}
