using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using LocalAudiobookStudio.Core.Settings;

namespace LocalAudiobookStudio.Cli.Proxy;

/// <summary>
/// las --proxy: sends the command to the las proxy (started when none runs) and plays its output on this console,
/// so the command runs on workers that stay loaded between runs. Exits with the command's exit code.
/// </summary>
public static class ProxyClient
{
    /// <summary>LAS_PROXY=1: every command that can goes through the proxy (for a whole script).</summary>
    public static bool EnabledByEnvironment =>
        Environment.GetEnvironmentVariable("LAS_PROXY") is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <param name="start">Start a proxy when none runs; otherwise report that none runs.</param>
    /// <param name="cancel">Ctrl+C from the caller, which handles it; null to handle Ctrl+C here.</param>
    public static async Task<int> RunAsync(GlobalOptions g, string[] args, bool start = true, CancellationToken? cancel = null)
    {
        // The queue and start messages are for people; scripts reading --json, or asking for quiet, don't get them.
        var chatty = !g.Quiet && !g.Json;
        if (SettingsStore.DetectMode() == null)
        {
            Console.Error.WriteLine("Local Audiobook Studio hasn't been set up on this machine. Start the app once and finish its setup.");
            return Program.ExitSetup;
        }

        NamedPipeClientStream? pipe;
        try
        {
            pipe = await ConnectAsync(TimeSpan.FromMilliseconds(300))
                   ?? (start ? await StartAndConnectAsync(chatty) : null);
        }
        catch (ProxyException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Program.ExitSetup;
        }
        if (pipe == null)
        {
            if (!g.Quiet) Console.Error.WriteLine("No las proxy is running.");
            return Program.ExitOk;
        }

        await using var connection = pipe;
        var frames = new FrameStream(pipe);
        var status = new StatusLine(chatty);
        var cancelled = false;
        void SendCancel()
        {
            try { frames.Write(FrameType.Cancel); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // The first Ctrl+C cancels this command's jobs in the proxy; a second one ends at once (the proxy then
            // cancels what is left, as the pipe closes).
            if (cancelled) return;
            cancelled = e.Cancel = true;
            status.Clear();
            Console.Error.WriteLine("Cancelling… (Ctrl+C again to quit at once)");
            SendCancel();
        };
        // A caller with its own Ctrl+C handling passes its token instead.
        if (cancel == null) Console.CancelKeyPress += onCancel;
        await using var cancelReg = cancel?.Register(() => { cancelled = true; SendCancel(); }) ?? default;
        try
        {
            if (await frames.ReadAsync() is not { Type: FrameType.Hello })
                return Gone();
            frames.WriteJson(FrameType.Request, new ProxyRequest(args, Directory.GetCurrentDirectory(), Console.IsInputRedirected));

            Stream? stdout = null;
            while (await frames.ReadAsync() is { } f)
            {
                switch (f.Type)
                {
                    case FrameType.Out:
                        status.Clear();
                        Console.Out.Write(FrameStream.Text(f.Payload));
                        break;
                    case FrameType.Error:
                        status.Clear();
                        Console.Error.Write(FrameStream.Text(f.Payload));
                        break;
                    case FrameType.Binary:
                        stdout ??= Console.OpenStandardOutput();
                        stdout.Write(f.Payload);
                        break;
                    case FrameType.Queue:
                        status.Show(FrameStream.Json<QueueInfo>(f.Payload));
                        break;
                    case FrameType.NeedStdin:
                        // Read on another thread: the pipe keeps being read meanwhile (Ctrl+C, output).
                        _ = Task.Run(() =>
                        {
                            var text = Console.In.ReadToEnd();
                            try { frames.WriteText(FrameType.Stdin, text); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                        });
                        break;
                    case FrameType.Exit:
                        status.Clear();
                        stdout?.Flush();
                        Console.Out.Flush();
                        return FrameStream.ExitCode(f.Payload);
                }
            }
            return Gone();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Gone();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        int Gone()
        {
            status.Clear();
            Console.Error.WriteLine(cancelled ? "Cancelled." : "The las proxy stopped before the command finished.");
            return cancelled ? Program.ExitCancelled : Program.ExitFailures;
        }
    }

    /// <summary>A connection to the running proxy, or null when none answers in time.</summary>
    internal static async Task<NamedPipeClientStream?> ConnectAsync(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", ProxyPipe.Name, PipeDirection.InOut, ProxyPipe.Options);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            throw new ProxyException("The las proxy's pipe belongs to another user.");
        }
    }

    internal static async Task<NamedPipeClientStream> StartAndConnectAsync(bool chatty, int? idleMinutes = null)
    {
        if (chatty) Console.Error.WriteLine("Starting the las proxy…");
        var process = StartServer(idleMinutes);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (await ConnectAsync(TimeSpan.FromMilliseconds(500)) is { } pipe) return pipe;
            // It exits at once when another proxy got the pipe first; that one answers on the next try.
            if (process is { HasExited: true } && sw.Elapsed > TimeSpan.FromSeconds(5))
                throw new ProxyException("The las proxy exited while starting. Run the command without --proxy to see why.");
        }
        throw new ProxyException("The las proxy didn't answer. Run the command without --proxy, or see the newest logs/proxy-*.log in the data folder.");
    }

    /// <summary>
    /// Starts <c>las proxy serve</c> on its own: no console window, none of this process's handles (a script reading
    /// this process's output would otherwise wait for the proxy to exit too), outside this process's job (terminals and
    /// CI end a job's processes with it) and in las's own folder (not holding the current folder open).
    /// </summary>
    private static Process? StartServer(int? idleMinutes)
    {
        var exe = Environment.ProcessPath ?? throw new ProxyException("Can't tell where las is to start the las proxy.");
        var args = new List<string>();
        // Run as 'dotnet las.dll' (not the published las.exe): start the same way.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args.Add(Path.Combine(AppContext.BaseDirectory, "las.dll"));
        args.AddRange(["proxy", "serve"]);
        if (idleMinutes is { } idle) args.AddRange(["--idle", idle.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var dir = AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
        {
            var pid = WindowsStart.Detached(exe, args, dir);
            try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new ProxyException("Couldn't start the las proxy.");
        // Its standard streams are pipes of its own (not this terminal's); it writes nothing to them.
        p.StandardInput.Close();
        p.StandardOutput.Close();
        p.StandardError.Close();
        return p;
    }

    /// <summary>Shows the request's place in the queue on stderr, on one line it rewrites (or once, when stderr isn't a console).</summary>
    private sealed class StatusLine(bool enabled)
    {
        private readonly object _lock = new();
        private int _length;
        private bool _printed;

        public void Show(QueueInfo q)
        {
            if (!enabled) return;
            lock (_lock)
            {
                var text = q.Started ? null : q.Ahead == 0 ? "Waiting in queue: next…" : $"Waiting in queue: {q.Ahead} job{(q.Ahead == 1 ? "" : "s")} ahead…";
                if (Console.IsErrorRedirected)
                {
                    if (text != null && !_printed) Console.Error.WriteLine(text);
                    _printed |= text != null;
                    return;
                }
                ClearLocked();
                if (text == null) return;
                Console.Error.Write(text);
                _length = text.Length;
            }
        }

        public void Clear()
        {
            lock (_lock) ClearLocked();
        }

        private void ClearLocked()
        {
            if (_length == 0) return;
            Console.Error.Write("\r" + new string(' ', _length) + "\r");
            _length = 0;
        }
    }

    private static class WindowsStart
    {
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateNewProcessGroup = 0x00000200;
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int ErrorAccessDenied = 5;

        public static int Detached(string exe, IReadOnlyList<string> args, string dir)
        {
            var commandLine = string.Join(' ', new[] { exe }.Concat(args).Select(Quote));
            var flags = CreateNoWindow | CreateNewProcessGroup | CreateUnicodeEnvironment;
            // Breaking away from the job fails when the job doesn't allow it; then it stays in the job.
            if (Create(commandLine, dir, flags | CreateBreakawayFromJob, out var pid)) return pid;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAccessDenied && Create(commandLine, dir, flags, out pid)) return pid;
            throw new ProxyException($"Couldn't start the las proxy: {new Win32Exception(error == ErrorAccessDenied ? Marshal.GetLastWin32Error() : error).Message}");
        }

        private static bool Create(string commandLine, string dir, uint flags, out int pid)
        {
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var cl = new System.Text.StringBuilder(commandLine);
            if (!CreateProcessW(null, cl, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, dir, ref si, out var pi))
            {
                pid = 0;
                return false;
            }
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            pid = pi.dwProcessId;
            return true;
        }

        /// <summary>One argument as CommandLineToArgvW reads it back.</summary>
        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
            var sb = new System.Text.StringBuilder("\"");
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            return sb.Append('\\', slashes * 2).Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string? lpApplicationName, System.Text.StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>The las proxy can't be reached or started.</summary>
public sealed class ProxyException(string message) : Exception(message);
