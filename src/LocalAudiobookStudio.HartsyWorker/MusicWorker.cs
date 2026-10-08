using System.Text.Json.Nodes;
using HartsyInference.Audio.Cache;
using HartsyInference.Engine;
using HartsyInference.Engine.Audio;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>
/// A music / sound model from HartsyInference's music catalog ("hartsyModality": "music"): the segment text is
/// the prompt and the segment length the duration.
/// </summary>
internal sealed class MusicWorker(WorkerOptions options) : IModelWorker
{
    private readonly object _gate = new();
    private InferenceEngine? _engine;
    private ModelSpec? _spec;
    private string? _currentId;
    private CancellationTokenSource? _currentCts;

    public async Task LoadAsync(string? id)
    {
        Protocol.Progress(id, 0.02, "Starting HartsyInference");
        _engine = WorkerDevice.CreateEngine();
        var spec = Spec(ModelVariants.ForMusic(options.Model, new JsonObject()));
        // Prepare stage: download the default variant's files. HartsyInference loads music models on the first
        // generation (there is no lease to hold one resident), which then only reads them from disk.
        await DownloadAsync(id, spec, CancellationToken.None);
        Protocol.Send("prepared", id);
        Protocol.Send("ready", id);
    }

    private static ModelSpec Spec(string token) => new() { Requested = token, Modality = Modality.Music };

    /// <summary>
    /// Gets the model's files from the app (downloaded into its store, linked into HartsyInference's folders), then
    /// checks with HartsyInference's own list, which fetches only what the app doesn't have.
    /// </summary>
    private async Task DownloadAsync(string? id, ModelSpec spec, CancellationToken ct)
    {
        var token = spec.Requested;
        await ModelLinks.EnsureAsync(id, ModelLinks.ForMusic(token), ct);
        if (ModelVariants.MusicAssets(token) is { } assets)
        {
            foreach (var asset in assets)
            {
                var name = Path.GetFileName(asset.TargetName ?? asset.RepoPath ?? asset.Repo);
                Protocol.Progress(id, 0.05, $"Checking {name}");
                await ModelDownloader.EnsureSideModelAsync(asset,
                    (_, fraction) => Protocol.Progress(id, 0.05 + 0.85 * fraction, $"Downloading {name} ({fraction:P0})"), ct);
            }
            foreach (var (repo, file) in ModelVariants.MusicSharedFiles(token))
            {
                Protocol.Progress(id, 0.9, $"Checking {repo}");
                await AudioModelCache.GetAsync(repo, file, category: "music",
                    progress: new Progress<long>(bytes => Protocol.Progress(id, 0.9, $"Downloading {repo} ({bytes / (1 << 20)} MB)")), ct: ct);
            }
            return;
        }
        var prefetch = await _engine!.ModelPrefetch.PrefetchAsync(spec,
            new Progress<AudioFetchProgress>(p => Protocol.Progress(id, 0.1, p.ToString())), ct);
        if (!prefetch.Supported) Protocol.Log($"prefetch: {prefetch.Message}");
    }

    public void Cancel(string? id)
    {
        lock (_gate)
            if (_currentCts != null && (id == null || id == _currentId)) _currentCts.Cancel();
    }

    public async Task GenerateAsync(string? id, JsonObject msg)
    {
        if (_engine == null) throw new InvalidOperationException("generate before load");
        var finalPath = (string?)msg["outPath"] ?? throw new ArgumentException("generate request has no outPath");
        var prompt = (string?)msg["text"] ?? "";
        var settings = msg["settings"] as JsonObject ?? new JsonObject();
        var seed = Settings.Long(msg, "seed") ?? Settings.Long(settings, "seed") ?? Random.Shared.Next(1, int.MaxValue);
        var duration = Settings.Double(msg, "durationSec") ?? 10;

        var partial = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPath))!);
        using var cts = new CancellationTokenSource();
        lock (_gate) { _currentId = id; _currentCts = cts; }
        try
        {
            var spec = Spec(ModelVariants.ForMusic(options.Model, settings));
            if (_spec != null && _spec.Requested != spec.Requested)
            {
                // One music model in memory at a time: switching variants (turbo ↔ sft) unloads the previous one.
                Protocol.Log($"switching model: {_spec.Requested} -> {spec.Requested}");
                _engine.FreeMemory();
            }
            await DownloadAsync(id, spec, cts.Token);
            if (_spec?.Requested != spec.Requested) Protocol.Progress(id, 0.02, $"Loading {spec.Requested}");
            _spec = spec;

            var request = ModelVariants.MusicRequest(options.Model, prompt, duration, settings, (int)(seed % int.MaxValue));
            var result = await _engine.Music.GenerateAsync(spec, request,
                new Progress<StepPreview>(p => Protocol.Progress(id, p.TotalSteps > 0 ? 0.05 + 0.9 * p.Step / p.TotalSteps : 0.5,
                    $"Step {p.Step} of {p.TotalSteps}")), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (result.Data.Length == 0) throw new InvalidOperationException("the model returned no audio");
            if (!string.Equals(result.Format, "wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"the model returned {result.Format}, not WAV");
            await File.WriteAllBytesAsync(partial, result.Data, CancellationToken.None);
            File.Move(partial, finalPath, overwrite: true);
            Protocol.Send("done", id, ("path", finalPath), ("durationMs", (int)Math.Round(result.DurationSeconds * 1000)),
                ("seed", seed), ("sampleRate", result.SampleRate));
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            throw;
        }
        finally
        {
            lock (_gate) { _currentId = null; _currentCts = null; }
        }
    }

    public void Dispose() => _engine?.Dispose();
}
