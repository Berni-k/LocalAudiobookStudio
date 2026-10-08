using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Workers.Models;

/// <summary>
/// Contents of <c>workers/models.json</c>: every model a worker can ask the app for, by id. The app downloads
/// a model into its own folder in the store (<c>models/store/&lt;id&gt;</c>) when a worker first asks for it.
/// </summary>
public sealed class ModelsFile
{
    public Dictionary<string, ModelEntry> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One model: where to get it and exactly which files make it up.</summary>
public sealed class ModelEntry
{
    /// <summary>The key in models.json (set when loading).</summary>
    [JsonIgnore]
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>The repo needs a Hugging Face token and its terms accepted on the Hub.</summary>
    public bool Gated { get; set; }

    /// <summary>
    /// Where the files come from, tried in order for each file. A file is accepted from any source as long
    /// as its hash matches, so a mirror can stand in when the first source disappears.
    /// </summary>
    public List<ModelSource> Sources { get; set; } = new();

    /// <summary>
    /// Only for <c>las models pin</c>: which files of the first source to list (glob patterns, <c>*</c>
    /// matches across folders). Without it every file except documentation and images.
    /// </summary>
    public List<string>? Include { get; set; }

    /// <summary>Only for <c>las models pin</c>: files to leave out (glob patterns).</summary>
    public List<string>? Exclude { get; set; }

    /// <summary>
    /// The files the model needs, by path inside the model folder. Written by <c>las models pin</c>. A model can
    /// have none (a set of voices a worker fetches one by one, see <see cref="ModelStore.EnsureFileAsync"/>).
    /// </summary>
    public Dictionary<string, ModelFileInfo> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Other models that come along with this one (a tokenizer the model loads by name).</summary>
    public List<string> Requires { get; set; } = new();

    [JsonIgnore]
    public long TotalBytes => Files.Values.Sum(f => f.Size);

    [JsonIgnore]
    public string Name => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    /// <summary>
    /// Hash of the file list. Stored in a downloaded model's marker: when models.json changes the files (a new
    /// pin), the marker no longer matches and the model is checked and completed again.
    /// </summary>
    public string FilesHash()
    {
        var sb = new StringBuilder();
        foreach (var (path, f) in Files.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(path).Append('|').Append(f.Size).Append('|').Append(f.Expected).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}

/// <summary>A Hugging Face repo at a pinned commit, or a URL with <c>{file}</c> standing for the file's path.</summary>
public sealed class ModelSource
{
    public string? Repo { get; set; }
    public string? Revision { get; set; }
    public string? Url { get; set; }

    [JsonIgnore]
    public bool IsHub => !string.IsNullOrEmpty(Repo);

    public override string ToString() => IsHub ? $"{Repo}@{Revision?[..Math.Min(8, Revision.Length)]}" : Url ?? "?";
}

/// <summary>
/// A listed file: its size and hash. Large files (stored with LFS on the Hub) have a SHA-256; small ones only the
/// Hub's git hash (SHA-1 of "blob &lt;size&gt;\0" and the content), the only hash the Hub publishes for them. So a
/// gated repo can be pinned without access to its files.
/// </summary>
public sealed class ModelFileInfo
{
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public string? GitSha1 { get; set; }

    /// <summary>The expected hash in lowercase hex (SHA-256, else the git hash).</summary>
    [JsonIgnore]
    public string Expected => (Sha256 ?? GitSha1 ?? "").ToLowerInvariant();

    /// <summary>A hash in progress that ends up equal to <see cref="Expected"/> when the bytes are right.</summary>
    public IncrementalHash NewHash()
    {
        if (Sha256 != null) return IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var git = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        git.AppendData(Encoding.ASCII.GetBytes($"blob {Size}\0"));
        return git;
    }
}

/// <summary>The models in <c>workers/models.json</c>.</summary>
public sealed class ModelCatalog
{
    public const string FileName = "models.json";

    private readonly Dictionary<string, ModelEntry> _models;

    private ModelCatalog(Dictionary<string, ModelEntry> models, List<string> errors)
    {
        _models = models;
        LoadErrors = errors;
    }

    /// <summary>Problems found while loading (shown with the manifest errors).</summary>
    public IReadOnlyList<string> LoadErrors { get; }

    public IReadOnlyCollection<ModelEntry> All => _models.Values;

    public ModelEntry? Get(string? id) => id != null && _models.TryGetValue(id, out var m) ? m : null;

    /// <summary>The models and everything they require (a worker's models with their tokenizers), each once, in order.</summary>
    public IReadOnlyList<string> WithRequired(IEnumerable<string> ids)
    {
        var result = new List<string>();
        void Add(string id)
        {
            if (result.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
            result.Add(id);
            foreach (var r in Get(id)?.Requires ?? []) Add(r);
        }
        foreach (var id in ids) Add(id);
        return result;
    }

    /// <summary>Reads <c>models.json</c> from the workers folder; a missing or broken file gives an empty catalog and an error.</summary>
    public static ModelCatalog Load(string workersRoot)
    {
        var path = Path.Combine(workersRoot, FileName);
        var errors = new List<string>();
        var models = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = WorkersJson.Deserialize<ModelsFile>(File.ReadAllText(path));
            foreach (var (id, entry) in file.Models)
            {
                entry.Id = id;
                if (!IsValidId(id)) { errors.Add($"{FileName}: '{id}' is not a valid model id (letters, digits, '.', '-', '_')."); continue; }
                if (entry.Sources.Count == 0) errors.Add($"{FileName}: {id} has no sources.");
                foreach (var f in entry.Files.Keys.Where(f => !IsSafeRelativePath(f)))
                    errors.Add($"{FileName}: {id} lists an invalid file path '{f}'.");
                models[id] = entry;
            }
            foreach (var m in models.Values)
                foreach (var r in m.Requires.Where(r => !models.ContainsKey(r)))
                    errors.Add($"{FileName}: {m.Id} requires '{r}', which isn't defined.");
        }
        catch (FileNotFoundException)
        {
            // No models at all; a manifest that names one reports it as unknown.
        }
        catch (Exception ex)
        {
            errors.Add($"{FileName}: {ex.Message}");
        }
        return new ModelCatalog(models, errors);
    }

    /// <summary>A catalog built in code (tests).</summary>
    public static ModelCatalog FromEntries(IEnumerable<ModelEntry> entries) =>
        new(entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase), []);

    /// <summary>Ids are folder names in the store.</summary>
    public static bool IsValidId(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && id is not ("." or "..");

    /// <summary>A relative path that stays inside its folder ("voices/af_heart.pt", not "../x" or "C:/x").</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\\')) return false;
        return path.Split('/').All(p => p.Length > 0 && p is not ("." or ".."));
    }
}
