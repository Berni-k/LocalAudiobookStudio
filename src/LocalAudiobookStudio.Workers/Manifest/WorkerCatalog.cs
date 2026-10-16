using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers.Models;

namespace LocalAudiobookStudio.Workers.Manifest;

/// <summary>
/// All workers found under the <c>workers/</c> folder, keyed by id. A worker can have several runtimes:
/// <c>manifest.json</c> holds what they share and each <c>manifest.&lt;runtime&gt;.json</c> (python, hartsy)
/// the rest, its fields replacing the shared file's. Without such files <c>manifest.json</c> is the whole
/// manifest of a Python worker. <see cref="Get(string?)"/> returns the runtime chosen on this machine.
/// </summary>
public sealed class WorkerCatalog
{
    private readonly Dictionary<string, List<WorkerManifest>> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnvironmentDefinition> _environments = new(StringComparer.OrdinalIgnoreCase);

    public WorkerCatalog(string workersRoot)
    {
        WorkersRoot = workersRoot;
        Reload();
    }

    /// <summary>The folder containing <c>common/</c> and one folder per worker.</summary>
    public string WorkersRoot { get; }

    /// <summary>The models workers can ask for (<c>models.json</c> in the workers folder).</summary>
    public ModelCatalog Models { get; private set; } = ModelCatalog.FromEntries([]);

    /// <summary>
    /// The runtime chosen for a worker (the "runtime" worker setting), or null for its first runtime.
    /// Set by the app; without it every worker uses its first runtime.
    /// </summary>
    public Func<string, string?>? RuntimeChoice { get; set; }

