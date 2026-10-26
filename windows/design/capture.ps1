# FreeFlow UI capture helper.
# Modes:
#   launch  - start -Exe with -ExeArgs, wait for a window whose title contains -Title, foreground, capture -> -Out
#   shot    - find existing window (-Title), foreground, capture -> -Out
#   click   - click at (-RelX,-RelY) inside the window rect (client area), optional -Out capture afterwards
# All coordinates/rects are physical pixels (process is made DPI-aware).
param(
  [string]$Mode = "shot",
  [string]$Exe = "",
  [string]$ExeArgs = "",
  [string]$Title = "FreeFlow Settings",
  [string]$Out = "",
  [int]$RelX = -1,
  [int]$RelY = -1,
  [int]$SleepAfterMs = 700,
  [int]$TimeoutMs = 20000
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class FFWin {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindByTitle(string substring) {
        IntPtr found = IntPtr.Zero;
        bool ok = EnumWindows((h, lp) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            string title = sb.ToString();
            if (title.Length > 0 && title.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        LastEnumOk = ok;
        return found;
    }
    public static bool LastEnumOk;
    public static int VisibleSeen;

    public static string DumpTitles() {
        var sbAll = new StringBuilder();
        EnumWindows((h, lp) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            int n = GetWindowTextW(h, sb, 512);
            if (n > 0) sbAll.Append("[").Append(n).Append(":").Append(sb.ToString()).Append("] ");
            return true;
        }, IntPtr.Zero);
        return sbAll.ToString();
    }

    public static RECT Frame(IntPtr h) {
        RECT r;
        if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) GetWindowRectFallback(h, out r);
        return r;
    }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    private static void GetWindowRectFallback(IntPtr h, out RECT r) { GetWindowRect(h, out r); }

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
}
"@

[FFWin]::SetProcessDPIAware() | Out-Null

function Get-Target([string]$substring, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $hwnd = [FFWin]::FindByTitle($substring)
        if ($hwnd -ne [IntPtr]::Zero) { return $hwnd }
        Start-Sleep -Milliseconds 150
    } while ($sw.ElapsedMilliseconds -lt $timeoutMs)
    throw "window with title containing '$substring' not found (enumOk=$([FFWin]::LastEnumOk), procAlive=$([string]::Join(',', @(Get-Process FreeFlow -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }))))"
}

function Save-Shot([IntPtr]$hwnd, [string]$path) {
    [FFWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 350
    $r = [FFWin]::Frame($hwnd)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "bad rect" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # PrintWindow renders the window itself, so overlapping windows don't pollute the shot.
    $hdc = $g.GetHdc()
    $ok = [FFWin]::PrintWindow($hwnd, $hdc, 2) # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    if (-not $ok) {
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "SAVED $path ($($r.Left),$($r.Top) ${w}x${h}) printWindow=$ok"
}

switch ($Mode) {
    "launch" {
        if ($ExeArgs -ne "") {
            $p = Start-Process -FilePath $Exe -ArgumentList $ExeArgs -PassThru
        } else {
            $p = Start-Process -FilePath $Exe -PassThru
        }
        $hwnd = Get-Target $Title $TimeoutMs
        Start-Sleep -Milliseconds $SleepAfterMs
        Save-Shot $hwnd $Out
    }
    "shot" {
        $hwnd = Get-Target $Title $TimeoutMs
        Start-Sleep -Milliseconds $SleepAfterMs
        Save-Shot $hwnd $Out
    }
    "click" {
        $hwnd = Get-Target $Title $TimeoutMs
        [FFWin]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 250
        $r = [FFWin]::Frame($hwnd)
        $x = $r.Left + $RelX; $y = $r.Top + $RelY
        [FFWin]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 80
        [FFWin]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # LEFTDOWN
        [FFWin]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # LEFTUP
        Start-Sleep -Milliseconds $SleepAfterMs
        Write-Output "CLICKED rel($RelX,$RelY) abs($x,$y)"
        if ($Out -ne "") { Save-Shot $hwnd $Out }
    }
    default { throw "unknown mode $Mode" }
    "nudge" {
        $hwnd = Get-Target $Title $TimeoutMs
        [FFWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x0005) | Out-Null # NOSIZE|NOZORDER -> full frame redraw
        Start-Sleep -Milliseconds 400
        Write-Output "NUDGED"
        if ($Out -ne "") { Save-Shot $hwnd $Out }
    }
    "resize" {
        # resize to physical (-RelX,-RelY) pixels then capture
        $hwnd = Get-Target $Title $TimeoutMs
        [FFWin]::SetForegroundWindow($hwnd) | Out-Null
        [FFWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $RelX, $RelY, 0x0004) | Out-Null # NOMOVE
        Start-Sleep -Milliseconds 500
        Write-Output "RESIZED to $RelX x $RelY"
        if ($Out -ne "") { Save-Shot $hwnd $Out }
    }
    "probe" {
        $found = [FFWin]::FindByTitle($Title)
        Write-Output "FindByTitle('$Title') -> $found (enumOk=$([FFWin]::LastEnumOk))"
        Write-Output ("visible titled windows: " + [FFWin]::DumpTitles())
    }
}
