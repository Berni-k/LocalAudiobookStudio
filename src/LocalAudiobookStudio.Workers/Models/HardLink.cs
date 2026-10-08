using System.Runtime.InteropServices;

namespace LocalAudiobookStudio.Workers.Models;

/// <summary>
/// Hard links: one file on disk under two names, so a model shared with HartsyInference's own folders takes
/// its space once. .NET has no API for them, so this calls the OS.
/// </summary>
internal static class HardLink
{
    /// <summary>Creates <paramref name="link"/> as a hard link to <paramref name="existing"/>; false when the OS refuses (other drive, FAT).</summary>
    public static bool TryCreate(string existing, string link)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CreateHardLinkW(link, existing, IntPtr.Zero) : LinkUnix(existing, link) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);
}
