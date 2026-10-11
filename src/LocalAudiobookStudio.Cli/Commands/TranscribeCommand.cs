using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary>
/// <c>las transcribe</c>: what a transcription worker hears in audio files, with word timings. Given the text the
/// audio should contain, it also lists the words that are missing, extra or different.
/// </summary>
public static class TranscribeCommand
{
    private static readonly OptionSpec[] Options =
    [
        new("--worker", "-w"),
        new("--runtime"),
        new("--device"),
        new("--set", Repeatable: true),
        new("--text", "-t"),
        new("--text-file", "-f"),
        new("--words", HasValue: false),
        new("--mode"),
        new("--speakers"),
        new("--speaker-count"),
        new("--recursive", "-r", HasValue: false),
    ];

    /// <summary>One audio file to transcribe.</summary>
    private sealed class Unit(string audio, string? expected)
    {
        public string Audio { get; } = audio;
        public string? Expected { get; } = expected;
        public string Wav { get; set; } = "";
        public TranscriptResult? Transcript { get; set; }
        public TranscriptCheck? Check { get; set; }
        public string? Error { get; set; }
        public double Seconds { get; set; }
    }

    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("transcribe", tokens, Options);
        var units = CollectUnits(ctx, a);

        var host = await ctx.StartHostAsync();
        var (manifest, settings) = ResolveWorker(ctx, host, a);
        var mode = ModeOf(manifest, a.Get("--mode"));
        // Aligning finds the words of a given text: without the text it has nothing to do, and comparing its words
        // with that text would always "match". What it gives is the timing, so the words are always shown.
        var aligner = mode == ListenMode.Align;
        if (aligner && units.Where(u => u.Expected == null).Select(u => Path.GetFileName(u.Audio)).ToList() is { Count: > 0 } missing)
            throw new UsageException($"transcribe: aligning finds the words of a known text, so it needs the text: give -t or -f " +
                                     $"(missing for {string.Join(", ", missing)}).");
        if (mode == ListenMode.Transcribe && a.Has("--words"))
            ctx.Info("Text only (--mode transcribe): the words have no times.");
        // Hints for workers that tell voices apart; any worker may ignore them.
        var speakers = a.Get("--speakers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int? speakerCount = null;
        if (a.Get("--speaker-count") is { } countText)
            speakerCount = int.TryParse(countText, out var count) && count > 0
                ? count
                : throw new UsageException($"transcribe: --speaker-count is a number of voices, not '{countText}'.");
        var temp = Path.Combine(host.DataRoot.Temp, "las-transcribe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            return await TranscribeAsync(ctx, host, manifest, settings, mode, (a.Has("--words") || aligner) && mode != ListenMode.Transcribe,
                aligner, units, temp, speakers, speakerCount);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* the data folder's tmp is cleaned another time */ }
        }
    }

    // ------------------------------------------------------------------ inputs

    private static List<Unit> CollectUnits(CliContext ctx, CliArgs a)
    {
        if (a.Positionals.Count == 0) throw new UsageException("transcribe: give one or more audio files or folders.");
        if (a.Has("--text") && a.Has("--text-file")) throw new UsageException("transcribe: give either --text or --text-file, not both.");

        var files = new List<string>();
        foreach (var input in a.Positionals)
        {
            if (Directory.Exists(input))
            {
                var found = Directory.EnumerateFiles(input, "*", a.Has("--recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(AudioDecoder.CanDecode)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (found.Count == 0) ctx.Info($"No audio files ({string.Join(", ", AudioDecoder.Extensions)}) in {input}.");
                files.AddRange(found);
            }
            else if (File.Exists(input))
            {
                if (!AudioDecoder.CanDecode(input))
                    throw new UsageException($"transcribe: {input} isn't a file it can read ({string.Join(", ", AudioDecoder.Extensions)}).");
                files.Add(input);
            }
            else throw new UsageException($"transcribe: {input} doesn't exist.");
        }
        files = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // The expected text: --text for one file; --text-file is a file for one, or a folder of <audio name>.txt.
        var textFile = a.Get("--text-file");
        var textFolder = textFile != null && Directory.Exists(textFile) ? textFile : null;
        if (files.Count > 1 && (a.Has("--text") || (textFile != null && textFolder == null)))
            throw new UsageException("transcribe: --text and a --text-file file go with one audio file; for several, give --text-file a folder of <audio name>.txt files.");
        if (textFile != null && textFolder == null && !File.Exists(textFile))
            throw new UsageException($"transcribe: {textFile} doesn't exist.");

        return files.Select(f =>
        {
            string? expected = a.Get("--text");
            if (textFolder != null)
            {
                var txt = Path.Combine(textFolder, Path.GetFileNameWithoutExtension(f) + ".txt");
                if (File.Exists(txt)) expected = TextDecoding.ReadFile(txt);
                else ctx.Info($"No {Path.GetFileName(txt)} in {textFolder}; {Path.GetFileName(f)} is only transcribed.");
            }
            else if (textFile != null) expected = TextDecoding.ReadFile(textFile);
            return new Unit(f, expected);
        }).ToList();
    }

    /// <summary>
    /// What to ask the worker for: --mode (transcribe, align, transcribe-align), checked against its tags, else the most
    /// it can do (<see cref="WorkerManifest.ListenMode"/>).
    /// </summary>
    private static ListenMode ModeOf(WorkerManifest manifest, string? requested)
    {
        if (requested == null) return manifest.ListenMode;
        var mode = requested.ToLowerInvariant() switch
        {
            "transcribe" => ListenMode.Transcribe,
            "align" => ListenMode.Align,
            "transcribe-align" or "transcribealign" => ListenMode.TranscribeAlign,
            _ => throw new UsageException($"transcribe: --mode is transcribe, align or transcribe-align, not '{requested}'."),
        };
        var can = mode switch
        {
            ListenMode.Transcribe => manifest.CanTranscribe,
            ListenMode.Align => manifest.ListenMode == ListenMode.Align,
            _ => manifest.ListenMode == ListenMode.TranscribeAlign,
        };
        if (!can)
            throw new UsageException($"transcribe: {manifest.DisplayName} can't do --mode {requested} (it is tagged {string.Join(", ", manifest.Tags)}; " +
                                     $"it does {ModeName(manifest.ListenMode)}).");
        return mode;
    }

    private static string ModeName(ListenMode mode) => mode switch
    {
        ListenMode.Transcribe => "transcribe",
        ListenMode.Align => "align",
        _ => "transcribe-align",
    };

    /// <summary>The worker (--worker, else the first installed transcriber) and its settings with the --set overrides.</summary>
    private static (WorkerManifest manifest, JsonObject settings) ResolveWorker(CliContext ctx, StudioHost host, CliArgs a)
    {
        // Transcribers before aligners: the default must work without a text.
        var transcribers = host.Catalog.Transcribers.OrderByDescending(m => m.HasTag(WorkerTags.Transcription)).ToList();
        var workerId = a.Get("--worker")
                       ?? transcribers.FirstOrDefault(m => host.WhyWorkerCannotRun(m.Id) == null)?.Id
                       ?? transcribers.FirstOrDefault()?.Id
                       ?? throw new SetupException("No transcription worker is available.");
        var manifest = host.Catalog.Get(workerId)
                       ?? throw new UsageException($"transcribe: unknown worker '{workerId}'. Transcription workers: {string.Join(", ", transcribers.Select(m => m.Id))}.");
        if (!manifest.IsTranscriber)
            throw new UsageException($"transcribe: {manifest.DisplayName} doesn't transcribe. Transcription workers: {string.Join(", ", transcribers.Select(m => m.Id))}.");
        if (a.Get("--runtime") is { } runtime) manifest = WorkerSetupCommands.UseRuntime(host, manifest, runtime, "transcribe");
        if (a.Get("--device") is { } device) WorkerSetupCommands.UseDevice(host, manifest, device, "transcribe");
        if (manifest.Broken is { } broken) ctx.Info($"Warning: {manifest.DisplayName} is known to be broken: {broken}");
        if (host.WhyWorkerCannotRun(manifest.Id) is { } why) throw new SetupException(why);

        var overrides = new JsonObject();
        foreach (var (key, value) in a.GetPairs("--set"))
        {
            try { overrides[key] = OneShot.ParseSettingValue(manifest, key, value); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                throw new UsageException($"transcribe: --set {key}: {ex.Message}");
            }
        }
        return (manifest, OneShot.MergeSettings(manifest.DefaultSettings(), overrides, manifest, Path.GetFullPath));
    }

    // ------------------------------------------------------------------ transcribing

    private static async Task<int> TranscribeAsync(CliContext ctx, StudioHost host, WorkerManifest manifest, JsonObject settings,
        ListenMode mode, bool showWords, bool aligner, List<Unit> units, string temp, List<string>? speakers, int? speakerCount)
    {
        var jobs = new List<GenerationJob>();
        var owner = new Dictionary<Guid, Unit>();
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            try
            {
                unit.Wav = Path.Combine(temp, $"{i:D5}.wav");
                Transcription.PrepareAudio(unit.Audio, unit.Wav);
            }
            catch (Exception ex)
            {
                unit.Error = $"Can't read the audio: {ex.Message}";
                continue;
            }
            var job = Transcription.Job(manifest, unit.Wav, unit.Expected, settings, Path.GetFileName(unit.Audio),
                JobPriority.Normal, tag: "las-transcribe", readingOrder: i, mode: mode, speakers: speakers, speakerCount: speakerCount);
            jobs.Add(job);
            owner[job.Id] = unit;
        }

        var sync = new object();
        var done = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        ctx.Info($"{manifest.DisplayName}: {units.Count} file(s)");

        void Finished(GenerationJob job)
        {
            if (!owner.TryGetValue(job.Id, out var unit)) return;
            lock (sync)
            {
                done++;
                if (job.State != JobState.Completed || job.Transcript == null)
                {
                    unit.Error = job.State == JobState.Cancelled ? "Cancelled." : job.LastError ?? "Failed.";
                }
                else
                {
                    unit.Transcript = job.Transcript;
                    unit.Seconds = (job.FinishedUtc - job.StartedUtc)?.TotalSeconds ?? 0;
                    if (unit.Expected != null && !aligner) unit.Check = Transcription.Check(unit.Expected, job.Transcript);
                }
                Report(ctx, unit, showWords, units.Count > 1);
                if (done == jobs.Count) all.TrySetResult();
            }
        }

        foreach (var unit in units.Where(u => u.Error != null)) Report(ctx, unit, showWords, units.Count > 1);
        if (jobs.Count > 0)
        {
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
        }

        var failed = units.Count(u => u.Error != null);
        var checkedUnits = units.Where(u => u.Check != null).ToList();
        ctx.Info($"{units.Count - failed} of {units.Count} transcribed in {sw.Elapsed.TotalSeconds:0.0} s{(failed > 0 ? $", {failed} failed" : "")}" +
                 (checkedUnits.Count > 0 ? $"; {checkedUnits.Count(u => u.Check!.Errors > 0)} of {checkedUnits.Count} differ from their text." : "."));
        ctx.WriteJson(new
        {
            worker = manifest.Id,
            results = units.Select(u => new
            {
                input = u.Audio,
                ok = u.Error == null,
                error = u.Error,
                text = u.Transcript?.Text,
                language = u.Transcript?.Language,
                words = u.Transcript?.Words.Select(w => new { text = w.Text, startMs = w.StartMs, endMs = w.EndMs, confidence = Round(w.Confidence), speaker = w.Speaker }),
                check = u.Check is not { } c ? null : new
                {
                    expectedWords = c.ExpectedCount,
                    errors = c.Errors,
                    substituted = c.Substitutions,
                    missing = c.Missing,
                    extra = c.Extra,
                    wordErrorRate = Math.Round(c.WordErrorRate, 4),
                    words = c.Words.Select(w => new
                    {
                        kind = w.Kind,
                        expected = w.Expected,
                        heard = w.Heard,
                        startMs = w.StartMs,
                        endMs = w.EndMs,
                        textStart = w.Expected != null ? w.TextStart : (int?)null,
                        textLength = w.Expected != null ? w.TextLength : (int?)null,
                    }),
                },
            }),
        });
        return failed > 0 ? Program.ExitFailures : Program.ExitOk;
    }

    private static double? Round(double? d) => d is { } v ? Math.Round(v, 3) : null;

    /// <summary>Prints one file's result (stdout, without --json). Called under the lock.</summary>
    private static void Report(CliContext ctx, Unit unit, bool showWords, bool several)
    {
        var name = Path.GetFileName(unit.Audio);
        if (unit.Error != null)
        {
            ctx.Info($"FAILED {name}: {unit.Error}");
            return;
        }
        var t = unit.Transcript!;
        ctx.Detail($"{name}: {unit.Seconds:0.0} s");
        if (several) ctx.Out($"{name}:");
        var indent = several ? "  " : "";
        ctx.Out(indent + t.Text);
        if (showWords)
            foreach (var w in t.Words)
                ctx.Out($"{indent}  {Seconds(w.StartMs),7} {Seconds(w.EndMs),7}  {(w.Speaker != null ? $"[{w.Speaker}] " : "")}{w.Text}{(w.Confidence is { } p ? $"  ({p:0.00})" : "")}");
        if (unit.Check is not { } c) return;
        if (c.Errors == 0)
        {
            ctx.Out($"{indent}Matches the text ({c.ExpectedCount} words{(c.Words.Any(w => w.Kind == WordMatch.Close) ? ", some spelled differently" : "")}).");
            return;
        }
        ctx.Out($"{indent}{c.Errors} difference(s) in {c.ExpectedCount} words (word error rate {c.WordErrorRate:P0}):");
        foreach (var w in c.Words.Where(w => w.IsError))
        {
            var what = w.Kind switch
            {
                WordMatch.Missing => $"missing  \"{w.Expected}\"",
                WordMatch.Extra => $"extra    \"{w.Heard}\"",
                _ => $"heard    \"{w.Heard}\" for \"{w.Expected}\"",
            };
            ctx.Out($"{indent}  {Seconds(w.StartMs),7}  {what}");
        }
    }

    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";
}
