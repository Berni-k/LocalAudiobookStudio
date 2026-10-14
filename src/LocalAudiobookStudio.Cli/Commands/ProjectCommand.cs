using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary>
/// <c>las project &lt;folder&gt; &lt;step&gt; [options] [&lt;step&gt; [options]]…</c>: opens a project once and runs the
/// steps in order (for example import, erase-takes, generate, export). A step that fails stops the rest.
/// </summary>
public static class ProjectCommand
{
    private sealed record Step(string Name, CliArgs Args);

    private static readonly Dictionary<string, OptionSpec[]> StepOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["import"] =
        [
            new("--keep-takes", HasValue: false), new("--max-chars"), new("--chapter-regex"), new("--every-line", HasValue: false),
            new("--no-headings", HasValue: false), new("--names"), new("--title"),
        ],
        ["erase-takes"] = [],
        ["generate"] = [new("--all", HasValue: false), new("--failed", HasValue: false), new("--chapter", "-c", Repeatable: true)],
        ["export"] =
        [
            new("--output", "-o"), new("--format", "-f"), new("--bitrate"), new("--mono", HasValue: false), new("--limiter", HasValue: false),
            new("--title"), new("--author"), new("--cover"), new("--complete-only", HasValue: false), new("--chapter", "-c", Repeatable: true),
        ],
        ["status"] = [],
        ["timeline"] = [new("--chapter", "-c", Repeatable: true)],
        ["speakers"] = [],
        ["speaker"] =
        [
            new("--create", HasValue: false), new("--name"), new("--worker", "-w"), new("--voice"), new("--clone"),
            new("--set", Repeatable: true), new("--unset", Repeatable: true), new("--default", HasValue: false),
        ],
    };

    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) throw new UsageException("project: give the project folder, then steps (e.g. import book.txt generate export).");
        var folder = tokens[0];
        var steps = ParseSteps(tokens.Skip(1).ToList());
        if (steps.Count == 0) steps.Add(new Step("status", CliArgs.Parse("status", [], StepOptions["status"])));

        var host = await ctx.StartHostAsync();
        string? createTitle = null;
        if (steps[0].Name == "import")
        {
            var file = steps[0].Args.Positionals.FirstOrDefault();
            createTitle = steps[0].Args.Get("--title") ?? (file is null or "-" ? "Untitled" : Path.GetFileNameWithoutExtension(file));
        }
        else if (!ProjectStore.IsProjectFolder(folder))
        {
            throw new UsageException($"project: no project in {Path.GetFullPath(folder)}. Start with an import step to create one.");
        }

        HeadlessProject project;
        try { project = await HeadlessProject.OpenAsync(host, folder, createTitle); }
        catch (IOException ex) when (ex is not FileNotFoundException) { throw new SetupException(ex.Message); }

        using (project)
        {
            ctx.Detail($"Project: {project.Project.File.Title} ({project.Project.Paths.Root})");
            var results = new List<object>();
            var code = Program.ExitOk;
            foreach (var step in steps)
            {
                ctx.Cancel.ThrowIfCancellationRequested();
                var (result, ok) = step.Name switch
                {
                    "import" => (Import(ctx, project, step.Args), true),
                    "erase-takes" => (EraseTakes(ctx, project), true),
                    "generate" => await GenerateAsync(ctx, project, step.Args),
                    "export" => (await ExportAsync(ctx, project, step.Args), true),
                    "status" => (Status(ctx, project), true),
                    "timeline" => (await TimelineAsync(ctx, project, step.Args), true),
                    "speakers" => (Speakers(ctx, project), true),
                    "speaker" => (SpeakerEdit(ctx, project, step.Args), true),
                    _ => throw new UsageException($"project: unknown step '{step.Name}'."),
                };
                results.Add(new { step = step.Name, result });
                if (!ok)
                {
                    code = Program.ExitFailures;
                    var rest = steps.SkipWhile(s => s != step).Skip(1).Select(s => s.Name).ToList();
                    if (rest.Count > 0) ctx.Info($"Stopping: {step.Name} had failures, so {string.Join(", ", rest)} didn't run.");
                    break;
                }
            }
            ctx.WriteJson(new { project = project.Project.Paths.Root, title = project.Project.File.Title, steps = results });
            return code;
        }
    }

    /// <summary>Splits the tokens at step names; each step's options are checked before anything runs.</summary>
    private static List<Step> ParseSteps(List<string> tokens)
    {
        var steps = new List<Step>();
        var i = 0;
        while (i < tokens.Count)
        {
            var name = tokens[i].ToLowerInvariant();
            if (!StepOptions.TryGetValue(name, out var specs))
                throw new UsageException($"project: '{tokens[i]}' isn't a step. Steps: {string.Join(", ", StepOptions.Keys)}.");
            // The step's tokens run to the next step name (an option's value is never taken for one).
            var j = i + 1;
            while (j < tokens.Count && !StepOptions.ContainsKey(tokens[j]))
            {
                var takesValue = specs.Any(s => s.HasValue && (s.Name.Equals(tokens[j], StringComparison.OrdinalIgnoreCase) || s.Alias == tokens[j]));
                j += takesValue ? 2 : 1;
            }
            j = Math.Min(j, tokens.Count);
            var args = CliArgs.Parse(name, tokens.GetRange(i + 1, j - i - 1), specs);
            Validate(name, args);
            steps.Add(new Step(name, args));
            i = j;
        }
        return steps;
    }

    private static void Validate(string name, CliArgs a)
    {
        switch (name)
        {
            case "import":
                if (a.Positionals.Count != 1) throw new UsageException("import: give one text file (or - for stdin).");
                if (a.Positionals[0] != "-" && !File.Exists(a.Positionals[0])) throw new UsageException($"import: {a.Positionals[0]} doesn't exist.");
                a.GetInt("--max-chars", 50, 100_000);
                if (a.Get("--chapter-regex") is { } rx)
                {
                    try { _ = new Regex(rx); }
                    catch (ArgumentException ex) { throw new UsageException("import: --chapter-regex: " + ex.Message); }
                }
                break;
            case "generate":
                a.NoPositionals();
                if (a.Has("--all") && a.Has("--failed")) throw new UsageException("generate: --all and --failed don't go together.");
                a.GetChapters();
                break;
            case "export":
                a.NoPositionals();
                ParseFormat(a.Get("--format"));
                a.GetInt("--bitrate", 16, 512);
                a.GetChapters();
                if (a.Get("--cover") is { } cover && !File.Exists(cover)) throw new UsageException($"export: {cover} doesn't exist.");
                break;
            case "timeline":
                a.NoPositionals();
                a.GetChapters();
                break;
            case "speaker":
                if (a.Positionals.Count != 1) throw new UsageException("speaker: give the speaker id (or name).");
                if (a.Get("--clone") is { } clip && !File.Exists(clip)) throw new UsageException($"speaker: {clip} doesn't exist.");
                a.GetPairs("--set");
                break;
            default:
                a.NoPositionals();
                break;
        }
    }

    // ================================================================== import

    private static object Import(CliContext ctx, HeadlessProject p, CliArgs a)
    {
        var file = a.Positionals[0];
        var text = file == "-" ? ctx.Io.ReadStdin() : TextDecoding.ReadFile(file);
        var options = ProjectImport.Options(p.Host, p.Project, a.GetInt("--max-chars") ?? 1500);
        if (a.Get("--chapter-regex") is { } rx) options.ChapterRegex = rx;
        options.EveryLineIsParagraph = a.Has("--every-line");
        options.SpeakHeadings = !a.Has("--no-headings");
        if (a.Get("--names") is { } names)
            options.KnownSpeakerNames = options.KnownSpeakerNames
                .Concat(names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var r = p.ImportReplacingAll(text, options, a.Has("--keep-takes"));
        ctx.Info($"Imported {r.Segments} segments in {r.Chapters} chapter(s)" +
                 (r.NewSpeakers > 0 ? $", {r.NewSpeakers} new speaker(s)" : "") +
                 (a.Has("--keep-takes") ? $", {r.KeptTakes} kept their takes" : "") + ".");
        return r;
    }

    // ================================================================== erase takes

    private static object EraseTakes(CliContext ctx, HeadlessProject p)
    {
        var r = p.EraseTakes();
        ctx.Info($"Erased the takes of {r.Segments} segment(s) ({r.FilesDeleted} files)" +
                 (r.SkippedLocked > 0 ? $"; {r.SkippedLocked} locked kept theirs" : "") +
                 (r.SkippedRecorded > 0 ? $"; {r.SkippedRecorded} microphone recordings kept" : "") + ".");
        return r;
    }

    // ================================================================== generate

    private static async Task<(object, bool)> GenerateAsync(CliContext ctx, HeadlessProject p, CliArgs a)
    {
        var scope = a.Has("--all") ? GenerateScope.All : a.Has("--failed") ? GenerateScope.Failed : GenerateScope.Missing;
        var chapters = a.GetChapters();
        CheckChapters(p, chapters);

        // Workers that aren't installed would fail every segment; say so once instead.
        var workers = p.Project.Chapters.Where((_, i) => chapters == null || chapters.Contains(i))
            .SelectMany(c => c.Items)
            .Where(i => i.IsGenerated && !p.Engine.IsRecorded(i) && !i.Locked)
            .Select(i => p.Engine.WorkerFor(i)?.Id ?? p.Project.FindSpeaker(i.Speaker)?.Worker)
            .Where(w => w != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var problems = workers.Select(w => p.Host.WhyWorkerCannotRun(w!)).OfType<string>().ToList();
        if (problems.Count > 0) throw new SetupException(string.Join(Environment.NewLine, problems));

        var sw = Stopwatch.StartNew();
        var width = 1;
        var r = await p.GenerateAsync(scope, chapters, e =>
        {
            width = e.Total.ToString().Length;
            var n = e.Done.ToString().PadLeft(width);
            var job = e.Job;
            switch (job.State)
            {
                case JobState.Completed:
                    ctx.Info($"[{n}/{e.Total}] {job.Label}  ({job.DeviceName}, {(job.FinishedUtc - job.StartedUtc)?.TotalSeconds:0.0} s{(e.Selected ? "" : ", not selected: the segment changed")})");
                    break;
                case JobState.Failed:
                    ctx.Info($"[{n}/{e.Total}] FAILED {job.Label}: {job.LastError}");
                    break;
            }
        }, ctx.Cancel);

        if (r.Queued == 0)
            ctx.Info("Nothing to generate" + (scope == GenerateScope.Missing ? ": everything is up to date." : "."));
        else
            ctx.Info($"Generated {r.Completed} of {r.Queued} in {FormatElapsed(sw.Elapsed)}" +
                     (r.Failed > 0 ? $", {r.Failed} failed" : "") + (r.Cancelled > 0 ? $", {r.Cancelled} cancelled" : "") + ".");
        if (r.SkippedLocked > 0) ctx.Info($"{r.SkippedLocked} locked segment(s) skipped.");
        if (r.SkippedRecorded > 0) ctx.Info($"{r.SkippedRecorded} segment(s) of microphone speakers skipped (record them in the app).");
        if (r.NotQueued.Count > 0)
        {
            ctx.Info($"{r.NotQueued.Count} segment(s) couldn't be queued:");
            foreach (var e in r.NotQueued.Take(20)) ctx.Info("  " + e);
            if (r.NotQueued.Count > 20) ctx.Info($"  … and {r.NotQueued.Count - 20} more");
        }
        ctx.Cancel.ThrowIfCancellationRequested();
        var ok = r.Failed == 0 && r.NotQueued.Count == 0;
        return (r, ok);
    }

    private static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.TotalMinutes >= 1 ? t.ToString(@"m\:ss") + " min" : $"{t.TotalSeconds:0.0} s";

    private static void CheckChapters(HeadlessProject p, HashSet<int>? chapters)
    {
        if (chapters?.FirstOrDefault(c => c >= p.Project.Chapters.Count) is int bad && bad >= p.Project.Chapters.Count)
            throw new UsageException($"--chapter {bad + 1}: the project has {p.Project.Chapters.Count} chapter(s).");
    }

    // ================================================================== export

    private static ExportFormat ParseFormat(string? s) => s?.ToLowerInvariant() switch
    {
        null or "m4b" => ExportFormat.M4b,
        "mp3" => ExportFormat.Mp3Single,
        "mp3-chapters" => ExportFormat.Mp3PerChapter,
        "wav" => ExportFormat.Wav,
        "wav-segments" => ExportFormat.WavPerSegment,
        "mp3-segments" => ExportFormat.Mp3PerSegment,
        _ => throw new UsageException($"export: unknown format '{s}'. Formats: m4b, mp3, mp3-chapters, wav, wav-segments, mp3-segments."),
    };

    private static async Task<object> ExportAsync(CliContext ctx, HeadlessProject p, CliArgs a)
    {
        var format = ParseFormat(a.Get("--format"));
        var file = p.Project.File;
        var title = a.Get("--title") ?? file.Title;
        var chapters = a.GetChapters();
        CheckChapters(p, chapters);

        // -o: the file for m4b/mp3 (a folder or a name without extension gets the usual name), a folder otherwise.
        var output = a.Get("--output");
        string outputPath;
        var single = format is ExportFormat.M4b or ExportFormat.Mp3Single;
        if (output == null) outputPath = ExportPlanner.OutputPath(format, p.Project.Paths.ExportDir, title);
        else if (single && Path.HasExtension(output) && !Directory.Exists(output)) outputPath = Path.GetFullPath(output);
        else outputPath = ExportPlanner.OutputPath(format, Path.GetFullPath(output), title);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var options = new ExportOptions
        {
            Format = format,
            BitrateKbps = a.GetInt("--bitrate") ?? (format.IsMp3() ? 128 : 96),
            Mono = a.Has("--mono"),
            FinalLimiter = a.Has("--limiter"),
            Title = title,
            Author = a.Get("--author") ?? file.Author,
            CoverImagePath = a.Get("--cover") is { } c ? Path.GetFullPath(c) : file.CoverImage is { } fc ? p.Project.Paths.Resolve(fc) : null,
            OutputPath = outputPath,
        };

        var missing = MissingSummary(p, chapters);
        if (missing != null) ctx.Info((a.Has("--complete-only") ? "Leaving out incomplete chapters: " : "Warning: ") + missing);

        var sw = Stopwatch.StartNew();
        var lastStage = "";
        var lastTenth = -1;
        var progress = new Progress<ExportProgress>(pr =>
        {
            var tenth = (int)(pr.Fraction * 10);
            if (pr.Stage == lastStage && tenth == lastTenth) return;
            lastStage = pr.Stage;
            lastTenth = tenth;
            ctx.Detail($"{pr.Stage} {pr.Fraction:P0}");
        });
        ctx.Info($"Exporting {format} …");
        var outputs = await p.ExportAsync(options, chapters, a.Has("--complete-only"), progress, ctx.Detail, ctx.Cancel);
        ctx.Info($"Exported {outputs.Count} file(s) in {FormatElapsed(sw.Elapsed)}.");
        foreach (var o in outputs) ctx.Out(o);
        return new { format, outputs };
    }

    /// <summary>"3 not generated, 1 stale" for the chapters being exported, or null when everything is current.</summary>
    private static string? MissingSummary(HeadlessProject p, HashSet<int>? chapters)
    {
        var items = p.Project.Chapters.Where((_, i) => chapters == null || chapters.Contains(i)).SelectMany(c => c.Items).ToList();
        var states = items.Where(i => i.HasAudio).GroupBy(p.Engine.StateOf).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        if (states.GetValueOrDefault(SegmentAudioState.NotGenerated) is > 0 and var m) parts.Add($"{m} not generated (silence of the estimated length)");
        if (states.GetValueOrDefault(SegmentAudioState.Stale) is > 0 and var s) parts.Add($"{s} stale (the old take is used)");
        if (states.GetValueOrDefault(SegmentAudioState.Failed) is > 0 and var f) parts.Add($"{f} failed");
        return parts.Count == 0 ? null : string.Join(", ", parts) + ".";
    }

    // ================================================================== status and timeline

    private static object Status(CliContext ctx, HeadlessProject p)
    {
        var engine = p.Engine;
        var chapters = p.Project.Chapters.Select((ch, i) =>
        {
            var audio = ch.Items.Where(x => x.HasAudio).ToList();
            var states = audio.GroupBy(engine.StateOf).ToDictionary(g => g.Key, g => g.Count());
            return new
            {
                number = i + 1,
                title = ch.Title,
                segments = audio.Count,
                ready = states.GetValueOrDefault(SegmentAudioState.Ready),
                notGenerated = states.GetValueOrDefault(SegmentAudioState.NotGenerated),
                stale = states.GetValueOrDefault(SegmentAudioState.Stale),
                failed = states.GetValueOrDefault(SegmentAudioState.Failed),
                locked = ch.Items.Count(x => x.Locked),
            };
        }).ToList();
        var total = new
        {
            chapters = chapters.Count,
            segments = chapters.Sum(c => c.segments),
            ready = chapters.Sum(c => c.ready),
            notGenerated = chapters.Sum(c => c.notGenerated),
            stale = chapters.Sum(c => c.stale),
            failed = chapters.Sum(c => c.failed),
            locked = chapters.Sum(c => c.locked),
        };
        ctx.Out($"{p.Project.File.Title}  ({p.Project.Paths.Root})");
        ctx.Out($"{total.chapters} chapter(s), {total.segments} segment(s): {total.ready} ready, {total.notGenerated} not generated, {total.stale} stale, {total.failed} failed" +
                (total.locked > 0 ? $", {total.locked} locked" : ""));
        var w = Math.Max(5, chapters.Select(c => c.title.Length).DefaultIfEmpty(5).Max());
        w = Math.Min(w, 40);
        foreach (var c in chapters)
        {
            var t = c.title.Length > w ? c.title[..(w - 1)] + "…" : c.title;
            ctx.Out($"{c.number,4}. {t.PadRight(w)}  {c.segments,5} seg  {c.ready,5} ready  {c.notGenerated,5} new  {c.stale,5} stale  {c.failed,5} failed");
        }
        return new { total, chapters };
    }

    private static async Task<object> TimelineAsync(CliContext ctx, HeadlessProject p, CliArgs a)
    {
        var chapters = a.GetChapters();
        CheckChapters(p, chapters);
        var engine = p.Engine;
        var (layout, _) = await engine.ComputeTimelineAsync();
        ctx.Out($"Total {TimeFormat.Hms(layout.TotalMs, layout.HasEstimates)}{(layout.HasEstimates ? " (some lengths estimated)" : "")}");
        var rows = new List<object>();
        foreach (var t in layout.Items)
        {
            if (chapters != null && !chapters.Contains(t.ChapterIndex)) continue;
            var item = p.Project.Chapters[t.ChapterIndex].Items[t.ItemIndex];
            var state = item.IsGenerated || item.Kind == ItemKind.Clip ? engine.StateOf(item).ToString() : "";
            var text = item.Kind switch
            {
                ItemKind.Pause => "[pause]",
                ItemKind.Comment => "[comment] " + item.Text,
                ItemKind.Unknown => "[unknown] " + item.DisplayText,
                ItemKind.Clip => $"[clip {item.ClipFile}]",
                _ => item.Text ?? "",
            };
            rows.Add(new
            {
                chapter = t.ChapterIndex + 1,
                item = t.ItemIndex + 1,
                id = item.Id,
                kind = item.Kind,
                speaker = item.Speaker,
                state,
                startMs = Math.Round(t.StartMs),
                durationMs = Math.Round(t.DurationMs),
                estimate = t.IsEstimate,
                background = t.IsBackground,
                take = item.SelectedTake,
                error = item.LastError,
                text,
            });
            var excerpt = text.Replace('\n', ' ');
            if (excerpt.Length > 60) excerpt = excerpt[..59] + "…";
            ctx.Out($"{t.ChapterIndex + 1,3}.{t.ItemIndex + 1,-4} {TimeFormat.Hms(t.StartMs),9} {TimeFormat.Short(t.DurationMs),8}{(t.IsEstimate ? "~" : " ")} {state,-12} {item.Speaker ?? "",-12} {excerpt}");
        }
        return new { totalMs = Math.Round(layout.TotalMs), hasEstimates = layout.HasEstimates, items = rows };
    }

    // ================================================================== speakers

    private static object Speakers(CliContext ctx, HeadlessProject p)
    {
        var counts = p.Project.AllItems().Where(i => i.Speaker != null).GroupBy(i => i.Speaker!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var list = p.Project.Speakers.Values.OrderBy(s => s.Id != p.Project.Defaults.Speaker).ThenBy(s => s.DisplayName).Select(s => SpeakerInfo(p, s, counts.GetValueOrDefault(s.Id))).ToList();
        foreach (var s in p.Project.Speakers.Values.OrderBy(s => s.Id != p.Project.Defaults.Speaker).ThenBy(s => s.DisplayName))
        {
            var voice = s.Settings[WorkerManifest.VoiceKey]?.ToString() ?? (s.Voice != null ? "clone: " + s.Voice : "");
            ctx.Out($"{s.Id,-16} {s.DisplayName,-20} {(string.IsNullOrEmpty(s.Worker) ? "(microphone)" : s.Worker),-14} {voice,-16} {counts.GetValueOrDefault(s.Id),5} seg" +
                    (s.Id.Equals(p.Project.Defaults.Speaker, StringComparison.OrdinalIgnoreCase) ? "  (default)" : ""));
        }
        return list;
    }

    private static object SpeakerInfo(HeadlessProject p, Speaker s, int segments) => new
    {
        id = s.Id,
        name = s.DisplayName,
        worker = s.Worker,
        voiceClip = s.Voice,
        settings = s.Settings,
        aliases = s.MarkerAliases,
        segments,
        isDefault = s.Id.Equals(p.Project.Defaults.Speaker, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Creates or changes a speaker. Its segments become stale when their generation inputs change.</summary>
    private static object SpeakerEdit(CliContext ctx, HeadlessProject p, CliArgs a)
    {
        var project = p.Project;
        var host = p.Host;
        var id = a.Positionals[0];
        var sp = project.FindSpeaker(id) ?? project.FindSpeakerByLabel(id);
        if (sp == null)
        {
            if (!a.Has("--create")) throw new UsageException($"speaker: no speaker '{id}' (add --create to make it). Speakers: {string.Join(", ", project.Speakers.Keys)}.");
            var worker = a.Get("--worker") ?? project.FindSpeaker(project.Defaults.Speaker)?.Worker ?? host.DefaultTtsWorker;
            sp = p.Engine.AddSpeaker(a.Get("--name") ?? id, worker, project.Speakers.Count);
            ctx.Info($"Created speaker {sp.Id}.");
        }
        else if (a.Get("--worker") is { } w && !w.Equals(sp.Worker, StringComparison.OrdinalIgnoreCase))
        {
            if (host.Catalog.Get(w) is not { } wm) throw new UsageException($"speaker: unknown worker '{w}'.");
            sp.Worker = wm.Id;
            sp.Settings = wm.DefaultSettings();
            sp.Voice = null;
        }
        if (a.Get("--name") is { } name) sp.DisplayName = name;

        var manifest = host.Catalog.Get(sp.Worker);
        if (manifest == null && (a.Has("--voice") || a.Has("--set")))
            throw new UsageException($"speaker: {sp.DisplayName} has no worker; give one with --worker.");
        if (a.Get("--voice") is { } v)
        {
            if (manifest!.VoiceSetting is not { } vs || !vs.Options.Contains(v))
                throw new UsageException($"speaker: '{v}' isn't a voice of {manifest.DisplayName}." +
                                         (manifest.VoiceSetting is { Options.Count: > 0 } o ? $" Its voices: {string.Join(", ", o.Options)}." : ""));
            sp.Settings[WorkerManifest.VoiceKey] = v;
        }
        if (a.Get("--clone") is { } clip)
        {
            if (manifest?.VoiceCloning != true) throw new UsageException($"speaker: {manifest?.DisplayName ?? "this worker"} doesn't clone voices from a clip.");
            Directory.CreateDirectory(project.Paths.SpeakersDir);
            var fileName = sp.Id + ".ref" + Path.GetExtension(clip).ToLowerInvariant();
            File.Copy(clip, Path.Combine(project.Paths.SpeakersDir, fileName), overwrite: true);
            sp.Voice = fileName;
        }
        foreach (var (key, value) in a.GetPairs("--set"))
        {
            try { sp.Settings[key] = OneShot.ParseSettingValue(manifest!, key, value); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                throw new UsageException($"speaker: --set {key}: {ex.Message}");
            }
        }
        foreach (var key in a.GetAll("--unset")) sp.Settings.Remove(key);
        if (a.Has("--default") && !project.Defaults.Speaker.Equals(sp.Id, StringComparison.OrdinalIgnoreCase))
        {
            project.Defaults.Speaker = sp.Id;
            ProjectStore.SaveAll(project);
        }
        ProjectStore.SaveSpeaker(project, sp);

        var count = project.AllItems().Count(i => string.Equals(i.Speaker, sp.Id, StringComparison.OrdinalIgnoreCase));
        var stale = project.AllItems().Count(i => string.Equals(i.Speaker, sp.Id, StringComparison.OrdinalIgnoreCase) && p.Engine.StateOf(i) == SegmentAudioState.Stale);
        ctx.Info($"Saved speaker {sp.Id} ({sp.Worker}{(sp.Settings[WorkerManifest.VoiceKey] is JsonNode vn ? ", voice " + vn : "")}); {count} segment(s), {stale} now stale.");
        return SpeakerInfo(p, sp, count);
    }
}
