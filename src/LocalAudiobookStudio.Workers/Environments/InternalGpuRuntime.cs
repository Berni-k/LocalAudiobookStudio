using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LocalAudiobookStudio.Workers.Environments;

/// <summary>
/// The internal worker's GPU runtime (<c>internal-gpu</c>): the GPU builds its engines download on install. The bundled
/// worker (tools/internal-worker/) has only CPU builds; each engine lists here what it adds for the GPU, and gets it in its own
/// sub-folder of the runtime's shared folder (<c>envs/internal-gpu/&lt;engine&gt;</c>), so engines install and update
/// independently. NVIDIA's own libraries (cudart, cuBLAS, cuDNN) come from the user's CUDA toolkit on the PATH, as for
/// HartsyInference (<see cref="CudaToolkit"/>). Every package is pinned by NuGet's SHA-512.
/// </summary>
/// <remarks>
/// whisper: Whisper.net's Vulkan build and the CUDA 13 or 12 build for the toolkit found, unpacked to
/// <c>whisper/runtimes/&lt;backend&gt;/&lt;rid&gt;</c>, where Whisper.net looks (the worker gets the runtime's folder with
/// <c>--gpu-folder</c>). Installing or changing the toolkit later changes <see cref="Fingerprint"/>: the runtime shows an
/// update, and Repair fetches the matching build. tdt: none yet (ONNX Runtime on the CPU).
/// </remarks>
public static class InternalGpuRuntime
{
    /// <summary>Must match Whisper.net in Directory.Packages.props: the GPU builds have to fit the bundled worker.</summary>
    public const string WhisperNetVersion = "1.9.1";

    /// <summary>
    /// The internal worker's engines (a manifest's "engine"), named for the kind of model they run: "whisper" (any ggml
    /// Whisper model on whisper.cpp), "tdt" (NeMo TDT transducers such as Parakeet, exported to ONNX). See
    /// InternalWorker/Engines.cs.
    /// </summary>
    public static readonly IReadOnlyList<string> Engines = ["whisper", "tdt", "ctc-align", "speaker-embed", "pyannote"];

    private const string MarkerFile = ".installed";

    /// <summary>A pinned NuGet package and what to unpack from it: entries under <see cref="From"/> into <see cref="To"/>.</summary>
    private sealed record Package(string Name, string Id, string Version, long Size, string Sha512, string From, string To);

    private static string Rid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    /// <summary>Whether an engine has GPU builds to download (otherwise it has no internal-gpu runtime).</summary>
    public static bool HasGpuBuilds(string engine) => engine == "whisper";

    /// <summary>What this machine downloads for an engine.</summary>
    private static IReadOnlyList<Package> Packages(string engine) => engine switch
    {
        "whisper" => WhisperCuda(CudaToolkit.MajorVersion()) is { } cuda ? [WhisperVulkan, cuda] : [WhisperVulkan],
        _ => [],
    };

    private static Package WhisperVulkan => new("whisper.cpp Vulkan build", "Whisper.net.Runtime.Vulkan", WhisperNetVersion, 36_867_136,
        "hRYbrj76y09g38wZccGb3DvNiempYMq6Zf6FX/gKzxjS2Ff1JeTZ/XSSuJ0UgU13q/TAsD/Gl/Ctu0PmNcFjdA==", $"build/{Rid}/", $"runtimes/vulkan/{Rid}");

    /// <summary>whisper.cpp's CUDA build for a toolkit's major version (Whisper.net looks in runtimes/cuda for 13, runtimes/cuda12 for 12).</summary>
    private static Package? WhisperCuda(int? major) => (major, OperatingSystem.IsWindows()) switch
    {
        (13, true) => new("whisper.cpp CUDA 13 build", "Whisper.net.Runtime.Cuda.Windows", WhisperNetVersion, 142_586_522,
            "J7ZyOZmhgrZCZLGNKNoIxWGcDQYYTsqiDYaHTqAiy/Yo3wN/7UAXqPEA9IICmGwp8X0WZBNkcD86934yALgEZQ==", "build/win-x64/", "runtimes/cuda/win-x64"),
        (13, false) => new("whisper.cpp CUDA 13 build", "Whisper.net.Runtime.Cuda.Linux", WhisperNetVersion, 143_915_614,
            "yDHwzV6pfAxtaJkKOVdW5hJ+81wc7eGS53ZsOhRaw8SE7ZAG+zqwJb4tI1h1rvCd15IRAn6W18c9aL3V7oMK1g==", "build/linux-x64/", "runtimes/cuda/linux-x64"),
        (12, true) => new("whisper.cpp CUDA 12 build", "Whisper.net.Runtime.Cuda12.Windows", WhisperNetVersion, 250_058_371,
            "pzvo217y8nHPcZav82zJ/uHstnsCFpNn6dodrOi2WjwY3j7xyPcBBhGXeRvvpRuZiRY0cuT8BETcY9nKGqVOZw==", "build/win-x64/", "runtimes/cuda12/win-x64"),
        (12, false) => new("whisper.cpp CUDA 12 build", "Whisper.net.Runtime.Cuda12.Linux", WhisperNetVersion, 251_554_140,
            "3tnwpgjZX0ZWiYUe8S9mSVGC/elDsK/lDAnr5fq+Glye19rfqmbZX+hRV5JFr2qKWF2OueTzarcaQxfmkBo0QA==", "build/linux-x64/", "runtimes/cuda12/linux-x64"),
        _ => null,
    };

