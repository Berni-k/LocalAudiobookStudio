using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.Workers.Models;

/// <summary>
/// A folder that is another folder: a junction on Windows (no admin rights or Developer Mode needed, unlike a
/// symbolic link), a symbolic link elsewhere. HartsyInference's folder for a model becomes such a link to the
/// model's store folder, so whatever it writes there (converted weights, voices) lands in the store.
/// </summary>
internal static class DirectoryLink
{
    /// <summary>Makes <paramref name="link"/> (which must not exist) point at the folder <paramref name="target"/>; false when the file system can't.</summary>
    public static bool TryCreate(string link, string target)
    {
        target = Path.GetFullPath(target);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            Directory.CreateDirectory(link);
            if (SetJunction(link, target)) return true;
            Directory.Delete(link);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static bool IsLink(string folder) =>
        Directory.Exists(folder) && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>The folder a link points at (full path), or null.</summary>
    public static string? TargetOf(string folder)
    {
        var target = new DirectoryInfo(folder).LinkTarget;
        if (target == null) return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        return Path.GetFullPath(target, Path.GetDirectoryName(Path.GetFullPath(folder))!);
    }

    /// <summary>Removes the link itself; the folder it points at is untouched.</summary>
    public static void Remove(string folder) => Directory.Delete(folder);

    // ------------------------------------------------------------------ Windows junctions

    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>Writes a mount-point reparse point (what <c>mklink /J</c> makes) onto the empty folder <paramref name="link"/>.</summary>
    private static bool SetJunction(string link, string target)
    {
        using var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        // REPARSE_DATA_BUFFER with a MountPointReparseBuffer: four offsets/lengths, then both names, each null-terminated.
        var pathBuffer = substitute.Length + 2 + print.Length + 2;
        var dataLength = 8 + pathBuffer;
        var buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                          // substitute name offset
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);   // print name offset
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        return DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
