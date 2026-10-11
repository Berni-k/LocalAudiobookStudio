using System.Text.RegularExpressions;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Engine.Projects;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary>Commands that only look: workers, devices, how a text would be split, the version (installing workers is in <see cref="WorkerSetupCommands"/>).</summary>
public static class InfoCommands
{
    /// <summary><c>las workers [id]</c>: the catalog with install state; one worker's settings and voices.</summary>
    public static async Task<int> WorkersAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("workers", tokens, new OptionSpec("--runtime"));
        if (a.Positionals.Count > 0 && WorkerSetupCommands.Actions.Contains(a.Positionals[0].ToLowerInvariant()))
            return await WorkerSetupCommands.RunAsync(ctx, a.Positionals[0].ToLowerInvariant(), a);
        if (a.Has("--runtime")) throw new UsageException("workers: --runtime goes with install, repair or uninstall.");
        var host = await ctx.StartHostAsync();
        if (a.Positionals.Count == 0)
        {
            var list = host.Catalog.All.Select(m => new
            {
                id = m.Id,
                name = m.DisplayName,
                tags = m.Tags,
                runtime = m.Runtime,
                status = host.Environments.GetWorkerStatus(m.Id),
                ready = host.WhyWorkerCannotRun(m.Id) == null,
                broken = m.Broken,
                isDefault = m.Id == host.DefaultTtsWorker,
                experimental = m.Experimental,
            }).ToList();
            foreach (var w in list)
                ctx.Out($"{w.id,-18} {w.status,-16} {w.runtime,-8} {w.name}{(w.isDefault ? "  (default)" : "")}{(w.experimental ? "  (experimental)" : "")}{(w.broken != null ? "  (BROKEN)" : "")}");
            ctx.WriteJson(list);
            return Program.ExitOk;
        }

