using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Services;

/// <summary>
/// Asks Windows to draw the native title bar in dark mode (Windows 10 1809+ and Windows 11),
/// so the OS frame matches the dark theme. Does nothing on other platforms.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call once at startup: every window gets a dark title bar when it loads.</summary>
    public static void Register()
    {
        if (!OperatingSystem.IsWindows()) return;
        Control.LoadedEvent.AddClassHandler<Window>((w, _) => Apply(w), RoutingStrategies.Direct);
    }

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        var on = 1;
        try
        {
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
        }
        catch
        {
            // Older Windows: keep the default frame.
        }
    }
}