    /// <summary>An engine's download size on this machine, for messages.</summary>
    public static long DownloadBytes(string engine) => Packages(engine).Sum(p => p.Size);

    /// <summary>The engine's folder inside the runtime's folder.</summary>
    public static string EngineFolder(string runtimeFolder, string engine) => Path.Combine(runtimeFolder, engine);

    /// <summary>Whether the runtime's folder holds the GPU builds this machine needs for the engine (its install finished).</summary>
    public static bool IsInstalled(string runtimeFolder, string engine)
    {
        var marker = Path.Combine(EngineFolder(runtimeFolder, engine), MarkerFile);
        return File.Exists(marker) && File.ReadAllText(marker).Trim() == Fingerprint(engine);
    }

    /// <summary>Identifies an engine's builds: a new version or another CUDA toolkit makes its install out of date.</summary>
    public static string Fingerprint(string engine) =>
        engine + ":" + string.Join(",", Packages(engine).Select(p => $"{p.Id}/{p.Version}"));

    /// <summary>
    /// Downloads and unpacks an engine's GPU builds, unless they are already there. Packages go through
    /// <paramref name="tmpDir"/> and are checked against their SHA-512 before anything is unpacked.
    /// </summary>
    public static async Task InstallAsync(string runtimeFolder, string engine, string tmpDir, Action<string> log, CancellationToken ct)
    {
        if (IsInstalled(runtimeFolder, engine))
        {
            log($"== The GPU builds for {engine} are already downloaded.");
            return;
        }
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException("The GPU runtime is available for Windows and Linux on x64.");

        var major = CudaToolkit.MajorVersion();
        log(major is 12 or 13
            ? $"== Found the CUDA {major} toolkit"
            : "== No CUDA 12 or 13 toolkit found (cudart on the PATH): no CUDA builds. For CUDA on an NVIDIA GPU, install the " +
              "CUDA toolkit, then Repair this runtime.");
        var packages = Packages(engine);
        log($"== Downloading the GPU builds for {engine}: {string.Join(", ", packages.Select(p => p.Name))} ({packages.Sum(p => p.Size) / (1024 * 1024)} MB)");
        var folder = EngineFolder(runtimeFolder, engine);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(tmpDir);

        foreach (var package in packages)
        {
            var archive = Path.Combine(tmpDir, $"internal-gpu-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(package, archive, log, ct).ConfigureAwait(false);
                log($"   unpacking {package.Name}");
                Unpack(archive, package, folder);
            }
            finally
            {
                try { File.Delete(archive); } catch { /* tmp is cleaned another time */ }
            }
        }
        File.WriteAllText(Path.Combine(folder, MarkerFile), Fingerprint(engine));
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalAudiobookStudio", "1.0"));
        return client;
    }

    private static async Task DownloadAsync(Package package, string path, Action<string> log, CancellationToken ct)
    {
        log($"   downloading {package.Name} ({package.Size / (1024 * 1024)} MB)");
        var id = package.Id.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{package.Version}/{id}.{package.Version}.nupkg";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[1024 * 1024];
            long done = 0;
            var nextReport = package.Size / 5;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, n);
                done += n;
                if (done < nextReport || done >= package.Size) continue;
                log($"   {package.Name}: {done * 100 / package.Size} %");
                nextReport += package.Size / 5;
            }
        }
        if (Convert.ToBase64String(hash.GetHashAndReset()) != package.Sha512)
            throw new InvalidDataException($"The download of {package.Name} is damaged (checksum mismatch). Please try again.");
    }

    /// <summary>The files under the package's <c>From</c> folder go into its <c>To</c> folder.</summary>
    private static void Unpack(string archive, Package package, string folder)
    {
        var target = Path.Combine(folder, package.To);
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(archive);
        var unpacked = 0;
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(package.From, StringComparison.Ordinal) || entry.Name.Length == 0) continue;
            entry.ExtractToFile(Path.Combine(target, entry.Name), overwrite: true);
            unpacked++;
        }
        if (unpacked == 0) throw new InvalidDataException($"{package.Name} contains no libraries for this platform.");
    }
}

