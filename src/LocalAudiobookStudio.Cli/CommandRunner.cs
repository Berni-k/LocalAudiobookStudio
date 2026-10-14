using LocalAudiobookStudio.Cli.Commands;
using LocalAudiobookStudio.Engine.Export;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Cli;

/// <summary>The global options before (or among) a command's own: --json, -v, -q, --gpu, --proxy, --help.</summary>
public sealed record GlobalOptions(bool Verbose, bool Quiet, bool Json, bool Help, bool Proxy, int? Gpu, List<string> Rest)
{
    public string? Command => Rest.Count > 0 ? Rest[0].ToLowerInvariant() : null;

    public static GlobalOptions Parse(IReadOnlyList<string> args)
    {
        bool verbose = false, quiet = false, json = false, help = false, proxy = false;
        int? gpu = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-v" or "--verbose": verbose = true; break;
                case "-q" or "--quiet": quiet = true; break;
                case "--json": json = true; break;
                case "--proxy": proxy = true; break;
                case "-h" or "--help" or "-?" or "/?": help = true; break;
                case "--gpu":
                    if (i + 1 >= args.Count || !int.TryParse(args[++i], out var g) || g < 0)
                        throw new UsageException("--gpu takes a GPU number (see 'las devices').");
                    gpu = g;
                    break;
                default: rest.Add(a); break;
            }
        }
        return new GlobalOptions(verbose, quiet, json, help, proxy, gpu, rest);
    }
}

/// <summary>Runs one command and turns what went wrong into its message and exit code; for las itself and the las proxy.</summary>
public static class CommandRunner
{
    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> rest)
    {
        var command = rest[0].ToLowerInvariant();
        var tail = rest.Skip(1).ToList();
        try
        {
            var code = command switch
            {
                "tts" => await TtsCommand.RunAsync(ctx, tail),
                "transcribe" => await TranscribeCommand.RunAsync(ctx, tail),
                "compare" => await CompareCommand.RunAsync(ctx, tail),
                "project" => await ProjectCommand.RunAsync(ctx, tail),
                "workers" => await InfoCommands.WorkersAsync(ctx, tail),
                "models" => await ModelsCommand.RunAsync(ctx, tail),
                "devices" => await InfoCommands.DevicesAsync(ctx, tail),
                "doctor" => await InfoCommands.DoctorAsync(ctx, tail),
                "split" => await InfoCommands.SplitAsync(ctx, tail),
                "proxy" => await Proxy.ProxyCommand.RunAsync(ctx, tail),
                // Sent by 'las proxy unload'; only the proxy runs it, on its shared host.
                Proxy.ProxyCommand.UnloadCommand when ctx.Shared => await Proxy.ProxyCommand.UnloadAsync(ctx, tail),
                "version" => InfoCommands.Version(ctx),
                _ => throw new UsageException($"Unknown command '{rest[0]}'."),
            };
            return ctx.Cancel.IsCancellationRequested ? Program.ExitCancelled : code;
        }
        catch (UsageException ex)
        {
            ctx.Error(ex.Message);
            ctx.Error($"Run 'las help{(command is "tts" or "transcribe" or "compare" or "project" or "proxy" ? " " + command : "")}' for usage.");
            return Program.ExitUsage;
        }
        catch (SetupException ex)
        {
            ctx.Error(ex.Message);
            return Program.ExitSetup;
        }
        catch (FfmpegNotFoundException ex)
        {
            ctx.Error(ex.Message);
            return Program.ExitSetup;
        }
        catch (OperationCanceledException) when (ctx.Cancel.IsCancellationRequested)
        {
            ctx.Error("Cancelled.");
            return Program.ExitCancelled;
        }
        catch (Exception ex)
        {
            ctx.Error("Error: " + ex.Message);
            ctx.Host?.Log.LogError(ex, "Command failed");
            if (ctx.Verbose) ctx.Error(ex.ToString());
            return Program.ExitFailures;
        }
    }
}