    /// <summary>The chosen runtime of every worker, highest <see cref="WorkerManifest.Priority"/> first, then by name.</summary>
    public IReadOnlyList<WorkerManifest> All => _byId.Keys.Select(id => Get(id)!)
        .OrderByDescending(m => m.Priority).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The workers that generate audio (a speaker's worker), in the order of <see cref="All"/>.</summary>
    public IReadOnlyList<WorkerManifest> Generators => All.Where(m => !m.IsListener).ToList();

    /// <summary>The workers that transcribe or align audio, in the order of <see cref="All"/>.</summary>
    public IReadOnlyList<WorkerManifest> Transcribers => All.Where(m => m.IsTranscriber).ToList();

    /// <summary>The workers that judge how alike voices are (tagged Speaker Verification), in the order of <see cref="All"/>.</summary>
    public IReadOnlyList<WorkerManifest> SimScorers => All.Where(m => m.VerifiesSpeaker).ToList();

    /// <summary>Every runtime of every worker.</summary>
    public IEnumerable<WorkerManifest> AllRuntimes => _byId.Values.SelectMany(v => v);

    /// <summary>Problems found while loading manifests (shown in the Workers dialog).</summary>
    public List<string> LoadErrors { get; } = new();

    /// <summary>The worker with the runtime chosen on this machine (its first runtime when none is chosen).</summary>
    public WorkerManifest? Get(string? id)
    {
        if (id == null || !_byId.TryGetValue(id, out var runtimes)) return null;
        var chosen = RuntimeChoice?.Invoke(id);
        return runtimes.FirstOrDefault(m => string.Equals(m.Runtime, chosen, StringComparison.OrdinalIgnoreCase)) ?? runtimes[0];
    }

    /// <summary>The worker with a given runtime, or null when it doesn't have that runtime.</summary>
    public WorkerManifest? Get(string? id, string runtime) =>
        id != null && _byId.TryGetValue(id, out var runtimes)
            ? runtimes.FirstOrDefault(m => string.Equals(m.Runtime, runtime, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>True when every runtime of the worker is experimental (none is well tested).</summary>
    public bool IsExperimental(string? id) =>
        id != null && _byId.TryGetValue(id, out var runtimes) && runtimes.All(m => m.Experimental);

    /// <summary>The worker's runtime whose installs are recorded in <paramref name="group"/>.</summary>
    public WorkerManifest? InGroup(string? id, string group) =>
        id != null && _byId.TryGetValue(id, out var runtimes)
            ? runtimes.FirstOrDefault(m => string.Equals(m.EnvGroup, group, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>The shared environment of that name from <c>environments.json</c>, or null (a worker's own group).</summary>
    public EnvironmentDefinition? Environment(string? group) =>
        group != null && _environments.TryGetValue(group, out var e) ? e : null;

    public IEnumerable<string> EnvGroups => AllRuntimes.Select(m => m.EnvGroup).Distinct(StringComparer.OrdinalIgnoreCase).Order();

    public IReadOnlyList<WorkerManifest> WorkersInGroup(string group) =>
        AllRuntimes.Where(m => string.Equals(m.EnvGroup, group, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.Id).ToList();

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public void Reload()
    {
        _byId.Clear();
        _environments.Clear();
        LoadErrors.Clear();
        if (!Directory.Exists(WorkersRoot)) return;
        LoadEnvironments();
        Models = ModelCatalog.Load(WorkersRoot);
        LoadErrors.AddRange(Models.LoadErrors);

        foreach (var dir in Directory.EnumerateDirectories(WorkersRoot))
        {
            var file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file)) continue;
            JsonObject shared;
            try
            {
                shared = JsonNode.Parse(File.ReadAllText(file), NodeOptions, DocumentOptions) as JsonObject
                         ?? throw new JsonException("not a JSON object");
            }
            catch (Exception ex)
            {
                LoadErrors.Add($"{file}: {ex.Message}");
                continue;
            }
            // "hidden": true in manifest.json leaves a worker out entirely (not ready to be offered yet).
            if (shared["hidden"] is JsonValue hidden && hidden.TryGetValue<bool>(out var isHidden) && isHidden) continue;

            // manifest.python.json, manifest.hartsy.json, …: one runtime each. The first is the default: the
            // order of "runtimeOrder" in manifest.json, otherwise Python first, then by name.
            var order = (shared[RuntimeOrderKey] as JsonArray)?.Select(n => n?.GetValue<string>().ToLowerInvariant()).OfType<string>().ToList() ?? [];
            var overlays = Directory.EnumerateFiles(dir, "manifest.*.json")
                .Select(f => (File: f, Runtime: Path.GetFileName(f)["manifest.".Length..^".json".Length].ToLowerInvariant()))
                .Where(o => o.Runtime.Length > 0)
                .OrderBy(o => order.IndexOf(o.Runtime) is >= 0 and var i ? i : order.Count)
                .ThenBy(o => o.Runtime == WorkerRuntimes.Python ? 0 : 1).ThenBy(o => o.Runtime)
                .ToList();

            var runtimes = new List<WorkerManifest>();
            if (overlays.Count == 0)
            {
                if (Parse(shared, dir, null, file) is { } m) runtimes.Add(m);
            }
            foreach (var (overlayFile, runtime) in overlays)
            {
                try
                {
                    var merged = (JsonObject)shared.DeepClone();
                    var overlay = JsonNode.Parse(File.ReadAllText(overlayFile), NodeOptions, DocumentOptions) as JsonObject
                                  ?? throw new JsonException("not a JSON object");
                    foreach (var (key, value) in overlay) merged[key] = value?.DeepClone();
                    if (Parse(merged, dir, runtime, overlayFile) is { } m) runtimes.Add(m);
                }
                catch (Exception ex)
                {
                    LoadErrors.Add($"{overlayFile}: {ex.Message}");
                }
            }
            if (runtimes.Count == 0) continue;

            var names = runtimes.Select(m => m.Runtime).ToList();
            foreach (var m in runtimes) m.Runtimes = names;
            _byId[runtimes[0].Id] = runtimes;
        }
    }

    private WorkerManifest? Parse(JsonObject json, string dir, string? runtime, string file)
    {
        try
        {
            var m = WorkersJson.Deserialize<WorkerManifest>(json.ToJsonString());
            m.Folder = dir;
            if (string.IsNullOrEmpty(m.Id)) m.Id = Path.GetFileName(dir);
            if (runtime != null) m.Runtime = runtime;
            if (m.IsHartsy)
            {
                // No venv: the install state is kept under the shared "hartsy" group.
                m.EnvGroup = WorkerRuntimes.HartsyGroup;
                if (string.IsNullOrEmpty(m.HartsyModel)) throw new JsonException("the hartsy runtime needs \"hartsyModel\"");
            }
            // The bundled internal worker: the install state (and the GPU runtime's downloaded builds) per runtime.
            if (m.IsInternal)
            {
                m.EnvGroup = m.Runtime;
                if (!Environments.InternalGpuRuntime.Engines.Contains(m.Engine ?? ""))
                    throw new JsonException($"the {m.Runtime} runtime needs \"engine\" ({string.Join(", ", Environments.InternalGpuRuntime.Engines)})");
                if (m.IsInternalGpu && !Environments.InternalGpuRuntime.HasGpuBuilds(m.Engine!))
                    throw new JsonException($"the {m.Engine} engine has no GPU builds; leave out the {m.Runtime} runtime");
                // The engine loads only models the Workers window knows about (and can show and delete).
                foreach (var entry in (m.EngineConfig?["models"] as JsonObject ?? []).Select(e => e.Value?["model"]?.GetValue<string>()))
                    if (entry == null || !m.Models.Contains(entry))
                        LoadErrors.Add($"{file}: \"engineConfig\" names model '{entry}', which isn't in its \"models\"");
            }
            if (string.IsNullOrEmpty(m.EnvGroup)) m.EnvGroup = m.Id;
            // A shared environment decides the Python version and the torch build for every worker in it.
            if (!m.IsBundled && Environment(m.EnvGroup) is { } env)
            {
                m.Python = env.Python;
                m.TorchIndexUrl = env.TorchIndexUrl;
            }
            m.RuntimeLabel = m.IsInternal ? WorkerRuntimes.InternalLabel(m.Engine, m.IsInternalGpu)
                : m.Runtime == WorkerRuntimes.Python && Environment(m.EnvGroup) is { } torchEnv
                  && torchEnv.Packages.Any(p => p.StartsWith("torch", StringComparison.OrdinalIgnoreCase))
                    ? WorkerRuntimes.PyTorchLabel
                    : WorkerRuntimes.Label(m.Runtime);
            MarkUnsupported(m, file);
            foreach (var def in m.Settings.Where(d => d.AddsSettings != null && !SettingSources.Readers.Contains(d.AddsSettings)))
                LoadErrors.Add($"{file}: setting '{def.Key}' has an unknown \"addsSettings\" reader '{def.AddsSettings}' " +
                               $"(known: {string.Join(", ", SettingSources.Readers)})");
            foreach (var id in m.Models.Where(id => Models.Get(id) == null))
                LoadErrors.Add($"{file}: \"models\" names '{id}', which isn't in {ModelCatalog.FileName}");
            CheckFusion(m, file);
            if (m.InlineTags is { Length: > 0 } tags)
                try { _ = new System.Text.RegularExpressions.Regex(tags); }
                catch (ArgumentException ex) { LoadErrors.Add($"{file}: \"inlineTags\" isn't a valid regular expression: {ex.Message}"); }
            if (m.MultiGpu && !m.RunsOnGpu)
                LoadErrors.Add($"{file}: \"multiGpu\" needs a worker that runs on the GPU (\"devices\" with \"cuda\")");
            // Alignment times what the worker hears: without Transcription it has nothing to time. An aligner of a
            // given text is tagged Alignment Forced.
            if (m.CanAlign && !m.CanTranscribe)
                LoadErrors.Add($"{file}: tag \"{WorkerTags.Alignment}\" needs \"{WorkerTags.Transcription}\"; " +
                               $"a worker that times a given text is tagged \"{WorkerTags.ForcedAlignment}\"");
            return m;
        }
        catch (Exception ex)
        {
            LoadErrors.Add($"{file}: {ex.Message}");
            return null;
        }
    }

    private void LoadEnvironments()
    {
        var file = Path.Combine(WorkersRoot, EnvironmentsFileName);
        if (!File.Exists(file)) return;
        try
        {
            foreach (var e in WorkersJson.Deserialize<EnvironmentsFile>(File.ReadAllText(file)).Environments)
            {
                if (string.IsNullOrWhiteSpace(e.Name)) throw new JsonException("an environment has no \"name\"");                if (!_environments.TryAdd(e.Name, e)) throw new JsonException($"environment '{e.Name}' is defined twice");
            }
        }
        catch (Exception ex)
        {
            LoadErrors.Add($"{file}: {ex.Message}");
        }
    }

    public const string EnvironmentsFileName = "environments.json";

    /// <summary>In manifest.json: the runtimes in the order offered; the first is the default.</summary>
    public const string RuntimeOrderKey = "runtimeOrder";

    /// <summary>Every runtime of a worker, the default first.</summary>
    public IReadOnlyList<WorkerManifest> RuntimesOf(string id) =>
        _byId.TryGetValue(id, out var runtimes) ? runtimes : [];

    /// <summary>Applies the runtime's "unsupportedSettings" / "unsupportedOptions" to its setting definitions.</summary>
    private void MarkUnsupported(WorkerManifest m, string file)
    {
        var reason = $"Not supported on {m.RuntimeLabel}";
        foreach (var key in m.UnsupportedSettings)
        {
            if (m.Settings.FirstOrDefault(d => d.Key == key) is { } def) def.UnsupportedReason = reason;
            else LoadErrors.Add($"{file}: unsupportedSettings names an unknown setting '{key}'");
        }
        foreach (var (key, options) in m.UnsupportedOptions)
        {
            if (m.Settings.FirstOrDefault(d => d.Key == key) is not { } def)
            {
                LoadErrors.Add($"{file}: unsupportedOptions names an unknown setting '{key}'");
                continue;
            }
            def.UnsupportedOptionsReason = reason;
            foreach (var o in options) def.UnsupportedOptions.Add(o);
        }
    }

    /// <summary>
    /// A "fusion" entry the app could act on wrongly: the bundled workers refuse fused requests (a runtime overlay turns
    /// fusion off with <c>"fusion": null</c>), and a context kind the app doesn't know would never be sent.
    /// </summary>
    private void CheckFusion(WorkerManifest m, string file)
    {
        if (m.Fusion is not { } f) return;
        if (m.IsBundled) LoadErrors.Add($"{file}: the {m.Runtime} runtime can't fuse generations; set \"fusion\": null in its overlay");
        if (f.Speakers < 1) LoadErrors.Add($"{file}: \"fusion\" needs \"speakers\" of 1 or more");
        foreach (var kind in f.Context.Where(k => k is not (FusionContext.Text or FusionContext.Audio)))
            LoadErrors.Add($"{file}: \"fusion\" has an unknown context kind '{kind}' ({FusionContext.Text}, {FusionContext.Audio})");
    }

    /// <summary>Default location: the <c>workers</c> folder next to the executable.</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "workers");
}
