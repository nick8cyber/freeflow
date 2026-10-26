using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace FreeFlow.UI;

/// <summary>
/// Borderless, non-activating recording overlay (Windows analog of the macOS
/// RecordingOverlay): a dark gradient card with a real DWM drop shadow, pinned to
/// the TOP of the cursor's monitor — the bottom, where input fields live, stays
/// clear — showing an animated equalizer, the live interim transcript (tail
/// first), the paste destination ("→ App") and a LIVE badge. Re-asserts topmost
/// during recording so pinned-topmost windows cannot cover it.
/// </summary>
public sealed class OverlayForm : Form
{
    private enum OverlayMode { Recording, Error }

    private const int BarCount = 5;
    private const int MaxStoredTranscriptChars = 400;

    private readonly WinFormsTimer _hideTimer = new() { Interval = 1200 };
    private readonly WinFormsTimer _errorTimer = new() { Interval = 4000 };
    private readonly WinFormsTimer _animTimer = new() { Interval = 33 };
    private readonly double[] _barLevels = new double[BarCount];
    private readonly double[] _barPhases = new double[BarCount];
    private double _level;
    private string _statusText = "";
    private string _transcript = "";
    private string _targetApp = "";
    private OverlayMode _mode;
    private int _animTick;
    private readonly Font _transcriptFont = new("Segoe UI", 13f);

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(22, 22, 27);
        // The level bar repaints up to 30×/s; without double buffering each repaint
        // erases the background first and the whole card visibly flickers.
        DoubleBuffered = true;
        Size = new Size(700 * DeviceDpi / 96, 112 * DeviceDpi / 96);
        Text = "FreeFlow Overlay"; // invisible (no caption) — lets test tooling find the window

        // Non-activating + transparent-to-input so dictation targets keep focus.
        const int wsExNoactivate = 0x08000000;
        const int wsExToolwindow = 0x00000080;
        CreateParamsStyles = wsExNoactivate | wsExToolwindow;

