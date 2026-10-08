using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LocalAudiobookStudio.Engine.Hosting;

/// <summary>
/// Gets the tools the app needs from the machine when they are missing, into the data folder (always writable,
/// unlike an app installed under Program Files): ffmpeg on Windows (on Linux the package manager is the way, see
/// <see cref="FfmpegInstallCommand"/>) and uv on both. Downloads are checked against their published SHA-256.
/// </summary>
public static class ToolInstaller
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The official "release essentials" build from gyan.dev (linked from ffmpeg.org): ffmpeg and ffprobe.</summary>
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>The uv version the app is tested with (tools/uv/VERSION next to the app, else this).</summary>
    public static string UvVersion { get; } = ReadUvVersion();

    private static string Rid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    /// <summary>Where a downloaded ffmpeg goes: &lt;data folder&gt;/tools/ffmpeg.</summary>
    public static string FfmpegDir(string dataRoot) => Path.Combine(dataRoot, "tools", "ffmpeg");

    /// <summary>Where a downloaded uv goes: &lt;data folder&gt;/tools/uv/&lt;rid&gt;.</summary>
    public static string UvDir(string dataRoot) => Path.Combine(dataRoot, "tools", "uv", Rid);

    /// <summary>ffmpeg can be downloaded on Windows; on Linux it comes from the package manager.</summary>
    public static bool CanDownloadFfmpeg => OperatingSystem.IsWindows();

    /// <summary>uv can be downloaded for Windows x64 and Linux x64.</summary>
    public static bool CanDownloadUv => RuntimeInformation.OSArchitecture == Architecture.X64 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    /// <summary>
    /// The command that installs ffmpeg on this Linux distribution (from /etc/os-release), or a generic one.
    /// Null on other systems.
    /// </summary>
    public static string? FfmpegInstallCommand()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var ids = new List<string>();
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("ID=", StringComparison.Ordinal) || line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
                    ids.AddRange(line[(line.IndexOf('=') + 1)..].Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch (IOException)
        {
            // No os-release: the generic command below.
        }
        bool Is(params string[] names) => ids.Any(i => names.Contains(i, StringComparer.OrdinalIgnoreCase));
        if (Is("debian", "ubuntu")) return "sudo apt install ffmpeg";
        if (Is("fedora")) return "sudo dnf install ffmpeg-free";
        if (Is("arch")) return "sudo pacman -S ffmpeg";
        if (Is("suse", "opensuse", "opensuse-leap", "opensuse-tumbleweed")) return "sudo zypper install ffmpeg";
        return "sudo apt install ffmpeg";
    }

    /// <summary>Downloads ffmpeg (Windows) into <see cref="FfmpegDir"/>. Returns the path of ffmpeg.exe.</summary>
    public static async Task<string> DownloadFfmpegAsync(string dataRoot, IProgress<string>? progress, CancellationToken ct)
    {
        if (!CanDownloadFfmpeg) throw new PlatformNotSupportedException("ffmpeg is only downloaded on Windows; on Linux install it with the package manager.");
        var dir = FfmpegDir(dataRoot);
        var zip = await DownloadVerifiedAsync(FfmpegZipUrl, FfmpegZipUrl + ".sha256", "ffmpeg", dataRoot, progress, ct);
        try
        {
            progress?.Report("Unpacking ffmpeg…");
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zip);
                var found = false;
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) continue;
                    Directory.CreateDirectory(dir);
                    entry.ExtractToFile(Path.Combine(dir, name), overwrite: true);
                    found |= name == "ffmpeg.exe";
                }
                if (!found) throw new InvalidDataException("The download doesn't contain ffmpeg.exe.");
            }, ct);
        }
        finally
        {
            TryDelete(zip);
        }
        return Path.Combine(dir, "ffmpeg.exe");
    }

    /// <summary>Downloads the pinned uv release into <see cref="UvDir"/>. Returns the path of uv.</summary>
    public static async Task<string> DownloadUvAsync(string dataRoot, IProgress<string>? progress, CancellationToken ct)
    {
        if (!CanDownloadUv) throw new PlatformNotSupportedException("uv is downloaded for Windows x64 and Linux x64 only; install it from https://docs.astral.sh/uv/.");
        var asset = OperatingSystem.IsWindows() ? "uv-x86_64-pc-windows-msvc.zip" : "uv-x86_64-unknown-linux-gnu.tar.gz";
        var url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{asset}";
        var dir = UvDir(dataRoot);
        var exe = OperatingSystem.IsWindows() ? "uv.exe" : "uv";
        var archivePath = await DownloadVerifiedAsync(url, url + ".sha256", "uv", dataRoot, progress, ct);
        try
        {
            progress?.Report("Unpacking uv…");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, exe);
                if (OperatingSystem.IsWindows())
                {
                    using var archive = ZipFile.OpenRead(archivePath);
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(exe, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("The download doesn't contain uv.exe.");
                    entry.ExtractToFile(target, overwrite: true);
                }
                else
                {
                    using var file = File.OpenRead(archivePath);
                    using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    using var tar = new TarReader(gzip);
                    var found = false;
                    while (tar.GetNextEntry() is { } entry)
                    {
                        if (entry.EntryType != TarEntryType.RegularFile || Path.GetFileName(entry.Name) != exe) continue;
                        entry.ExtractToFile(target, overwrite: true);
                        found = true;
                        break;
                    }
                    if (!found) throw new InvalidDataException("The download doesn't contain uv.");
                    File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }, ct);
        }
        finally
        {
            TryDelete(archivePath);
        }
        return Path.Combine(dir, exe);
    }

    /// <summary>Downloads <paramref name="url"/> into the data folder's tmp with progress, and checks its SHA-256.</summary>
    private static async Task<string> DownloadVerifiedAsync(string url, string sha256Url, string what, string dataRoot,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Downloading {what}…");
        var expected = (await Http.GetStringAsync(sha256Url, ct)).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                       ?? throw new InvalidDataException($"No checksum published for {what}.");

        var tmpDir = Path.Combine(dataRoot, "tmp");
        Directory.CreateDirectory(tmpDir);
        var path = Path.Combine(tmpDir, $"{what}-{Guid.NewGuid():N}{Path.GetExtension(url)}");
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(path))
            {
                var buffer = new byte[256 * 1024];
                long done = 0, reported = -1;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    var mb = done / (1024 * 1024);
                    if (mb == reported) continue;
                    reported = mb;
                    progress?.Report(total is > 0
                        ? $"Downloading {what}… {done * 100 / total.Value} % ({mb} of {total.Value / (1024 * 1024)} MB)"
                        : $"Downloading {what}… {mb} MB");
                }
            }
            progress?.Report($"Checking {what}…");
            string actual;
            await using (var file = File.OpenRead(path))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The {what} download is damaged (checksum mismatch). Please try again.");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LocalAudiobookStudio/{AppVersion.Number}");
        return client;
    }

    private static string ReadUvVersion()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "tools", "uv", "VERSION");
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } v) return v;
        }
        catch (IOException)
        {
        }
        return "0.12.19";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* left in tmp; cleaned another time */ }
    }
}
