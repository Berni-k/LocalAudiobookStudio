using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary>
/// <c>las compare</c>: how alike the voices in audio files are, by a speaker verification worker. Without a reference it
/// prints the pairwise similarities and how well each file fits the others (the way to compare models or seeds);
/// with one, each file's score against it.
/// </summary>
public static class CompareCommand
{
    private static readonly OptionSpec[] Options =
    [
        new("--ref", Repeatable: true),
        new("--worker", "-w"),
        new("--runtime"),
        new("--device"),
        new("--set", Repeatable: true),
        new("--recursive", "-r", HasValue: false),
    ];

    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("compare", tokens, Options);
        var references = a.GetAll("--ref").Select(f => Existing(ctx.FullPath(f), "--ref")).ToList();
        var files = CollectFiles(ctx, a).Where(f => !references.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (references.Count == 0 && files.Count < 2)
            throw new UsageException("compare: give at least two audio files to compare with each other, or one or more files and --ref <clip>.");
        if (references.Count > 0 && files.Count < 1)
            throw new UsageException("compare: give the audio files to score against --ref.");

        var host = await ctx.StartHostAsync();
        var (manifest, settings) = ResolveWorker(ctx, host, a);

        var temp = Path.Combine(host.DataRoot.Temp, "las-compare-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            // References first, as in the request: the matrix follows this order.
            var all = references.Select(r => (Path: r, IsReference: true)).Concat(files.Select(f => (Path: f, IsReference: false))).ToList();
            var voiceFiles = new List<VoiceFile>();
            for (var i = 0; i < all.Count; i++)
            {
                var wav = Path.Combine(temp, $"{i:D5}.wav");
                try { Transcription.PrepareAudio(all[i].Path, wav); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    throw new UsageException($"compare: can't read {all[i].Path}: {ex.Message}");
                }
                voiceFiles.Add(new VoiceFile { Path = wav, Role = all[i].IsReference ? VoiceRoles.Reference : VoiceRoles.Candidate });
            }

            var scores = await ScoreAsync(ctx, host, manifest, settings, voiceFiles);
            Report(ctx, manifest, all.Select(f => f.Path).ToList(), references.Count, scores);
            return Program.ExitOk;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* the data folder's tmp is cleaned another time */ }
        }
    }

    private static string Existing(string path, string option)
    {
        if (!File.Exists(path)) throw new UsageException($"compare: {option} {path} doesn't exist.");
        if (!AudioDecoder.CanDecode(path)) throw new UsageException($"compare: {path} isn't a file it can read ({string.Join(", ", AudioDecoder.Extensions)}).");
        return path;
    }

