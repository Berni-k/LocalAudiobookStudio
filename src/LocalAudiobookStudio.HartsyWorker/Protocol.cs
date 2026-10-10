using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>A model to ask the app for (an id in workers/models.json) and where HartsyInference looks for its files.</summary>
internal sealed record ModelNeed(string Model, string? LinkTo = null, IReadOnlyDictionary<string, string>? Links = null);

/// <summary>
/// Protocol v2 (see workers/common/protocol.py): newline-delimited JSON, messages on stdout only.
/// Everything else (our logging, HartsyInference's Logs, which already uses stderr) goes to stderr.
/// The internal worker compiles this file too (linked), so it uses nothing but the BCL.
/// </summary>
internal static partial class Protocol
{
    public const int Version = 2;

    private static readonly object SendLock = new();
    private static Stream? _out;

    /// <summary>
    /// Takes stdout for protocol messages and points Console.Out (and, on Windows, the process's
    /// standard output handle, which native libraries read when they load) at stderr.
    /// </summary>
    public static void Initialize()
    {
        var err = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        if (OperatingSystem.IsWindows())
        {
            _out = Console.OpenStandardOutput();
            SetStdHandle(StdOutputHandle, GetStdHandle(StdErrorHandle));
        }
        else
        {
            // Keep a private copy of fd 1 for the protocol, then make fd 1 itself point at stderr.
            var protocolFd = dup(1);
            _out = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)protocolFd, ownsHandle: true), FileAccess.Write);
            dup2(2, 1);
        }
        Console.SetOut(err);
        Console.SetError(err);
    }

    public static void Log(string line) => Console.Error.WriteLine(line);

    public static void Send(JsonObject msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString() + "\n");
        lock (SendLock)
        {
            _out!.Write(bytes);
            _out.Flush();
        }
    }

    public static void Send(string type, string? id, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["type"] = type };
        if (id != null) o["id"] = id;
        foreach (var (k, v) in fields)
            if (v != null) o[k] = v;
        Send(o);
    }

    private static long _lastProgressTicks;

    /// <summary>Progress, throttled to 10 per second like protocol.py (download callbacks fire per chunk).</summary>
    public static void Progress(string? id, double fraction, string? message = null)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastProgressTicks);
        if (fraction < 1 && now - last < 100) return;
        Interlocked.Exchange(ref _lastProgressTicks, now);
        Send("progress", id, ("fraction", Math.Clamp(fraction, 0, 1)), ("message", message));
    }

    public static void Error(string? id, string message, bool fatal, string? trace = null) =>
        Send("error", id, ("message", message), ("fatal", fatal), ("trace", trace));

    /// <summary>A generate request with parts (a fused generation), which these workers don't do: the app generates them one by one.</summary>
    public static bool RefuseParts(string? id, JsonObject msg)
    {
        if (msg["parts"] is not JsonArray { Count: > 0 }) return false;
        Send("error", id, ("message", "this worker doesn't render several parts together"), ("fatal", false), ("code", "unfusable"));
        return true;
    }

    // ------------------------------------------------------------------ requests to the app

    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Replies = new();
    private static int _nextRef;

    /// <summary>
    /// Asks the app for a model (needModel) while handling the request <paramref name="requestId"/> and waits for it:
    /// the app downloads it the first time (showing the progress itself), links its files where asked, and replies.
    /// </summary>
    public static async Task<string> NeedModelAsync(string? requestId, ModelNeed need, CancellationToken ct)
    {
        Log($"asking the app for model {need.Model}");
        var fields = new List<(string, JsonNode?)> { ("model", need.Model), ("linkTo", need.LinkTo) };
        if (need.Links is { Count: > 0 } links)
        {
            var o = new JsonObject();
            foreach (var (file, target) in links) o[file] = target;
            fields.Add(("links", o));
        }
        return ReplyPath(await AskAsync("needModel", requestId, fields, ct), "modelReady", need.Model);
    }

    /// <summary>An extra file of a model that models.json doesn't list (a preset voice), fetched by the app.</summary>
    public static async Task<string> NeedFileAsync(string? requestId, string model, string file, CancellationToken ct) =>
        ReplyPath(await AskAsync("needFile", requestId, [("model", model), ("file", file)], ct), "fileReady", $"{model}/{file}");

    /// <summary>Hands a reply to the request waiting for it; false for any other message.</summary>
    public static bool TryDeliverReply(JsonObject msg)
    {
        if ((string?)msg["type"] is not ("modelReady" or "modelFailed" or "fileReady" or "fileFailed")) return false;
        if ((string?)msg["ref"] is { } r && Replies.TryRemove(r, out var tcs)) tcs.TrySetResult(msg);
        return true;
    }

    /// <summary>stdin closed: nobody will answer the open requests.</summary>
    public static void FailReplies()
    {
        foreach (var r in Replies.Keys)
            if (Replies.TryRemove(r, out var tcs)) tcs.TrySetException(new IOException("the app went away"));
    }

    private static async Task<JsonObject> AskAsync(string type, string? requestId, IEnumerable<(string, JsonNode?)> fields, CancellationToken ct)
    {
        var r = "h" + Interlocked.Increment(ref _nextRef);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Replies[r] = tcs;
        Send(type, requestId, [("ref", r), .. fields]);
        try
        {
            // A cancel also reaches the app, which answers "cancelled"; this stops waiting at once either way.
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            Replies.TryRemove(r, out _);
        }
    }

    private static string ReplyPath(JsonObject reply, string okType, string what)
    {
        if ((string?)reply["type"] == okType && (string?)reply["path"] is { Length: > 0 } path) return path;
        if ((bool?)reply["cancelled"] == true) throw new OperationCanceledException();
        throw new InvalidOperationException((string?)reply["message"] ?? $"{what} is not available");
    }

    /// <summary>
    /// Reads stdin lines as JSON objects. On Windows it never blocks inside ReadFile: a thread blocked in
    /// ReadFile on a synchronous pipe also blocks GetFileType on that handle, which C runtimes call while
    /// a DLL loads (CUDA, cuBLAS), so loading would hang until the next message. Same fix as protocol.py.
    /// </summary>
    public static IEnumerable<JsonObject> ReadMessages()
    {
        foreach (var raw in OperatingSystem.IsWindows() ? WindowsLines() : PosixLines())
        {
            var line = raw.TrimStart('﻿').Trim();
            if (line.Length == 0) continue;
            JsonObject? msg = null;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { }
            if (msg == null) { Log($"ignoring unparseable input: {line[..Math.Min(200, line.Length)]}"); continue; }
            yield return msg;
        }
    }

    private static IEnumerable<string> PosixLines()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static IEnumerable<string> WindowsLines()
    {
        var handle = GetStdHandle(StdInputHandle);
        using var stdin = Console.OpenStandardInput();
        var pending = new List<byte>();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
                break; // pipe closed: the app went away
            if (available == 0)
            {
                Thread.Sleep(20);
                continue;
            }
            var n = stdin.Read(buffer, 0, (int)Math.Min(available, (uint)buffer.Length));
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    yield return Encoding.UTF8.GetString(pending.ToArray());
                    pending.Clear();
                }
                else pending.Add(buffer[i]);
            }
        }
        if (pending.Count > 0) yield return Encoding.UTF8.GetString(pending.ToArray());
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldFd, int newFd);

    private const int StdInputHandle = -10, StdOutputHandle = -11, StdErrorHandle = -12;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekNamedPipe(IntPtr hNamedPipe, IntPtr lpBuffer, uint nBufferSize,
        IntPtr lpBytesRead, out uint lpTotalBytesAvail, IntPtr lpBytesLeftThisMessage);
}
