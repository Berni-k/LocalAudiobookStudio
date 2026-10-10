using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.Workers.Environments;

public enum EnvStatus
{
    NotInstalled,
    Installing,
    Ready,
    UpdateAvailable,
    Broken,
}

/// <summary>Contents of <c>.env-state.json</c> in each venv.</summary>
public sealed class EnvState
{
    public string PythonVersion { get; set; } = "";
    /// <summary>Unused since per-worker installs (see <see cref="WorkerHashes"/>); kept so older files still load.</summary>
    public string RequirementsHash { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
    public string? UvVersion { get; set; }
    public Dictionary<string, bool> SelfTests { get; set; } = new();
    public Dictionary<string, string> SelfTestOutput { get; set; } = new();

    /// <summary>
    /// Workers installed in this environment (each worker is installed separately). Environments from
    /// before per-worker installs don't have the list: then every self-tested worker counts as installed.
    /// </summary>
    public List<string>? InstalledWorkers { get; set; }

    /// <summary>Per installed worker: hash of what it installed (see EnvironmentManager.WorkerHash).</summary>
    public Dictionary<string, string> WorkerHashes { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Installed
    {
        get => InstalledWorkers ?? SelfTests.Keys.ToList();
        set => InstalledWorkers = value;
    }
}

/// <summary>
/// Installs, repairs and removes one venv per environment group using the bundled uv.
/// Workers in the same group share a venv built from the union of their requirement files.
/// </summary>
public sealed class EnvironmentManager
{
    private readonly HashSet<string> _installing = new(StringComparer.OrdinalIgnoreCase); // groups being changed
    private readonly HashSet<string> _installingWorkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Packages published after this moment are ignored (uv <c>--exclude-newer</c>), so an install resolves
    /// the same versions on any day instead of picking up untested new releases. To move forward: set it
    /// to today, reinstall the workers, test them, then commit.
    /// </summary>
    public const string PackageCutoff = "2026-10-03T18:00:00Z";

    public EnvironmentManager(DataRootPaths dataRoot, WorkerCatalog catalog, string? uvPath, string? hartsyWorkerPath = null)
    {
        DataRoot = dataRoot;
        Catalog = catalog;
        UvPath = uvPath;
        HartsyWorkerPath = hartsyWorkerPath;
    }

    public DataRootPaths DataRoot { get; }
    public WorkerCatalog Catalog { get; }

    /// <summary>The bundled uv binary, or null if it isn't present.</summary>
    public string? UvPath { get; private set; }

    /// <summary>Uses a uv found or downloaded after startup (the system check's "Download uv").</summary>
    public void UseUv(string path) => UvPath = path;

    /// <summary>The bundled HartsyInference worker (hartsy/las-hartsy-worker), or null if it isn't present.</summary>
    public string? HartsyWorkerPath { get; }

    public event Action<string>? StatusChanged;

    /// <summary>
    /// The uv to use: the bundled one for this platform in <c>tools/uv/&lt;rid&gt;/</c>, else one downloaded into the
    /// data folder (<paramref name="dataRoot"/>/tools/uv/&lt;rid&gt;/), else a uv on the PATH (for example from a
    /// package manager on Linux). Either way its cache and Pythons stay in the data folder.
    /// </summary>
    public static string? FindUv(string? dataRoot = null, string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "uv.exe" : "uv";
        foreach (var root in new[] { baseDir, dataRoot })
        {
            if (root == null) continue;
            var local = Path.Combine(root, "tools", "uv", rid, exe);
            if (!File.Exists(local)) continue;
            EnsureExecutable(local);
            return local;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Finds the bundled HartsyInference worker, which the app's build publishes into <c>hartsy/</c>.</summary>
    public static string? FindHartsyWorker(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "las-hartsy-worker.exe" : "las-hartsy-worker";
        var path = Path.Combine(baseDir, "hartsy", exe);
        if (!File.Exists(path)) return null;
        EnsureExecutable(path);
        return path;
    }

    /// <summary>On Linux, files extracted from a zip may lose the executable bit.</summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            var exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(path, mode | exec);
        }
        catch
        {
            // Read-only install location; uv will fail later with a clear error.
        }
    }

    public async Task<string?> GetUvVersionAsync(CancellationToken ct = default)
    {
        if (UvPath == null) return null;
        var sb = new StringBuilder();
        try
        {
            await ProcessRunner.RunAsync(UvPath, ["--version"], DataRoot.BaseEnvironment(), l => { if (!l.StartsWith("> ")) sb.AppendLine(l); }, ct).ConfigureAwait(false);
            return sb.ToString().Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The HartsyInference version the bundled worker is built on ("2.0.0-alpha.245"), or null.</summary>
    public async Task<string?> GetHartsyVersionAsync(CancellationToken ct = default)
    {
        if (HartsyWorkerPath == null) return null;
        string? version = null;
        try
        {
            await ProcessRunner.RunAsync(HartsyWorkerPath, ["--version"], HartsyEnvironment(),
                l => { if (l.StartsWith("HartsyInference ", StringComparison.Ordinal)) version = l["HartsyInference ".Length..].Trim(); }, ct).ConfigureAwait(false);
        }
        catch
        {
            // A worker that doesn't start: no version to show.
        }
        return version;
    }

    /// <summary>Environment for the HartsyInference worker process.</summary>
    private Dictionary<string, string> HartsyEnvironment()
    {
        var env = DataRoot.BaseEnvironment();
        // The single-file worker unpacks itself (DLLs, CUDA kernels) here instead of %TEMP%\.net.
        env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(DataRoot.Cache, "dotnet-bundle");
        env["LAS_HARTSY_SETTINGS"] = DataRoot.HartsySettingsFile;
        return env;
    }

    public EnvState? ReadState(string group)
    {
        var f = DataRoot.EnvStateFile(group);
        if (!File.Exists(f)) return null;
        try { return WorkersJson.Deserialize<EnvState>(File.ReadAllText(f)); }
        catch { return null; }
    }

    private void WriteState(string group, EnvState state) =>
        File.WriteAllText(DataRoot.EnvStateFile(group), WorkersJson.Serialize(state, indented: true));

    /// <summary>The workers installed in a group's environment (each one is installed separately).</summary>
    public IReadOnlyList<string> InstalledWorkers(string group) => ReadState(group)?.Installed ?? [];

    /// <summary>Status of one worker: installed (into its group's shared environment) or not, and healthy or not.</summary>
    public EnvStatus GetWorkerStatus(string workerId)
    {
        var m = Catalog.Get(workerId);
        if (m == null) return EnvStatus.NotInstalled;
        lock (_installing)
            if (_installingWorkers.Contains(m.Id)) return EnvStatus.Installing;

        var state = ReadState(m.EnvGroup);
        if (state == null || !state.Installed.Contains(m.Id, StringComparer.OrdinalIgnoreCase)) return EnvStatus.NotInstalled;
        if (!InterpreterPresent(m)) return EnvStatus.Broken;
        if (!state.SelfTests.TryGetValue(m.Id, out var ok) || !ok) return EnvStatus.Broken;
        // Environments from before per-worker installs have no hash for the worker: treat them as current.
        if (state.WorkerHashes.TryGetValue(m.Id, out var hash) && hash != WorkerHash(m)) return EnvStatus.UpdateAvailable;
        return EnvStatus.Ready;
    }

    /// <summary>Whether this runtime of a worker is installed (whichever runtime is chosen for it).</summary>
    public bool IsRuntimeInstalled(WorkerManifest runtime) =>
        ReadState(runtime.EnvGroup)?.Installed.Contains(runtime.Id, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Whether this runtime of a worker is installed and passed its self-test (whichever runtime is chosen).</summary>
    public bool IsRuntimeReady(WorkerManifest runtime)
    {
        var state = ReadState(runtime.EnvGroup);
        return state != null && state.Installed.Contains(runtime.Id, StringComparer.OrdinalIgnoreCase)
               && state.SelfTests.TryGetValue(runtime.Id, out var ok) && ok
               && InterpreterPresent(runtime);
    }

    /// <summary>
    /// The worker's executable is there: the HartsyInference worker, or the venv's python with the base Python it
    /// was made from. uv's python.exe on Windows is a small launcher; when the base install it names is gone
    /// (removed, or another Python version replaced it), the launcher stays but can't start anything.
    /// </summary>
    private bool InterpreterPresent(WorkerManifest m) =>
        m.IsHartsy
            ? File.Exists(HartsyWorkerPath ?? "")
            : File.Exists(DataRoot.EnvPython(m.EnvGroup)) && DataRootPaths.VenvBaseExists(DataRoot.EnvDir(m.EnvGroup));

    /// <summary>True when the worker is installed and passed its self-test (an available update doesn't matter).</summary>
    public bool IsWorkerReady(string workerId) => GetWorkerStatus(workerId) is EnvStatus.Ready or EnvStatus.UpdateAvailable;

    public string PythonVersionFor(string group) =>
        Catalog.Environment(group)?.Python ?? Catalog.WorkersInGroup(group).Select(w => w.Python).Where(v => !string.IsNullOrWhiteSpace(v))
            .OrderByDescending(v => v, StringComparer.Ordinal).FirstOrDefault() ?? "3.11";

    /// <summary>
    /// Hash of what one worker installs: its group's Python version, index URL, shared packages and overrides
    /// (environments.json), and its requirements file.
    /// </summary>
    public string WorkerHash(WorkerManifest w)
    {
        var sb = new StringBuilder();
        if (w.IsHartsy)
        {
            // A new worker build (another HartsyInference version) asks for a new self-test.
            sb.Append("hartsy=").Append(w.HartsyModel).Append('\n');
            if (HartsyWorkerPath != null && File.Exists(HartsyWorkerPath)) sb.Append("exe=").Append(new FileInfo(HartsyWorkerPath).Length);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }
        sb.Append("python=").Append(PythonVersionFor(w.EnvGroup)).Append('\n');
        sb.Append("index=").Append(w.TorchIndexUrl).Append('\n');
        if (Catalog.Environment(w.EnvGroup) is { } envDef)
        {
            foreach (var p in envDef.Packages) sb.Append("package=").Append(p).Append('\n');
            foreach (var o in envDef.Overrides) sb.Append("override=").Append(o).Append('\n');
        }
        var req = Path.Combine(w.Folder, w.Requirements);
        if (File.Exists(req)) sb.Append(File.ReadAllText(req).Replace("\r\n", "\n"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Installs one worker: creates its group's environment if needed (otherwise adds the worker's packages
    /// to it), then self-tests every installed worker of the group, since new packages can affect them.
    /// </summary>
    public Task<bool> InstallWorkerAsync(string workerId, Action<string> log, CancellationToken ct) =>
        InstallCoreAsync(workerId, rebuild: false, log, ct);

    /// <summary>Rebuilds the worker's environment from scratch with every worker installed in it (fast: packages come from the cache).</summary>
    public Task<bool> RepairWorkerAsync(string workerId, Action<string> log, CancellationToken ct) =>
        InstallCoreAsync(workerId, rebuild: true, log, ct);

    private async Task<bool> InstallCoreAsync(string workerId, bool rebuild, Action<string> log, CancellationToken ct)
    {
        if (Catalog.Get(workerId) is { IsHartsy: true } hartsy) return await InstallHartsyAsync(hartsy, log, ct);
        if (UvPath == null)
        {
            log("ERROR: uv was not found, neither in tools/uv nor on the PATH. Install uv (https://docs.astral.sh/uv/) or run tools/uv/fetch-uv.ps1 (Windows) / tools/uv/fetch-uv.sh (Linux), then restart.");
            return false;
        }
        var worker = Catalog.Get(workerId);
        if (worker == null) { log($"ERROR: unknown worker '{workerId}'."); return false; }
        var group = worker.EnvGroup;

        lock (_installing)
        {
            if (!_installing.Add(group)) { log($"Another installation in the '{group}' environment is already running."); return false; }
            _installingWorkers.Add(worker.Id);
        }
        StatusChanged?.Invoke(group);

        FileStream? groupLock = null;
        try
        {
            DataRoot.EnsureCreated();
            groupLock = LockGroup(group, log);
            if (groupLock == null) return false;
            var env = DataRoot.BaseEnvironment();
            var python = PythonVersionFor(group);
            var envDir = DataRoot.EnvDir(group);
            var envPython = DataRoot.EnvPython(group);

            var old = ReadState(group);
            // The environment can be reused if it is intact and on the group's Python version.
            var reuse = !rebuild && old != null && File.Exists(envPython) && DataRootPaths.VenvBaseExists(envDir) && old.PythonVersion == python;
            var installed = (old?.Installed ?? []).Where(id => Catalog.InGroup(id, group) != null)
                .Append(worker.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // A new or rebuilt environment gets every installed worker's packages; a reused one only the new worker's.
            var toInstall = (reuse ? [worker.Id] : installed).Select(id => Catalog.InGroup(id, group)!).ToList();

            log(reuse
                ? $"== Installing {worker.DisplayName} into the existing '{group}' environment"
                : $"== {(rebuild ? "Rebuilding" : "Creating")} the '{group}' environment for: {string.Join(", ", toInstall.Select(w => w.DisplayName))}");

            if (!reuse)
            {
                log($"== uv python install {python}");
                // --no-bin: no python3.x launchers in ~/.local/bin; nothing outside the data root.
                if (await Uv(["python", "install", python, "--no-bin"], env, log, ct) != 0) return Fail(log, "Python installation failed.");
                log($"== creating venv {envDir}");
                if (Directory.Exists(envDir)) Directory.Delete(envDir, recursive: true);
                if (await Uv(["venv", envDir, "--python", python], env, log, ct) != 0) return Fail(log, "Creating the venv failed.");
            }

            log("== installing packages");
            var args = new List<string> { "pip", "install", "--python", envPython };
            if (Catalog.Environment(group) is { } envDef)
            {
                args.AddRange(envDef.Packages);
                if (envDef.Overrides.Count > 0)
                {
                    var overrides = Path.Combine(envDir, OverridesFileName);
                    File.WriteAllLines(overrides, envDef.Overrides);
                    args.Add("--override"); args.Add(overrides);
                }
            }
            foreach (var w in toInstall)
            {
                var req = Path.Combine(w.Folder, w.Requirements);
                if (File.Exists(req)) { args.Add("-r"); args.Add(req); }
            }
            foreach (var url in toInstall.Select(w => w.TorchIndexUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
            {
                args.Add("--extra-index-url"); args.Add(url!);
            }
            args.Add("--index-strategy"); args.Add("unsafe-best-match");
            args.Add("--exclude-newer"); args.Add(PackageCutoff);
            if (await Uv(args, env, log, ct) != 0) return Fail(log, "Package installation failed.");

            log("== self-tests");
            var state = new EnvState
            {
                PythonVersion = python,
                InstalledUtc = reuse ? old!.InstalledUtc : DateTime.UtcNow,
                UvVersion = await GetUvVersionAsync(ct),
                Installed = installed,
            };
            foreach (var w in installed.Select(id => Catalog.InGroup(id, group)!))
            {
                var output = new StringBuilder();
                var code = await RunSelfTestAsync(w, l => { output.AppendLine(l); log($"[{w.Id}] {l}"); }, ct);
                state.SelfTests[w.Id] = code == 0;
                state.SelfTestOutput[w.Id] = output.ToString();
                // Only workers whose packages went in now are current; the others keep their old hash.
                state.WorkerHashes[w.Id] = toInstall.Contains(w) ? WorkerHash(w) : old?.WorkerHashes.GetValueOrDefault(w.Id) ?? WorkerHash(w);
                log(code == 0 ? $"   {w.DisplayName}: PASS" : $"   {w.DisplayName}: FAIL (exit code {code})");
            }
            WriteState(group, state);
            var ok = state.SelfTests.Values.All(v => v);
            log(ok ? $"== Done: {worker.DisplayName} is ready." : "== Finished with self-test failures. See the log above.");
            return ok;
        }
        catch (OperationCanceledException)
        {
            log("== Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            log("ERROR: " + ex.Message);
            return false;
        }
        finally
        {
            groupLock?.Dispose();
            lock (_installing)
            {
                _installing.Remove(group);
                _installingWorkers.Remove(worker.Id);
            }
            StatusChanged?.Invoke(group);
        }
    }

    /// <summary>
    /// Locks an environment against other processes while it changes: the app and <c>las</c> share the data folder, and
    /// two uv runs in one venv break it. Null (with a message in the log) when another process holds it.
    /// </summary>
    private FileStream? LockGroup(string group, Action<string> log)
    {
        Directory.CreateDirectory(DataRoot.Envs);
        try
        {
            return new FileStream(Path.Combine(DataRoot.Envs, group + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            log($"ERROR: another program (the app or las) is changing the '{group}' environment. Try again when it is done.");
            return null;
        }
    }

    public Task<int> RunSelfTestAsync(WorkerManifest w, Action<string> log, CancellationToken ct)
    {
        var env = WorkerEnvironment(w, gpuIndex: null);
        // The self-test checks CUDA only for workers that can't fall back to the CPU.
        env["LAS_DEVICES"] = string.Join(",", new[] { w.RunsOnGpu ? DeviceNames.Cuda : null, w.RunsOnCpu ? DeviceNames.Cpu : null }.OfType<string>());
        var (exe, args, dir) = WorkerCommand(w);
        return ProcessRunner.RunAsync(exe, [.. args, "--selftest"], env, log, ct, dir);
    }

    /// <summary>How to start a worker process: the program, its arguments and its working directory.</summary>
    public (string Executable, List<string> Arguments, string WorkingDirectory) WorkerCommand(WorkerManifest w) =>
        w.IsHartsy
            ? (HartsyWorkerPath ?? throw new InvalidOperationException("The HartsyInference worker (hartsy/las-hartsy-worker) is missing from the app folder."),
               HartsyArguments(w), w.Folder)
            : (DataRoot.EnvPython(w.EnvGroup), ["-u", Path.Combine(w.Folder, w.Entry)], w.Folder);

    private static List<string> HartsyArguments(WorkerManifest w)
    {
        var args = new List<string> { "--worker", w.Id, "--model", w.HartsyModel!, "--model-version", w.ModelVersion };
        if (w.HartsyModality is { Length: > 0 } modality) args.AddRange(["--modality", modality]);
        return args;
    }

    /// <summary>
    /// Installs a worker on the HartsyInference runtime. There are no packages: the bundled worker only has to
    /// bring up its backend. The model downloads when it first runs, like the Python workers' models.
    /// </summary>
    private async Task<bool> InstallHartsyAsync(WorkerManifest worker, Action<string> log, CancellationToken ct)
    {
        var group = worker.EnvGroup;
        lock (_installing)
        {
            if (!_installing.Add(group)) { log("Another HartsyInference installation is already running."); return false; }
            _installingWorkers.Add(worker.Id);
        }
        StatusChanged?.Invoke(group);
        try
        {
            if (HartsyWorkerPath == null)
                return Fail(log, "The HartsyInference worker (hartsy/las-hartsy-worker) is missing from the app folder. Rebuild the app.");
            DataRoot.EnsureCreated();
            Directory.CreateDirectory(DataRoot.EnvDir(group));
            log($"== {worker.DisplayName} on HartsyInference: self-test of {HartsyWorkerPath}");
            var output = new StringBuilder();
            var code = await RunSelfTestAsync(worker, l => { output.AppendLine(l); log($"[{worker.Id}] {l}"); }, ct);

            var state = ReadState(group) ?? new EnvState { InstalledUtc = DateTime.UtcNow };
            state.Installed = state.Installed.Where(id => Catalog.InGroup(id, group) != null)
                .Append(worker.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.SelfTests[worker.Id] = code == 0;
            state.SelfTestOutput[worker.Id] = output.ToString();
            state.WorkerHashes[worker.Id] = WorkerHash(worker);
            WriteState(group, state);
            log(code == 0 ? $"== Done: {worker.DisplayName} is ready. Its model downloads when it first runs."
                : $"== Self-test failed (exit code {code}). See the log above.");
            return code == 0;
        }
        catch (OperationCanceledException)
        {
            log("== Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            log("ERROR: " + ex.Message);
            return false;
        }
        finally
        {
            lock (_installing)
            {
                _installing.Remove(group);
                _installingWorkers.Remove(worker.Id);
            }
            StatusChanged?.Invoke(group);
        }
    }

    /// <summary>
    /// Uninstalls one worker. Its environment is deleted when no other installed worker uses it; otherwise
    /// it stays (the worker's packages remain in it, which is harmless). Models stay in the models folder.
    /// </summary>
    public Task UninstallWorkerAsync(string workerId, Action<string> log)
    {
        var worker = Catalog.Get(workerId);
        if (worker == null) return Task.CompletedTask;
        var group = worker.EnvGroup;
        lock (_installing)
            if (_installing.Contains(group)) { log($"An installation in the '{group}' environment is running; try again when it is done."); return Task.CompletedTask; }

        using var groupLock = LockGroup(group, log);
        if (groupLock == null) return Task.CompletedTask;
        var state = ReadState(group);
        var remaining = (state?.Installed ?? []).Where(id => !string.Equals(id, worker.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var dir = DataRoot.EnvDir(group);
        if (remaining.Count == 0)
        {
            if (Directory.Exists(dir))
            {
                log($"{worker.DisplayName} uninstalled. No other worker uses the '{group}' environment: removing {dir}");
                Directory.Delete(dir, recursive: true);
            }
        }
        else if (state != null)
        {
            state.Installed = remaining;
            state.SelfTests.Remove(worker.Id);
            state.SelfTestOutput.Remove(worker.Id);
            state.WorkerHashes.Remove(worker.Id);
            WriteState(group, state);
            log($"{worker.DisplayName} uninstalled. The '{group}' environment stays for: {string.Join(", ", remaining.Select(id => Catalog.Get(id)?.DisplayName ?? id))}");
        }
        StatusChanged?.Invoke(group);
        return Task.CompletedTask;
    }
    /// <summary>Written into each shared environment for uv --override (from environments.json).</summary>
    private const string OverridesFileName = ".overrides.txt";

    /// <summary>
    /// Environment folders that no worker uses any more: left behind when workers moved to another environment
    /// (environments.json changed). Workers that were installed there show as not installed until reinstalled.
    /// </summary>
    public IReadOnlyList<UnusedEnvironment> UnusedEnvironments()
    {
        if (!Directory.Exists(DataRoot.Envs)) return [];
        var used = Catalog.EnvGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_installing) used.UnionWith(_installing);
        return Directory.EnumerateDirectories(DataRoot.Envs)
            .Select(Path.GetFileName).OfType<string>()
            .Where(name => !used.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new UnusedEnvironment(name, DataRoot.EnvDir(name), ReadState(name)?.Installed ?? []))
            .ToList();
    }

    /// <summary>Deletes an environment folder that no worker uses (see <see cref="UnusedEnvironments"/>).</summary>
    public void DeleteUnusedEnvironment(string name, Action<string> log)
    {
        if (!UnusedEnvironments().Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            log($"'{name}' is in use; not deleted.");
            return;
        }
        var dir = DataRoot.EnvDir(name);
        log($"Removing the unused environment {dir}");
        Directory.Delete(dir, recursive: true);
    }

    public async Task ClearCacheAsync(Action<string> log, CancellationToken ct)
    {
        if (UvPath != null)
        {
            await Uv(["cache", "clean"], DataRoot.BaseEnvironment(), log, ct);
        }
        else if (Directory.Exists(DataRoot.UvCache))
        {
            Directory.Delete(DataRoot.UvCache, recursive: true);
        }
    }

    /// <summary>Environment for a worker process: base variables, PYTHONPATH to the workers folder, and GPU pinning or CPU (gpuIndex -1).</summary>
    public Dictionary<string, string> WorkerEnvironment(WorkerManifest w, int? gpuIndex)
    {
        Dictionary<string, string> env;
        if (w.IsHartsy)
        {
            env = HartsyEnvironment();
        }
        else
        {
            env = DataRoot.BaseEnvironment();
            env["PYTHONPATH"] = Catalog.WorkersRoot;
            env["VIRTUAL_ENV"] = DataRoot.EnvDir(w.EnvGroup);
            // Models come from the app (needModel); a library that tries to fetch one by repo id fails loudly
            // instead of filling a second copy into the Hugging Face cache.
            env["HF_HUB_OFFLINE"] = "1";
        }
        if (gpuIndex is int g)
        {
            // The CPU (-1) hides every GPU, so even code that ignores LAS_DEVICE stays on the CPU.
            env["CUDA_VISIBLE_DEVICES"] = g < 0 ? "" : g.ToString();
            env["LAS_DEVICE"] = g < 0 ? DeviceNames.Cpu : DeviceNames.Cuda;
        }
        return env;
    }

    private Task<int> Uv(IEnumerable<string> args, Dictionary<string, string> env, Action<string> log, CancellationToken ct)
    {
        // --no-config goes after the subcommand so user-level uv.toml files can't change behavior.
        var list = args.ToList();
        var insertAt = list.Count > 0 && list[0] is "python" or "pip" or "cache" ? 2 : 1;
        list.Insert(Math.Min(insertAt, list.Count), "--no-config");
        return ProcessRunner.RunAsync(UvPath!, list, env, log, ct);
    }

    private static bool Fail(Action<string> log, string message)
    {
        log("ERROR: " + message);
        return false;
    }
}

/// <summary>An environment folder no worker uses, and the workers that were installed in it.</summary>
public sealed record UnusedEnvironment(string Name, string Folder, IReadOnlyList<string> InstalledWorkers);
