using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using NAudio.Wave;

namespace LocalAudiobookStudio.Tests;

/// <summary>
/// Fake workers for fused generations. Every part becomes a tone of 20 ms per character followed by
/// <see cref="GapMs"/> of silence. "duo" says where the parts are, "duet" doesn't, "picky" refuses parts. "listen"
/// hears that same layout back from the text it is given, words spread evenly over their part; it never hears
/// "mumble", and hears "river" as "ocean".
/// </summary>
internal sealed class FusionConnection(string workerId) : IWorkerConnection
{
    public const int GapMs = 300;

    public string WorkerId { get; } = workerId;
    public HelloInfo? Hello { get; private set; }
    public bool IsAlive { get; private set; }
    public event Action<int?>? Exited;
    public event Action<string>? LogLine { add { } remove { } }

    /// <summary>Every request a worker got, for checking what the app sent.</summary>
    public static readonly List<GenerateRequest> Requests = [];

    public Task StartAsync(CancellationToken ct)
    {
        IsAlive = true;
        Hello = new HelloInfo(2, WorkerId, "v1", "cpu");
        return Task.CompletedTask;
    }

    public Task LoadAsync(IProgress<WorkerProgress>? progress, CancellationToken ct, Action? prepared = null) => Task.CompletedTask;

    private static int Length(string text) => Math.Max(100, text.Length * 20);

    public Task<GenerateResult> GenerateAsync(GenerateRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (request.Parts == null)
        {
            EngineTests.WriteTone(request.OutPath, Length(request.Text ?? ""));
            return Task.FromResult(new GenerateResult(request.OutPath, Length(request.Text ?? ""), 1, 24000));
        }
        if (WorkerId == "picky") throw new WorkerErrorException("no thanks", false, null, ErrorCodes.Unfusable);

        var spans = new List<PartSpan?>();
        using (var w = new WaveFileWriter(request.OutPath, new WaveFormat(24000, 16, 1)))
        {
            var at = 0;
            foreach (var part in request.Parts)
            {
                var ms = Length(part.Text ?? "");
                for (int i = 0; i < 24 * ms; i++) w.WriteSample((float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 24000.0)));
                for (int i = 0; i < 24 * GapMs; i++) w.WriteSample(0f);
                spans.Add(new PartSpan(at, at + ms));
                at += ms + GapMs;
            }
        }
        var duration = request.Parts.Sum(p => Length(p.Text ?? "") + GapMs);
        return Task.FromResult(new GenerateResult(request.OutPath, duration, 1, 24000, WorkerId == "duo" ? spans : null));
    }

    public Task<TranscriptResult> TranscribeAsync(TranscribeRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var words = new List<TranscriptWord>();
        var at = 0;
        foreach (var part in (request.Text ?? "").Split('\n'))
        {
            var ms = Length(part);
            var said = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < said.Length; i++)
                if (!said[i].Contains("mumble"))
                    words.Add(new TranscriptWord(said[i].StartsWith("river") ? "ocean" : said[i],
                        at + ms * i / said.Length, at + ms * (i + 1) / said.Length - 10));
            at += ms + GapMs;
        }
        return Task.FromResult(new TranscriptResult(request.Text ?? "", words, "en"));
    }

    public Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VoiceEntry>>([]);
    public Task ShutdownAsync(TimeSpan grace) { Kill(); return Task.CompletedTask; }

    public void Kill()
    {
        if (!IsAlive) return;
        IsAlive = false;
        Exited?.Invoke(0);
    }

    public ValueTask DisposeAsync() { IsAlive = false; return ValueTask.CompletedTask; }
}

/// <summary>Planning fused generations from the speakers' context settings, and cutting their results into takes.</summary>
public sealed class FusionTests : IAsyncDisposable
{
    private readonly TempDir _tmp = new();
    private readonly GenerationScheduler _scheduler;
    private readonly StudioHost _host;

    private const string Fusing = """
        "envGroup": "basic", "modelVersion": "v1", "maxChars": 400,
        "settings": [ { "key": "voice", "type": "enum", "default": "a", "options": ["a", "b"], "perVoice": true },
                      { "key": "steps", "type": "int", "default": 10 }, { "key": "seed", "type": "seed" } ]
        """;

