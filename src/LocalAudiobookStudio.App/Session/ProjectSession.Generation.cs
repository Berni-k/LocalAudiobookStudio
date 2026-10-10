using System.Text.Json.Nodes;
using Avalonia.Threading;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.ViewModels;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.Session;

/// <summary>Turning segments into generation jobs, and handling finished takes.</summary>
public sealed partial class ProjectSession
{
    /// <summary>
    /// Queues generation for the given items. Clips, pauses and locked items are skipped; with
    /// <paramref name="skipLocked"/> false (an explicit request) skipping a locked one is reported.
    /// </summary>
    public int Generate(IEnumerable<ItemViewModel> items, JobPriority priority, bool skipLocked)
    {
        var jobs = new List<GenerationJob>();
        var errors = new List<string>();
        var lockedSkipped = false;
        var recordedSkipped = false;
        foreach (var vm in items)
        {
            if (!vm.IsGenerated) continue;
            if (vm.IsLocked) { lockedSkipped = true; continue; }
            if (IsRecorded(vm.Model)) { recordedSkipped = true; continue; } // recorded with the microphone, not generated
            var job = BuildJob(vm, priority, out var error);
            if (job != null) jobs.Add(job);
            else if (error != null) errors.Add(error);
        }
        if (jobs.Count > 0)
        {
            // Clear old errors so the row shows "queued" rather than "failed".
            foreach (var j in jobs)
                if (_itemVms.TryGetValue(j.ItemId, out var vm) && vm.Model.LastError != null)
                {
                    vm.Model.LastError = null;
                    MarkChapterDirty(vm.Chapter);
                }
            AppHost.Scheduler.EnqueueMany(jobs);
        }
        if (errors.Count > 0)
            Notify(errors.Count == 1 ? errors[0] : $"{errors.Count} items could not be queued: {errors[0]}");
        else if (lockedSkipped && !skipLocked)
            Notify(LockedMessage);
        else if (recordedSkipped && jobs.Count == 0 && !skipLocked)
            Notify("This speaker has no model: use the record button on the segment to record it.");
        return jobs.Count;
    }

    /// <summary>Queues every stale, failed or not-yet-generated item (reading order), skipping locked ones.</summary>
    public int GenerateStale(IEnumerable<ChapterViewModel> chapters, JobPriority priority = JobPriority.Normal)
    {
        var items = chapters.SelectMany(c => c.Items)
            .Where(i => i.IsGenerated && !i.IsLocked && !IsRecorded(i.Model) && i.Status is ItemStatus.Stale or ItemStatus.NotGenerated or ItemStatus.Failed);
        return Generate(items, priority, skipLocked: true);
    }

    public GenerationJob? BuildJob(ItemViewModel vm, JobPriority priority, out string? error) =>
        Engine.BuildJob(vm.Model, ChapterIndex(vm.Chapter), vm.Chapter.Items.IndexOf(vm), priority, out error);

    /// <inheritdoc cref="ProjectEngine.MergeSettings"/>
    public JsonObject MergeSettings(JsonObject? baseSettings, JsonObject? overrides, WorkerManifest manifest) =>
        Engine.MergeSettings(baseSettings, overrides, manifest);

    /// <inheritdoc cref="ProjectEngine.SettingDefinitionsFor(Speaker, WorkerManifest)"/>
    public List<SettingDefinition> SettingDefinitionsFor(Speaker speaker, WorkerManifest worker) => Engine.SettingDefinitionsFor(speaker, worker);

    /// <inheritdoc cref="ProjectEngine.SettingDefinitionsFor(TimelineItem)"/>
    public List<SettingDefinition> SettingDefinitionsFor(TimelineItem item) => Engine.SettingDefinitionsFor(item);

    /// <inheritdoc cref="ProjectEngine.ResolveVoice"/>
    public string? ResolveVoice(string? voice) => Engine.ResolveVoice(voice);

    // ------------------------------------------------------------------ completion

