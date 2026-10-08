using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using NAudio.Wave;

namespace LocalAudiobookStudio.Engine;

/// <summary>
/// Generations outside any project: the Speakers dialog's preview and the command line's <c>las tts</c>.
/// A job writes exactly to its output path; nothing is analysed or saved as a take.
/// </summary>
public static partial class OneShot
{
    /// <summary>
    /// Settings overlaid with overrides; file and asset settings that are relative paths become absolute through
    /// <paramref name="resolveRelative"/> (project-relative in a project, the working folder on the command line).
    /// </summary>
    public static JsonObject MergeSettings(JsonObject? baseSettings, JsonObject? overrides, WorkerManifest manifest, Func<string, string> resolveRelative)
    {
        var result = (JsonObject)(baseSettings?.DeepClone() ?? new JsonObject());
        if (overrides != null)
            foreach (var (k, v) in overrides) result[k] = v?.DeepClone();
        foreach (var def in manifest.SettingsFor(result, resolveRelative).Where(d => d.Type is SettingTypes.File or SettingTypes.Asset))
        {
            if (result[def.Key] is JsonValue jv && jv.TryGetValue<string>(out var rel) && !string.IsNullOrWhiteSpace(rel) && !Path.IsPathRooted(rel))
                result[def.Key] = resolveRelative(rel);
        }
        return result;
    }

    /// <summary>The "seed" setting as a number, or null (random).</summary>
    public static long? SeedFrom(JsonObject settings)
    {
        if (settings["seed"] is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        }
        return null;
    }

    /// <summary>
    /// A setting value typed from text ("key=value" on the command line): numbers, booleans and text by the
    /// manifest's setting type; for unknown keys, whatever the text looks like.
    /// </summary>
    public static JsonNode? ParseSettingValue(WorkerManifest manifest, string key, string text)
    {
        var def = manifest.Settings.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        switch (def?.Type)
        {
            case SettingTypes.Float:
                return double.Parse(text, CultureInfo.InvariantCulture);
            case SettingTypes.Int:
            case SettingTypes.Seed:
                return long.Parse(text, CultureInfo.InvariantCulture);
            case SettingTypes.Bool:
                return bool.Parse(text);
            case SettingTypes.Enum:
                if (def.Options.Count > 0 && !def.Options.Contains(text))
                    throw new ArgumentException($"'{text}' is not an option of {key}: {string.Join(", ", def.Options)}");
                return text;
            case null:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                if (bool.TryParse(text, out var b)) return b;
                return text;
            default:
                return text;
        }
    }

    /// <summary>
    /// The length a worker that needs one gets when none is given: one named in the text ("thunder, 4s"), else the
    /// default, within the worker's range for these settings and rounded to 0.1 s.
    /// </summary>
    public static double GuessLength(WorkerManifest worker, string? text, JsonObject? settings) =>
        Math.Round(worker.ClampLength(LengthGuess.NamedIn(text) ?? LengthGuess.DefaultSeconds, settings), 1);

    /// <summary>
    /// A job that generates <paramref name="text"/> to <paramref name="outPath"/>. A worker that takes a length gets
    /// <paramref name="durationSec"/>; one that needs a length and gets none, <see cref="GuessLength"/>.
    /// </summary>
    public static GenerationJob Job(WorkerManifest worker, string text, string? voice, JsonObject settings, string outPath,
        string label, JobPriority priority, long? seed = null, double? durationSec = null, object? tag = null, long readingOrder = 0) =>
        new()
        {
            WorkerId = worker.Id,
            Priority = priority,
            ReadingOrder = readingOrder,
            FixedOutPath = outPath,
            Label = label,
            Tag = tag,
            Request = new GenerateRequest
            {
                Text = text,
                Voice = voice,
                DurationSec = !worker.SupportsLength ? null
                    : durationSec is double d ? worker.ClampLength(d, settings)
                    : worker.RequiresLength ? GuessLength(worker, text, settings)
                    : null,
                Settings = settings,
                Seed = seed,
            },
        };

    // ------------------------------------------------------------------ long texts

    /// <summary>
    /// Splits a text into pieces a worker takes in one request: paragraphs (blank lines), and paragraphs
    /// longer than the worker's limit at sentences. Line breaks inside a paragraph become spaces.
    /// </summary>
    public static List<List<string>> SplitForWorker(string text, int maxChars)
    {
        var paragraphs = new List<List<string>>();
        foreach (var p in ParagraphBreak().Split(text.Replace("\r\n", "\n")))
        {
            var flat = Whitespace().Replace(p, " ").Trim();
            if (flat.Length == 0) continue;
            paragraphs.Add(SentenceSplitter.SplitToLimit(flat, Math.Max(50, maxChars)));
        }
        return paragraphs;
    }

    /// <summary>
    /// Joins WAV files (all in the same format, as one worker writes them) into one, with
    /// <paramref name="gapsMs"/>[i] of silence after file i.
    /// </summary>
    public static void ConcatenateWavs(IReadOnlyList<string> inputs, IReadOnlyList<int> gapsMs, string output)
    {
        if (inputs.Count == 0) throw new ArgumentException("Nothing to join.");
        WaveFormat? format = null;
        using var writer = new Func<WaveFileWriter>(() =>
        {
            using var first = new WaveFileReader(inputs[0]);
            format = first.WaveFormat;
            return new WaveFileWriter(output, format);
        })();
        var buffer = new byte[64 * 1024];
        for (int i = 0; i < inputs.Count; i++)
        {
            using (var reader = new WaveFileReader(inputs[i]))
            {
                if (!reader.WaveFormat.Equals(format))
                    throw new InvalidDataException($"{Path.GetFileName(inputs[i])} has a different format ({reader.WaveFormat}) than the first part ({format}).");
                int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, n);
            }
            var gap = i < gapsMs.Count ? gapsMs[i] : 0;
            if (gap > 0 && i < inputs.Count - 1)
            {
                var bytes = (long)format!.AverageBytesPerSecond * gap / 1000;
                bytes -= bytes % format.BlockAlign;
                Array.Clear(buffer);
                while (bytes > 0)
                {
                    var chunk = (int)Math.Min(buffer.Length, bytes);
                    writer.Write(buffer, 0, chunk);
                    bytes -= chunk;
                }
            }
        }
    }

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
