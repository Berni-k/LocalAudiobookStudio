using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using NAudio.Wave;

namespace LocalAudiobookStudio.Tests;

/// <summary>A fake worker that writes a real WAV: a 440 Hz tone, 20 ms per character of text.</summary>
internal sealed class ToneConnection(string workerId) : IWorkerConnection
{
    public string WorkerId { get; } = workerId;
    public HelloInfo? Hello { get; private set; }
    public bool IsAlive { get; private set; }
    public event Action<int?>? Exited;
    public event Action<string>? LogLine { add { } remove { } }

    public Task StartAsync(CancellationToken ct)
    {
        IsAlive = true;
        Hello = new HelloInfo(1, WorkerId, "v1", "cpu");
        return Task.CompletedTask;
    }

    public Task LoadAsync(IProgress<WorkerProgress>? progress, CancellationToken ct, Action? prepared = null)
    {
        prepared?.Invoke();
        return Task.CompletedTask;
    }

    public Task<GenerateResult> GenerateAsync(GenerateRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct)
    {
        var ms = Math.Max(100, (request.Text ?? "").Length * 20);
        EngineTests.WriteTone(request.OutPath, ms);
        return Task.FromResult(new GenerateResult(request.OutPath, ms, request.Seed ?? 1, 24000));
    }

    public Task<TranscriptResult> TranscribeAsync(TranscribeRequest request, IProgress<WorkerProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

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

/// <summary>The engine without the app: one-off generation helpers, and a project imported, generated and erased headless.</summary>
public sealed class EngineTests : IAsyncDisposable
{
    private readonly TempDir _tmp = new();
    private readonly GenerationScheduler _scheduler;
    private readonly StudioHost _host;

    public EngineTests()
    {
        var workers = Path.Combine(_tmp.Path, "workers", "tone");
        Directory.CreateDirectory(workers);
        File.WriteAllText(Path.Combine(workers, "manifest.json"), """
            { "id": "tone", "displayName": "Tone", "modelVersion": "v1", "maxChars": 60,
              "settings": [ { "key": "voice", "type": "enum", "default": "a", "options": ["a", "b"] },
                            { "key": "speed", "type": "float", "default": 1.0 } ] }
            """);
        _scheduler = new GenerationScheduler((w, _) => new ToneConnection(w), _ => WorkerHosting.AnyLocal);
        _scheduler.ConfigureDevices([new DeviceConfig { Kind = DeviceKind.Gpu, GpuIndex = 0, Name = "GPU 0", MaxActive = 2 }]);
        _host = StudioHost.CreateForTests(new WorkerCatalog(Path.Combine(_tmp.Path, "workers")), _scheduler);
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        _tmp.Dispose();
    }

    internal static void WriteTone(string path, int ms)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new WaveFileWriter(path, new WaveFormat(24000, 16, 1));
        var n = 24000 * ms / 1000;
        for (int i = 0; i < n; i++) w.WriteSample((float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 24000.0)));
    }

    // ------------------------------------------------------------------ one-off generation helpers

    [Fact]
    public void SplitForWorkerKeepsParagraphsAndLimit()
    {
        var text = "First paragraph, short.\nStill the first.\n\n  \nSecond one. It has two sentences that together are too long for the limit.";
        var parts = OneShot.SplitForWorker(text, 60);
        Assert.Equal(2, parts.Count);
        Assert.Equal(["First paragraph, short. Still the first."], parts[0]);
        Assert.True(parts[1].Count > 1);
        Assert.All(parts[1], p => Assert.True(p.Length <= 60));
    }

    [Fact]
    public void ConcatenateWavsAddsGaps()
    {
        var a = Path.Combine(_tmp.Path, "a.wav");
        var b = Path.Combine(_tmp.Path, "b.wav");
        var outPath = Path.Combine(_tmp.Path, "ab.wav");
        WriteTone(a, 100);
        WriteTone(b, 200);
        OneShot.ConcatenateWavs([a, b], [50, 999], outPath); // no gap after the last part
        Assert.Equal(350, AudioDecoder.ReadInfo(outPath).DurationMs, 1);
    }

