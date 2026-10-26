using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeFlow.Services;

public sealed record ForegroundContext(string AppName, string WindowTitle, string? JpegBase64);

/// <summary>
/// Port of AppContextService (Windows analog): gathers the foreground app name and
/// window title, and optionally a JPEG screenshot of the foreground window. The
/// screenshot is opt-in on Windows (no per-app Screen Recording permission exists).
/// </summary>
public static class ContextService
{
    public static ForegroundContext Collect(bool includeScreenshot)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return new ForegroundContext("", "", null);

        var title = GetWindowTextTitle(hwnd);
        var appName = GetProcessName(hwnd);
        if (string.IsNullOrWhiteSpace(title)) title = appName; // Swift: ?? appName

        string? jpeg = null;
        if (includeScreenshot)
        {
            try { jpeg = CaptureWindowJpeg(hwnd); }
            catch (Exception) { jpeg = null; }
        }

        return new ForegroundContext(appName, title, jpeg);
    }

    public static string BuildMetadata(ForegroundContext context)
    {
        // Port of the macOS metadata block; selected text is sent in full (Swift does
        // not truncate — only trims), and "(none)" matches the Swift None sentinel.
        var selection = SelectionCapture.GetSelectedText().Trim();
        return new StringBuilder()
            .AppendLine($"App: {context.AppName}")
            .AppendLine($"Window: {context.WindowTitle}")
            .Append($"Selected text: {(string.IsNullOrWhiteSpace(selection) ? "(none)" : selection)}")
            .ToString();
    }

    private static string GetWindowTextTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        _ = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return string.IsNullOrEmpty(name) ? "" : name;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string? CaptureWindowJpeg(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
        }

        // Port of the macOS screenshot constraints: max dimension 1024, JPEG q≈0.5,
        // shrunk further if the base64 payload is too large.
        foreach (var scale in new[] { 1.0, 0.75, 0.5 })
        {
            foreach (var quality in new[] { 50L, 25L, 12L })
            {
                using var scaled = scale >= 1.0 ? bitmap : Scale(bitmap, scale);
                var base64 = EncodeJpeg(scaled, quality);
                if (base64.Length <= 500_000) return base64;
            }
        }
        return null;
    }

    private static Bitmap Scale(Bitmap source, double scale)
    {
        var size = new System.Drawing.Size(
            Math.Max(1, (int)(source.Width * scale)),
            Math.Max(1, (int)(source.Height * scale)));
        var resized = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, size.Width, size.Height);
        return resized;
    }

    private static string EncodeJpeg(Bitmap bitmap, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, encoder, parameters);
        return Convert.ToBase64String(stream.ToArray());
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
