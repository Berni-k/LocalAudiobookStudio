using System.Diagnostics;
using System.Text;

namespace LocalAudiobookStudio.Engine.Export;

public sealed class FfmpegNotFoundException() : Exception(
    "ffmpeg was not found. Get it from Help → System check (Windows: one click; Linux: the command shown there), " +
    "or put it in the app's tools/ffmpeg folder, set its path in Settings, or add it to PATH.");

public static class Ffmpeg
{
    /// <summary>
    /// tools/ffmpeg next to the app, then the configured path, then one downloaded into the data folder
    /// (<paramref name="dataRoot"/>/tools/ffmpeg, see ToolInstaller), then PATH.
    /// </summary>
    public static string? Locate(string? configuredPath, string? dataRoot)
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", exe);
        if (File.Exists(bundled)) return bundled;
        var bundledBin = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", exe);
        if (File.Exists(bundledBin)) return bundledBin;
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;
        if (dataRoot != null && Path.Combine(Hosting.ToolInstaller.FfmpegDir(dataRoot), exe) is var downloaded && File.Exists(downloaded))
            return downloaded;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Runs ffmpeg; reports progress from its "time=" output when <paramref name="totalMs"/> is known.</summary>
    public static Task RunAsync(string ffmpeg, IReadOnlyList<string> args, double totalMs,
        IProgress<double>? progress, Action<string>? log, CancellationToken ct) =>
        RunCoreAsync(ffmpeg, args, totalMs, progress, null, log, ct);

    /// <summary>
    /// Runs ffmpeg while <paramref name="feedStdin"/> writes its input (e.g. "-i pipe:0") on a background
    /// thread; stdin is closed when the feeder returns.
    /// </summary>
    public static Task RunAsync(string ffmpeg, IReadOnlyList<string> args, Action<Stream, CancellationToken> feedStdin,
        Action<string>? log, CancellationToken ct) =>
        RunCoreAsync(ffmpeg, args, 0, null, feedStdin, log, ct);

    private static async Task RunCoreAsync(string ffmpeg, IReadOnlyList<string> args, double totalMs,
        IProgress<double>? progress, Action<Stream, CancellationToken>? feedStdin, Action<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = feedStdin != null,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // BOM-free: with the console code page set to UTF-8 the stdin writer would otherwise put a
        // preamble in front of the raw audio and shift every sample by three bytes.
        if (feedStdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.ArgumentList.Add("-hide_banner");
        if (feedStdin == null) psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-y");
        var fixedArgs = psi.ArgumentList.Count;
        foreach (var a in args) psi.ArgumentList.Add(a);
        log?.Invoke("> ffmpeg " + string.Join(" ", psi.ArgumentList.Skip(fixedArgs).Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        using var p = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                if (tail.Count > 30) tail.Dequeue();
            }
            var idx = e.Data.IndexOf("time=", StringComparison.Ordinal);
            if (idx >= 0 && totalMs > 0 && progress != null)
            {
                var ts = e.Data.Substring(idx + 5).Split(' ')[0];
                if (TimeSpan.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, out var t))
                    progress.Report(Math.Clamp(t.TotalMilliseconds / totalMs, 0, 1));
            }
        };
        p.OutputDataReceived += (_, _) => { };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();

        var feed = feedStdin == null ? Task.CompletedTask : Task.Run(() =>
        {
            try { feedStdin(p.StandardInput.BaseStream, ct); }
            finally { try { p.StandardInput.Close(); } catch { } }
        }, CancellationToken.None);

        IOException? pipeError = null;
        try
        {
            // A broken pipe means ffmpeg quit early; its exit code and output explain why.
            try { await feed.ConfigureAwait(false); }
            catch (IOException ex) { pipeError = ex; }
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled, or the feeder failed: don't leave ffmpeg finishing a truncated file.
            try { p.Kill(true); } catch { }
            throw;
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            string details;
            lock (tail) details = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"ffmpeg failed with exit code {p.ExitCode}:{Environment.NewLine}{details}");
        }
        if (pipeError != null) throw new InvalidOperationException("ffmpeg stopped reading the audio stream.", pipeError);
    }

    /// <summary>Converts any audio file ffmpeg can read into a WAV (used when importing FLAC/OGG/MP3 clips).</summary>
    public static Task ConvertToWavAsync(string ffmpeg, string input, string output, CancellationToken ct) =>
        RunAsync(ffmpeg, ["-i", input, "-vn", "-c:a", "pcm_s24le", output], 0, null, null, ct);

    /// <summary>Escapes a value for an ffmetadata file.</summary>
    public static string EscapeMetadata(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
