# Capture the dictation overlay demo on every connected monitor.
# Launches FreeFlow --demo-overlay (dev helper), parks the cursor on each
# monitor in turn so the card anchors there, and grabs three states per
# monitor: recording (equalizer + live transcript), processing status, and
# the error card. Screenshots land in -OutDir as monitor<N>-<state>.png.
#
# Usage:  powershell -File capture-overlay.ps1 [-Exe path] [-OutDir dir]
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI.
param(
  [string]$Exe = "..\src\FreeFlow\bin\Debug\net8.0-windows\FreeFlow.exe",
  [string]$OutDir = ".\overlay-shots"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public class FFCap {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumMonProc(IntPtr hmon, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumMonProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hmon, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    // Physical-pixel rects of all monitors. The process must have called
    // SetProcessDPIAware() before this so coordinates are not virtualized.
    static List<RECT> _monitors = new List<RECT>();
    static bool MonitorEnumProc(IntPtr hmon, IntPtr hdc, ref RECT rect, IntPtr data) {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (GetMonitorInfoW(hmon, ref mi)) _monitors.Add(mi.rcMonitor);
        return true;
    }
    public static List<RECT> Monitors() {
        _monitors.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumProc, IntPtr.Zero);
        return _monitors;
    }

    static IntPtr _found;
    static string _needle;
    static bool WindowEnumProc(IntPtr h, IntPtr lp) {
        if (!IsWindowVisible(h)) return true;
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        if (sb.ToString().IndexOf(_needle, StringComparison.OrdinalIgnoreCase) >= 0) { _found = h; return false; }
        return true;
    }
    public static IntPtr FindByTitle(string substring) {
        _found = IntPtr.Zero; _needle = substring;
        EnumWindows(WindowEnumProc, IntPtr.Zero);
        return _found;
    }

    // DWM extended frame bounds - physical pixels, shadow excluded.
    public static RECT Frame(IntPtr h) {
        RECT r;
        DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT)));
        return r;
    }
}
"@
[FFCap]::SetProcessDPIAware() | Out-Null

function Save-Shot([IntPtr]$hwnd, [string]$path) {
    $r = [FFCap]::Frame($hwnd)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "bad rect" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [FFCap]::PrintWindow($hwnd, $hdc, 2) # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    if (-not $ok) { throw "PrintWindow failed" }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output ("SAVED {0} ({1},{2} {3}x{4})" -f $path, $r.Left, $r.Top, $w, $h)
}

$exePath = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$idx = 0
foreach ($mon in [FFCap]::Monitors()) {
    $cx = $mon.Left + [int](($mon.Right - $mon.Left) / 2)
    $cy = $mon.Top + [int](($mon.Bottom - $mon.Top) / 2)
    Write-Output ("=== monitor {0}: {1}x{2} at ({3},{4}), cursor to ({5},{6}) ===" -f $idx, ($mon.Right - $mon.Left), ($mon.Bottom - $mon.Top), $mon.Left, $mon.Top, $cx, $cy)
    [void][FFCap]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 300

    $p = Start-Process -FilePath $exePath -ArgumentList "--demo-overlay" -PassThru
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $hwnd = [IntPtr]::Zero
    while ($sw.ElapsedMilliseconds -lt 8000) {
        $hwnd = [FFCap]::FindByTitle("FreeFlow Overlay")
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        throw "overlay window not found"
    }

    # Demo timeline (from Program.RunOverlayDemo): transcript at 0.9s, status at
    # 4.2s, error at 7.6s. Beats sit late inside each window: process start ->
    # timer start has up to ~1.5s of cold-start jitter.
    foreach ($beat in @(@{ t = 1600; name = "rec" }, @{ t = 4600; name = "status" }, @{ t = 9900; name = "error" })) {
        $wait = $beat.t - $sw.ElapsedMilliseconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
        Save-Shot $hwnd (Join-Path $OutDir ("monitor{0}-{1}.png" -f $idx, $beat.name))
    }

    if (-not $p.WaitForExit(7000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $idx++
}
Write-Output "DONE: captured $idx monitor(s)"
