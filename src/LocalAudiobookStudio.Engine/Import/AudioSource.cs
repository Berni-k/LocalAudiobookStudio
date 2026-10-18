using System.Globalization;
using System.Text.RegularExpressions;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Engine.Hosting;

namespace LocalAudiobookStudio.Engine;

/// <summary>
/// A recording made ready for import: the 16 kHz mono copy the models listen to, the original as PCM (to cut takes from, by
/// offset, without holding it in memory), its length and the chapters the file declares. The copies are temporary files, deleted
/// by <see cref="Dispose"/>.
/// </summary>
public sealed partial class AudioSource : IDisposable
{
    private AudioSource(string file, string folder) { File = file; Folder = folder; }

    public string File { get; }
    public string Folder { get; }

    /// <summary>16 kHz mono 16-bit WAV for the transcription workers.</summary>
    public string AnalysisWav { get; private set; } = "";

    /// <summary>The original's samples as a PCM WAV (its own rate and channels); null when the takes aren't wanted.</summary>
    public string? OriginalWav { get; private set; }

    public int DurationMs { get; private set; }
    public List<SourceChapter> Chapters { get; } = new();

    /// <summary>
    /// Decodes <paramref name="file"/> (anything ffmpeg reads; without ffmpeg, WAV, Opus and MP3) into the working copies.
    /// <paramref name="keepOriginal"/> also writes the original-quality PCM that takes are cut from.
    /// </summary>
    public static async Task<AudioSource> PrepareAsync(StudioHost host, string file, bool keepOriginal,
        IProgress<(double Fraction, string Message)>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(host.DataRoot.Temp, "import-audio", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var source = new AudioSource(file, folder) { AnalysisWav = Path.Combine(folder, "analysis-16k.wav") };
        try
        {
            if (Ffmpeg.Locate(host.Settings.FfmpegPath, host.DataRoot.Root) is { } ffmpeg)
                await source.WithFfmpegAsync(ffmpeg, keepOriginal, progress, ct).ConfigureAwait(false);
            else
                await source.WithoutFfmpegAsync(keepOriginal, progress, ct).ConfigureAwait(false);
            if (source.DurationMs <= 0) source.DurationMs = (int)AudioDecoder.ReadInfo(source.AnalysisWav).DurationMs;
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private async Task WithFfmpegAsync(string ffmpeg, bool keepOriginal, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        // What the file says about itself: its length, and its chapters (m4b atoms, ID3 CHAP).
        progress?.Report((0, "Reading the file…"));
        var metadata = Path.Combine(Folder, "metadata.txt");
        await Ffmpeg.RunAsync(ffmpeg, ["-i", File, "-f", "ffmetadata", metadata], 0, null, line =>
        {
            if (DurationMs == 0 && DurationLine().Match(line) is { Success: true } m)
                DurationMs = (int)TimeSpan.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).TotalMilliseconds;
        }, ct).ConfigureAwait(false);
        if (System.IO.File.Exists(metadata)) Chapters.AddRange(ParseChapters(System.IO.File.ReadAllLines(metadata)));

        // One run, two outputs: what the models listen to, and (for the takes) the original's samples.
        var args = new List<string> { "-i", File, "-vn", "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", AnalysisWav };
        if (keepOriginal)
        {
            OriginalWav = Path.Combine(Folder, "original.wav");
            args.AddRange(["-map", "0:a:0", "-c:a", "pcm_s16le", OriginalWav]);
        }
        var fraction = new Progress<double>(f => progress?.Report((f, $"Reading the audio… {f:P0}")));
        await Ffmpeg.RunAsync(ffmpeg, args, DurationMs, fraction, null, ct).ConfigureAwait(false);
    }

    private async Task WithoutFfmpegAsync(bool keepOriginal, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        if (!AudioDecoder.CanDecode(File)) throw new FfmpegNotFoundException();
        progress?.Report((0, "Reading the audio…"));
        await Task.Run(() =>
        {
            var decoded = AudioDecoder.Decode(File);
            ct.ThrowIfCancellationRequested();
            DurationMs = (int)decoded.DurationMs;
            if (keepOriginal)
            {
                OriginalWav = Path.Combine(Folder, "original.wav");
                AudioDecoder.WriteWav(decoded, OriginalWav);
            }
            Transcription.PrepareAudio(File, AnalysisWav);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The chapters of an ffmetadata file: <c>[CHAPTER]</c> blocks with TIMEBASE, START and title.</summary>
    public static List<SourceChapter> ParseChapters(IEnumerable<string> lines)
    {
        var chapters = new List<SourceChapter>();
        double timebase = 1.0 / 1000;
        long? start = null;
        string? title = null;
        var inChapter = false;
        void Flush()
        {
            if (inChapter && start is long s) chapters.Add(new SourceChapter(title ?? "", (int)Math.Round(s * timebase * 1000)));
            start = null;
            title = null;
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                inChapter = line.Equals("[CHAPTER]", StringComparison.OrdinalIgnoreCase);
                timebase = 1.0 / 1000;
                continue;
            }
            var eq = line.IndexOf('=');
            if (!inChapter || eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("TIMEBASE", StringComparison.OrdinalIgnoreCase) && value.Split('/') is [var n, var d]
                && double.TryParse(n, CultureInfo.InvariantCulture, out var num) && double.TryParse(d, CultureInfo.InvariantCulture, out var den) && den != 0)
                timebase = num / den;
            else if (key.Equals("START", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, CultureInfo.InvariantCulture, out var s)) start = s;
            else if (key.Equals("title", StringComparison.OrdinalIgnoreCase)) title = value.Replace("\\=", "=").Replace("\\;", ";").Replace("\\#", "#").Replace("\\\\", "\\");
        }
        Flush();
        return chapters;
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, true); } catch (IOException) { /* cache */ } catch (UnauthorizedAccessException) { /* cache */ }
    }

    [GeneratedRegex(@"Duration:\s*(\d+:\d\d:\d\d(?:\.\d+)?)")]
    private static partial Regex DurationLine();
}
