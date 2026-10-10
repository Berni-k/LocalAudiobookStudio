using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.Tests;

/// <summary>Workers with several runtimes: manifest overlays, the runtime choice and per-runtime install state.</summary>
public sealed class WorkerRuntimeTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string _root => _tmp.Path;
    private readonly string _workers;
    private readonly DataRootPaths _data;
    private string? _choice;

    public WorkerRuntimeTests()
    {
        _workers = Path.Combine(_root, "workers");
        var dual = Path.Combine(_workers, "dual");
        Directory.CreateDirectory(dual);
        File.WriteAllText(Path.Combine(dual, "manifest.json"),
            """{ "id": "dual", "displayName": "Dual", "modelVersion": "v1", "settings": [ { "key": "speed", "type": "float", "default": 1.0 } ] }""");
        File.WriteAllText(Path.Combine(dual, "manifest.python.json"),
            """{ "envGroup": "torch", "entry": "worker.py", "models": ["org-model"] }""");
        File.WriteAllText(Path.Combine(dual, "manifest.hartsy.json"),
            """{ "hartsyModel": "dualmodel", "models": ["org-model"], "settings": [] }""");
        File.WriteAllText(Path.Combine(_workers, "models.json"),
            """{ "models": { "org-model": { "sources": [ { "url": "https://example.invalid/{file}" } ], "files": {} } } }""");

        var limited = Path.Combine(_workers, "limited");
        Directory.CreateDirectory(limited);
        File.WriteAllText(Path.Combine(limited, "manifest.json"),
            """
            { "id": "limited", "displayName": "Limited", "settings": [
              { "key": "voice", "type": "enum", "default": "a", "options": ["a", "b", "c"], "optionLabels": ["A", "B", "C"] },
              { "key": "instruct", "type": "string" } ] }
            """);
        File.WriteAllText(Path.Combine(limited, "manifest.python.json"), """{ "envGroup": "torch" }""");
        File.WriteAllText(Path.Combine(limited, "manifest.hartsy.json"),
            """{ "hartsyModel": "lim", "unsupportedSettings": ["instruct"], "unsupportedOptions": { "voice": ["b"] } }""");

        var legacy = Path.Combine(_workers, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "manifest.json"), """{ "id": "legacy", "displayName": "Legacy", "envGroup": "torch" }""");

        _data = new DataRootPaths(Path.Combine(_root, "data"));
    }

    public void Dispose() => _tmp.Dispose();

    private WorkerCatalog Catalog() => new(_workers) { RuntimeChoice = _ => _choice };

    [Fact]
    public void Overlays_give_one_manifest_per_runtime_with_python_first()
    {
        var catalog = Catalog();
        Assert.Empty(catalog.LoadErrors);
        var python = catalog.Get("dual", WorkerRuntimes.Python)!;
        var hartsy = catalog.Get("dual", WorkerRuntimes.Hartsy)!;

        Assert.Equal([WorkerRuntimes.Python, WorkerRuntimes.Hartsy], python.Runtimes);
        Assert.Equal("torch", python.EnvGroup);
        Assert.Equal(["org-model"], python.Models);
        Assert.Single(python.Settings); // from manifest.json

        Assert.True(hartsy.IsHartsy);
        Assert.Equal(WorkerRuntimes.HartsyGroup, hartsy.EnvGroup);
        Assert.Equal("dualmodel", hartsy.HartsyModel);
        Assert.Empty(hartsy.Settings); // the overlay replaced the shared list
        Assert.Equal("v1", hartsy.ModelVersion);
    }

    [Fact]
    public void A_manifest_without_overlays_is_a_python_worker()
    {
        var legacy = Catalog().Get("legacy")!;
        Assert.Equal(WorkerRuntimes.Python, legacy.Runtime);
        Assert.Equal([WorkerRuntimes.Python], legacy.Runtimes);
        Assert.Equal("torch", legacy.EnvGroup);
    }

    [Fact]
    public void Get_follows_the_runtime_choice_and_falls_back_to_the_first()
    {
        var catalog = Catalog();
        Assert.Equal(WorkerRuntimes.Python, catalog.Get("dual")!.Runtime);
        _choice = WorkerRuntimes.Hartsy;
        Assert.Equal(WorkerRuntimes.Hartsy, catalog.Get("dual")!.Runtime);
        _choice = "unknown";
        Assert.Equal(WorkerRuntimes.Python, catalog.Get("dual")!.Runtime);
    }

    [Fact]
    public void Each_runtime_has_its_own_install_state()
    {
        var catalog = Catalog();
        var exe = Path.Combine(_root, "las-hartsy-worker.exe");
        File.WriteAllText(exe, "");
        var env = new EnvironmentManager(_data, catalog, uvPath: null, hartsyWorkerPath: exe);

        // Python installed, Hartsy not.
        var python = _data.EnvPython("torch");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "");
        var state = new EnvState { PythonVersion = "3.11", InstalledWorkers = ["dual", "legacy"] };
        state.SelfTests["dual"] = state.SelfTests["legacy"] = true;
        File.WriteAllText(_data.EnvStateFile("torch"),
            System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));

        Assert.True(env.IsWorkerReady("dual"));
        _choice = WorkerRuntimes.Hartsy;
        Assert.Equal(EnvStatus.NotInstalled, env.GetWorkerStatus("dual"));
        Assert.True(env.IsWorkerReady("legacy"));

        var (executable, args, _) = env.WorkerCommand(catalog.Get("dual")!);
        Assert.Equal(exe, executable);
        Assert.Equal(["--worker", "dual", "--model", "dualmodel", "--model-version", "v1"], args);
    }

    [Fact]
    public void A_runtime_can_mark_settings_and_options_unsupported()
    {
        var catalog = Catalog();
        Assert.Empty(catalog.LoadErrors);
        var python = catalog.Get("limited", WorkerRuntimes.Python)!;
        var hartsy = catalog.Get("limited", WorkerRuntimes.Hartsy)!;

        Assert.All(python.Settings, d => Assert.Null(d.UnsupportedReason));
        Assert.Empty(python.Settings[0].UnsupportedOptions);
        Assert.Equal("Not supported on HartsyInference (.NET)", hartsy.Settings.Single(d => d.Key == "instruct").UnsupportedReason);
        Assert.Equal(["b"], hartsy.Settings.Single(d => d.Key == "voice").UnsupportedOptions);

        // A chosen unsupported option is refused; the default ("a") and other options are fine.
        Assert.Contains("\"B\"", hartsy.UnsupportedChoice(new System.Text.Json.Nodes.JsonObject { ["voice"] = "b" }));
        Assert.Null(hartsy.UnsupportedChoice(new System.Text.Json.Nodes.JsonObject { ["voice"] = "c" }));
        Assert.Null(hartsy.UnsupportedChoice(new System.Text.Json.Nodes.JsonObject()));
        Assert.Null(python.UnsupportedChoice(new System.Text.Json.Nodes.JsonObject { ["voice"] = "b" }));
    }

    [Fact]
    public void The_shipped_manifests_load_without_errors()
    {
        var catalog = new WorkerCatalog(Repo.Combine("workers"));
        Assert.Empty(catalog.LoadErrors);
        Assert.NotEmpty(catalog.All);
        foreach (var m in catalog.All)
        {
            Assert.NotEmpty(m.Runtimes);
            foreach (var runtime in m.Runtimes)
            {
                var r = catalog.Get(m.Id, runtime)!;
                Assert.Equal(runtime, r.Runtime);
                Assert.False(string.IsNullOrEmpty(r.EnvGroup), $"{m.Id} ({runtime}) has no environment");
            }
        }
        // HartsyInference is Kokoro's default (manifest "runtimeOrder"): the smallest install, no Python and no GPU needed.
        Assert.Equal(WorkerRuntimes.Hartsy, catalog.Get("kokoro")!.Runtime);
    }

    [Fact]
    public void Hidden_workers_are_left_out()
    {
        using var tmp = new TempDir();
        foreach (var (id, hidden) in new[] { ("shown", ""), ("wip", "\"hidden\": true,") })
        {
            Directory.CreateDirectory(Path.Combine(tmp.Path, id));
            File.WriteAllText(Path.Combine(tmp.Path, id, "manifest.json"),
                $$"""{ "id": "{{id}}", "displayName": "{{id}}", {{hidden}} "envGroup": "basic", "modelVersion": "1" }""");
        }
        var catalog = new WorkerCatalog(tmp.Path);
        Assert.Empty(catalog.LoadErrors);
        Assert.NotNull(catalog.Get("shown"));
        Assert.Null(catalog.Get("wip"));
    }

    [Fact]
    public void Transcription_workers_are_not_offered_as_voices()
    {
        var catalog = new WorkerCatalog(Repo.Combine("workers"));
        var parakeet = catalog.Get("parakeet")!;
        Assert.True(parakeet.IsTranscriber);
        Assert.Contains(parakeet, catalog.Transcribers);
        Assert.DoesNotContain(catalog.Generators, m => m.Id == "parakeet");
        Assert.Contains(catalog.Generators, m => m.Id == "kokoro");
        Assert.Equal(WorkerRuntimes.Onnx, parakeet.Runtime);
    }

    [Fact]
    public void Workers_are_listed_by_priority_then_name()
    {
        var ids = new WorkerCatalog(Repo.Combine("workers")).All.Select(m => m.Id).ToList();
        Assert.Equal("kokoro", ids[0]);
        Assert.Equal("test_tone", ids[^1]); // debug tools last
    }
}