        var random = new Random(0x600DF10F); // fixed seed: stable phases per launch
        for (var i = 0; i < BarCount; i++) _barPhases[i] = random.NextDouble() * Math.PI * 2;

        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); Hide(); };
        _animTimer.Tick += (_, _) => TickAnimation();
    }

    private int CreateParamsStyles { get; set; }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= CreateParamsStyles;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RebuildRegion();
        // Borderless forms get no system shadow; extending frame by one bottom pixel
        // brings the standard DWM drop shadow back.
        var margins = new MARGINS { Bottom = 1 };
        DwmExtendFrameIntoClientArea(Handle, ref margins);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RebuildRegion();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _hideTimer.Dispose();
        _errorTimer.Dispose();
        _animTimer.Dispose();
    }

    private void RebuildRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var old = Region;
        Region = new Region(RoundedPath(ClientRectangle, LogicalToDeviceUnits(20)));
        old?.Dispose();
        Invalidate();
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private Rectangle BarsRect()
    {
        int pad = LogicalToDeviceUnits(22);
        int barWidth = LogicalToDeviceUnits(6);
        int gap = LogicalToDeviceUnits(4);
        return new Rectangle(pad, LogicalToDeviceUnits(17),
            BarCount * barWidth + (BarCount - 1) * gap, LogicalToDeviceUnits(56));
    }

    private Rectangle TextRect()
    {
        var bars = BarsRect();
        int pad = LogicalToDeviceUnits(22);
        return new Rectangle(bars.Right + LogicalToDeviceUnits(16), LogicalToDeviceUnits(14),
            ClientRectangle.Width - pad - bars.Right - LogicalToDeviceUnits(16) - PillsStripWidth(),
            LogicalToDeviceUnits(60));
    }

    /// <summary>Width reserved for the state + destination pills on the first line.</summary>
    private int PillsStripWidth()
    {
        int gap = LogicalToDeviceUnits(8);
        return MeasurePill("→ " + (_targetApp.Length > 0 ? _targetApp : "clipboard")).Width
            + gap + MeasurePill(_mode == OverlayMode.Error ? "ERROR" : "REC").Width
            + LogicalToDeviceUnits(18);
    }

    private Size MeasurePill(string text)
    {
        var textSize = TextRenderer.MeasureText(text, Theme.FontCaption);
        int dot = LogicalToDeviceUnits(8);
        return new Size(textSize.Width + dot + LogicalToDeviceUnits(5) + LogicalToDeviceUnits(18),
            LogicalToDeviceUnits(22));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Vertical gradient card — subtle, but visibly not a flat rectangle.
        var rect = ClientRectangle;
        using (var background = new LinearGradientBrush(rect, Color.FromArgb(34, 34, 41), Color.FromArgb(20, 20, 25), 90f))
        {
            g.FillRectangle(background, rect);
        }

        int radius = LogicalToDeviceUnits(20);
        bool error = _mode == OverlayMode.Error;
        using (var border = new Pen(error ? Theme.DangerStrong : Color.FromArgb(64, 64, 74)))
        {
            using var path = RoundedPath(rect, radius);
            g.DrawPath(border, path);
        }

        // Equalizer bars.
        var bars = BarsRect();
        int barWidth = LogicalToDeviceUnits(6);
        int gap = LogicalToDeviceUnits(4);
        int baseBarHeight = LogicalToDeviceUnits(8);
        int corner = LogicalToDeviceUnits(3);
        for (var i = 0; i < BarCount; i++)
        {
            int height = Math.Max(baseBarHeight, (int)(bars.Height * Math.Clamp(_barLevels[i], 0, 1)));
            int x = bars.Left + i * (barWidth + gap);
            int y = bars.Bottom - height;
            var top = Color.FromArgb(168, error ? Theme.DangerStrong.R : Theme.AccentBright.R, error ? Theme.DangerStrong.G : Theme.AccentBright.G, error ? Theme.DangerStrong.B : Theme.AccentBright.B);
            using (var barGradient = new LinearGradientBrush(new Rectangle(x, y, barWidth, height), top, Theme.Accent, 90f))
            {
                using var path = RoundedPath(new Rectangle(x, y, barWidth, height), corner);
                g.FillPath(barGradient, path);
            }
        }

        // Live transcript (realtime) — newest tail shown, oldest trimmed — or the
        // current status while nothing was heard yet. Regular weight reads clean
        // on dark; semibold turned into mush.
        var textRect = TextRect();
        var primary = _transcript.Length > 0 ? _transcript : _statusText;
        primary = FitTail(primary, textRect, _transcriptFont);
        TextRenderer.DrawText(g, primary, _transcriptFont, textRect,
            error ? Theme.Danger : Theme.TextBright,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        // Top-right pills, same line as the transcript's first row: state + paste
        // destination. The text rect is narrowed by their width, so nothing overlaps.
        int pillHeight = LogicalToDeviceUnits(22);
        int pillGap = LogicalToDeviceUnits(8);
        int pillTop = rect.Top + LogicalToDeviceUnits(12);
        int right = rect.Right - LogicalToDeviceUnits(18);

        var targetPill = DrawPill(g, "→ " + (_targetApp.Length > 0 ? _targetApp : "clipboard"),
            right, pillTop, pillHeight, Theme.TextCaption, Color.FromArgb(38, 38, 46), null);
        var recText = error ? "ERROR" : "REC";
        DrawPill(g, recText,
            right - targetPill.Width - pillGap, pillTop, pillHeight,
            error ? Theme.Danger : Theme.AccentBright,
            error ? Color.FromArgb(60, 24, 24, 28) : Color.FromArgb(48, 120, 40, 44),
            error ? Theme.DangerStrong : Theme.Danger);
    }

    /// <summary>Draws a rounded pill with an optional leading dot; returns its width.</summary>
    private Size DrawPill(Graphics g, string text, int right, int top, int height,
        Color textColor, Color backColor, Color? dotColor)
    {
        var textSize = TextRenderer.MeasureText(text, Theme.FontCaption);
        int textWidth = textSize.Width;
        int dot = dotColor.HasValue ? LogicalToDeviceUnits(8) : 0;
        int width = textWidth + dot + (dot > 0 ? LogicalToDeviceUnits(5) : 0) + LogicalToDeviceUnits(18);
        var rect = new Rectangle(right - width, top, width, height);
        using (var brush = new SolidBrush(backColor))
        using (var path = RoundedPath(rect, height / 2))
        {
            g.FillPath(brush, path);
        }
        if (dotColor.HasValue)
        {
            var dotY = rect.Top + (height - dot) / 2;
            using var dotBrush = new SolidBrush(dotColor.Value);
            g.FillEllipse(dotBrush, new Rectangle(rect.Left + LogicalToDeviceUnits(9), dotY, dot, dot));
        }
        TextRenderer.DrawText(g, text, Theme.FontCaption,
            new Rectangle(rect.Left + LogicalToDeviceUnits(9) + dot, rect.Top, textWidth + 4, height),
            textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return new Size(width, height);
    }

    /// <summary>Trims a long string from its START so the newest words stay visible;
    /// pure tail-ellipsis would hide exactly the part being dictated right now.</summary>
    private string FitTail(string text, Rectangle rect, Font font)
    {
        if (text.Length == 0) return text;
        if (TextRenderer.MeasureText(text, font, rect.Size, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= rect.Height)
            return text;
        var tail = text;
        while (tail.Length > 16 &&
               TextRenderer.MeasureText("…" + tail, font, rect.Size, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height > rect.Height)
        {
            var cut = Math.Max(16, tail.Length / 4);
            tail = tail[cut..];
        }
        return "…" + tail;
    }

    private void TickAnimation()
    {
        if (_mode != OverlayMode.Recording || !Visible)
        {
            _animTimer.Stop();
            return;
        }
        _animTick++;
        // Re-assert topmost periodically: another pinned-topmost window (pinned
        // Telegram, overlay widgets) installed after us sits above in the topmost
        // band and the card reads as "hidden". NOACTIVATE keeps focus untouched.
        if (_animTick % 15 == 0) ReassertTopMost();
        for (var i = 0; i < BarCount; i++)
        {
            // Ease toward the band target: live level shaped by a slow per-bar
            // wave so the row dances even when the level is steady; decays smoothly
            // to a small breathing floor between words.
            var wave = 0.55 + 0.45 * Math.Sin(_animTick * 0.21 + _barPhases[i]);
            var target = Math.Max(_level * wave, 0.06 + 0.02 * Math.Sin(_animTick * 0.09 + _barPhases[i] * 2));
            _barLevels[i] += (target - _barLevels[i]) * (target > _barLevels[i] ? 0.45 : 0.16);
        }
        Invalidate();
    }

    private void ReassertTopMost() =>
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);

    public void ShowRecording()
    {
        if (InvokeRequired) { BeginInvoke(ShowRecording); return; }
        _errorTimer.Stop();
        _hideTimer.Stop();
        _mode = OverlayMode.Recording;
        _level = 0;
        _transcript = "";
        Array.Clear(_barLevels);
        PositionOverlay();
        RebuildRegion();
        ReassertTopMost();
        Show();
        _animTimer.Start();
    }

    public void UpdateLevel(double level)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateLevel(level)); return; }
        _level = Math.Clamp(level, 0, 1);
    }

    /// <summary>Live interim transcript from realtime streaming (full text so far).</summary>
    public void UpdateTranscript(string transcript)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateTranscript(transcript)); return; }
        _transcript = transcript.Length > MaxStoredTranscriptChars
            ? "…" + transcript[^MaxStoredTranscriptChars..]
            : transcript;
        if (Visible && _mode == OverlayMode.Recording) Invalidate();
    }

    /// <summary>Paste destination captured at recording start (foreground app name).</summary>
    public void UpdateTargetApp(string appName)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateTargetApp(appName)); return; }
        _targetApp = appName;
        if (Visible) Invalidate();
    }

    public void ShowStatus(string status)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowStatus(status)); return; }
        _statusText = status;
        if (!Visible) ShowRecording();
        Invalidate();
    }

    public void ShowError(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowError(message)); return; }
        _hideTimer.Stop();
        _animTimer.Stop();
        _mode = OverlayMode.Error;
        _statusText = message;
        _transcript = "";
        PositionOverlay();
        RebuildRegion();
        ReassertTopMost();
        Show();
        Invalidate();
        _errorTimer.Start();
    }

    public void HideAfterDelay()
    {
        if (InvokeRequired) { BeginInvoke(HideAfterDelay); return; }
        _level = 0;
        Invalidate();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Pins the card to the TOP center of the cursor's monitor: the bottom
    /// of the screen is where input fields live — the overlay must never cover them.
    /// Clamped into the monitor's working area so mixed-DPI coordinate surprises
    /// can never push it off-screen.</summary>
    private void PositionOverlay()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(area.Left + (area.Width - Width) / 2, area.Left + 8, area.Right - Width - 8);
        var y = Math.Clamp(area.Top + 12, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        Location = new Point(x, y);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpShowWindow = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }
}
