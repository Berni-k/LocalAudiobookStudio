using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Workers.Models;

/// <summary>
/// Fills in a models.json entry's <c>files</c> (path, size, SHA-256) from its first source: the Hub's file list
/// at the pinned revision, or a local copy of the model. Used by <c>las models pin</c> when a model is added or its
/// revision moves; the app itself only reads models.json.
/// </summary>
public sealed class ModelPinner
{
    /// <summary>Left out unless an entry's "include" asks for them: documentation and pictures.</summary>
    private static readonly string[] DefaultExclude = [".gitattributes", "*.md", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.mp4", "*.pdf"];

    private readonly HttpClient _http;

    public ModelPinner(HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalAudiobookStudio", "1.0"));
    }

    public string HubEndpoint { get; set; } = "https://huggingface.co";
    public string? Token { get; set; }

    /// <summary>A file of a Hub source: its path, size, SHA-256 (only for files stored with LFS) and git hash.</summary>
    private sealed record RemoteFile(string Path, long Size, string? Sha256, string? GitSha1);

    /// <summary>
    /// Lists the entry's files with their hashes and writes them into <paramref name="node"/> (the entry's object in
    /// models.json). <paramref name="fromFolder"/>: hash a local copy instead of asking the Hub. Returns notes to show.
    /// </summary>
    public async Task<List<string>> PinAsync(ModelEntry entry, JsonObject node, string? fromFolder, Action<string> log, CancellationToken ct)
    {
        var notes = new List<string>();
        var files = new SortedDictionary<string, ModelFileInfo>(StringComparer.Ordinal);
        if (fromFolder != null)
        {
            foreach (var f in Directory.EnumerateFiles(fromFolder, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(fromFolder, f).Replace('\\', '/');
                if (!Wanted(entry, rel) || rel.EndsWith(".partial", StringComparison.Ordinal) || Path.GetFileName(rel) == ModelStore.MarkerName) continue;
                log($"  hashing {rel}");
                // Resolve links (the HF cache links snapshot files to blobs) and hash the real file.
                var real = new FileInfo(f).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? f;
                files[rel] = new ModelFileInfo { Size = new FileInfo(real).Length, Sha256 = await ModelStore.Sha256OfFileAsync(real, ct) };
            }
        }
        else
        {
            var source = entry.Sources.FirstOrDefault(s => s.IsHub)
                         ?? throw new ModelStoreException($"{entry.Id} has no Hugging Face source to pin from (use --from <folder>).");
            // Small files aren't in LFS: the Hub only has their git hash, which is enough to check them.
            foreach (var f in (await ListAsync(source, ct)).Where(f => Wanted(entry, f.Path)))
                files[f.Path] = new ModelFileInfo { Size = f.Size, Sha256 = f.Sha256, GitSha1 = f.Sha256 == null ? f.GitSha1 : null };
            // Other Hub sources should hold the same files; say which differ (they would be skipped when downloading).
            foreach (var other in entry.Sources.Where(s => s.IsHub && s != source))
            {
                try
                {
                    var theirs = (await ListAsync(other, ct)).ToDictionary(f => f.Path);
                    var differ = files.Where(f => !theirs.TryGetValue(f.Key, out var t) || t.Size != f.Value.Size
                                                  || (f.Value.Sha256 != null ? t.Sha256 != f.Value.Sha256 : t.GitSha1 != f.Value.GitSha1))
                        .Select(f => f.Key).ToList();
                    notes.Add(differ.Count == 0 ? $"{other}: same files" : $"{other}: differs in {string.Join(", ", differ.Take(5))}{(differ.Count > 5 ? ", …" : "")}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add($"{other}: could not compare ({ex.Message})");
                }
            }
        }
        if (files.Count == 0) throw new ModelStoreException($"{entry.Id}: no files matched (check \"include\").");

        var obj = new JsonObject();
        foreach (var (path, info) in files)
            obj[path] = info.Sha256 != null
                ? new JsonObject { ["size"] = info.Size, ["sha256"] = info.Sha256 }
                : new JsonObject { ["size"] = info.Size, ["gitSha1"] = info.GitSha1 };
        node["files"] = obj;
        notes.Insert(0, $"{files.Count} file(s), {ModelStore.FormatBytes(files.Values.Sum(f => f.Size))}");
        return notes;
    }

    private async Task<List<RemoteFile>> ListAsync(ModelSource source, CancellationToken ct)
    {
        var result = new List<RemoteFile>();
        string? url = $"{HubEndpoint.TrimEnd('/')}/api/models/{source.Repo}/tree/{Uri.EscapeDataString(source.Revision ?? "main")}?recursive=true";
        while (url != null)
        {
            using var request = Request(url);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ModelStoreException($"Hugging Face refused the file list of {source.Repo}: set a token in Settings, accept the repo's terms, or use --from <folder>.");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "file") continue;
                var path = item.GetProperty("path").GetString()!;
                var size = item.GetProperty("size").GetInt64();
                var git = item.TryGetProperty("oid", out var oid) ? oid.GetString() : null;
                string? sha = null;
                if (item.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                {
                    sha = lfs.GetProperty("oid").GetString();
                    size = lfs.TryGetProperty("size", out var s) ? s.GetInt64() : size;
                }
                result.Add(new RemoteFile(path, size, sha, git));
            }
            url = NextLink(response);
        }
        return result;
    }

    /// <summary>The Hub pages long file lists with a <c>Link: &lt;…&gt;; rel="next"</c> header.</summary>
    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in values.SelectMany(v => v.Split(',')))
        {
            var m = Regex.Match(part, "<([^>]+)>\\s*;\\s*rel=\"next\"");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private HttpRequestMessage Request(string url)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(Token)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return r;
    }

    private static bool Wanted(ModelEntry entry, string path)
    {
        var include = entry.Include is { Count: > 0 } inc ? inc.Any(p => Glob(p, path)) : !DefaultExclude.Any(p => Glob(p, path));
        return include && !(entry.Exclude ?? []).Any(p => Glob(p, path));
    }

    /// <summary>fnmatch-style patterns, like huggingface_hub's allow_patterns: <c>*</c> also matches across folders.</summary>
    public static bool Glob(string pattern, string path) =>
        Regex.IsMatch(path, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$");

    /// <summary>Writes models.json back, one line per file so diffs stay readable.</summary>
    public static void Save(string path, JsonNode root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        json = Regex.Replace(json, "\\{\\s*\"size\": (\\d+),\\s*\"(sha256|gitSha1)\": \"([0-9a-f]+)\"\\s*\\}", "{ \"size\": $1, \"$2\": \"$3\" }");
        // Sources and short lists of strings on one line too.
        json = Regex.Replace(json, "\\{\\s*\"repo\": (\"[^\"]*\"),\\s*\"revision\": (\"[^\"]*\")\\s*\\}", "{ \"repo\": $1, \"revision\": $2 }");
        json = Regex.Replace(json, "\\{\\s*\"url\": (\"[^\"]*\")\\s*\\}", "{ \"url\": $1 }");
        json = Regex.Replace(json, "\\[\\s*(\"[^\"\\n]*\"(?:,\\s*\"[^\"\\n]*\")*)\\s*\\]", m => "[" + Regex.Replace(m.Groups[1].Value, ",\\s*", ", ") + "]");
        File.WriteAllText(path, json.Replace("\r\n", "\n") + "\n");
    }
}