    public FusionTests()
    {
        var root = Path.Combine(_tmp.Path, "workers");
        void Worker(string id, string body)
        {
            Directory.CreateDirectory(Path.Combine(root, id));
            File.WriteAllText(Path.Combine(root, id, "manifest.json"), $$"""{ "id": "{{id}}", "displayName": "{{id}}", {{body}} }""");
        }
        Worker("duo", $$"""{{Fusing}}, "fusion": { "speakers": 3, "context": ["text", "audio"], "spans": true }""");
        Worker("duet", $$"""{{Fusing}}, "fusion": { "speakers": 3, "context": ["text"] }""");
        Worker("picky", $$"""{{Fusing}}, "fusion": { "speakers": 3, "context": ["text"], "spans": true }""");
        Worker("tone", """ "envGroup": "basic", "modelVersion": "v1" """);
        Worker("listen", """ "envGroup": "basic", "modelVersion": "v1", "tags": ["Transcription"] """);
        lock (FusionConnection.Requests) FusionConnection.Requests.Clear();
        _scheduler = new GenerationScheduler((w, _) => new FusionConnection(w), _ => WorkerHosting.AnyLocal);
        _scheduler.ConfigureDevices([new DeviceConfig { Kind = DeviceKind.Gpu, GpuIndex = 0, Name = "GPU 0", MaxActive = 2 }]);
        _host = StudioHost.CreateForTests(new WorkerCatalog(root), _scheduler);
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        _tmp.Dispose();
    }

    /// <summary>
    /// One chapter of six lines: narrator, Anna, Ben, the tone narrator, Anna, narrator. Every speaker on
    /// <paramref name="worker"/> batches; the tone speaker's worker doesn't fuse.
    /// </summary>
    private async Task<HeadlessProject> Open(string worker, string? transcriber = null, string lastLine = "Off she went to the market.")
    {
        var p = await HeadlessProject.OpenAsync(_host, Path.Combine(_tmp.Path, "Book-" + Guid.NewGuid().ToString("N")[..6]), "Book");
        p.ImportReplacingAll($"""
            Chapter 1

            It was a quiet morning.

            Where were you all night?

            Out walking by the river.

            The bells rang twice.

            You never tell me anything.

            {lastLine}
            """, ProjectImport.Options(_host, p.Project), keepTakes: false);
        Speaker Add(string id, string w) => p.Project.Speakers[id] = new Speaker
        {
            Id = id, DisplayName = id, Worker = w, Transcriber = transcriber ?? "", Settings = _host.Catalog.Get(w)!.DefaultSettings(),
            // Lines may be context for others, but get none themselves.
            Context = new SpeakerContext { Batch = w != "tone", Include = true, Before = 0, After = 0 },
        };
        Add("narrator", worker);
        Add("anna", worker);
        Add("ben", worker);
        Add("bells", "tone");
        // The spoken chapter heading isn't part of the scene.
        p.Project.Chapters[0].Items.Remove(p.Project.Chapters[0].Items.First(i => i.Kind == ItemKind.Tts));
        var items = p.Project.Chapters[0].Items.Where(i => i.Kind == ItemKind.Tts).ToList();
        string[] who = ["narrator", "anna", "ben", "bells", "anna", "narrator"];
        for (int i = 0; i < items.Count; i++) items[i].Speaker = who[i];
        return p;
    }

    private static List<TimelineItem> Lines(HeadlessProject p) => p.Project.Chapters[0].Items.Where(i => i.Kind == ItemKind.Tts).ToList();