/// <summary>
/// The NVIDIA CUDA toolkit on this machine, as GPU builds load it: its runtime library (cudart) by name from the PATH
/// (Windows) or the library path (Linux). The toolkit's own folder (CUDA_PATH, /usr/local/cuda) counts too, and is added
/// to a worker's search path (<see cref="LibraryFolders"/>), in case its installer didn't put it on the PATH. So is
/// cuDNN's (<see cref="CudnnFolder"/>), whose installer never does.
/// </summary>
public static class CudaToolkit
{
    /// <summary>13 or 12 when that toolkit's runtime is found (13 first), otherwise null.</summary>
    public static int? MajorVersion()
    {
        foreach (var major in new[] { 13, 12 })
        {
            var name = OperatingSystem.IsWindows() ? $"cudart64_{major}.dll" : $"libcudart.so.{major}";
            if (SearchFolders().Any(dir => File.Exists(Path.Combine(dir, name)))) return major;
        }
        return null;
    }

    /// <summary>The toolkit's and cuDNN's library folders that may not be on the search path: added for the GPU builds.</summary>
    public static IEnumerable<string> LibraryFolders() =>
        CudnnFolder() is { } cudnn ? [.. ToolkitFolders(), cudnn] : ToolkitFolders();

    private static IEnumerable<string> ToolkitFolders()
    {
        if (Environment.GetEnvironmentVariable("CUDA_PATH") is { Length: > 0 } cuda)
        {
            yield return Path.Combine(cuda, "bin");
            yield return Path.Combine(cuda, "bin", "x64"); // CUDA 13 on Windows keeps its DLLs here
            yield return Path.Combine(cuda, "lib64");
        }
        if (OperatingSystem.IsLinux()) yield return "/usr/local/cuda/lib64";
    }

    /// <summary>
    /// Windows: the folder of the cuDNN 9 build HartsyInference can use, which cuDNN's installer puts under
    /// Program Files\NVIDIA\CUDNN\v9.x\bin\&lt;cuda&gt;[\x64] (one per CUDA version) without adding it to the PATH.
    /// The build must be for the toolkit's CUDA major version (it needs that toolkit's cuBLAS; another crashes the
    /// worker) and the driver's (HartsyInference refuses a mismatch, which can hang it). Null when none fits.
    /// </summary>
    public static string? CudnnFolder()
    {
        if (MajorVersion() is not int toolkit || DriverMajorVersion() is { } driver && driver != toolkit) return null;
        return CudnnBuilds().FirstOrDefault(b => b.CudaMajor == toolkit).Folder;
    }

    /// <summary>The cuDNN 9 builds installed under Program Files\NVIDIA\CUDNN (Windows), newest first.</summary>
    public static IReadOnlyList<(string Folder, int CudaMajor)> CudnnBuilds()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA", "CUDNN");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root, "v9.*")
                .OrderByDescending(d => FolderVersion(d, 1))
                .Where(d => Directory.Exists(Path.Combine(d, "bin")))
                .SelectMany(d => Directory.EnumerateDirectories(Path.Combine(d, "bin")).OrderByDescending(b => FolderVersion(b, 0)))
                .SelectMany(b => new[] { (Folder: Path.Combine(b, "x64"), CudaMajor: FolderVersion(b, 0).Major), (Folder: b, CudaMajor: FolderVersion(b, 0).Major) })
                .Where(b => File.Exists(Path.Combine(b.Folder, "cudnn64_9.dll")))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The CUDA major version the NVIDIA driver supports (13 for 13.3), or null without a driver.</summary>
    public static int? DriverMajorVersion()
    {
        try
        {
            return CuDriverGetVersion(out var version) == 0 && version > 0 ? version / 1000 : null;
        }
        catch
        {
            return null; // no NVIDIA driver
        }
    }

    [System.Runtime.InteropServices.DllImport("nvcuda.dll", EntryPoint = "cuDriverGetVersion")]
    private static extern int CuDriverGetVersion(out int version);

    // "v9.26" (skip 1) or "12.9" as a version, for picking the newest folder.
    private static Version FolderVersion(string dir, int skip) =>
        Version.TryParse(Path.GetFileName(dir)[skip..], out var v) ? v : new Version();

    private static IEnumerable<string> SearchFolders()
    {
        var variable = OperatingSystem.IsWindows() ? "PATH" : "LD_LIBRARY_PATH";
        var dirs = (Environment.GetEnvironmentVariable(variable) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim('"')).Concat(ToolkitFolders());
        if (OperatingSystem.IsLinux()) dirs = dirs.Concat(["/usr/lib/x86_64-linux-gnu", "/usr/lib64"]);
        return dirs.Where(d => d.Length > 0).Distinct();
    }
}
