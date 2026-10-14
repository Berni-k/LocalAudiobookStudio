using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Core.Settings;

namespace LocalAudiobookStudio.Cli.Proxy;

/// <summary>What a frame on the proxy's pipe carries. Below 20: client to server; from 20: server to client.</summary>
public enum FrameType : byte
{
    /// <summary>A command to run: <see cref="ProxyRequest"/>.</summary>
    Request = 1,
    /// <summary>All of the client's stdin (UTF-8), sent after <see cref="NeedStdin"/>.</summary>
    Stdin = 2,
    /// <summary>Ctrl+C: cancel the request's jobs.</summary>
    Cancel = 3,
    /// <summary>las proxy status: answered with <see cref="StatusReply"/>.</summary>
    Status = 4,
    /// <summary>las proxy stop: cancel everything and exit.</summary>
    Stop = 5,

    /// <summary>The first frame on every connection: <see cref="ProxyHello"/>.</summary>
    Hello = 20,
    /// <summary>Text for the client's stdout (UTF-8).</summary>
    Out = 21,
    /// <summary>Text for the client's stderr (UTF-8).</summary>
    Error = 22,
    /// <summary>Bytes for the client's stdout (tts -o -).</summary>
    Binary = 23,
    /// <summary>The request's place in the queue: <see cref="QueueInfo"/>.</summary>
    Queue = 24,
    /// <summary>The command reads stdin: the client sends it as <see cref="Stdin"/>.</summary>
    NeedStdin = 25,
    /// <summary>The command finished: its exit code (int32, little endian). The last frame of a request.</summary>
    Exit = 26,
    /// <summary>The answer to <see cref="Status"/>: <see cref="ProxyStatus"/>.</summary>
    StatusReply = 27,
}

public sealed record ProxyRequest(string[] Args, string Cwd, bool StdinRedirected);

public sealed record ProxyHello(int Pid, string Version);

/// <summary>Started: one of the request's jobs runs (or all are done). Otherwise Ahead jobs run or wait before them.</summary>
public sealed record QueueInfo(bool Started, int Ahead);

public sealed record ProxyStatus(int Pid, string Version, DateTime StartedUtc, int Clients, int Queued, int Running,
    double IdleMinutes, List<ProxyWorker> Workers);

public sealed record ProxyWorker(string Worker, string Runtime, string Device, string State, string? Job);

/// <summary>
/// The proxy's pipe: frames of a type byte, a 4-byte little-endian length and the payload. Writes are whole frames
/// under a lock, so command output from several threads never interleaves within one.
/// </summary>
public sealed class FrameStream(Stream stream)
{
    private const int MaxPayload = 512 * 1024 * 1024; // a whole book on stdin fits
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _writeLock = new();

    public Stream Stream => stream;

    public void Write(FrameType type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], payload.Length);
        lock (_writeLock)
        {
            stream.Write(header);
            stream.Write(payload);
            stream.Flush();
        }
    }

    public void Write(FrameType type) => Write(type, ReadOnlySpan<byte>.Empty);

    public void WriteText(FrameType type, string text) => Write(type, Encoding.UTF8.GetBytes(text));

    public void WriteJson<T>(FrameType type, T value) => Write(type, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    public void WriteExit(int code)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, code);
        Write(FrameType.Exit, b);
    }

    /// <summary>The next frame, or null when the other side closed the pipe.</summary>
    public async Task<(FrameType Type, byte[] Payload)?> ReadAsync(CancellationToken ct = default)
    {
        var header = new byte[5];
        if (!await FillAsync(header, ct)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayload) throw new InvalidDataException($"A frame of {length} bytes on the las proxy's pipe.");
        var payload = new byte[length];
        if (length > 0 && !await FillAsync(payload, ct)) return null;
        return ((FrameType)header[0], payload);
    }

    private async Task<bool> FillAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static string Text(byte[] payload) => Encoding.UTF8.GetString(payload);

    public static T Json<T>(byte[] payload) => JsonSerializer.Deserialize<T>(payload, JsonOptions)!;

    public static int ExitCode(byte[] payload) => BinaryPrimitives.ReadInt32LittleEndian(payload);
}

public static class ProxyPipe
{
    /// <summary>
    /// One proxy per user and per copy of las: a build elsewhere has its own workers/ and tools/, and a portable copy
    /// its own settings, so they don't share one.
    /// </summary>
    public static string Name
    {
        get
        {
            var key = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant() + "|" + SettingsStore.DetectMode();
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
            return $"las-proxy-{Environment.UserName.ToLowerInvariant()}-{hash}";
        }
    }

    /// <summary>Only this user's processes may connect (and the client checks that the server is this user's too).</summary>
    public const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
}