    [Fact]
    public void ParseSettingValueFollowsManifestTypes()
    {
        var m = _host.Catalog.Get("tone")!;
        Assert.Equal(1.5, OneShot.ParseSettingValue(m, "speed", "1.5")!.GetValue<double>());
        Assert.Equal("b", OneShot.ParseSettingValue(m, "voice", "b")!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => OneShot.ParseSettingValue(m, "voice", "zzz"));
        Assert.Equal(7, OneShot.ParseSettingValue(m, "unknown", "7")!.GetValue<long>());
        Assert.True(OneShot.ParseSettingValue(m, "unknown", "true")!.GetValue<bool>());
        Assert.Equal("text", OneShot.ParseSettingValue(m, "unknown", "text")!.GetValue<string>());
    }

    // ------------------------------------------------------------------ headless project

    private const string Book = """
        Chapter 1

        It was a quiet morning in the village.

        The baker opened his shop early.

        Chapter 2

        By noon, the square was full of people.
        """;

    [Fact]
    public async Task ImportGenerateChangeAndEraseHeadless()
    {
        var folder = Path.Combine(_tmp.Path, "Book");
        using var p = await HeadlessProject.OpenAsync(_host, folder, "Book");
        var imported = p.ImportReplacingAll(Book, ProjectImport.Options(_host, p.Project), keepTakes: false);
        Assert.Equal(2, imported.Chapters);
        var segments = p.Project.AllItems().Where(i => i.IsGenerated).ToList();
        Assert.All(segments, i => Assert.Equal(SegmentAudioState.NotGenerated, p.Engine.StateOf(i)));

        // Everything is generated, selected and saved.
        var events = 0;
        var r = await p.GenerateAsync(GenerateScope.Missing, null, _ => Interlocked.Increment(ref events), CancellationToken.None);
        Assert.Equal(segments.Count, r.Queued);
        Assert.Equal(segments.Count, r.Completed);
        Assert.Equal(segments.Count, events);
        Assert.All(segments, i => Assert.Equal(SegmentAudioState.Ready, p.Engine.StateOf(i)));
        Assert.Equal(0, (await p.GenerateAsync(GenerateScope.Missing, null, null, CancellationToken.None)).Queued);

        // Takes are stored as Opus (the default), the workers' WAVs gone; the timeline still knows their lengths.
        Assert.Equal(segments.Count, Directory.EnumerateFiles(p.Project.Paths.AudioDir, "take-*.opus", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateFiles(p.Project.Paths.AudioDir, "take-*.wav", SearchOption.AllDirectories));
        Assert.All(segments, i => Assert.Equal(AudioFormats.Opus, p.Engine.Takes.Selected(i)!.Format));

        // The timeline uses the real lengths; export covers both chapters.
        var (layout, _) = await p.Engine.ComputeTimelineAsync();
        Assert.False(layout.HasEstimates);
        var chapters = ExportPlanner.Chapters(p.Project.Chapters, layout, [0, 1]);
        Assert.Equal(2, chapters.Count);
        Assert.True(ExportPlanner.IsComplete(p.Engine, layout, 1));

        // A speaker change makes its segments stale; only chapter 2 is regenerated.
        var narrator = p.Project.Speakers["narrator"];
        narrator.Settings["voice"] = "b";
        Assert.All(segments, i => Assert.Equal(SegmentAudioState.Stale, p.Engine.StateOf(i)));
        r = await p.GenerateAsync(GenerateScope.Missing, new HashSet<int> { 1 }, null, CancellationToken.None);
        Assert.Equal(p.Project.Chapters[1].Items.Count(i => i.IsGenerated), r.Completed);
        Assert.All(p.Project.Chapters[1].Items, i => Assert.Equal(SegmentAudioState.Ready, p.Engine.StateOf(i)));

        // Re-importing with --keep-takes keeps the takes of unchanged text.
        var changed = Book.Replace("The baker opened his shop early.", "The baker opened her shop late.");
        var again = p.ImportReplacingAll(changed, ProjectImport.Options(_host, p.Project), keepTakes: true);
        Assert.Equal(again.Segments - 1, again.KeptTakes);

        // Erasing leaves nothing generated and no take files.
        var erased = p.EraseTakes();
        Assert.True(erased.FilesDeleted > 0);
        Assert.All(p.Project.AllItems().Where(i => i.IsGenerated), i => Assert.Equal(SegmentAudioState.NotGenerated, p.Engine.StateOf(i)));
        Assert.Empty(Directory.EnumerateFiles(p.Project.Paths.AudioDir, "take-*.wav", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(p.Project.Paths.AudioDir, "take-*.opus", SearchOption.AllDirectories));

        // What was saved is what a fresh open sees.
        p.Dispose();
        using var reopened = await HeadlessProject.OpenAsync(_host, folder);
        Assert.Equal(again.Segments, reopened.Project.AllItems().Count());
    }

    [Fact]
    public async Task WavTakesCompressToOpusAndStayPlayable()
    {
        using var p = await HeadlessProject.OpenAsync(_host, Path.Combine(_tmp.Path, "Wav"), "Wav");
        p.Project.Defaults.AudioFormat = AudioFormats.Wav;
        p.ImportReplacingAll(Book, ProjectImport.Options(_host, p.Project), keepTakes: false);
        await p.GenerateAsync(GenerateScope.Missing, null, null, CancellationToken.None);
        var segments = p.Project.AllItems().Where(i => i.IsGenerated).ToList();
        var lengths = segments.ToDictionary(i => i.Id, i => p.Engine.Takes.Selected(i)!.DurationMs);
        Assert.Equal(segments.Count, p.Engine.UncompressedTakes().Count);

        var (count, saved) = p.Engine.CompressTakes(null, CancellationToken.None);

        Assert.Equal(segments.Count, count);
        Assert.True(saved > 0);
        Assert.Equal(0, p.Engine.UncompressedTakes().Count);
        Assert.Empty(Directory.EnumerateFiles(p.Project.Paths.AudioDir, "take-*.wav", SearchOption.AllDirectories));
        foreach (var item in segments)
        {
            // The take file says Opus (also after a reload from disk) and the audio decodes to the same length.
            p.Engine.Takes.Reload(item.Id);
            var take = p.Engine.Takes.Selected(item)!;
            Assert.Equal(AudioFormats.Opus, take.Format);
            var decoded = AudioDecoder.Decode(p.Project.Paths.TakeAudio(item.Id, take));
            Assert.InRange(decoded.DurationMs, lengths[item.Id] - 1, lengths[item.Id] + 1);
        }
    }

    [Fact]
    public async Task SegmentExportPlansEachGeneratedSectionAloneAndSkipsTheRest()
    {
        using var p = await HeadlessProject.OpenAsync(_host, Path.Combine(_tmp.Path, "Lines"), "Lines");
        p.ImportReplacingAll(Book, ProjectImport.Options(_host, p.Project), keepTakes: false);
        await p.GenerateAsync(GenerateScope.Missing, new HashSet<int> { 0 }, null, CancellationToken.None);
        var first = p.Project.Chapters[0].Items.Where(i => i.HasAudio).ToList();
        var second = p.Project.Chapters[1].Items.Count(i => i.HasAudio);

        var (segments, skipped) = ExportPlanner.Segments(p.Engine, [0, 1]);

        Assert.Equal(first.Count, segments.Count);
        Assert.Equal(second, skipped);
        Assert.Equal("Chapter 1 - 1", segments[0].Name);
        // Each plan is the section alone from 0: its trimmed take, nothing else.
        var take = p.Engine.Takes.Selected(first[0])!;
        Assert.Single(segments[0].Plan.Items);
        Assert.Equal(0, segments[0].Plan.Items[0].StartMs);
        Assert.Equal(take.DurationMs - take.TrimStartMs - take.TrimEndMs, segments[0].Plan.TotalMs, 1.0);
    }

    [Fact]
    public async Task SecondOpenIsRefusedWhileLocked()
    {
        var folder = Path.Combine(_tmp.Path, "Locked");
        using var first = await HeadlessProject.OpenAsync(_host, folder, "Locked");
        await Assert.ThrowsAsync<IOException>(() => HeadlessProject.OpenAsync(_host, folder));
    }

    [Fact]
    public async Task OneShotJobWritesExactlyItsOutput()
    {
        var m = _host.Catalog.Get("tone")!;
        var outPath = Path.Combine(_tmp.Path, "preview.wav");
        var job = OneShot.Job(m, "Hello there.", null, m.DefaultSettings(), outPath, "test", JobPriority.Normal);
        var done = new TaskCompletionSource<GenerationJob>();
        _scheduler.JobCompleted += j => { if (j.Id == job.Id) done.TrySetResult(j); };
        _scheduler.JobFailed += j => { if (j.Id == job.Id) done.TrySetResult(j); };
        _scheduler.Enqueue(job);
        var finished = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Completed, finished.State);
        Assert.True(File.Exists(outPath));
        Assert.Single(Directory.EnumerateFiles(_tmp.Path, "*.wav"));
    }
}
