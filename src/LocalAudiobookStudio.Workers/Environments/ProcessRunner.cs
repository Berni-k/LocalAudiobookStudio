using System.Diagnostics;
using System.Text;

namespace LocalAudiobookStudio.Workers.Environments;

/// <summary>Runs a command to completion, streaming stdout and stderr lines, with cancellation.</summary>
public static class ProcessRunner
{
    public static async Task<int> RunAsync(
        string fileName,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onLine,
        CancellationToken ct,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        onLine?.Invoke("> " + Quote(fileName) + " " + string.Join(" ", psi.ArgumentList.Select(Quote)));

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        if (!p.Start()) throw new InvalidOperationException($"Could not start {fileName}.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        // Make sure the async readers have flushed their last lines.
        p.WaitForExit();
        return p.ExitCode;
    }

    public static string Quote(string s) => s.Contains(' ') || s.Length == 0 ? "\"" + s + "\"" : s;
}
