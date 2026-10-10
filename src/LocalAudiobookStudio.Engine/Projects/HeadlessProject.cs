using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>Which segments <see cref="HeadlessProject.GenerateAsync"/> queues.</summary>
public enum GenerateScope
{
    /// <summary>Not generated, stale or failed (what the app's "Generate missing" does).</summary>
    Missing,
    /// <summary>Every segment, even ones with a current take (a new take each).</summary>
    All,
    /// <summary>Only segments whose last generation failed.</summary>
    Failed,
}

/// <summary>
/// A job of <see cref="HeadlessProject.GenerateAsync"/> that finished segments: <see cref="Done"/> and <see cref="Total"/>
/// count segments (a fused generation finishes several at once). <see cref="Selected"/>: all its new takes were selected.
/// </summary>
public sealed record GenerationEvent(GenerationJob Job, int Done, int Total, bool Selected);

/// <param name="NotQueued">Segments that couldn't be queued, with why (an unknown speaker, empty text…).</param>
/// <param name="Errors">The failed generations' errors.</param>
public sealed record GenerateSummary(int Queued, int Completed, int Failed, int Cancelled, int SkippedLocked, int SkippedRecorded,
    IReadOnlyList<string> NotQueued, IReadOnlyList<string> Errors);

public sealed record ImportSummary(int Chapters, int Segments, int NewSpeakers, int KeptTakes);

public sealed record EraseSummary(int Segments, int FilesDeleted, int SkippedLocked, int SkippedRecorded);

/// <summary>
/// A project opened without the app: the command line's import, erase, generate and export steps. Holds the
/// project lock while open. Changes are saved as they happen, so an interrupted run keeps what finished.
/// </summary>
public sealed class HeadlessProject : IDisposable
{
    private readonly object _sync = new();
    private readonly ProjectLock _lock;
    private readonly ILogger _log;

    private HeadlessProject(StudioHost host, Project project, ProjectLock projectLock)
    {
        _lock = projectLock;
        _log = host.LoggerFactory.CreateLogger("Project");
        Engine = new ProjectEngine(host, project) { SpeakerChanged = sp => ProjectStore.SaveSpeaker(project, sp) };
    }

    public ProjectEngine Engine { get; }
    public Project Project => Engine.Project;
    public StudioHost Host => Engine.Host;

    /// <summary>Opens a project; with <paramref name="createTitle"/>, creates it first when the folder has none.</summary>
    public static async Task<HeadlessProject> OpenAsync(StudioHost host, string folder, string? createTitle = null)
    {
        folder = Path.GetFullPath(folder);
        if (!ProjectStore.IsProjectFolder(folder))
        {
            if (createTitle == null) throw new FileNotFoundException($"No project in {folder} ({ProjectFile.FileName} is missing).");
            ProjectImport.Create(host, folder, createTitle);
        }
        var projectLock = ProjectLock.TryAcquire(folder, "las")
            ?? throw new IOException($"The project in {folder} is open in Local Audiobook Studio (or another las run). Close it there first.");
        try
        {
            var project = await Task.Run(() => ProjectStore.Load(folder));
            // A read-only project starts clean (see Dispose).
            if (project.IsReadOnly) ReadOnlyProject.DiscardSessionTakes(folder);
            var p = new HeadlessProject(host, project, projectLock);
            foreach (var ch in p.Engine.Migrate()) ProjectStore.SaveChapter(project, ch);
            await p.Engine.IndexAudioAsync();
            return p;
        }
        catch
        {
            projectLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases the project. A read-only project's saves were skipped all along, and the takes this run generated
    /// are deleted now: they were only kept for the run's own steps (an export after a generate).
    /// </summary>
    public void Dispose()
    {
        if (Project.IsReadOnly) ReadOnlyProject.DiscardSessionTakes(Project.Paths.Root);
        _lock.Dispose();
    }

    // ================================================================== import

    /// <summary>
    /// Replaces every chapter with the text's. With <paramref name="keepTakes"/>, a chapter with the same title
    /// keeps the takes of segments whose text didn't change (as the app's "Replace chapter" does); otherwise all
    /// segments are new. The audio of segments that are gone is deleted.
    /// </summary>
    public ImportSummary ImportReplacingAll(string text, ImportOptions options, bool keepTakes)
    {
        var result = TextImporter.Parse(text, options);
        if (result.SegmentCount == 0) throw new InvalidDataException("The text has no segments.");
        var speakersBefore = Project.Speakers.Count;
        var map = ProjectImport.MapLabels(Engine, result.SpeakerLabels);
        var imported = TextImporter.ToChapters(result, map);

        var kept = 0;
        if (keepTakes)
        {
            var unused = Project.Chapters.ToList();
            foreach (var ch in imported)
            {
                var old = unused.FirstOrDefault(o => string.Equals(o.Title, ch.Title, StringComparison.CurrentCultureIgnoreCase));
                if (old == null) continue;
                unused.Remove(old);
                var oldIds = old.Items.Select(i => i.Id).ToHashSet();
                ch.Id = old.Id;
                ch.Items = ScriptMerger.Merge(old.Items, ch.Items);
                kept += ch.Items.Count(i => oldIds.Contains(i.Id) && i.SelectedTake != null);
            }
        }

        var oldChapters = Project.Chapters.Select(c => c.Id).ToList();
        Project.Chapters.Clear();
        Project.Chapters.AddRange(imported);
        ProjectStore.SaveAll(Project);
        foreach (var id in oldChapters.Where(id => imported.All(c => c.Id != id)))
            ProjectStore.DeleteChapterFile(Project, id);
        DeleteAudioOfRemovedSegments();
        _log.LogInformation("Imported {Segments} segments in {Chapters} chapters ({Kept} kept their takes)", result.SegmentCount, imported.Count, kept);
        return new ImportSummary(imported.Count, result.SegmentCount, Project.Speakers.Count - speakersBefore, kept);
    }

    private void DeleteAudioOfRemovedSegments()
    {
        if (!Directory.Exists(Project.Paths.AudioDir)) return;
        var ids = Project.AllItems().Select(i => i.Id).ToHashSet();
        foreach (var dir in Directory.EnumerateDirectories(Project.Paths.AudioDir))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out var id) || ids.Contains(id)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete {Dir}", dir); }
            Engine.Takes.Remove(id);
        }
    }

