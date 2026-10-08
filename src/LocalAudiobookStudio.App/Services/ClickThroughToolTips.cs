using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace LocalAudiobookStudio.App.Services;

/// <summary>
/// Makes tooltips click-through on Windows: a click on a tooltip goes to whatever is behind it
/// instead of just closing the tooltip.
/// </summary>
/// <remarks>
/// Each tooltip is its own small popup window. When it opens, its window procedure is wrapped so
/// that the hit test (WM_NCHITTEST) answers HTTRANSPARENT. Windows then sends the mouse to the
/// window underneath on the same thread, which is our own window, exactly like native Win32
/// tooltips. Does nothing on other platforms.
/// </remarks>
public static class ClickThroughToolTips
{
    private const int GwlpWndProc = -4;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcDestroy = 0x0082;
    private static readonly IntPtr HtTransparent = new(-1);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Wrapped windows: their original procedure, and our delegate (kept alive here so the GC can't collect it).</summary>
    private static readonly Dictionary<IntPtr, (IntPtr original, WndProc hook)> Hooked = new();

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value);

    /// <summary>Call once at startup: every tooltip is made click-through when it opens.</summary>
    public static void Register()
    {
        if (!OperatingSystem.IsWindows()) return;
        Control.LoadedEvent.AddClassHandler<ToolTip>((tip, _) => Apply(tip), RoutingStrategies.Direct);
    }

    private static void Apply(ToolTip tip)
    {
        // Only real popup windows; never touch a main window or dialog.
        if (TopLevel.GetTopLevel(tip) is not PopupRoot popup) return;
        var hwnd = popup.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || Hooked.ContainsKey(hwnd)) return;
        try
        {
            WndProc hook = (h, msg, w, l) => Hook(h, msg, w, l);
            var original = SetWindowLong(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(hook));
            if (original == IntPtr.Zero) return;
            Hooked[hwnd] = (original, hook);
        }
        catch
        {
            // Interop not available: tooltips keep the default behavior.
        }
    }

    private static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Hooked.TryGetValue(hwnd, out var entry)) return IntPtr.Zero;
        if (msg == WmNcHitTest) return HtTransparent;
        if (msg == WmNcDestroy)
        {
            // The popup window is going away: put the original procedure back and forget it.
            SetWindowLong(hwnd, GwlpWndProc, entry.original);
            Hooked.Remove(hwnd);
        }
        return CallWindowProc(entry.original, hwnd, msg, wParam, lParam);
    }
}
