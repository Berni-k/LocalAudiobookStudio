using LocalAudiobookStudio.Workers.Environments;

namespace LocalAudiobookStudio.Workers.Scheduling;

/// <param name="Driver">The NVIDIA driver version ("610.88"), when nvidia-smi reports it.</param>
/// <param name="ComputeCapability">"8.6" for an RTX 3090; null from drivers that don't report it.</param>
public sealed record GpuInfo(int Index, string Name, int MemoryMb, string? Driver = null, string? ComputeCapability = null);

public static class GpuDetector
{
    /// <summary>Runs nvidia-smi. Returns an empty list if it's missing or fails (then only workers set to the CPU can run).</summary>
    public static async Task<IReadOnlyList<GpuInfo>> DetectAsync(CancellationToken ct = default) =>
        // Older drivers don't know compute_cap and fail the whole query: then ask for the basic fields only.
        await QueryAsync("index,name,memory.total,driver_version,compute_cap", ct).ConfigureAwait(false)
        ?? await QueryAsync("index,name,memory.total", ct).ConfigureAwait(false)
        ?? [];

    private static async Task<IReadOnlyList<GpuInfo>?> QueryAsync(string fields, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var code = await ProcessRunner.RunAsync("nvidia-smi",
                ["--query-gpu=" + fields, "--format=csv,noheader,nounits"],
                null, l => { if (!l.StartsWith("> ")) lines.Add(l); }, cts.Token).ConfigureAwait(false);
            if (code != 0) return null;
        }
        catch
        {
            return null;
        }
        return Parse(lines);
    }

    internal static List<GpuInfo> Parse(IEnumerable<string> lines)
    {
        var result = new List<GpuInfo>();
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var idx)) continue;
            var mem = 0;
            if (parts.Length >= 3) int.TryParse(new string(parts[2].TakeWhile(char.IsDigit).ToArray()), out mem);
            string? Field(int i) => parts.Length > i && parts[i].Length > 0 && !parts[i].StartsWith('[') ? parts[i] : null;
            result.Add(new GpuInfo(idx, parts[1], mem, Field(3), Field(4)));
        }
        return result;
    }
}
