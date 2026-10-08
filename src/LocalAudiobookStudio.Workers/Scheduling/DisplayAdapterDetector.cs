using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Workers.Scheduling;

/// <summary>A graphics adapter of any vendor. <paramref name="MemoryMb"/> is 0 when unknown.</summary>
public sealed record DisplayAdapter(string Name, string Vendor, int MemoryMb)
{
    public bool IsNvidia => Vendor == DisplayAdapterDetector.Nvidia;
}

/// <summary>
/// Lists every graphics adapter, whatever the vendor: DXGI on Windows, /sys/class/drm on Linux. Workers only
/// run on NVIDIA GPUs (those come from <see cref="GpuDetector"/>, nvidia-smi); this is so other GPUs can be
/// named in the setup check instead of looking like "no GPU".
/// Only GPU vendors count (NVIDIA, AMD, Intel): a server's IPMI/BMC display chip (ASPEED, Matrox), a virtual
/// machine's display (VMware, VirtualBox, QXL) and Microsoft's software adapters only show a screen.
/// </summary>
public static partial class DisplayAdapterDetector
{
    public const string Nvidia = "NVIDIA", Amd = "AMD", Intel = "Intel";

    public static IReadOnlyList<DisplayAdapter> Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return DetectWindows();
            if (OperatingSystem.IsLinux()) return DetectLinux();
        }
        catch
        {
            // Detection is informational only.
        }
        return [];
    }

    /// <summary>The GPU vendor, or null for display-only adapters (see the class summary).</summary>
    private static string? VendorName(uint pciVendor) => pciVendor switch
    {
        0x10DE => Nvidia,
        0x1002 or 0x1022 => Amd,
        0x8086 => Intel,
        _ => null,
    };

    // ------------------------------------------------------------------ Windows: DXGI

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

    // vtable slots: IUnknown (3) + IDXGIObject (4) + IDXGIFactory (5) → IDXGIFactory1::EnumAdapters1 is 12;
    // IUnknown (3) + IDXGIObject (4) + IDXGIAdapter (3) → IDXGIAdapter1::GetDesc1 is 10.
    private const int EnumAdapters1Slot = 12, GetDesc1Slot = 10;
    private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

    private static T Method<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    private static List<DisplayAdapter> DetectWindows()
    {
        var result = new List<DisplayAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == IntPtr.Zero) return result;
        try
        {
            var enumAdapters = Method<EnumAdapters1Fn>(factory, EnumAdapters1Slot);
            for (uint i = 0; enumAdapters(factory, i, out var adapter) == 0; i++)
            {
                try
                {
                    if (Method<GetDesc1Fn>(adapter, GetDesc1Slot)(adapter, out var d) != 0 || (d.Flags & SoftwareAdapter) != 0) continue;
                    if (VendorName(d.VendorId) is { } vendor)
                        result.Add(new DisplayAdapter(d.Description.Trim(), vendor, (int)(d.DedicatedVideoMemory / (1024 * 1024))));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        return result;
    }

    // ------------------------------------------------------------------ Linux: /sys/class/drm

    [GeneratedRegex(@"^card\d+$")]
    private static partial Regex CardRegex();

    private static List<DisplayAdapter> DetectLinux()
    {
        var result = new List<DisplayAdapter>();
        const string drm = "/sys/class/drm";
        if (!Directory.Exists(drm)) return result;
        foreach (var card in Directory.EnumerateDirectories(drm).Where(c => CardRegex().IsMatch(Path.GetFileName(c))).Order())
        {
            var device = Path.Combine(card, "device");
            if (!TryHex(Path.Combine(device, "vendor"), out var vendor) || VendorName(vendor) is not { } vendorName) continue;
            TryHex(Path.Combine(device, "device"), out var deviceId);
            var name = PciName(device) ?? $"{vendorName} GPU ({vendor:x4}:{deviceId:x4})";
            // amdgpu reports its VRAM; other drivers don't (NVIDIA's comes from nvidia-smi anyway).
            var memory = File.Exists(Path.Combine(device, "mem_info_vram_total")) &&
                         long.TryParse(File.ReadAllText(Path.Combine(device, "mem_info_vram_total")).Trim(), out var bytes)
                ? (int)(bytes / (1024 * 1024))
                : 0;
            result.Add(new DisplayAdapter(name, vendorName, memory));
        }
        return result;
    }

    private static bool TryHex(string file, out uint value)
    {
        value = 0;
        try
        {
            var text = File.ReadAllText(file).Trim();
            return text.StartsWith("0x") && uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The marketing name from lspci ("Advanced Micro Devices, Inc. [AMD/ATI] Navi 31 [Radeon RX 7900 XT]"), if it is installed.</summary>
    private static string? PciName(string deviceDir)
    {
        try
        {
            var slot = Path.GetFileName(new DirectoryInfo(deviceDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? "");
            if (string.IsNullOrEmpty(slot)) return null;
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("lspci", ["-mm", "-s", slot])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) return null;
            // Slot "Class" "Vendor" "Device" …
            var fields = Regex.Matches(output.Result, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            return fields.Count >= 3 ? $"{fields[1]} {fields[2]}".Trim() : null;
        }
        catch
        {
            return null; // no lspci
        }
    }
}