    [Fact]
    public async Task NeighboursThatAllowItAreBatched()
    {
        using var p = await Open("duo");
        var lines = Lines(p);
        var errors = new List<string>();

        var jobs = p.Engine.BuildJobs(lines, JobPriority.Normal, errors);

        Assert.Empty(errors);
        // narrator, Anna, Ben together; the bells alone (their worker doesn't fuse); Anna and the narrator together.
        Assert.Equal([3, 0, 2], jobs.Select(j => j.Members?.Count ?? 0));
        Assert.Equal(lines[3].Id, jobs[1].ItemId);
        var first = jobs[0].Request;
        Assert.Equal(["narrator", "anna", "ben"], first.Parts!.Select(x => x.Speaker));
        Assert.All(first.Parts!, x => Assert.Equal(PartRoles.Generate, x.Role));
        // The voice setting goes on each part; the rest is shared.
        Assert.True(first.Parts![0].Settings!.ContainsKey("voice"));
        Assert.False(first.Settings.ContainsKey("voice"));

        // A shared setting that differs splits the group; a voice setting doesn't.
        lines[1].SettingsOverride = new() { ["voice"] = "b" };
        lines[2].SettingsOverride = new() { ["steps"] = 20 };
        Assert.Equal([2, 0, 0, 2], p.Engine.BuildJobs(lines, JobPriority.Normal, errors).Select(j => j.Members?.Count ?? 0));
    }

    [Fact]
    public async Task ContextComesFromTheSpeakersSettings()
    {
        using var p = await Open("duo");
        var lines = Lines(p);
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = true, Before = 2, After = 1 };

