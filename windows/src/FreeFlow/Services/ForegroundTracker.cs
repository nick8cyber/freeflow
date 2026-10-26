using System.Runtime.InteropServices;
using System.Text;

namespace FreeFlow.Services;

/// <summary>
/// Remembers the last foreground window that is NOT the taskbar/tray and not one
/// of our own windows. Clicking the tray (to open the menu) moves focus to the
/// shell — pasting after that would send Ctrl+V into the taskbar and lose the
/// text. Clipboard managers use the same remember-last-real-window trick.
/// </summary>
public static class ForegroundTracker
{
    private static long _lastRealForeground;

    /// <summary>Call periodically (cheap: one GetForegroundWindow + class read).</summary>
    public static void Update()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        if (IsShellOrOwnWindow(fg)) return;
        Interlocked.Exchange(ref _lastRealForeground, fg);
    }

    /// <summary>Last remembered non-shell foreground window; Zero when unknown.</summary>
    public static IntPtr Last => (IntPtr)Interlocked.Read(ref _lastRealForeground);

    private static bool IsShellOrOwnWindow(IntPtr hwnd)
    {
        var className = GetWindowClassName(hwnd);
        if (className is "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
            or "XamlExplorerHostIslandWindow" or "Shell_SecondaryTrayWnd")
            return true;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
