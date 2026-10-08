using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;

namespace LocalAudiobookStudio.Audio.Mixing;

/// <summary>Where an item's audio comes from: the selected take or an imported clip.</summary>
public sealed record AudioSourceInfo(
    string Path,
    double RawDurationMs,
    double? LoudnessLufs,
    double DefaultTrimStartMs,
    double DefaultTrimEndMs);

/// <summary>Turns the project timeline into a <see cref="MixPlan"/>, applying gain, trims and fades.</summary>
public static class ProjectMixPlanBuilder
{
    /// <summary>Fade-out used when a background is cut short or looped and has no fade of its own.</summary>
    public const double BackgroundCutFadeMs = 800;

    /// <summary>Effective trims: the item's explicit trim, else the source's detected silence.</summary>
    public static (double start, double end) Trims(TimelineItem item, AudioSourceInfo src) =>
        (item.TrimStartMs ?? src.DefaultTrimStartMs, item.TrimEndMs ?? src.DefaultTrimEndMs);

    /// <summary>gain = (target − measured, clamped ±20 dB) + item gain. Normalization can be turned off per item.</summary>
    public static double EffectiveGainDb(TimelineItem item, AudioSourceInfo src, double targetLufs)
    {
        var auto = item.Normalize == false ? 0.0 : LoudnessMeter.AutoGainDb(targetLufs, src.LoudnessLufs);
        return auto + item.GainDb;
    }

    /// <param name="placeholders">
    /// When true, items without audio yet (estimated length) get quiet placeholder static so playback
    /// sounds like "something goes here". Only for playback; export plans must leave it off.
    /// </param>
    public static MixPlan Build(
        IReadOnlyList<Chapter> chapters,
        ProjectDefaults defaults,
        TimelineLayout layout,
        Func<TimelineItem, AudioSourceInfo?> sourceOf,
        Func<TimelineItem, bool>? include = null,
        bool placeholders = false)
    {
        var items = new List<MixItem>();
        foreach (var t in layout.Items)
        {
            if (t.ChapterIndex >= chapters.Count) continue;
            var ch = chapters[t.ChapterIndex];
            if (t.ItemIndex >= ch.Items.Count) continue;
            var item = ch.Items[t.ItemIndex];
            if (item.Id != t.ItemId) continue; // layout is stale; skip until recomputed
            if (include != null && !include(item)) continue;
            if (!item.HasAudio) continue; // pauses and comments

            var src = t.IsEstimate ? null : sourceOf(item);
            if (src == null)
            {
                if (placeholders && t.DurationMs > 0)
                    items.Add(new MixItem(item.Id, t.StartMs, t.DurationMs, MixItem.PlaceholderNoise, 0, 0, 40, 40));
                continue;
            }
            var (trimStart, trimEnd) = Trims(item, src);
            var natural = TimelineCalculator.TrimmedMs(src.RawDurationMs, trimStart, trimEnd);
            var loop = item.IsBackground && item.BackgroundLoop == true && natural > 0 && t.DurationMs > natural;
            // A background cut short (or looped) fades out instead of stopping dead.
            var cut = item.IsBackground && (loop || t.DurationMs < natural - 1);
            items.Add(new MixItem(
                item.Id,
                t.StartMs,
                t.DurationMs,
                src.Path,
                trimStart,
                EffectiveGainDb(item, src, defaults.TargetLoudnessLufs),
                item.FadeInMs ?? 0,
                item.FadeOutMs ?? (cut ? BackgroundCutFadeMs : 0),
                loop ? natural : 0));
        }
        return new MixPlan(items, layout.TotalMs);
    }
}