        var job = Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, []));

        // The narrator's line after it is left out: it would be a fourth voice, and duo takes three.
        var parts = job.Request.Parts!;
        Assert.Equal([PartRoles.Context, PartRoles.Context, PartRoles.Generate], parts.Select(x => x.Role));
        Assert.Equal(["ben", "bells", "anna"], parts.Select(x => x.Speaker));
        // The bells' speaker uses another worker: its line comes without a voice.
        Assert.Null(parts[1].Settings);
        Assert.Equal("a", parts[0].Settings!["voice"]!.GetValue<string>());
        Assert.Equal(lines[4].Id, Assert.Single(job.Members!).ItemId);

        // Context that isn't included, and nothing to batch: an ordinary job.
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = false, Before = 2, Batch = true };
        Assert.Null(Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, [])).Members);

        // A line that doesn't fit in "max characters" ends the context.
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = true, Before = 3, MaxChars = 30 };
        Assert.Equal(["bells", "anna"], Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, [])).Request.Parts!.Select(x => x.Speaker));

        // A batch gets context around it, from its first speaker's "before" and its last one's "after".
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = true, Before = 1, Batch = true };
        p.Project.Speakers["narrator"].Context = new SpeakerContext { Include = true, After = 1, Batch = true };
        parts = Assert.Single(p.Engine.BuildJobs([lines[4], lines[5]], JobPriority.Normal, [])).Request.Parts!;
        Assert.Equal(["bells", "anna", "narrator"], parts.Select(x => x.Speaker));
        Assert.Equal([PartRoles.Context, PartRoles.Generate, PartRoles.Generate], parts.Select(x => x.Role));
    }

    [Fact]
    public async Task WorkersThatDontFuseGetContextAsText()
    {
        using var p = await Open("duo", transcriber: "listen");
        var lines = Lines(p);
        // The bells' worker renders one line at a time: context is plain text around it, batching doesn't apply.
        p.Project.Speakers["bells"].Context = new SpeakerContext { Include = true, Before = 1, After = 1, Batch = true };

        var job = Assert.Single(p.Engine.BuildJobs([lines[3]], JobPriority.Normal, []));
        Assert.Null(job.Request.Parts);
        Assert.Equal("Out walking by the river. The bells rang twice. You never tell me anything.", job.Request.Text);
        Assert.Equal(lines[3].Id, Assert.Single(job.Members!).ItemId);

        // Transcription finds the line again.
        await p.GenerateAsync(GenerateScope.Missing, new HashSet<int> { 0 }, null, CancellationToken.None);
        var take = p.Engine.Takes.Selected(lines[3])!;
        Assert.Equal(TakeFusion.Transcript, take.Fusion!.Split);
        Assert.Equal("tone", take.Worker);
        Assert.Equal(0, take.TranscriptErrors);
    }

    [Fact]
    public async Task BackgroundItemsAndPausesArePassedOverAndClipsEndARun()
    {
        using var p = await Open("duo");
        var lines = Lines(p);
        var chapter = p.Project.Chapters[0];

        // The bells play in the background: the conversation runs on through them; they are generated on their own.
        lines[3].Background = true;
        chapter.Items.Insert(chapter.Items.IndexOf(lines[2]), new TimelineItem { Kind = ItemKind.Pause, DurationMs = 500 });
        var jobs = p.Engine.BuildJobs(lines, JobPriority.Normal, []);
        Assert.Equal([5, 0], jobs.Select(j => j.Members?.Count ?? 0));
        Assert.Equal(lines[3].Id, jobs[1].ItemId);

        // Context looks past them too.
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = true, Before = 1 };
        Assert.Equal(["ben", "anna"], Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, [])).Request.Parts!.Select(x => x.Speaker));

        // A clip ends a batch, and the context.
        chapter.Items.Insert(chapter.Items.IndexOf(lines[4]), new TimelineItem { Kind = ItemKind.Clip, ClipFile = "assets/door.wav" });
        Assert.Null(Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, [])).Members);
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = true, Before = 0, After = 0, Batch = true };
        Assert.Equal([3, 0, 2], p.Engine.BuildJobs(lines, JobPriority.Normal, []).Select(j => j.Members?.Count ?? 0));
    }

    [Fact]
    public async Task ContextEndsAtASpeakerWithoutIt()
    {
        using var p = await Open("duo");
        var lines = Lines(p);
        p.Project.Speakers["anna"].Context = new SpeakerContext { Include = true, Before = 2 };

        // The bells' speaker doesn't include context: Anna's second line gets none.
        p.Project.Speakers["bells"].Context.Include = false;
        Assert.Null(Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, [])).Members);

        // With it, but taking none itself, its line is context for Anna.
        p.Project.Speakers["bells"].Context = new SpeakerContext { Include = true, Before = 0, After = 0 };
        Assert.Equal(["ben", "bells", "anna"], Assert.Single(p.Engine.BuildJobs([lines[4]], JobPriority.Normal, [])).Request.Parts!.Select(x => x.Speaker));
    }

    [Fact]
    public async Task BatchesKeepToEverySpeakersLimits()
    {
        using var p = await Open("duo");
        var lines = Lines(p);

        // Anna's batches have at most two segments: narrator and Anna go together, Ben alone.
        p.Project.Speakers["anna"].Context.BatchMaxSegments = 2;
        Assert.Equal([2, 0, 0, 2], p.Engine.BuildJobs(lines, JobPriority.Normal, []).Select(j => j.Members?.Count ?? 0));

        // The narrator's at most 30 characters: "It was a quiet morning." and Anna's line together are 48.
        p.Project.Speakers["anna"].Context.BatchMaxSegments = null;
        p.Project.Speakers["narrator"].Context.BatchMaxChars = 30;
        Assert.Equal([0, 2, 0, 0, 0], p.Engine.BuildJobs(lines, JobPriority.Normal, []).Select(j => j.Members?.Count ?? 0));
    }

    [Fact]
    public async Task SpansFromTheWorkerCutTheTakes()
    {
        using var p = await Open("duo");
        var lines = Lines(p);

        var r = await p.GenerateAsync(GenerateScope.Missing, null, null, CancellationToken.None);

        Assert.Equal(6, r.Queued);
        Assert.Equal(6, r.Completed);
        Assert.All(lines, i => Assert.Equal(SegmentAudioState.Ready, p.Engine.StateOf(i)));
        foreach (var i in lines.Where(i => i.Speaker != "bells"))
        {
            var take = p.Engine.Takes.Selected(i)!;
            Assert.Equal(TakeFusion.Worker, take.Fusion!.Split);
            Assert.InRange(take.DurationMs, i.Text!.Length * 20 - 2, i.Text.Length * 20 + 2); // exactly its part
        }
        Assert.Equal(3, p.Engine.Takes.Selected(lines[0])!.Fusion!.Segments);
        Assert.Null(p.Engine.Takes.Selected(lines[3])!.Fusion);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(p.Project.Paths.CacheDir, "fused")));
    }

    [Fact]
    public async Task WithoutSpansTheTranscriptCutsTheTakes()
    {
        // The last line's last word isn't heard: it can't be cut out, so it is generated again on its own.
        using var p = await Open("duet", transcriber: "listen", lastLine: "Off she went, mumble");
        var lines = Lines(p);

        var r = await p.GenerateAsync(GenerateScope.Missing, null, null, CancellationToken.None);

        Assert.Equal(6, r.Completed);
        Assert.All(lines, i => Assert.Equal(SegmentAudioState.Ready, p.Engine.StateOf(i)));
        foreach (var i in lines.Take(3).Append(lines[4]))
        {
            var take = p.Engine.Takes.Selected(i)!;
            Assert.Equal(TakeFusion.Transcript, take.Fusion!.Split);
            Assert.Equal("duet", take.Worker); // the worker that made it, not the one that cut it
            // Ben's last word is misheard ("ocean"), but it still says where his line ends: it is cut out.
            Assert.Equal(i == lines[2] ? 1 : 0, take.TranscriptErrors);
            Assert.True(File.Exists(p.Project.Paths.TakeTranscript(i.Id, take.Take)));
        }
        Assert.Null(p.Engine.Takes.Selected(lines[5])!.Fusion);
        lock (FusionConnection.Requests)
            Assert.Contains(FusionConnection.Requests, q => q.Parts == null && q.Text == lines[5].Text);
    }

    [Fact]
    public async Task KeptGenerationsRecordWhatWasSentAndHowItWasCut()
    {
        _host.Settings.KeepFusedGenerations = true;
        using var p = await Open("duet", transcriber: "listen", lastLine: "Off she went, mumble");
        var lines = Lines(p);

        await p.GenerateAsync(GenerateScope.Missing, null, null, CancellationToken.None);

        var id = p.Engine.Takes.Selected(lines[0])!.Fusion!.Record!;
        var dir = p.Project.Paths.FusionRecordDir(id);
        Assert.True(File.Exists(Path.Combine(dir, FusionRecord.AudioFile)));
        var record = Core.Serialization.CoreJson.Deserialize<FusionRecord>(File.ReadAllText(Path.Combine(dir, FusionRecord.RecordFile)));
        Assert.Equal("duet", record.Worker);
        Assert.Equal(TakeFusion.Transcript, record.Split);
        Assert.Equal("listen", record.Transcriber);
        Assert.NotEmpty(record.Transcript!);
        Assert.Equal(["narrator", "anna", "ben"], record.Parts.Select(x => x.Speaker));
        Assert.All(record.Parts, x => Assert.True(x.CutEndMs > x.CutStartMs));
        Assert.Equal(p.Engine.Takes.Selected(lines[1])!.Take, record.Parts[1].Take);

        // The other batch's last line wasn't cut out, and says why.
        var other = p.Engine.Takes.Selected(lines[4])!.Fusion!.Record!;
        var second = Core.Serialization.CoreJson.Deserialize<FusionRecord>(
            File.ReadAllText(Path.Combine(p.Project.Paths.FusionRecordDir(other), FusionRecord.RecordFile)));
        Assert.Null(second.Parts[1].Take);
        Assert.Contains("wasn't heard", second.Parts[1].Note);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(p.Project.Paths.CacheDir, "fused")));
    }

    [Fact]
    public async Task WithoutSpansOrTranscriberNothingIsFused()
    {
        using var p = await Open("duet");
        Assert.All(p.Engine.BuildJobs(Lines(p), JobPriority.Normal, []), j => Assert.Null(j.Members));
    }

    [Fact]
    public async Task RefusedPartsAreGeneratedOneByOne()
    {
        using var p = await Open("picky");
        var lines = Lines(p);

        var r = await p.GenerateAsync(GenerateScope.Missing, null, null, CancellationToken.None);

        Assert.Equal(6, r.Completed);
        Assert.Equal(0, r.Failed);
        Assert.All(lines, i => Assert.Null(p.Engine.Takes.Selected(i)!.Fusion));
    }
}
