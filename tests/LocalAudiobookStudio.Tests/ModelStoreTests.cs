using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Models;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Tests;

/// <summary>The app's model store against a fake web server: downloads, checks, links, overrides and deleting.</summary>
public sealed class ModelStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeServer _server = new();
    private string Models => Path.Combine(_tmp.Path, "models");

    public void Dispose() => _tmp.Dispose();

    private static readonly byte[] Weights = Encoding.UTF8.GetBytes(new string('w', 5000));
    private static readonly byte[] Config = Encoding.UTF8.GetBytes("{\"layers\": 4}");

    private static string Sha256(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
    private static string GitSha1(byte[] b) => Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {b.Length}\0"), .. b]));

    private static ModelEntry Entry(string id, params ModelSource[] sources) => new()
    {
        Id = id,
        DisplayName = id,
        Sources = [.. sources],
        Files = new(StringComparer.Ordinal)
        {
            ["model.safetensors"] = new ModelFileInfo { Size = Weights.Length, Sha256 = Sha256(Weights) },
            ["sub/config.json"] = new ModelFileInfo { Size = Config.Length, GitSha1 = GitSha1(Config) },
        },
    };

    private static ModelSource Hub(string repo) => new() { Repo = repo, Revision = "abc123" };

    private void Serve(string repo, byte[]? weights = null, byte[]? config = null)
    {
        _server.Files[$"https://hub.test/{repo}/resolve/abc123/model.safetensors"] = weights ?? Weights;
        _server.Files[$"https://hub.test/{repo}/resolve/abc123/sub/config.json"] = config ?? Config;
    }

    private ModelStore Store(params ModelEntry[] entries) =>
        new(ModelCatalog.FromEntries(entries), Path.Combine(Models, "store"), Models, handler: _server) { HubEndpoint = "https://hub.test" };

    [Fact]
    public async Task Downloads_checks_and_then_trusts_the_marker()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        var location = await store.EnsureAsync("m");

        Assert.Equal(Weights, File.ReadAllBytes(Path.Combine(location.Path, "model.safetensors")));
        Assert.Equal(Config, File.ReadAllBytes(Path.Combine(location.Path, "sub", "config.json")));
        Assert.Equal(ModelStatus.Ready, store.GetStatus("m"));
        Assert.Equal(2, _server.Requests.Count);

        _server.Requests.Clear();
        await store.EnsureAsync("m");
        Assert.Empty(_server.Requests); // the marker vouches for the files: no network, no hashing
    }

    [Fact]
    public async Task A_source_with_wrong_bytes_is_skipped_for_the_next()
    {
        Serve("bad/m", weights: Encoding.UTF8.GetBytes(new string('x', 5000)));
        Serve("good/m");
        var store = Store(Entry("m", Hub("bad/m"), Hub("good/m")));
        var location = await store.EnsureAsync("m");
        Assert.Equal(Weights, File.ReadAllBytes(Path.Combine(location.Path, "model.safetensors")));
    }

    [Fact]
    public async Task A_changed_file_is_downloaded_again()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        var location = await store.EnsureAsync("m");
        File.WriteAllText(Path.Combine(location.Path, "model.safetensors"), "broken");
        Assert.Equal(ModelStatus.Partial, store.GetStatus("m"));

        _server.Requests.Clear();
        await store.EnsureAsync("m");
        Assert.Equal(Weights, File.ReadAllBytes(Path.Combine(location.Path, "model.safetensors")));
        Assert.Single(_server.Requests); // only the changed file
    }

    [Fact]
    public async Task An_interrupted_download_resumes()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        var folder = store.FolderOf("m");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.safetensors.partial"), Weights[..1000]);

        await store.EnsureAsync("m");
        Assert.Equal(Weights, File.ReadAllBytes(Path.Combine(folder, "model.safetensors")));
        Assert.Contains(_server.Requests, r => r.Range == 1000);
    }

    [Fact]
    public async Task Required_models_come_along()
    {
        Serve("org/m");
        Serve("org/tok");
        var main = Entry("m", Hub("org/m"));
        main.Requires.Add("tok");
        var store = Store(main, Entry("tok", Hub("org/tok")));
        await store.EnsureAsync("m");
        Assert.Equal(ModelStatus.Ready, store.GetStatus("tok"));
    }

    [Fact]
    public async Task A_linked_folder_is_the_store_folder_and_goes_with_the_model()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        var hartsy = Path.Combine(Models, "audio", "tts", "org--m");
        var single = Path.Combine(Models, "audio", "music", "renamed.safetensors");
        await store.EnsureAsync("m", [new ModelLink(hartsy), new ModelLink(single, "model.safetensors")]);

        Assert.True(DirectoryLink.IsLink(hartsy));
        Assert.Equal(Config, File.ReadAllBytes(Path.Combine(hartsy, "sub", "config.json")));
        Assert.Equal(Weights, File.ReadAllBytes(single));
        // What the worker writes into its folder lands in the store, and counts once.
        File.WriteAllText(Path.Combine(hartsy, "converted.bin"), "made by the worker");
        Assert.True(File.Exists(Path.Combine(store.FolderOf("m"), "converted.bin")));
        Assert.Equal(Weights.Length + Config.Length + "made by the worker".Length, store.SizeOnDisk("m") - new FileInfo(Path.Combine(store.FolderOf("m"), ModelStore.MarkerName)).Length);

        _server.Requests.Clear();
        await store.EnsureAsync("m", [new ModelLink(hartsy)]); // already linked: nothing changes
        Assert.True(DirectoryLink.IsLink(hartsy));
        Assert.Empty(_server.Requests);

        await store.DeleteAsync("m");
        Assert.False(Directory.Exists(store.FolderOf("m")));
        Assert.False(Directory.Exists(hartsy));
        Assert.False(File.Exists(single));
        Assert.Equal(ModelStatus.NotDownloaded, store.GetStatus("m"));
    }

    [Fact]
    public async Task A_real_folder_in_the_way_is_replaced_and_its_extra_files_kept()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        var hartsy = Path.Combine(Models, "audio", "tts", "org--m");
        Directory.CreateDirectory(Path.Combine(hartsy, "voices"));
        File.WriteAllText(Path.Combine(hartsy, "model.safetensors"), "the worker's own older download");
        File.WriteAllText(Path.Combine(hartsy, "voices", "a.pt"), "voice");

        await store.EnsureAsync("m", [new ModelLink(hartsy)]);
        Assert.True(DirectoryLink.IsLink(hartsy));
        Assert.Equal(Weights, File.ReadAllBytes(Path.Combine(hartsy, "model.safetensors")));
        Assert.Equal("voice", File.ReadAllText(Path.Combine(store.FolderOf("m"), "voices", "a.pt")));
    }

    [Fact]
    public async Task Links_outside_the_models_folder_are_refused()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        await Assert.ThrowsAsync<ModelStoreException>(() => store.EnsureAsync("m", [new ModelLink(Path.Combine(_tmp.Path, "elsewhere"))]));
    }

    [Fact]
    public async Task An_override_folder_is_used_as_it_is()
    {
        var own = Path.Combine(_tmp.Path, "my-finetune");
        Directory.CreateDirectory(own);
        var store = Store(Entry("m", Hub("org/m")));
        store.Override = id => id == "m" ? own : null;

        var location = await store.EnsureAsync("m");
        Assert.True(location.IsOverride);
        Assert.Equal(own, location.Path);
        Assert.Equal(ModelStatus.Override, store.GetStatus("m"));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Extra_files_come_from_the_models_source_unchecked()
    {
        _server.Files["https://voices.test/en-Carter.wav"] = [1, 2, 3];
        var voices = new ModelEntry { Id = "voices", Sources = [new ModelSource { Url = "https://voices.test/{file}" }] };
        var store = Store(voices);

        var path = await store.EnsureFileAsync("voices", "en-Carter.wav");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        await Assert.ThrowsAsync<ModelStoreException>(() => store.EnsureFileAsync("voices", "missing.wav"));
        await Assert.ThrowsAsync<ModelStoreException>(() => store.EnsureFileAsync("voices", "../escape.wav"));
    }

    [Fact]
    public async Task A_refused_gated_download_says_what_to_do()
    {
        _server.Status = HttpStatusCode.Unauthorized;
        var store = Store(Entry("m", Hub("stabilityai/gated")));
        var ex = await Assert.ThrowsAsync<ModelStoreException>(() => store.EnsureAsync("m"));
        Assert.Contains("Hugging Face token", ex.Message);
    }

    [Fact]
    public async Task Worker_requests_get_replies_with_the_path()
    {
        Serve("org/m");
        var store = Store(Entry("m", Hub("org/m")));
        var reply = await store.HandleRequestAsync(new WorkerMessage { Type = MessageTypes.NeedModel, Ref = "r1", Model = "m" }, null, default);
        Assert.Equal(MessageTypes.ModelReady, reply.Type);
        Assert.Equal("r1", reply.Ref);
        Assert.Equal(store.FolderOf("m"), reply.Path);

        var unknown = await store.HandleRequestAsync(new WorkerMessage { Type = MessageTypes.NeedModel, Ref = "r2", Model = "nope" }, null, default);
        Assert.Equal(MessageTypes.ModelFailed, unknown.Type);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var c = await store.HandleRequestAsync(new WorkerMessage { Type = MessageTypes.NeedModel, Ref = "r3", Model = "m" }, null, cancelled.Token);
        Assert.True(c.Cancelled);
    }

    [Fact]
    public void Pin_patterns_match_like_huggingface_hub()
    {
        Assert.True(ModelPinner.Glob("*.json", "config.json"));
        Assert.True(ModelPinner.Glob("*.json", "speech_tokenizer/config.json"));
        Assert.True(ModelPinner.Glob("voices/*.pt", "voices/af_heart.pt"));
        Assert.False(ModelPinner.Glob("*.json", "model.safetensors"));
    }

    [Fact]
    public void Shipped_models_are_complete_and_every_worker_names_known_ones()
    {
        var workers = new WorkerCatalog(Repo.Combine("workers"));
        Assert.Empty(workers.LoadErrors);
        foreach (var m in workers.Models.All)
        {
            Assert.True(ModelCatalog.IsValidId(m.Id), m.Id);
            // Every model with a Hub source has its file list (las models pin).
            if (m.Sources.Any(s => s.IsHub)) Assert.True(m.Files.Count > 0, $"{m.Id} isn't pinned");
            Assert.All(m.Sources.Where(s => s.IsHub), s => Assert.Matches("^[0-9a-f]{40}$", s.Revision ?? ""));
            // A real hash, not just a non-empty one: a masked or hand-edited value would fail every download.
            Assert.All(m.Files, f => Assert.Matches(f.Value.Sha256 != null ? "^[0-9a-f]{64}$" : "^[0-9a-f]{40}$", f.Value.Expected));
        }
        Assert.All(workers.AllRuntimes.Where(w => !w.External && w.Id != "test_tone"), w => Assert.NotEmpty(w.Models));
    }

    /// <summary>Serves fixed files, honours Range requests and records what was asked.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<(string Url, long? Range)> Requests { get; } = new();
        public HttpStatusCode? Status { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            Requests.Add((url, from));
            if (Status is { } status) return Task.FromResult(new HttpResponseMessage(status));
            if (!Files.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (from is long start)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)start..]) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
