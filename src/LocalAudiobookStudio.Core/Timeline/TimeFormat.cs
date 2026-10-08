using System.Globalization;

namespace LocalAudiobookStudio.Core.Timeline;

public static class TimeFormat
{
    /// <summary>h:mm:ss, prefixed with '~' when estimated.</summary>
    public static string Hms(double ms, bool estimate = false)
    {
        if (double.IsNaN(ms) || ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        var s = $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return estimate ? "~" + s : s;
    }

    /// <summary>m:ss.f for short durations, h:mm:ss beyond an hour.</summary>
    public static string Short(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? Hms(ms)
            : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}");
    }

    /// <summary>"+0.4 s" / "−1.5 s" for gap labels.</summary>
    public static string Gap(double ms) =>
        (ms < 0 ? "−" : "+") + (Math.Abs(ms) / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " s";
}
