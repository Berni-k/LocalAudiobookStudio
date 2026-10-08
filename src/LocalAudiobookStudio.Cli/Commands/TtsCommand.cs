using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary>
/// <c>las tts</c>: text to a WAV file, like the Speakers dialog's preview. No project is changed. Text comes
/// from the arguments, stdin, or text files (a folder of them is one batch, so the workers start once).
/// Texts longer than the worker takes are generated in parts and joined.
/// </summary>
public static class TtsCommand
{
    private static readonly OptionSpec[] Options =
    [
        new("--input", "-i", Repeatable: true),
        new("--output", "-o"),
        new("--worker", "-w"),
        new("--runtime"),
        new("--voice"),
        new("--set", Repeatable: true),
        new("--seed"),
        new("--length"),
        new("--gap"),
        new("--project", "-p"),
        new("--speaker", "-s"),
        new("--skip-existing", HasValue: false),
        new("--recursive", "-r", HasValue: false),
    ];

    private static readonly string[] TextExtensions = [".txt", ".md"];

    /// <summary>One text to one WAV.</summary>
    private sealed class Unit(string name, string text, string output)
    {
        public string Name { get; } = name;
        public string Text { get; } = text;
        public string Output { get; } = output;
        public List<string> Parts { get; } = new();
        public List<int> Gaps { get; } = new();
        public int Remaining { get; set; }
        public string? Error { get; set; }
        public double DurationMs { get; set; }
        public bool Done { get; set; }
    }

    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("tts", tokens, Options);
        var units = CollectUnits(ctx, a, out var skipped);
        var gapMs = a.GetInt("--gap", 0, 60_000) ?? 400;
        var length = a.GetDouble("--length");
        if (a.Get("--output") == "-" && ctx.Json) throw new UsageException("tts: -o - writes the audio to stdout, which --json needs for its result.");
        if (a.Has("--project") != a.Has("--speaker")) throw new UsageException("tts: --project and --speaker go together (a speaker of that project).");
        if (a.Has("--project") && a.Has("--runtime")) throw new UsageException("tts: --runtime can't be combined with --speaker; it picks a runtime of --worker.");
        if (a.Has("--project") && a.Has("--worker")) throw new UsageException("tts: --worker can't be combined with --speaker (the speaker's worker is used).");
        if (units.Count == 0)
        {
            ctx.Info($"Nothing to do: all {skipped} output file(s) exist (--skip-existing).");
            ctx.WriteJson(new { results = Array.Empty<object>(), skipped });
            return Program.ExitOk;
        }

        var host = await ctx.StartHostAsync();
        var (manifest, settings, voice) = ResolveVoice(ctx, host, a);
        var seed = a.Get("--seed") is { } s ? (long?)long.Parse(s) : OneShot.SeedFrom(settings);