    private static List<string> CollectFiles(CliContext ctx, CliArgs a)
    {
        var files = new List<string>();
        foreach (var input in a.Positionals.Select(ctx.FullPath))
        {
            if (Directory.Exists(input))
            {
                var found = Directory.EnumerateFiles(input, "*", a.Has("--recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(AudioDecoder.CanDecode).Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (found.Count == 0) ctx.Info($"No audio files ({string.Join(", ", AudioDecoder.Extensions)}) in {input}.");
                files.AddRange(found);
            }
            else files.Add(Existing(input, "file"));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The worker (--worker, else the first installed speaker verification worker) and its settings with the --set overrides.</summary>
    private static (WorkerManifest manifest, JsonObject settings) ResolveWorker(CliContext ctx, StudioHost host, CliArgs a)
    {
        var checkers = host.Catalog.SimScorers;
        var workerId = a.Get("--worker")
                       ?? checkers.FirstOrDefault(m => host.WhyWorkerCannotRun(m.Id) == null)?.Id
                       ?? checkers.FirstOrDefault()?.Id
                       ?? throw new SetupException("No speaker verification worker is available.");
        var manifest = host.Catalog.Get(workerId)
                       ?? throw new UsageException($"compare: unknown worker '{workerId}'. Speaker verification workers: {string.Join(", ", checkers.Select(m => m.Id))}.");
        if (!manifest.VerifiesSpeaker)
            throw new UsageException($"compare: {manifest.DisplayName} doesn't compare voices. Speaker verification workers: {string.Join(", ", checkers.Select(m => m.Id))}.");
        if (a.Get("--runtime") is { } runtime) manifest = WorkerSetupCommands.UseRuntime(host, manifest, runtime, "compare", ctx.Shared);
        if (a.Get("--device") is { } device) WorkerSetupCommands.UseDevice(host, manifest, device, "compare", ctx.Shared);
        if (manifest.Broken is { } broken) ctx.Info($"Warning: {manifest.DisplayName} is known to be broken: {broken}");
        if (host.WhyWorkerCannotRun(manifest.Id) is { } why) throw new SetupException(why);

        var overrides = new JsonObject();
        foreach (var (key, value) in a.GetPairs("--set"))
        {
            try { overrides[key] = OneShot.ParseSettingValue(manifest, key, value); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                throw new UsageException($"compare: --set {key}: {ex.Message}");
            }
        }
        return (manifest, OneShot.MergeSettings(manifest.DefaultSettings(), overrides, manifest, ctx.FullPath));
    }

    private static async Task<VoiceScores> ScoreAsync(CliContext ctx, StudioHost host, WorkerManifest manifest, JsonObject settings,
        List<VoiceFile> files)
    {
        var job = SpeakerVerification.Job(manifest, files, settings, $"Compare {files.Count} files", JobPriority.Normal, readingOrder: ctx.Order(0));
        var done = new TaskCompletionSource<GenerationJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(GenerationJob j) { if (ReferenceEquals(j, job)) done.TrySetResult(j); }

        var scheduler = host.Scheduler;
        scheduler.JobCompleted += Finished;
        scheduler.JobFailed += Finished;
        try
        {
            ctx.Info($"{manifest.DisplayName}: {files.Count} file(s)");
            await using var reg = ctx.Enqueue(scheduler, [job]);
            var finished = await done.Task;
            if (finished.State == JobState.Cancelled) throw new OperationCanceledException();
            return finished.VoiceScores ?? throw new InvalidOperationException(finished.LastError ?? "The comparison failed.");
        }
        finally
        {
            scheduler.JobCompleted -= Finished;
            scheduler.JobFailed -= Finished;
        }
    }

    // ------------------------------------------------------------------ output

    private static void Report(CliContext ctx, WorkerManifest manifest, List<string> paths, int referenceCount, VoiceScores scores)
    {
        var names = paths.Select(Path.GetFileName).Select(n => n ?? "").ToList();
        var shortClips = Enumerable.Range(0, paths.Count).Where(i => scores.SpeechMs[i] < SpeakerVerification.MinSpeechMs).ToList();
        var model = $"{manifest.Id} / {scores.Model}";

        if (referenceCount > 0)
        {
            ctx.Out($"Scores against {(referenceCount == 1 ? names[0] : $"the average of {referenceCount} references")} ({model}):");
            for (var i = 0; i < scores.Scores.Count; i++)
                ctx.Out($"  {scores.Scores[i].ToString("0.000", CultureInfo.InvariantCulture),6}  {names[referenceCount + i]}  ({Seconds(scores.SpeechMs[referenceCount + i])} of speech)");
            if (shortClips.Any(i => i >= referenceCount))
                ctx.Out($"Clips with less than {SpeakerVerification.MinSpeechMs / 1000.0:0.#} s of speech score unreliably.");
            ctx.WriteJson(new
            {
                worker = manifest.Id,
                model = scores.Model,
                references = names.Take(referenceCount),
                files = names.Skip(referenceCount).Select((n, i) => new { file = n, score = scores.Scores[i], speechMs = scores.SpeechMs[referenceCount + i] }),
                matrix = scores.Matrix,
            });
            return;
        }

        var fit = SpeakerVerification.Summarize(scores.Matrix);
        ctx.Out($"Similarity of every pair ({model}):");
        var sb = new StringBuilder("      ");
        for (var j = 0; j < paths.Count; j++) sb.Append($"{j + 1,7}");
        ctx.Out(sb.ToString());
        for (var i = 0; i < paths.Count; i++)
        {
            sb.Clear().Append($"{i + 1,4}  ");
            for (var j = 0; j < paths.Count; j++) sb.Append(i == j ? "      -" : scores.Matrix[i][j].ToString("0.000", CultureInfo.InvariantCulture).PadLeft(7));
            sb.Append("  ").Append(names[i]);
            ctx.Out(sb.ToString());
        }
        ctx.Out("");
        ctx.Out("How each file fits the others:");
        for (var i = 0; i < paths.Count; i++)
        {
            var f = fit.Files[i];
            ctx.Out($"  {i + 1,3}  mean {f.Mean.ToString("0.000", CultureInfo.InvariantCulture)}  min {f.Min.ToString("0.000", CultureInfo.InvariantCulture)} (with {f.MinIndex + 1})  " +
                    $"{Seconds(scores.SpeechMs[i])} of speech{(f.Outlier ? "  OUTLIER" : "")}{(scores.SpeechMs[i] < SpeakerVerification.MinSpeechMs ? "  short" : "")}  {names[i]}");
        }
        if (paths.Count <= SpeakerVerification.MinBaseline)
            ctx.Out($"(Outliers are only marked among {SpeakerVerification.MinBaseline + 1} or more files.)");
        ctx.Out("");
        ctx.Out($"Group ({model}): mean {fit.PairMean.ToString("0.000", CultureInfo.InvariantCulture)}, min {fit.PairMin.ToString("0.000", CultureInfo.InvariantCulture)}, spread {fit.PairSpread.ToString("0.000", CultureInfo.InvariantCulture)}");
        ctx.WriteJson(new
        {
            worker = manifest.Id,
            model = scores.Model,
            files = names.Select((n, i) => new
            {
                file = n,
                speechMs = scores.SpeechMs[i],
                mean = Math.Round(fit.Files[i].Mean, 4),
                min = Math.Round(fit.Files[i].Min, 4),
                minFile = names[fit.Files[i].MinIndex],
                outlier = fit.Files[i].Outlier,
            }),
            matrix = scores.Matrix,
            group = new { mean = Math.Round(fit.PairMean, 4), min = Math.Round(fit.PairMin, 4), spread = Math.Round(fit.PairSpread, 4) },
        });
    }

    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