    // ================================================================== erasing takes

    /// <summary>
    /// Deletes every generated take, so all segments need generation again. Locked segments and microphone
    /// recordings keep theirs (they can't be generated again).
    /// </summary>
    public EraseSummary EraseTakes()
    {
        int segments = 0, locked = 0, recorded = 0;
        foreach (var ch in Project.Chapters)
        {
            var changed = false;
            foreach (var item in ch.Items.Where(i => i.IsGenerated))
            {
                if (Engine.IsRecorded(item)) { if (item.SelectedTake != null) recorded++; continue; }
                if (item.Locked) { if (item.SelectedTake != null) locked++; continue; }
                if (item.SelectedTake == null && item.LastError == null && item.InputHash == null) continue;
                item.SelectedTake = null;
                item.InputHash = null;
                item.LastError = null;
                item.TrimStartMs = null;
                item.TrimEndMs = null;
                segments++;
                changed = true;
            }
            if (changed) ProjectStore.SaveChapter(Project, ch);
        }
        // Removes every take file that isn't selected now (the take numbering file stays, so numbers aren't reused).
        var files = TakeStore.CleanUpUnusedTakes(Project);
        foreach (var item in Project.AllItems().Where(i => i.IsGenerated)) Engine.Takes.Reload(item.Id);
        _log.LogInformation("Erased the takes of {Segments} segments ({Files} files)", segments, files);
        return new EraseSummary(segments, files, locked, recorded);
    }

    // ================================================================== generating