        var temp = Path.Combine(host.DataRoot.Temp, "las-tts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            return await GenerateAsync(ctx, host, manifest, settings, voice, seed, length, gapMs, units, skipped, temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* the data folder's tmp is cleaned another time */ }
        }
    }

    // ------------------------------------------------------------------ inputs

    private static List<Unit> CollectUnits(CliContext ctx, CliArgs a, out int skipped)
    {
        skipped = 0;
        var output = a.Get("--output");
        var inputs = a.GetAll("--input");
        if (inputs.Count > 0 && a.Positionals.Count > 0)
            throw new UsageException("tts: give either text or --input files, not both.");

        if (inputs.Count == 0)
        {
            string text;
            if (a.Positionals.Count > 0) text = string.Join(' ', a.Positionals);
            else if (Console.IsInputRedirected) text = Console.In.ReadToEnd();
            else throw new UsageException("tts: give the text as arguments, pipe it in, or use --input <file or folder>.");
            if (string.IsNullOrWhiteSpace(text)) throw new UsageException("tts: the text is empty.");
            if (output == null) throw new UsageException("tts: say where the audio goes with -o <file.wav> (or -o - for stdout).");
            if (output != "-" && Directory.Exists(output)) throw new UsageException($"tts: {output} is a folder; -o takes a .wav file here.");
            return [new Unit(a.Positionals.Count > 0 ? "text" : "stdin", text, output == "-" ? "-" : Path.GetFullPath(output))];
        }

        var files = new List<string>();
        var anyFolder = false;
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                anyFolder = true;
                var found = Directory.EnumerateFiles(input, "*", a.Has("--recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (found.Count == 0) ctx.Info($"No .txt or .md files in {input}.");
                files.AddRange(found);
            }
            else if (File.Exists(input)) files.Add(input);
            else throw new UsageException($"tts: {input} doesn't exist.");
        }
        files = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // One file may go to a named .wav; otherwise -o is a folder (made if needed), or each WAV goes next to its text.
        var singleFile = !anyFolder && files.Count == 1 && output != null && output != "-"
                         && Path.GetExtension(output).Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (output == "-") throw new UsageException("tts: -o - (stdout) works for one text, not --input files.");
        string? outDir = null;
        if (output != null && !singleFile)
        {
            outDir = Path.GetFullPath(output);
            if (File.Exists(outDir)) throw new UsageException($"tts: {output} is a file; with several inputs -o takes a folder.");
            Directory.CreateDirectory(outDir);
        }

        var units = new List<Unit>();
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var target = singleFile
                ? Path.GetFullPath(output!)
                : Path.Combine(outDir ?? Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + ".wav");
            if (!outputs.Add(target)) throw new UsageException($"tts: two inputs would both write {target}.");
            if (a.Has("--skip-existing") && File.Exists(target)) { skipped++; continue; }
            var text = TextDecoding.ReadFile(f);
            if (string.IsNullOrWhiteSpace(text)) { ctx.Info($"Skipping {Path.GetFileName(f)}: no text."); continue; }
            units.Add(new Unit(Path.GetFileName(f), text, target));
        }
        return units;
    }

    // ------------------------------------------------------------------ voice

    /// <summary>The worker, settings and voice: a project speaker's, or a worker's defaults; then --voice and --set.</summary>
    private static (WorkerManifest manifest, JsonObject settings, string? voice) ResolveVoice(CliContext ctx, StudioHost host, CliArgs a)
    {
        JsonObject? baseSettings = null;
        string? voice = null;
        Func<string, string> resolve = Path.GetFullPath;
        string workerId;
        if (a.Get("--project") is { } projectDir)
        {
            if (!ProjectStore.IsProjectFolder(projectDir)) throw new UsageException($"tts: no project in {projectDir}.");
            var project = ProjectStore.Load(projectDir);
            var id = a.Get("--speaker")!;
            var sp = project.FindSpeaker(id) ?? project.FindSpeakerByLabel(id)
                     ?? throw new UsageException($"tts: the project has no speaker '{id}'. Its speakers: {string.Join(", ", project.Speakers.Keys)}.");
            if (string.IsNullOrEmpty(sp.Worker)) throw new UsageException($"tts: {sp.DisplayName} is recorded with the microphone, not generated.");
            var engine = new ProjectEngine(host, project);
            workerId = sp.Worker;
            baseSettings = sp.Settings;
            voice = engine.ResolveVoice(sp.Voice);
            resolve = project.Paths.Resolve;
        }
        else
        {
            workerId = a.Get("--worker") ?? host.DefaultTtsWorker;
        }

        var manifest = host.Catalog.Get(workerId)
                       ?? throw new UsageException($"tts: unknown worker '{workerId}'. Workers: {string.Join(", ", host.Catalog.All.Select(m => m.Id))}.");
        if (a.Get("--runtime") is { } runtime)
        {
            var runtimes = host.Catalog.RuntimesOf(manifest.Id);
            var chosen = runtimes.FirstOrDefault(r => r.Runtime.Equals(runtime, StringComparison.OrdinalIgnoreCase))
                         ?? throw new UsageException($"tts: {manifest.DisplayName} has no runtime '{runtime}'. Its runtimes: {string.Join(", ", runtimes.Select(r => r.Runtime))}.");
            // In memory only: the CLI never saves the app's settings.
            if (!host.Settings.WorkerSettings.TryGetValue(manifest.Id, out var values)) host.Settings.WorkerSettings[manifest.Id] = values = new JsonObject();
            values[WorkerRuntimes.SettingKey] = chosen.Runtime;
            manifest = host.Catalog.Get(manifest.Id)!;
        }
        if (manifest.Broken is { } broken) ctx.Info($"Warning: {manifest.DisplayName} is known to be broken: {broken}");
        if (host.WhyWorkerCannotRun(manifest.Id) is { } why) throw new SetupException(why);
        baseSettings ??= manifest.DefaultSettings();

        var overrides = new JsonObject();
        if (a.Get("--voice") is { } v)
        {
            if (manifest.VoiceSetting is { } vs && vs.Options.Contains(v))
            {
                overrides[WorkerManifest.VoiceKey] = v;
            }
            else if (File.Exists(v))
            {
                if (!manifest.VoiceCloning) throw new UsageException($"tts: {manifest.DisplayName} doesn't clone voices from a clip.{VoiceList(manifest)}");
                voice = Path.GetFullPath(v);
            }
            else
            {
                throw new UsageException($"tts: '{v}' is neither a voice of {manifest.DisplayName} nor a file.{VoiceList(manifest)}");
            }
        }
        foreach (var (key, value) in a.GetPairs("--set"))
        {
            try { overrides[key] = OneShot.ParseSettingValue(manifest, key, value); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                throw new UsageException($"tts: --set {key}: {ex.Message}");
            }
        }
        var settings = OneShot.MergeSettings(baseSettings, overrides, manifest, resolve);
        if (manifest.UnsupportedChoice(settings) is { } unsupported) throw new UsageException("tts: " + unsupported);
        return (manifest, settings, voice);
    }

    private static string VoiceList(WorkerManifest m) =>
        m.VoiceSetting is { Options.Count: > 0 } vs ? $" Its voices: {string.Join(", ", vs.Options)}." : "";

    // ------------------------------------------------------------------ generating

    private static async Task<int> GenerateAsync(CliContext ctx, StudioHost host, WorkerManifest manifest, JsonObject settings,
        string? voice, long? seed, double? length, int gapMs, List<Unit> units, int skipped, string temp)
    {
        var jobs = new List<GenerationJob>();
        var owner = new Dictionary<Guid, Unit>();
        for (int u = 0; u < units.Count; u++)
        {
            var unit = units[u];
            // Split into what the worker takes, with a gap between paragraphs.
            var paragraphs = OneShot.SplitForWorker(unit.Text, manifest.MaxChars);
            var parts = paragraphs.SelectMany((p, pi) => p.Select((text, ci) => (text, gap: ci == p.Count - 1 ? gapMs : 0))).ToList();
            for (int k = 0; k < parts.Count; k++)
            {
                var path = Path.Combine(temp, $"{u:D5}-{k:D5}.wav");
                var label = parts.Count == 1 ? unit.Name : $"{unit.Name} {k + 1}/{parts.Count}";
                var job = OneShot.Job(manifest, parts[k].text, voice, settings, path, label, JobPriority.Normal, seed, length,
                    tag: "las-tts", readingOrder: u * 1_000_000L + k);
                jobs.Add(job);
                owner[job.Id] = unit;
                unit.Parts.Add(path);
                unit.Gaps.Add(parts[k].gap);
            }
            unit.Remaining = parts.Count;
        }

        var sync = new object();
        var done = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        ctx.Info($"{manifest.DisplayName}: {units.Count} text(s), {jobs.Count} part(s){(skipped > 0 ? $", {skipped} skipped" : "")}");

        void Finished(GenerationJob job)
        {
            if (!owner.TryGetValue(job.Id, out var unit)) return;
            lock (sync)
            {
                done++;
                if (job.State != JobState.Completed)
                    unit.Error ??= job.State == JobState.Cancelled ? "Cancelled." : job.LastError ?? "Failed.";
                else
                    ctx.Detail($"[{done}/{jobs.Count}] {job.Label} ({job.DeviceName}, {(job.FinishedUtc - job.StartedUtc)?.TotalSeconds:0.0} s)");
                if (--unit.Remaining == 0) Finish(ctx, unit);
                if (done == jobs.Count) all.TrySetResult();
            }
        }

        var scheduler = host.Scheduler;
        scheduler.JobCompleted += Finished;
        scheduler.JobFailed += Finished;
        try
        {
            await using var reg = ctx.Cancel.Register(scheduler.CancelAll);
            scheduler.EnqueueMany(jobs);
            await all.Task;
        }
        finally
        {
            scheduler.JobCompleted -= Finished;
            scheduler.JobFailed -= Finished;
        }

        var failed = units.Count(u => u.Error != null);
        ctx.Info($"{units.Count - failed} of {units.Count} done in {sw.Elapsed.TotalSeconds:0.0} s{(failed > 0 ? $", {failed} failed" : "")}.");
        ctx.WriteJson(new
        {
            worker = manifest.Id,
            skipped,
            results = units.Select(u => new
            {
                input = u.Name,
                output = u.Output,
                ok = u.Error == null,
                durationMs = u.Error == null ? Math.Round(u.DurationMs) : (double?)null,
                parts = u.Parts.Count,
                error = u.Error,
            }),
        });
        return failed > 0 ? Program.ExitFailures : Program.ExitOk;
    }

    /// <summary>Joins a text's parts into its output (or reports why it failed). Called under the lock.</summary>
    private static void Finish(CliContext ctx, Unit unit)
    {
        if (unit.Error == null)
        {
            try
            {
                var target = unit.Output == "-" ? unit.Parts[0] + ".joined.wav" : unit.Output;
                if (unit.Output != "-") Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (unit.Parts.Count == 1) File.Copy(unit.Parts[0], target, overwrite: true);
                else OneShot.ConcatenateWavs(unit.Parts, unit.Gaps, target);
                unit.DurationMs = AudioDecoder.ReadInfo(target).DurationMs;
                if (unit.Output == "-")
                {
                    using var stdout = Console.OpenStandardOutput();
                    using var wav = File.OpenRead(target);
                    wav.CopyTo(stdout);
                }
            }
            catch (Exception ex)
            {
                unit.Error = ex.Message;
            }
        }
        unit.Done = true;
        if (unit.Error != null)
            ctx.Info($"FAILED {unit.Name}: {unit.Error}");
        else
            ctx.Info($"done   {unit.Name} -> {(unit.Output == "-" ? "stdout" : unit.Output)} ({TimeFormat.Short(unit.DurationMs)}{(unit.Parts.Count > 1 ? $", {unit.Parts.Count} parts" : "")})");
        if (unit.Error == null && unit.Output != "-") ctx.Out(unit.Output);
    }
}
