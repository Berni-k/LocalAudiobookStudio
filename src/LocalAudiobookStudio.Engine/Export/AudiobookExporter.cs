using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LocalAudiobookStudio.Audio.Mixing;

namespace LocalAudiobookStudio.Engine.Export;

public enum ExportFormat
{
    M4b,
    Mp3Single,
    Mp3PerChapter,
    Wav,
    /// <summary>One WAV per section, named by its export id (<see cref="Core.Model.TimelineItem.ExportId"/>) or its number.</summary>
    WavPerSegment,
    /// <summary>One MP3 per section, named like <see cref="WavPerSegment"/>.</summary>
    Mp3PerSegment,
}

public static class ExportFormats
{
    /// <summary>One file per section rather than per chapter or for the whole book.</summary>
    public static bool IsPerSegment(this ExportFormat f) => f is ExportFormat.WavPerSegment or ExportFormat.Mp3PerSegment;

    public static bool IsMp3(this ExportFormat f) => f is ExportFormat.Mp3Single or ExportFormat.Mp3PerChapter or ExportFormat.Mp3PerSegment;
}

/// <summary>One section to export on its own: its file name (without extension) and its audio alone, starting at 0.</summary>
public sealed record ExportSegment(string Name, MixPlan Plan);

/// <summary>One chapter to render: its title and its time range on the book timeline.</summary>
public sealed record ExportChapter(string Title, double StartMs, double EndMs)
{
    public double DurationMs => Math.Max(0, EndMs - StartMs);
}

public sealed class ExportOptions
{
    public ExportFormat Format { get; set; } = ExportFormat.M4b;
    public int BitrateKbps { get; set; } = 96;
    public bool Mono { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string? CoverImagePath { get; set; }

    /// <summary>For single-file formats: the output file. For per-chapter formats: the output folder.</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>Optional gentle overall limiter pass (loudness is already handled per item).</summary>
    public bool FinalLimiter { get; set; }
}

public sealed record ExportProgress(string Stage, double Fraction);

/// <summary>
/// Streams chapters rendered by <see cref="TimelineMixer"/> as raw 32-bit float straight into ffmpeg's
/// stdin (no intermediate audio files): M4B with chapter markers, MP3 (single or per chapter) with tags, or WAV.
/// Small helper files go to <paramref name="tempDir"/>, which is deleted before and after each export.
/// </summary>
public sealed class AudiobookExporter(string ffmpegPath, AudioBufferCache cache, string tempDir)
{
    public Action<string>? Log { get; set; }

    public async Task<IReadOnlyList<string>> ExportAsync(
        MixPlan plan,
        IReadOnlyList<ExportChapter> chapters,
        ExportOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        if (chapters.Count == 0) throw new InvalidOperationException("Nothing to export.");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        Directory.CreateDirectory(tempDir);
        try
        {
            var outputs = options.Format switch
            {
                ExportFormat.M4b => [await EncodeSingleAsync(plan, chapters, options, "m4b", progress, ct).ConfigureAwait(false)],
                ExportFormat.Mp3Single => [await EncodeSingleAsync(plan, chapters, options, "mp3", progress, ct).ConfigureAwait(false)],
                ExportFormat.Mp3PerChapter => await EncodePerChapterAsync(plan, chapters, options, "mp3", progress, ct).ConfigureAwait(false),
                ExportFormat.Wav => await EncodePerChapterAsync(plan, chapters, options, "wav", progress, ct).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            };
            progress?.Report(new ExportProgress("Done", 1));
            return outputs;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>Writes [startMs, endMs) of the plan to <paramref name="output"/> as raw interleaved stereo f32le.</summary>
    public void RenderRange(MixPlan plan, double startMs, double endMs, Stream output, Action<double>? progress, CancellationToken ct)
    {
        var mixer = new TimelineMixer(plan, cache, stopAtEnd: false) { PrefetchMs = 20000 };
        mixer.Seek(startMs);
        var rate = mixer.SampleRate;
        var totalFrames = (long)Math.Round((endMs - startMs) * rate / 1000.0);
        var buffer = new float[rate]; // 0.5 s of stereo
        long written = 0;
        while (written < totalFrames)
        {
            ct.ThrowIfCancellationRequested();
            var frames = (int)Math.Min(buffer.Length / 2, totalFrames - written);
            var n = mixer.Read(buffer.AsSpan(0, frames * 2));
            if (n <= 0) break;
            output.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, n)));
            written += n / 2;
            progress?.Invoke((double)written / totalFrames);
        }
    }

