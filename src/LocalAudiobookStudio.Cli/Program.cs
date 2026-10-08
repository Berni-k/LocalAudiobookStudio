using System.Text;
using LocalAudiobookStudio.Cli.Commands;
using LocalAudiobookStudio.Engine.Export;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Cli;

internal static class Program
{
    public const int ExitOk = 0;
    public const int ExitFailures = 1;
    public const int ExitUsage = 2;
    public const int ExitSetup = 3;
    public const int ExitCancelled = 130;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);

        var verbose = false;
        var quiet = false;
        var json = false;
        var help = false;
        var rest = new List<string>();
        foreach (var a in args)
        {
            switch (a)
            {
                case "-v" or "--verbose": verbose = true; break;
                case "-q" or "--quiet": quiet = true; break;
                case "--json": json = true; break;
                case "-h" or "--help" or "-?" or "/?": help = true; break;
                default: rest.Add(a); break;
            }
        }
        if (rest.Count == 0 || rest[0] is "help")
        {
            Console.Out.Write(Help.Text(rest.Count > 1 ? rest[1] : null));
            return rest.Count == 0 && !help ? ExitUsage : ExitOk;
        }
        if (help)
        {
            Console.Out.Write(Help.Text(rest[0]));
            return ExitOk;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // The first Ctrl+C cancels the queue and stops the workers cleanly; a second one ends at once.
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            CliContext.Error("Cancelling… (Ctrl+C again to quit at once)");
            cts.Cancel();
        };

        var ctx = new CliContext(verbose, quiet, json, cts.Token);
        var command = rest[0].ToLowerInvariant();
        var tail = rest.Skip(1).ToList();
        try
        {
            var code = command switch
            {
                "tts" => await TtsCommand.RunAsync(ctx, tail),
                "project" => await ProjectCommand.RunAsync(ctx, tail),
                "workers" => await InfoCommands.WorkersAsync(ctx, tail),
                "models" => await ModelsCommand.RunAsync(ctx, tail),
                "devices" => await InfoCommands.DevicesAsync(ctx, tail),
                "doctor" => await InfoCommands.DoctorAsync(ctx, tail),
                "split" => await InfoCommands.SplitAsync(ctx, tail),
                "version" => InfoCommands.Version(ctx),
                _ => throw new UsageException($"Unknown command '{rest[0]}'."),
            };
            return cts.IsCancellationRequested ? ExitCancelled : code;
        }
        catch (UsageException ex)
        {
            CliContext.Error(ex.Message);
            CliContext.Error($"Run 'las help{(command is "tts" or "project" ? " " + command : "")}' for usage.");
            return ExitUsage;
        }
        catch (SetupException ex)
        {
            CliContext.Error(ex.Message);
            return ExitSetup;
        }
        catch (FfmpegNotFoundException ex)
        {
            CliContext.Error(ex.Message);
            return ExitSetup;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            CliContext.Error("Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            CliContext.Error("Error: " + ex.Message);
            ctx.Host?.Log.LogError(ex, "Command failed");
            if (verbose) CliContext.Error(ex.ToString());
            return ExitFailures;
        }
        finally
        {
            if (ctx.Host is { } host) await host.ShutdownAsync(TimeSpan.FromSeconds(15));
        }
    }
}
