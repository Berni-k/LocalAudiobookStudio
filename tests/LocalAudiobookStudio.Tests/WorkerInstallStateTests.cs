using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.Tests;

/// <summary>Per-worker install state in a shared environment: status, uninstall, and environment cleanup.</summary>
public sealed class WorkerInstallStateTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string _root => _tmp.Path;
    private readonly EnvironmentManager _env;
    private readonly DataRootPaths _data;
    private readonly WorkerCatalog _catalog;
    private readonly string _workers;

    public WorkerInstallStateTests()
    {
        var workers = _workers = Path.Combine(_root, "workers");
        WriteEnvironments("numpy<2");
        foreach (var id in new[] { "alpha", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(workers, id));
            File.WriteAllText(Path.Combine(workers, id, "manifest.json"),
                $$"""{ "id": "{{id}}", "displayName": "{{id}}", "envGroup": "shared" }""");
        }
        _data = new DataRootPaths(Path.Combine(_root, "data"));
        _catalog = new WorkerCatalog(workers);
        _env = new EnvironmentManager(_data, _catalog, uvPath: null);
    }

    /// <summary>environments.json defining the "shared" environment, with one override.</summary>
    private void WriteEnvironments(string overridePin)
    {
        Directory.CreateDirectory(_workers);
        File.WriteAllText(Path.Combine(_workers, WorkerCatalog.EnvironmentsFileName), $$"""
            // comments are allowed
            { "environments": [ { "name": "shared", "python": "3.12", "torchIndexUrl": "https://example.invalid/cu128",
                                  "packages": ["torch==2.11.0+cu128"], "overrides": ["{{overridePin}}"] } ] }
            """);
    }

    public void Dispose() => _tmp.Dispose();

    /// <summary>Writes a state file and a fake interpreter, as an install would.</summary>
    private void FakeInstall(params string[] installed)
    {
        var python = _data.EnvPython("shared");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "");
        var state = new EnvState { PythonVersion = "3.11", InstalledWorkers = installed.ToList() };
        foreach (var id in installed) state.SelfTests[id] = true;
        File.WriteAllText(_data.EnvStateFile("shared"),
            System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    [Fact]
    public void A_venv_whose_base_python_is_gone_is_broken()
    {
        FakeInstall("alpha");
        var home = Path.Combine(_root, "base-python");
        File.WriteAllLines(Path.Combine(_data.EnvDir("shared"), "pyvenv.cfg"), [$"home = {home}", "version_info = 3.11"]);
        Assert.Equal(EnvStatus.Broken, _env.GetWorkerStatus("alpha"));

        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, OperatingSystem.IsWindows() ? "python.exe" : "python3"), "");
        Assert.Equal(EnvStatus.Ready, _env.GetWorkerStatus("alpha"));
    }

    [Fact]
    public void Only_installed_workers_are_ready_even_when_they_share_an_environment()
    {
        FakeInstall("alpha");
        Assert.True(_env.IsWorkerReady("alpha"));
        Assert.Equal(EnvStatus.NotInstalled, _env.GetWorkerStatus("beta"));
    }

    [Fact]
    public async Task Uninstalling_one_worker_keeps_the_environment_for_the_other()
    {
        FakeInstall("alpha", "beta");
        await _env.UninstallWorkerAsync("beta", _ => { });
        Assert.True(Directory.Exists(_data.EnvDir("shared")));
        Assert.True(_env.IsWorkerReady("alpha"));
        Assert.Equal(EnvStatus.NotInstalled, _env.GetWorkerStatus("beta"));
        Assert.Equal(["alpha"], _env.InstalledWorkers("shared"));
    }

    [Fact]
    public async Task Uninstalling_the_last_worker_deletes_the_environment()
    {
        FakeInstall("alpha");
        await _env.UninstallWorkerAsync("alpha", _ => { });
        Assert.False(Directory.Exists(_data.EnvDir("shared")));
        Assert.Equal(EnvStatus.NotInstalled, _env.GetWorkerStatus("alpha"));
    }

    [Fact]
    public void A_shared_environment_sets_python_and_the_torch_index_for_its_workers()
    {
        Assert.Empty(_catalog.LoadErrors);
        var alpha = _catalog.Get("alpha")!;
        Assert.Equal("3.12", alpha.Python);
        Assert.Equal("https://example.invalid/cu128", alpha.TorchIndexUrl);
        Assert.Equal("3.12", _env.PythonVersionFor("shared"));
    }

    [Fact]
    public void Changing_an_override_changes_the_worker_hash()
    {
        var before = _env.WorkerHash(_catalog.Get("alpha")!);
        WriteEnvironments("numpy<3");
        _catalog.Reload();
        Assert.NotEqual(before, _env.WorkerHash(_catalog.Get("alpha")!));
    }

    [Fact]
    public void Environment_folders_no_worker_uses_are_listed_and_can_be_deleted()
    {
        FakeInstall("alpha");
        var old = _data.EnvDir("torch-cu124-a");
        Directory.CreateDirectory(old);
        File.WriteAllText(_data.EnvStateFile("torch-cu124-a"), """{ "installedWorkers": ["alpha"] }""");

        var unused = Assert.Single(_env.UnusedEnvironments());
        Assert.Equal("torch-cu124-a", unused.Name);
        Assert.Equal(["alpha"], unused.InstalledWorkers);

        _env.DeleteUnusedEnvironment("shared", _ => { }); // in use: refused
        Assert.True(Directory.Exists(_data.EnvDir("shared")));
        _env.DeleteUnusedEnvironment("torch-cu124-a", _ => { });
        Assert.False(Directory.Exists(old));
        Assert.Empty(_env.UnusedEnvironments());
    }
}