    /// <summary>ffmpeg input arguments matching what <see cref="RenderRange"/> writes.</summary>
    private List<string> RawInputArgs() =>
        ["-f", "f32le", "-ar", cache.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", "pipe:0"];

    private static Action<double> ChapterProgress(IProgress<ExportProgress>? progress, ExportChapter ch, double baseMs, double totalMs) =>
        f => progress?.Report(new ExportProgress($"Exporting {ch.Title}", (baseMs + f * ch.DurationMs) / Math.Max(1, totalMs)));

    private async Task<string> EncodeSingleAsync(MixPlan plan, IReadOnlyList<ExportChapter> chapters, ExportOptions o,
        string ext, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var output = o.OutputPath;
        if (Directory.Exists(output) || !Path.HasExtension(output))
            output = Path.Combine(output, SafeFileName(string.IsNullOrWhiteSpace(o.Title) ? "audiobook" : o.Title) + "." + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

        var meta = Path.Combine(tempDir, "meta.txt");
        File.WriteAllText(meta, BuildFfMetadata(o, chapters), new UTF8Encoding(false));

        var args = RawInputArgs();
        args.AddRange(["-i", meta]);
        var hasCover = !string.IsNullOrEmpty(o.CoverImagePath) && File.Exists(o.CoverImagePath);
        if (hasCover) args.AddRange(["-i", o.CoverImagePath!]);
        args.AddRange(["-map", "0:a", "-map_metadata", "1", "-map_chapters", "1"]);
        if (hasCover) args.AddRange(["-map", "2:v", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        if (o.FinalLimiter) args.AddRange(["-af", "alimiter=limit=0.9:level=false"]);
        if (o.Mono) args.AddRange(["-ac", "1"]);

        if (ext == "m4b")
            args.AddRange(["-c:a", "aac", "-b:a", $"{o.BitrateKbps}k", "-movflags", "+faststart", "-f", "ipod"]);
        else
            args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{o.BitrateKbps}k", "-id3v2_version", "3", "-write_id3v1", "1"]);
        args.Add(output);

        var totalMs = chapters.Sum(c => c.DurationMs);
        await Ffmpeg.RunAsync(ffmpegPath, args, (stdin, token) =>
        {
            double doneMs = 0;
            foreach (var ch in chapters)
            {
                RenderRange(plan, ch.StartMs, ch.EndMs, stdin, ChapterProgress(progress, ch, doneMs, totalMs), token);
                doneMs += ch.DurationMs;
            }
        }, Log, ct).ConfigureAwait(false);
        return output;
    }

    private async Task<IReadOnlyList<string>> EncodePerChapterAsync(MixPlan plan, IReadOnlyList<ExportChapter> chapters,
        ExportOptions o, string ext, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var folder = o.OutputPath;
        Directory.CreateDirectory(folder);
        var outputs = new List<string>();
        var totalMs = chapters.Sum(c => c.DurationMs);
        double doneMs = 0;
        for (int i = 0; i < chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ch = chapters[i];
            var name = $"{i + 1:00} - {SafeFileName(ch.Title)}.{ext}";
            var output = Path.Combine(folder, name);
            var args = RawInputArgs();
            if (o.FinalLimiter) args.AddRange(["-af", "alimiter=limit=0.9:level=false"]);
            if (o.Mono) args.AddRange(["-ac", "1"]);
            if (ext == "mp3")
            {
                args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{o.BitrateKbps}k", "-id3v2_version", "3",
                    "-metadata", $"title={ch.Title}",
                    "-metadata", $"artist={o.Author}",
                    "-metadata", $"album={o.Title}",
                    "-metadata", $"track={i + 1}/{chapters.Count}",
                    "-metadata", "genre=Audiobook"]);
            }
            else
            {
                args.AddRange(["-c:a", "pcm_s24le"]);
            }
            args.Add(output);
            var onProgress = ChapterProgress(progress, ch, doneMs, totalMs);
            await Ffmpeg.RunAsync(ffmpegPath, args,
                (stdin, token) => RenderRange(plan, ch.StartMs, ch.EndMs, stdin, onProgress, token), Log, ct).ConfigureAwait(false);
            doneMs += ch.DurationMs;
            outputs.Add(output);
        }
        return outputs;
    }

    /// <summary>
    /// Writes each section to its own file in <see cref="ExportOptions.OutputPath"/> (a folder): WAV or MP3,
    /// with the same mono and limiter options as the other formats. Returns the files written.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportSegmentsAsync(IReadOnlyList<ExportSegment> segments, ExportOptions o,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (segments.Count == 0) throw new InvalidOperationException("Nothing to export: no section has audio yet.");
        Directory.CreateDirectory(o.OutputPath);
        var ext = o.Format == ExportFormat.Mp3PerSegment ? "mp3" : "wav";
        var outputs = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var seg = segments[i];
            progress?.Report(new ExportProgress($"Exporting {seg.Name}", (double)i / segments.Count));
            var output = Path.Combine(o.OutputPath, seg.Name + "." + ext);
            var args = RawInputArgs();
            if (o.FinalLimiter) args.AddRange(["-af", "alimiter=limit=0.9:level=false"]);
            if (o.Mono) args.AddRange(["-ac", "1"]);
            if (ext == "mp3")
                args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{o.BitrateKbps}k", "-id3v2_version", "3",
                    "-metadata", $"title={seg.Name}", "-metadata", $"album={o.Title}", "-metadata", $"artist={o.Author}",
                    "-metadata", $"track={i + 1}/{segments.Count}"]);
            else
                args.AddRange(["-c:a", "pcm_s24le"]);
            args.Add(output);
            await Ffmpeg.RunAsync(ffmpegPath, args,
                (stdin, token) => RenderRange(seg.Plan, 0, seg.Plan.TotalMs, stdin, null, token), Log, ct).ConfigureAwait(false);
            outputs.Add(output);
        }
        progress?.Report(new ExportProgress("Done", 1));
        return outputs;
    }

    /// <summary>ffmetadata with title/author and one [CHAPTER] per rendered chapter (times relative to the output).</summary>
    public static string BuildFfMetadata(ExportOptions o, IReadOnlyList<ExportChapter> chapters)
    {
        var sb = new StringBuilder();
        sb.Append(";FFMETADATA1\n");
        sb.Append("title=").Append(Ffmpeg.EscapeMetadata(o.Title)).Append('\n');
        sb.Append("album=").Append(Ffmpeg.EscapeMetadata(o.Title)).Append('\n');
        sb.Append("artist=").Append(Ffmpeg.EscapeMetadata(o.Author)).Append('\n');
        sb.Append("album_artist=").Append(Ffmpeg.EscapeMetadata(o.Author)).Append('\n');
        sb.Append("genre=Audiobook\n");
        double t = 0;
        foreach (var c in chapters)
        {
            var start = (long)Math.Round(t);
            t += c.DurationMs;
            var end = (long)Math.Round(t);
            sb.Append("\n[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("title=").Append(Ffmpeg.EscapeMetadata(c.Title)).Append('\n');
        }
        return sb.ToString();
    }

    public static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }
}