    private void OnJobCompleted(GenerationJob job)
    {
        if (Engine.OwnsTranscript(job)) { OnTranscriptionFinished(job); return; }
        if (!Engine.Owns(job)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_itemVms.TryGetValue(job.ItemId, out var vm)) return; // deleted meanwhile
            // Only auto-select the new take if the input is still what was generated.
            if (Engine.ApplyCompletedTake(vm.Model, job))
                MarkChapterDirty(vm.Chapter);
            else
                Notify($"Take {job.TakeNumber} kept but not selected: the segment changed while it was generating.");
            vm.RefreshAudio();
            RefreshItems([vm]);
            TranscribeTake(vm, job.TakeNumber);
        });
    }

    // ------------------------------------------------------------------ transcripts

    /// <summary>
    /// Has the speaker's transcription worker listen to a new take (nothing happens for a speaker without one).
    /// The result is saved next to the take and shown on the segment's text.
    /// </summary>
    private void TranscribeTake(ItemViewModel vm, int take)
    {
        try
        {
            var job = Engine.BuildTranscriptionJob(vm.Model, take, ChapterIndex(vm.Chapter), vm.Chapter.Items.IndexOf(vm));
            if (job == null) return;
            vm.IsTranscribing = true;
            AppHost.Scheduler.Enqueue(job);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not start transcribing take {Take} of {Item}", take, vm.Id);
        }
    }

    /// <summary>Listens to the selected take of each segment again (its speaker's transcription worker).</summary>
    public void RerunTranscripts(IEnumerable<ItemViewModel> items)
    {
        var count = 0;
        foreach (var vm in items)
        {
            if (vm.Model.SelectedTake is not int take || Engine.TranscriberFor(vm.Model) == null) continue;
            TranscribeTake(vm, take);
            count++;
        }
        Notify(count == 0 ? "No transcription worker is set for this speaker." : $"Transcribing {count} segment(s)…");
    }

    /// <summary>A transcription job ended (completed or failed): saves the result and shows it.</summary>
    private void OnTranscriptionFinished(GenerationJob job)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var done = Engine.FinishTranscription(job);
            if (job.Tag is TranscriptJobTag tag && _itemVms.TryGetValue(tag.ItemId, out var listening)) listening.IsTranscribing = false;
            if (done == null)
            {
                if (job.State == JobState.Failed) _log.LogWarning("Transcription failed: {Error}", job.LastError);
                return;
            }
            if (_itemVms.TryGetValue(done.Value.ItemId, out var vm)) vm.RefreshTranscript();
        });
    }

    // ------------------------------------------------------------------ recording

    /// <summary>A speech segment whose speaker has no worker: it is recorded with the microphone, not generated.</summary>
    public bool IsRecorded(TimelineItem item) => Engine.IsRecorded(item);

    /// <summary>Where the next recording of a segment goes (a new take number; the file is renamed when done).</summary>
    public (int take, string partialPath) NewRecordingPath(ItemViewModel vm)
    {
        var dir = Paths.SegmentAudioDir(vm.Id);
        Directory.CreateDirectory(dir);
        var take = TakeStore.NextTakeNumber(Paths, vm.Id);
        return (take, Paths.TakeWav(vm.Id, take) + ".partial");
    }

    /// <summary>
    /// Turns a finished recording into a take, like a generated one: analysed (loudness, trims, peaks),
    /// saved and selected. Very short recordings (a mis-click) are discarded.
    /// </summary>
    public async Task AddRecordedTakeAsync(ItemViewModel vm, int take, string partialPath, double durationMs)
    {
        if (durationMs < 300)
        {
            try { File.Delete(partialPath); } catch { }
            Notify("Recording discarded: too short.");
            return;
        }
        var wav = Paths.TakeWav(vm.Id, take);
        var inputHash = ComputeHash(vm.Model) ?? "";
        var trim = AppHost.Host!.SilenceTrim;
        var info = await Task.Run(() =>
        {
            File.Move(partialPath, wav, overwrite: true);
            var analysis = TakeAnalyzer.Analyze(wav, Paths.TakePeaks(vm.Id, take), trim);
            var ti = new TakeInfo
            {
                Take = take,
                CreatedUtc = DateTime.UtcNow,
                InputHash = inputHash,
                Worker = "recording",
                DurationMs = analysis.DurationMs,
                SampleRate = analysis.SampleRate,
                LoudnessLufs = analysis.LoudnessLufs is double l ? Math.Round(l, 2) : null,
                TrimStartMs = analysis.TrimStartMs,
                TrimEndMs = analysis.TrimEndMs,
                TextLength = vm.Model.Text?.Length,
                Format = Engine.StoreAudio(wav),
            };
            TakeStore.SaveTakeInfo(Paths, vm.Id, ti);
            Takes.Add(vm.Id, ti);
            return ti;
        });
        if (!_itemVms.ContainsKey(vm.Id)) return; // deleted meanwhile; the files stay with the segment folder
        var item = vm.Model;
        item.SelectedTake = info.Take;
        item.InputHash = inputHash;
        item.LastError = null;
        item.TrimStartMs = null; // use the recording's detected silence
        item.TrimEndMs = null;
        MarkChapterDirty(vm.Chapter);
        vm.RefreshAudio();
        RefreshItems([vm]);
        Notify($"Recorded take {info.Take} ({Core.Timeline.TimeFormat.Short(info.DurationMs)}).");
        TranscribeTake(vm, info.Take);
    }

    private void OnJobFailed(GenerationJob job)
    {
        if (Engine.OwnsTranscript(job)) { OnTranscriptionFinished(job); return; }
        if (!Engine.Owns(job)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_itemVms.TryGetValue(job.ItemId, out var vm)) return;
            if (ProjectEngine.ApplyFailure(vm.Model, job))
            {
                _log.LogWarning("Generation failed for {Item}: {Error}", job.ItemId, job.LastError);
                MarkChapterDirty(vm.Chapter);
            }
            RefreshItems([vm], layout: false);
        });
    }

    // ------------------------------------------------------------------ clips

    public static readonly string[] AudioExtensions = [".wav", ".flac", ".ogg", ".mp3", ".m4a", ".aac", ".opus"];

    /// <summary>Raised after a file was added to the assets folder (the Assets panel rescans).</summary>
    public event Action? AssetsChanged;

    /// <summary>
    /// Adds a file to the project's assets: a ComfyUI workflow (.json) or an audio file. <paramref name="folder"/>
    /// is a folder below assets/ ("Voice Reference"), created when missing; null for assets/ itself. An audio file
    /// is named <paramref name="name"/> when given, else after the source file. Returns the project-relative path.
    /// </summary>
    public async Task<string> ImportAssetAsync(string source, string? folder = null, string? name = null)
    {
        var rel = Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? await ImportWorkflowFileAsync(source, folder)
            : await ImportClipFileAsync(source, folder, name);
        AssetsChanged?.Invoke();
        return rel;
    }

    private string AssetFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? Paths.AssetsDir : Path.Combine(Paths.AssetsDir, folder.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Copies an audio file into assets/ (or a folder below it). MP3 and Opus files are kept as they are. A WAV
    /// is stored in the project's audio format (compressed to Opus, or copied). Other formats (FLAC, M4A, …) are
    /// converted with ffmpeg first, so playback never needs ffmpeg. The asset is named <paramref name="name"/>
    /// (without extension) when given, else after the source file. Returns the project-relative path.
    /// </summary>
    public async Task<string> ImportClipFileAsync(string source, string? folder = null, string? name = null)
    {
        var dir = AssetFolder(folder);
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(source).ToLowerInvariant();
        var stem = AudiobookExporter.SafeFileName(name ?? Path.GetFileNameWithoutExtension(source));
        string Target(string extension)
        {
            var target = Path.Combine(dir, stem + extension);
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{stem}-{n}{extension}");
            return target;
        }

        string result;
        if (ext is ".mp3" or OggOpus.Extension)
        {
            await Task.Run(() => AudioDecoder.ReadInfo(source)); // refuses a file it can't play
            result = Target(ext);
            await Task.Run(() => File.Copy(source, result));
        }
        else
        {
            // A WAV (or ffmpeg's conversion to one) first, then the project's format.
            var wav = Target(".wav");
            var readable = ext == ".wav" && await Task.Run(() =>
            {
                try { AudioDecoder.ReadInfo(source); return true; }
                catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or EndOfStreamException) { return false; }
            });
            // Unusual WAV encodings (for example ADPCM) are normalized through ffmpeg too.
            if (readable) await Task.Run(() => File.Copy(source, wav));
            else await ConvertWithFfmpegAsync(source, wav);
            result = wav;
            if (Project.Defaults.AudioFormat == AudioFormats.Opus)
            {
                // A free name of its own: the WAV's name with .opus could be another asset.
                var opus = Target(OggOpus.Extension);
                await Task.Run(() =>
                {
                    OggOpus.Write(AudioDecoder.Decode(wav), opus, Engine.OpusBitrate);
                    File.Delete(wav);
                });
                result = opus;
            }
        }
        var rel = Paths.MakeRelative(result);
        await Task.Run(() => Takes.AnalyzeClip(rel));
        return rel;
    }

    /// <summary>Saves a take as a WAV file (a copy, or decoded when it is stored as Opus).</summary>
    public Task ExportTakeAsWavAsync(Guid itemId, int take, string target) => Task.Run(() =>
    {
        var source = Takes.TakeAudio(itemId, take);
        if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, overwrite: true);
        else AudioDecoder.WriteWav(AudioDecoder.Decode(source), target);
    });

    /// <inheritdoc cref="ProjectEngine.UncompressedTakes"/>
    public (int Count, long Bytes) UncompressedTakes() => Engine.UncompressedTakes();

    /// <summary>Converts every WAV take to Opus in the background (see <see cref="ProjectEngine.CompressTakes"/>).</summary>
    public async Task<(int Count, long BytesSaved)> CompressTakesAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var result = await Task.Run(() => Engine.CompressTakes(progress, ct), ct);
        RefreshAll();
        return result;
    }

    /// <summary>
    /// Copies a ComfyUI workflow into assets/ (or a folder below it) as &lt;name&gt;.comfy.json, after checking it
    /// is in the API format (throws <see cref="InvalidDataException"/> otherwise). Returns the project-relative path.
    /// </summary>
    public async Task<string> ImportWorkflowFileAsync(string source, string? folder = null)
    {
        await Task.Run(() => ComfyWorkflow.Read(source));
        var dir = AssetFolder(folder);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(source);
        var stem = AudiobookExporter.SafeFileName(name.EndsWith(ComfyWorkflow.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^ComfyWorkflow.Extension.Length] : Path.GetFileNameWithoutExtension(name));
        var target = Path.Combine(dir, stem + ComfyWorkflow.Extension);
        for (int n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{stem}-{n}{ComfyWorkflow.Extension}");
        await Task.Run(() => File.Copy(source, target));
        return Paths.MakeRelative(target);
    }

    private static async Task ConvertWithFfmpegAsync(string source, string target)
    {
        var ffmpeg = Ffmpeg.Locate(AppHost.Settings.FfmpegPath, AppHost.DataRoot.Root) ?? throw new FfmpegNotFoundException();
        await Ffmpeg.ConvertToWavAsync(ffmpeg, source, target, CancellationToken.None);
    }

    /// <summary>Deletes all takes that aren't selected. Returns the number of files removed.</summary>
    public async Task<int> CleanUpUnusedTakesAsync()
    {
        await FlushAsync();
        var count = await Task.Run(() => TakeStore.CleanUpUnusedTakes(Project));
        // The deleted takes' peaks must not be found again under the same path.
        PeaksCache.Clear();
        var ids = Project.AllItems().Where(i => i.IsGenerated).Select(i => i.Id).ToList();
        await Task.Run(() => { foreach (var id in ids) Takes.Reload(id); });
        foreach (var vm in AllItemVms()) vm.RefreshAudio();
        return count;
    }
}