        var m = host.Catalog.Get(a.Positionals[0]) ?? throw new UsageException($"workers: unknown worker '{a.Positionals[0]}'.");
        var info = new
        {
            id = m.Id,
            name = m.DisplayName,
            description = m.Description,
            tags = m.Tags,
            runtime = m.Runtime,
            modelVersion = m.ModelVersion,
            status = host.Environments.GetWorkerStatus(m.Id),
            whyNotReady = host.WhyWorkerCannotRun(m.Id),
            maxChars = m.MaxChars,
            sampleRate = m.SampleRate,
            voiceCloning = m.VoiceCloning,
            length = m.LengthMode,
            voices = m.VoiceSetting?.Options,
            transcribes = m.IsTranscriber,
            listens = m.IsTranscriber ? m.ListenMode.ToString() : null,
            settings = m.Settings.Select(s => new
            {
                key = s.Key,
                type = s.Type,
                label = s.Label,
                @default = s.Default,
                min = s.Min,
                max = s.Max,
                options = s.Options.Count > 0 ? s.Options : null,
                description = s.Description,
            }),
        };
        ctx.Out($"{m.DisplayName} ({m.Id}, {m.Runtime}) — {info.status}");
        var runtimes = host.Catalog.RuntimesOf(m.Id);
        if (runtimes.Count > 1)
            ctx.Out($"  runtimes ({(m.IsTranscriber ? "transcribe" : "tts")} --runtime): " + string.Join(", ", runtimes.Select(r =>
                $"{r.Runtime}{(host.Environments.IsRuntimeInstalled(r) ? " (installed)" : "")}{(r.Runtime == m.Runtime ? " *" : "")}")));
        if (info.whyNotReady != null) ctx.Out("  " + info.whyNotReady);
        if (!string.IsNullOrEmpty(m.Description)) ctx.Out("  " + m.Description);
        if (m.IsTranscriber)
            ctx.Out(m.ListenMode switch
            {
                ListenMode.TranscribeAlign => $"  hears words and their times: transcribe-align, or transcribe (las transcribe -w {m.Id})",
                ListenMode.Align => $"  times the words of a given text: align (las transcribe -w {m.Id} -t \"…\")",
                _ => $"  hears words, without times: transcribe (las transcribe -w {m.Id})",
            } + (m.AlignsText && m.CanTranscribe ? $"; also times a given text: align (--mode align, or --aligner {m.Id})" : "")
              + (m.TellsSpeakers ? "; says who said each word" : ""));
        else
            ctx.Out($"  max {m.MaxChars} characters per request, {m.SampleRate} Hz, length: {m.LengthMode}{(m.VoiceCloning ? ", clones voices from a clip" : "")}");
        if (m.Settings.Count > 0) ctx.Out("  settings (--set key=value):");
        foreach (var s in m.Settings)
        {
            var range = s.Min != null || s.Max != null ? $" [{s.Min}..{s.Max}]" : "";
            var options = s.Options.Count > 0 && s.Key != Workers.Manifest.WorkerManifest.VoiceKey ? $" {{{string.Join(", ", s.Options)}}}" : "";
            ctx.Out($"    {s.Key,-20} {s.Type,-6} default {s.Default?.ToJsonString() ?? "-"}{range}{options}");
        }
        if (m.VoiceSetting is { Options.Count: > 0 } vs) ctx.Out($"  voices (--voice): {string.Join(", ", vs.Options)}");
        ctx.WriteJson(info);
        return Program.ExitOk;
    }

    /// <summary><c>las devices</c>: GPUs found and the devices the scheduler uses.</summary>
    public static async Task<int> DevicesAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        CliArgs.Parse("devices", tokens).NoPositionals();
        var host = await ctx.StartHostAsync();
        var devices = host.Settings.Devices.Select(d => new
        {
            name = host.DeviceName(d),
            gpu = d.Gpu >= 0 ? d.Gpu : (int?)null,
            enabled = d.Enabled,
            maxActive = d.MaxActive,
            maxTotal = d.MaxTotal,
            cpuThreads = d.Gpu < 0 ? d.CpuThreads : (int?)null,
        }).ToList();
        foreach (var g in host.Gpus) ctx.Out($"GPU {g.Index}: {g.Name}, {g.MemoryMb} MB{(g.Driver != null ? ", driver " + g.Driver : "")}");
        if (host.Gpus.Count == 0) ctx.Out("No NVIDIA GPU found.");
        ctx.Out("Devices (from the app's settings):");
        foreach (var d in devices)
            ctx.Out($"  {d.name,-40} {(d.enabled ? "enabled" : "disabled"),-9} {d.maxActive?.ToString() ?? "1"} at once{(d.maxTotal != null ? $", {d.maxTotal} loaded" : "")}");
        ctx.WriteJson(new { gpus = host.Gpus, devices });
        return Program.ExitOk;
    }

    /// <summary><c>las split &lt;file&gt;</c>: how an import would split a text into chapters and segments (nothing is written).</summary>
    public static async Task<int> SplitAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("split", tokens,
            new OptionSpec("--max-chars"), new OptionSpec("--chapter-regex"), new OptionSpec("--every-line", HasValue: false),
            new OptionSpec("--no-headings", HasValue: false), new OptionSpec("--names"), new OptionSpec("--segments", HasValue: false));
        if (a.Positionals.Count != 1) throw new UsageException("split: give one text file (or - for stdin).");
        var file = a.Positionals[0];
        if (file != "-" && !File.Exists(file)) throw new UsageException($"split: {file} doesn't exist.");
        var host = await ctx.StartHostAsync();
        var text = file == "-" ? Console.In.ReadToEnd() : TextDecoding.ReadFile(file);
        var options = ProjectImport.Options(host, null, a.GetInt("--max-chars", 50, 100_000) ?? 1500);
        if (a.Get("--chapter-regex") is { } rx)
        {
            try { _ = new Regex(rx); }
            catch (ArgumentException ex) { throw new UsageException("split: --chapter-regex: " + ex.Message); }
            options.ChapterRegex = rx;
        }
        options.EveryLineIsParagraph = a.Has("--every-line");
        options.SpeakHeadings = !a.Has("--no-headings");
        if (a.Get("--names") is { } names) options.KnownSpeakerNames = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var r = TextImporter.Parse(text, options);
        ctx.Out($"{r.Chapters.Count} chapter(s), {r.SegmentCount} segment(s), {r.TextLength:N0} characters, {r.SpeakerLabels.Count} speaker label(s){(r.SpeakerLabels.Count > 0 ? ": " + string.Join(", ", r.SpeakerLabels) : "")}");
        for (int c = 0; c < r.Chapters.Count; c++)
        {
            var ch = r.Chapters[c];
            ctx.Out($"{c + 1,4}. {ch.Title}  ({ch.Items.Count} segments, {ch.Items.Where(i => i.Kind != ItemKind.Comment).Sum(i => i.Text?.Length ?? 0):N0} chars)");
            if (!a.Has("--segments")) continue;
            foreach (var i in ch.Items)
            {
                var who = i.Kind switch
                {
                    ItemKind.Pause => $"[pause {i.DurationMs} ms]",
                    ItemKind.Comment => "[comment]",
                    ItemKind.Clip => "[clip]",
                    _ => $"[{i.SpeakerLabel ?? "default"}]",
                };
                var body = (i.Text ?? i.ClipFile ?? "").Replace('\n', ' ');
                if (body.Length > 100) body = body[..99] + "…";
                ctx.Out($"        {who} {body}");
            }
        }
        ctx.WriteJson(new
        {
            chapters = r.Chapters.Select(ch => new
            {
                title = ch.Title,
                segments = ch.Items.Select(i => new { kind = i.Kind, speaker = i.SpeakerLabel, text = i.Text, heading = i.IsHeading, durationMs = i.DurationMs, durationSec = i.DurationSec, clip = i.ClipFile }),
            }),
            speakerLabels = r.SpeakerLabels,
            segmentCount = r.SegmentCount,
            textLength = r.TextLength,
        });
        return Program.ExitOk;
    }

    /// <summary>
    /// <c>las doctor</c>: the setup window's system check (GPU, driver, uv, HartsyInference, ffmpeg, disk space), plus
    /// where the settings and data are. Works before the app is set up. Exit code 3 when something is missing.
    /// </summary>
    public static async Task<int> DoctorAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("doctor", tokens, new OptionSpec("--fix", HasValue: false));
        a.NoPositionals();
        var mode = SettingsStore.DetectMode();
        if (mode != null) SettingsStore.UseMode(mode.Value);
        var settings = mode != null ? SettingsStore.Load() : new AppSettings();
        var dataRoot = SettingsStore.ResolveDataRoot(settings);

        async Task<List<SystemCheck>> Check() =>
            (await SystemChecks.RunAsync(settings.FfmpegPath, dataRoot)).Append(SystemChecks.DiskCheck(dataRoot)).ToList();
        var checks = await Check();

        // --fix: download what can be downloaded (uv; ffmpeg on Windows) into the data folder, then check again.
        if (a.Has("--fix"))
        {
            var downloads = checks.Select(c => c.Fix?.Kind).Where(k => k is CheckFixKind.DownloadFfmpeg or CheckFixKind.DownloadUv).ToList();
            if (downloads.Count == 0) ctx.Info("Nothing to download.");
            var progress = new Progress<string>(ctx.Detail);
            foreach (var kind in downloads)
            {
                ctx.Info(kind == CheckFixKind.DownloadUv ? "Downloading uv…" : "Downloading ffmpeg…");
                var path = kind == CheckFixKind.DownloadUv
                    ? await ToolInstaller.DownloadUvAsync(dataRoot, progress, ctx.Cancel)
                    : await ToolInstaller.DownloadFfmpegAsync(dataRoot, progress, ctx.Cancel);
                ctx.Info($"Installed {path}");
            }
            if (downloads.Count > 0) checks = await Check();
        }
        ctx.Out(mode == null
            ? "Setup: not done yet (start the app once)."
            : $"Setup: {(settings.FirstRunCompleted ? "done" : "not finished")}, {mode} mode; settings {SettingsStore.SettingsFile}");
        ctx.Out($"Data folder: {dataRoot}");
        foreach (var c in checks)
        {
            var mark = c.Status switch
            {
                CheckStatus.Ok => "ok     ",
                CheckStatus.Partial => "ok*    ",
                CheckStatus.Warning => "warn   ",
                CheckStatus.Missing => "MISSING",
                _ => "info   ",
            };
            ctx.Out($"{mark} {c.Title}");
            foreach (var line in c.Lines) ctx.Out($"        {line}");
            if (c.Fix is { Kind: CheckFixKind.RunCommand, Command: { } command }) ctx.Out($"        run: {command}");
            else if (c.Fix != null) ctx.Out("        fix: las doctor --fix");
        }
        ctx.WriteJson(new
        {
            setUp = mode != null && settings.FirstRunCompleted,
            mode,
            settingsFile = mode != null ? SettingsStore.SettingsFile : null,
            dataRoot,
            checks = checks.Select(c => new
            {
                title = c.Title,
                status = c.Status,
                lines = c.Lines.Select(l => l.ToString()),
                fix = c.Fix == null ? null : new { kind = c.Fix.Kind, command = c.Fix.Command },
            }),
        });
        return checks.Any(c => c.IsMissing) || mode == null ? Program.ExitSetup : Program.ExitOk;
    }

    public static int Version(CliContext ctx)
    {
        ctx.Out($"Local Audiobook Studio (las) {AppVersion.Display}");
        ctx.WriteJson(new { version = AppVersion.Number, commit = AppVersion.Commit });
        return Program.ExitOk;
    }
}
