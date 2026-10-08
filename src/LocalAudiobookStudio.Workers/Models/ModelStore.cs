using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LocalAudiobookStudio.Workers.Protocol;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Workers.Models;

public enum ModelStatus
{
    NotDownloaded,
    /// <summary>Some files are there (an interrupted download), or they haven't been checked against models.json.</summary>
    Partial,
    Ready,
    /// <summary>The user points this model at a folder of their own.</summary>
    Override,
}

/// <summary>
/// Where a worker looks for a model in its own folders (HartsyInference). Without <see cref="File"/> the folder
/// <see cref="Target"/> becomes a link to the model's folder (a junction on Windows); with it that one file becomes
/// a hard link of the model's file.
/// </summary>
public sealed record ModelLink(string Target, string? File = null);

/// <summary>A model ready to load: its folder and the source its files came from (null for an override).</summary>
public sealed record ModelLocation(string Path, ModelSource? Source, bool IsOverride);

/// <summary>A model couldn't be provided; the message says why and what to do.</summary>
public sealed class ModelStoreException(string message) : Exception(message);

/// <summary>
/// The app's model folders (<c>models/store/&lt;id&gt;</c>). Workers ask for a model when they need it
/// (<c>needModel</c>); the store downloads what's missing, checks every file against models.json and answers
/// with the folder. One request is served at a time, in order, so two workers never fetch the same files at once.
/// A marker in each folder records the checked files (size and time), so later requests don't hash gigabytes again.
/// </summary>
public sealed class ModelStore
{
    public const string MarkerName = ".las-model.json";
    private const string PartialSuffix = ".partial";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    /// <param name="root">The store folder (one folder per model).</param>
    /// <param name="linkRoot">Links are only made, and deleted with a model, inside this folder (the models folder).</param>
    public ModelStore(ModelCatalog catalog, string root, string linkRoot, ILogger? logger = null, HttpMessageHandler? handler = null)
    {
        Catalog = catalog;
        Root = Path.GetFullPath(root);
        LinkRoot = Path.GetFullPath(linkRoot);
        _logger = logger;
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });
        _http.Timeout = Timeout.InfiniteTimeSpan; // big files; stalls are caught per read instead
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalAudiobookStudio", "1.0"));
    }

    public ModelCatalog Catalog { get; }
    public string Root { get; }
    public string LinkRoot { get; }

    /// <summary>The Hugging Face access token (Settings), or null.</summary>
    public Func<string?>? Token { get; set; }

    /// <summary>The user's own folder for a model (Workers window), or null.</summary>
    public Func<string, string?>? Override { get; set; }

    public string HubEndpoint { get; set; } = "https://huggingface.co";

    /// <summary>How long a download may receive nothing before it counts as stalled.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Raised after a model was downloaded, completed or deleted (its id). Raised on a worker thread.</summary>
    public event Action<string>? Changed;

    public string FolderOf(string id) => Path.Combine(Root, id);

    // ------------------------------------------------------------------ worker requests

    /// <summary>Answers a worker's <c>needModel</c> or <c>needFile</c> with the matching reply message.</summary>
    public async Task<WorkerMessage> HandleRequestAsync(WorkerMessage request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var isFile = request.Type == MessageTypes.NeedFile;
        try
        {
            var id = request.Model ?? throw new ModelStoreException("The request names no model.");
            if (isFile)
            {
                var path = await EnsureFileAsync(id, request.File ?? throw new ModelStoreException("The request names no file."), progress, ct).ConfigureAwait(false);
                return new WorkerMessage { Type = MessageTypes.FileReady, Ref = request.Ref, Model = id, Path = path };
            }
            var links = new List<ModelLink>();
            if (!string.IsNullOrEmpty(request.LinkTo)) links.Add(new ModelLink(request.LinkTo));
            if (request.Links != null) links.AddRange(request.Links.Select(l => new ModelLink(l.Value, l.Key)));
            var location = await EnsureAsync(id, links, progress, ct).ConfigureAwait(false);
            return new WorkerMessage { Type = MessageTypes.ModelReady, Ref = request.Ref, Model = id, Path = location.Path };
        }
        catch (OperationCanceledException)
        {
            return new WorkerMessage { Type = isFile ? MessageTypes.FileFailed : MessageTypes.ModelFailed, Ref = request.Ref, Model = request.Model, Message = "cancelled", Cancelled = true };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Model request {Type} {Model} {File} failed: {Message}", request.Type, request.Model, request.File, ex.Message);
            return new WorkerMessage { Type = isFile ? MessageTypes.FileFailed : MessageTypes.ModelFailed, Ref = request.Ref, Model = request.Model, Message = ex.Message };
        }
    }

    // ------------------------------------------------------------------ models

    /// <summary>
    /// Makes sure a model (and the models it requires) is downloaded and checked, links it where asked, and
    /// returns its folder. Waits for any other request first.
    /// </summary>
    public async Task<ModelLocation> EnsureAsync(string id, IReadOnlyList<ModelLink>? links = null, IProgress<WorkerProgress>? progress = null, CancellationToken ct = default)
    {
        var entry = Catalog.Get(id) ?? throw new ModelStoreException($"Unknown model '{id}': it is not in workers/{ModelCatalog.FileName}.");
        await WaitTurnAsync(entry.Name, progress, ct).ConfigureAwait(false);
        try
        {
            return await EnsureLockedAsync(entry, links ?? [], progress, ct, depth: 0).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(entry.Id);
        }
    }

    private async Task<ModelLocation> EnsureLockedAsync(ModelEntry entry, IReadOnlyList<ModelLink> links, IProgress<WorkerProgress>? progress, CancellationToken ct, int depth)
    {
        if (depth > 8) throw new ModelStoreException($"{entry.Id}: the models it requires require each other in a circle.");
        foreach (var required in entry.Requires)
        {
            var r = Catalog.Get(required) ?? throw new ModelStoreException($"{entry.Id} requires '{required}', which is not in {ModelCatalog.FileName}.");
            await EnsureLockedAsync(r, [], progress, ct, depth + 1).ConfigureAwait(false);
        }

        var folder = FolderOf(entry.Id);
        var marker = ReadMarker(folder);
        if (OverrideFolder(entry.Id) is { } own)
        {
            if (links.Count > 0)
            {
                Directory.CreateDirectory(folder);
                marker ??= new ModelMarker { Id = entry.Id };
                ApplyLinks(entry, own, links, marker, onlyExisting: true);
                WriteMarker(folder, marker);
            }
            return new ModelLocation(own, null, IsOverride: true);
        }

        if (entry.Files.Count == 0 && entry.Sources.Any(s => s.IsHub))
            throw new ModelStoreException($"{entry.Name} has no file list in {ModelCatalog.FileName} yet: run 'las models pin {entry.Id}'.");
        Directory.CreateDirectory(folder);
        var filesHash = entry.FilesHash();
        var trusted = marker?.FilesHash == filesHash ? marker.Verified : null;
        var verified = new Dictionary<string, MarkerFile>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (path, info) in entry.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(PathIn(folder, path));
            if (!file.Exists) { missing.Add(path); continue; }
            if (trusted != null && trusted.TryGetValue(path, out var known) && known.Matches(file)) { verified[path] = known; continue; }
            // Not checked yet (copied in, or models.json changed): hash it once.
            progress?.Report(new WorkerProgress(0, $"Checking {entry.Name}: {path}"));
            if (file.Length == info.Size && await HashOfFileAsync(file.FullName, info, ct).ConfigureAwait(false) == info.Expected)
            {
                verified[path] = MarkerFile.Of(file);
                continue;
            }
            _logger?.LogInformation("{Model}: {File} doesn't match models.json; downloading it again", entry.Id, path);
            File.Delete(file.FullName);
            missing.Add(path);
        }

        var source = marker?.Source;
        if (missing.Count > 0)
        {
            var total = missing.Sum(p => entry.Files[p].Size);
            var done = 0L;
            _logger?.LogInformation("{Model}: downloading {Count} file(s), {Size}", entry.Id, missing.Count, FormatBytes(total));
            foreach (var path in missing)
            {
                var info = entry.Files[path];
                var start = done;
                var report = Throttled(bytes => progress?.Report(new WorkerProgress(
                    total > 0 ? Math.Clamp((start + bytes) / (double)total, 0, 1) : 0,
                    $"Downloading {entry.Name}: {FormatBytes(start + bytes)} of {FormatBytes(total)}")));
                source = await DownloadVerifiedAsync(entry, path, info, PathIn(folder, path), report, ct).ConfigureAwait(false);
                done += info.Size;
                verified[path] = MarkerFile.Of(new FileInfo(PathIn(folder, path)));
            }
            _logger?.LogInformation("{Model}: download complete", entry.Id);
        }

        marker ??= new ModelMarker { Id = entry.Id };
        marker.FilesHash = filesHash;
        marker.Verified = verified;
        marker.Source = source ?? entry.Sources.FirstOrDefault();
        ApplyLinks(entry, folder, links, marker, onlyExisting: false);
        WriteMarker(folder, marker);
        return new ModelLocation(folder, marker.Source, IsOverride: false);
    }

    // ------------------------------------------------------------------ extra files

    /// <summary>
    /// A file of a model that models.json doesn't list (a voice): downloaded into the model's folder from its
    /// sources, unchecked, and deleted with the folder. Waits for any other request first.
    /// </summary>
    public async Task<string> EnsureFileAsync(string id, string file, IProgress<WorkerProgress>? progress = null, CancellationToken ct = default)
    {
        var entry = Catalog.Get(id) ?? throw new ModelStoreException($"Unknown model '{id}': it is not in workers/{ModelCatalog.FileName}.");
        if (!ModelCatalog.IsSafeRelativePath(file)) throw new ModelStoreException($"Invalid file name '{file}'.");
        if (OverrideFolder(id) is { } own && File.Exists(PathIn(own, file))) return PathIn(own, file);
        var target = PathIn(FolderOf(id), file);
        if (File.Exists(target)) return target;

        await WaitTurnAsync(entry.Name, progress, ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target)) return target; // fetched while this request waited
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var marker = ReadMarker(FolderOf(id));
            // The source the model came from first: its extra files belong to the same version.
            var sources = (marker?.Source is { } used ? new[] { used } : []).Concat(entry.Sources.Where(s => !SameSource(s, marker?.Source))).ToList();
            var errors = new List<string>();
            var report = Throttled(bytes => progress?.Report(new WorkerProgress(0, $"Downloading {file} ({FormatBytes(bytes)})")));
            foreach (var source in sources)
            {
                try
                {
                    await DownloadAsync(source, file, target + PartialSuffix, report, hash: null, ct).ConfigureAwait(false);
                    File.Move(target + PartialSuffix, target, overwrite: true);
                    return target;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{source}: {ex.Message}");
                    TryDelete(target + PartialSuffix);
                }
            }
            throw new ModelStoreException($"Could not download {file} for {entry.Name}. {string.Join(" ", errors)}");
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(id);
        }
    }

    // ------------------------------------------------------------------ status, size, delete

    /// <summary>What's on disk, without hashing: the marker's record of each file's size and time.</summary>
    public ModelStatus GetStatus(string id)
    {
        if (OverrideFolder(id) != null) return ModelStatus.Override;
        var entry = Catalog.Get(id);
        var folder = FolderOf(id);
        if (entry == null || !Directory.Exists(folder)) return ModelStatus.NotDownloaded;
        if (entry.Files.Count == 0) return ModelStatus.Ready;
        var marker = ReadMarker(folder);
        if (marker?.FilesHash == entry.FilesHash()
            && entry.Files.Keys.All(p => marker.Verified.TryGetValue(p, out var r) && r.Matches(new FileInfo(PathIn(folder, p)))))
            return ModelStatus.Ready;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => Path.GetFileName(f) != MarkerName)
            ? ModelStatus.Partial : ModelStatus.NotDownloaded;
    }

    /// <summary>
    /// Bytes on disk: the model's folder (with what workers wrote into it through a folder link, like HartsyInference's
    /// converted weights), plus what they added to linked folders that are real folders (drives without junctions).
    /// Linked files are the same data and count once. Walks folders: call off the UI thread.
    /// </summary>
    public long SizeOnDisk(string id)
    {
        var folder = FolderOf(id);
        var total = Environments.WorkerDiskUsage.FolderSize(folder);
        var entry = Catalog.Get(id);
        foreach (var link in ReadMarker(folder)?.Links ?? [])
        {
            if (link.File != null || !Directory.Exists(link.Target) || DirectoryLink.IsLink(link.Target)) continue;
            foreach (var f in new DirectoryInfo(link.Target).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var rel = Path.GetRelativePath(link.Target, f.FullName).Replace('\\', '/');
                if (entry != null && entry.Files.TryGetValue(rel, out var info) && info.Size == f.Length) continue;
                total += f.Length;
            }
        }
        return total;
    }

    /// <summary>The folders a model is linked into (HartsyInference's), for display.</summary>
    public IReadOnlyList<string> LinkedFolders(string id) =>
        ReadMarker(FolderOf(id))?.Links.Where(l => l.File == null).Select(l => l.Target).ToList() ?? [];

    /// <summary>
    /// Deletes a model's folder and every link made to it (and what workers added to a linked folder). Stop the workers
    /// that use it first: Windows can't delete files a running worker has open.
    /// </summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var folder = FolderOf(id);
            var marker = ReadMarker(folder);
            try
            {
                foreach (var link in marker?.Links ?? [])
                {
                    var target = Path.GetFullPath(link.Target);
                    if (!IsInside(target, LinkRoot)) continue; // never delete outside the models folder
                    // A folder link goes first and alone, so deleting never walks through it.
                    if (link.File != null) TryDelete(target);
                    else if (DirectoryLink.IsLink(target)) DirectoryLink.Remove(target);
                    else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                }
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModelStoreException($"Could not delete every file of {id} ({ex.Message}). Is a worker still using it?");
            }
            _logger?.LogInformation("{Model}: deleted", id);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(id);
        }
    }

    // ------------------------------------------------------------------ downloading

    private async Task WaitTurnAsync(string name, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        if (await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        progress?.Report(new WorkerProgress(0, $"Waiting for another download before {name}…"));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Downloads one listed file, trying the sources in order until one delivers the right bytes.</summary>
    private async Task<ModelSource> DownloadVerifiedAsync(ModelEntry entry, string path, ModelFileInfo info, string target, Action<long> report, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + PartialSuffix;
        var errors = new List<string>();
        foreach (var source in entry.Sources)
        {
            try
            {
                var hash = await DownloadAsync(source, path, partial, report, info.NewHash(), ct).ConfigureAwait(false);
                var size = new FileInfo(partial).Length;
                if (size != info.Size || hash != info.Expected)
                {
                    TryDelete(partial);
                    throw new IOException($"the file differs from the pinned version (size {size}, expected {info.Size})");
                }
                File.Move(partial, target, overwrite: true);
                return source;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{source}: {ex.Message}");
                _logger?.LogWarning("{Model}: {File} from {Source} failed: {Message}", entry.Id, path, source, ex.Message);
            }
        }
        throw new ModelStoreException($"Could not download {entry.Name} ({path}). {string.Join(" ", errors)}");
    }

    /// <summary>
    /// Downloads <paramref name="path"/> from one source into <paramref name="partial"/>, resuming what's there.
    /// Returns the SHA-256 (lowercase hex) when <paramref name="hash"/> is given, else "".
    /// </summary>
    private async Task<string> DownloadAsync(ModelSource source, string path, string partial, Action<long> report, IncrementalHash? hash, CancellationToken ct)
    {
        using var _ = hash;
        for (var attempt = 0; ; attempt++)
        {
            var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, UrlOf(source, path));
            if (source.IsHub && Token?.Invoke() is { Length: > 0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && attempt == 0)
            {
                TryDelete(partial); // a stale or complete partial file: start over once
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && source.IsHub)
                throw new ModelStoreException(
                    $"Hugging Face refused the download of {source.Repo} ({(int)response.StatusCode}). Open https://huggingface.co/{source.Repo}, " +
                    "accept its terms with your account, then put a read-only access token in Settings → Hugging Face token.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("not found (404)");
            response.EnsureSuccessStatusCode();

            var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (hash != null && append) await HashExistingAsync(hash, partial, ct).ConfigureAwait(false);
            await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 20];
                var received = append ? have : 0;
                report(received);
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"the download stalled (nothing received for {StallTimeout.TotalSeconds:0} s)");
                    }
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash?.AppendData(buffer, 0, read);
                    received += read;
                    report(received);
                }
            }
            return hash != null ? Convert.ToHexStringLower(hash.GetHashAndReset()) : "";
        }
    }

    private static async Task HashExistingAsync(IncrementalHash hash, string file, CancellationToken ct)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
    }

    private static async Task<string> HashOfFileAsync(string file, ModelFileInfo info, CancellationToken ct)
    {
        using var hash = info.NewHash();
        await HashExistingAsync(hash, file, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static async Task<string> Sha256OfFileAsync(string file, CancellationToken ct)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }

    private string UrlOf(ModelSource source, string path)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return source.IsHub
            ? $"{HubEndpoint.TrimEnd('/')}/{source.Repo}/resolve/{Uri.EscapeDataString(source.Revision ?? "main")}/{escaped}"
            : (source.Url ?? throw new ModelStoreException("A model source has neither a repo nor a URL.")).Replace("{file}", escaped);
    }

    /// <summary>Calls <paramref name="report"/> at most four times a second (and for the first value).</summary>
    private static Action<long> Throttled(Action<long> report)
    {
        var last = DateTime.MinValue;
        return bytes =>
        {
            var now = DateTime.UtcNow;
            if (now - last < TimeSpan.FromMilliseconds(250)) return;
            last = now;
            report(bytes);
        };
    }

    // ------------------------------------------------------------------ links

    /// <summary>
    /// Puts the model where a worker looks for it: a whole folder becomes a link to the model's folder (a junction on
    /// Windows), so what the worker writes there lands in the store; a single file becomes a hard link. Where the drive
    /// can't do either, the files are hard-linked or copied one by one.
    /// </summary>
    private void ApplyLinks(ModelEntry entry, string sourceFolder, IReadOnlyList<ModelLink> links, ModelMarker marker, bool onlyExisting)
    {
        foreach (var link in links)
        {
            var target = Path.GetFullPath(link.Target);
            if (!IsInside(target, LinkRoot))
                throw new ModelStoreException($"Refusing to link {entry.Id} to {target}: links must stay inside {LinkRoot}.");
            if (link.File != null)
            {
                if (!ModelCatalog.IsSafeRelativePath(link.File)) throw new ModelStoreException($"Invalid file name '{link.File}'.");
                var source = PathIn(sourceFolder, link.File);
                if (!File.Exists(source))
                {
                    if (onlyExisting) continue;
                    throw new ModelStoreException($"{entry.Id} has no file '{link.File}' to link.");
                }
                LinkFile(source, target);
            }
            else
            {
                LinkFolder(entry, sourceFolder, target, adoptExtras: !onlyExisting);
            }
            if (!marker.Links.Any(l => string.Equals(Path.GetFullPath(l.Target), target, StringComparison.OrdinalIgnoreCase) && l.File == link.File))
                marker.Links.Add(new MarkerLink { Target = target, File = link.File });
        }
    }

    /// <summary>
    /// Makes <paramref name="target"/> a link to <paramref name="source"/>. A real folder already there (the worker's own
    /// earlier download) is replaced; files the store doesn't have (converted weights, voices) move into the model's
    /// folder first, unless that is the user's own folder (<paramref name="adoptExtras"/> false).
    /// </summary>
    private void LinkFolder(ModelEntry entry, string source, string target, bool adoptExtras)
    {
        if (SamePath(source, target) || IsInside(target, Root) || IsInside(Root, target))
            throw new ModelStoreException($"Refusing to link {entry.Id} to {target}: it overlaps the model store.");
        if (DirectoryLink.IsLink(target))
        {
            if (DirectoryLink.TargetOf(target) is { } now && SamePath(now, source)) return;
            DirectoryLink.Remove(target);
        }
        else if (Directory.Exists(target))
        {
            foreach (var f in new DirectoryInfo(target).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var rel = Path.GetRelativePath(target, f.FullName).Replace('\\', '/');
                if (!adoptExtras || entry.Files.ContainsKey(rel) || rel.EndsWith(PartialSuffix, StringComparison.Ordinal)) continue;
                var dest = PathIn(source, rel);
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(f.FullName, dest);
                _logger?.LogInformation("{Model}: moved {File} from {Folder} into the store", entry.Id, rel, target);
            }
            Directory.Delete(target, recursive: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (DirectoryLink.TryCreate(target, source)) return;
        _logger?.LogInformation("Could not link the folder {Target}; linking its files one by one instead", target);
        foreach (var path in entry.Files.Keys)
        {
            var file = PathIn(source, path);
            if (File.Exists(file)) LinkFile(file, PathIn(target, path));
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void LinkFile(string source, string target)
    {
        var src = new FileInfo(source);
        var dst = new FileInfo(target);
        // A hard link has the same size and time as its source; so does a copy, which gets the source's time.
        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (dst.Exists) File.Delete(target);
        if (HardLink.TryCreate(source, target)) return;
        _logger?.LogInformation("Could not hard-link {Target}; copying it instead (another drive, or the file system has no hard links)", target);
        var tmp = target + ".las-copy";
        File.Copy(source, tmp, overwrite: true);
        File.SetLastWriteTimeUtc(tmp, src.LastWriteTimeUtc);
        File.Move(tmp, target, overwrite: true);
    }

    // ------------------------------------------------------------------ helpers

    private string? OverrideFolder(string id) =>
        Override?.Invoke(id) is { Length: > 0 } f && Directory.Exists(f) ? Path.GetFullPath(f) : null;

    private static string PathIn(string folder, string relative) => Path.Combine([folder, .. relative.Split('/')]);

    private static bool IsInside(string path, string folder)
    {
        var rel = Path.GetRelativePath(folder, path);
        return rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }

    private static bool SameSource(ModelSource a, ModelSource? b) =>
        b != null && a.Repo == b.Repo && a.Revision == b.Revision && a.Url == b.Url;

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string FormatBytes(long bytes) => Environments.WorkerDiskUsage.Format(bytes);

    private static ModelMarker? ReadMarker(string folder)
    {
        var path = Path.Combine(folder, MarkerName);
        if (!File.Exists(path)) return null;
        try { return WorkersJson.Deserialize<ModelMarker>(File.ReadAllText(path)); }
        catch { return null; } // a broken marker only means the files get checked again
    }

    private static void WriteMarker(string folder, ModelMarker marker)
    {
        var path = Path.Combine(folder, MarkerName);
        File.WriteAllText(path + ".tmp", WorkersJson.Serialize(marker, indented: true));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>The marker in a model folder: which files were checked (by size and time), the source used, and the links made.</summary>
internal sealed class ModelMarker
{
    public string Id { get; set; } = "";
    public string FilesHash { get; set; } = "";
    public ModelSource? Source { get; set; }
    public Dictionary<string, MarkerFile> Verified { get; set; } = new(StringComparer.Ordinal);
    public List<MarkerLink> Links { get; set; } = new();
}

internal sealed class MarkerFile
{
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }

    public bool Matches(FileInfo f) => f.Exists && f.Length == Size && f.LastWriteTimeUtc.Ticks == ModifiedTicks;

    public static MarkerFile Of(FileInfo f) => new() { Size = f.Length, ModifiedTicks = f.LastWriteTimeUtc.Ticks };
}

internal sealed class MarkerLink
{
    public string Target { get; set; } = "";
    public string? File { get; set; }
}
