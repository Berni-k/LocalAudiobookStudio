using System.Text;
using LocalAudiobookStudio.Cli.Proxy;

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

        GlobalOptions g;
        try { g = GlobalOptions.Parse(args); }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }
        if (g.Command is null or "help")
        {
            Console.Out.Write(Help.Text(g.Rest.Count > 1 ? g.Rest[1] : null));
            return g.Rest.Count == 0 && !g.Help ? ExitUsage : ExitOk;
        }
        if (g.Help)
        {
            Console.Out.Write(Help.Text(g.Rest[0]));
            return ExitOk;
        }

        // --proxy (or LAS_PROXY=1 for a whole script): the command runs in the las proxy, which keeps its workers loaded.
        if (g.Command != "proxy" && (g.Proxy || ProxyClient.EnabledByEnvironment))
        {
            if (ProxyServer.Commands.Contains(g.Command))
                return await ProxyClient.RunAsync(g, args.Where(a => a != "--proxy").ToArray());
            if (g.Proxy)
            {
                Console.Error.WriteLine($"'las {g.Command}' doesn't run in the las proxy; only {string.Join(", ", ProxyServer.Commands)} do. Leave out --proxy.");
                return ExitUsage;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // The first Ctrl+C cancels the queue and stops the workers cleanly; a second one ends at once.
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            Console.Error.WriteLine("Cancelling… (Ctrl+C again to quit at once)");
            cts.Cancel();
        };

        var ctx = new CliContext(g.Verbose, g.Quiet, g.Json, cts.Token) { OnlyGpus = g.Gpus };
        try
        {
            return await CommandRunner.RunAsync(ctx, g.Rest);
        }
        finally
        {
            if (ctx.Host is { } host) await host.ShutdownAsync(TimeSpan.FromSeconds(15));
        }
    }
}