    /// <summary>
    /// Queues the segments in scope (in reading order; <paramref name="chapters"/> limits it to those chapter
    /// indexes) and waits until all are done. Each finished take is selected and its chapter saved at once.
    /// Cancelling cancels the queue.
    /// </summary>
    public async Task<GenerateSummary> GenerateAsync(GenerateScope scope, IReadOnlySet<int>? chapters,
        Action<GenerationEvent>? onFinished, CancellationToken ct)
    {
        var scheduler = Host.Scheduler;
        var wanted = new List<TimelineItem>();
        var notQueued = new List<string>();
        var errors = new List<string>();
        List<GenerationJob> jobs;
        int locked = 0, recorded = 0;
        lock (_sync)
        {
            for (int ci = 0; ci < Project.Chapters.Count; ci++)
            {
                if (chapters != null && !chapters.Contains(ci)) continue;
                var ch = Project.Chapters[ci];
                var dirty = false;
                foreach (var item in ch.Items)
                {
                    if (!item.IsGenerated) continue;
                    var state = Engine.StateOf(item);
                    var inScope = scope switch
                    {
                        GenerateScope.All => true,
                        GenerateScope.Failed => state == SegmentAudioState.Failed,
                        _ => state != SegmentAudioState.Ready,
                    };
                    if (!inScope) continue;
                    if (Engine.IsRecorded(item)) { recorded++; continue; }
                    if (item.Locked) { locked++; continue; }
                    wanted.Add(item);
                    if (item.LastError != null) { item.LastError = null; dirty = true; }
                }
                if (dirty) ProjectStore.SaveChapter(Project, ch);
            }
            jobs = Engine.BuildJobs(wanted, JobPriority.Normal, notQueued);
        }
        // Counted by segment: a fused generation finishes several, and what it can't do comes back as jobs of their own.
        var pending = jobs.SelectMany(j => j.Items).ToHashSet();
        var total = pending.Count;
        if (total == 0) return new GenerateSummary(0, 0, 0, 0, locked, recorded, notQueued, errors);

        int done = 0, completed = 0, failed = 0, cancelled = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(GenerationJob job)
        {
            if (!ProjectEngine.SegmentsOf(job).Any(pending.Contains)) return;
            var followUps = new List<GenerationJob>();
            lock (_sync)
            {
                var finished = 0;
                var selected = true;
                var dirty = new HashSet<Chapter>();
                void Done(Guid itemId) { if (pending.Remove(itemId)) finished++; }

                if (Engine.OwnsFusion(job))
                {
                    var outcome = Engine.FinishFusion(job);
                    foreach (var (item, _, isSelected, _) in outcome.Takes)
                    {
                        completed++;
                        selected &= isSelected;
                        Done(item.Id);
                        if (Project.FindItem(item.Id) is (Chapter ch, _)) dirty.Add(ch);
                    }
                    foreach (var item in outcome.Failed)
                    {
                        failed++;
                        errors.Add($"{job.Label}: {item.LastError}");
                        Done(item.Id);
                        if (Project.FindItem(item.Id) is (Chapter ch, _)) dirty.Add(ch);
                    }
                    foreach (var id in outcome.Cancelled) { cancelled++; Done(id); }
                    if (ct.IsCancellationRequested)
                        foreach (var id in outcome.FollowUps.SelectMany(ProjectEngine.SegmentsOf)) { cancelled++; Done(id); }
                    else
                        followUps.AddRange(outcome.FollowUps);
                }
                else if (Project.FindItem(job.ItemId) is (Chapter chapter, int index))
                {
                    var item = chapter.Items[index];
                    switch (job.State)
                    {
                        case JobState.Completed:
                            completed++;
                            selected = Engine.ApplyCompletedTake(item, job);
                            dirty.Add(chapter);
                            break;
                        case JobState.Failed:
                            failed++;
                            ProjectEngine.ApplyFailure(item, job);
                            dirty.Add(chapter);
                            errors.Add($"{job.Label}: {job.LastError}");
                            break;
                        default:
                            cancelled++;
                            break;
                    }
                    Done(item.Id);
                }
                foreach (var ch in dirty) ProjectStore.SaveChapter(Project, ch);
                if (finished > 0)
                {
                    done += finished;
                    onFinished?.Invoke(new GenerationEvent(job, done, total, selected));
                }
                if (pending.Count == 0) all.TrySetResult();
            }
            // Not under the lock: a job that can't be queued fails at once, back into this handler.
            if (followUps.Count > 0) scheduler.EnqueueMany(followUps);
        }

        scheduler.OnGenerated = Engine.SaveGeneratedTake;
        scheduler.JobCompleted += Finished;
        scheduler.JobFailed += Finished;
        try
        {
            await using var reg = ct.Register(scheduler.CancelAll);
            scheduler.EnqueueMany(jobs);
            await all.Task;
        }
        finally
        {
            scheduler.JobCompleted -= Finished;
            scheduler.JobFailed -= Finished;
            if (scheduler.OnGenerated == Engine.SaveGeneratedTake) scheduler.OnGenerated = null;
        }
        return new GenerateSummary(total, completed, failed, cancelled, locked, recorded, notQueued, errors);
    }

    // ================================================================== export

    /// <summary>
    /// Renders the chapters (all, or those indexes) and encodes them with ffmpeg. With
    /// <paramref name="onlyComplete"/>, chapters with missing, stale or failed audio are left out.
    /// Returns the files written.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportAsync(ExportOptions options, IReadOnlySet<int>? chapters, bool onlyComplete,
        IProgress<ExportProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var ffmpeg = Ffmpeg.Locate(Host.Settings.FfmpegPath, Host.DataRoot.Root) ?? throw new FfmpegNotFoundException();
        var (layout, mix) = await Engine.ComputeTimelineAsync();
        var indices = Enumerable.Range(0, Project.Chapters.Count)
            .Where(i => chapters == null || chapters.Contains(i))
            .Where(i => !onlyComplete || ExportPlanner.IsComplete(Engine, layout, i));
        if (options.Format.IsPerSegment())
        {
            var (segments, skipped) = ExportPlanner.Segments(Engine, indices);
            if (skipped > 0) log?.Invoke($"{skipped} section(s) without audio left out.");
            var segmentExporter = new AudiobookExporter(ffmpeg, new AudioBufferCache(Project.Defaults.SampleRate), Project.Paths.TempDir) { Log = log };
            var files = await Task.Run(() => segmentExporter.ExportSegmentsAsync(segments, options, progress, ct), ct);
            _log.LogInformation("Exported {Count} sections to {Output}", files.Count, options.OutputPath);
            return files;
        }
        var ranges = ExportPlanner.Chapters(Project.Chapters, layout, indices);
        if (ranges.Count == 0) throw new InvalidOperationException(onlyComplete ? "No complete chapters to export." : "No chapters to export.");
        var exporter = new AudiobookExporter(ffmpeg, new AudioBufferCache(Project.Defaults.SampleRate), Project.Paths.TempDir) { Log = log };
        var outputs = await Task.Run(() => exporter.ExportAsync(mix, ranges, options, progress, ct), ct);
        _log.LogInformation("Exported {Format} to {Output}", options.Format, options.OutputPath);
        return outputs;
    }
}
