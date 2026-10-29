using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace FreeFlow.UI;

/// <summary>
/// Borderless, non-activating recording overlay (Windows analog of the macOS
/// RecordingOverlay): a dark gradient card with a real DWM drop shadow, pinned to
/// the TOP of the paste-target's monitor — the bottom, where input fields live,
/// stays clear — showing an animated equalizer, the live interim transcript
/// (tail first), the paste destination ("→ App") and a REC/ERROR badge.
/// Re-asserts topmost during recording so pinned-topmost windows cannot cover it.
///
/// Scaling contract: the card has ONE logical design (the constants below, at
/// 96 DPI) and ONE scale source — the effective DPI of the monitor it is
/// presented on, read straight from that monitor (GetDpiForMonitor). Every
/// metric, the fonts and the window region derive from that scale. Fonts are
/// pixel-sized (GraphicsUnit.Pixel) and the SAME instance measures and draws a
/// given string: GDI point-size fonts resolve through whichever DC DPI each
/// call hits, so with point fonts the card could measure at one scale and
/// paint at another on mixed-DPI setups — pills ellipsised into smudges and
/// the transcript clipped at the card edge. WinForms' own per-monitor rescale
/// of the form is always followed by an idempotent re-snap to the canonical
/// size (a pure function of the monitor DPI), so scales cannot compound.
/// </summary>
public sealed class OverlayForm : Form
{
    private enum OverlayMode { Recording, Error }

    private const int BarCount = 5;

    // Logical design @96 DPI — every use goes through S().
    private const int LogicalWidth = 480;
    private const int LogicalHeight = 64;
    private const int LogicalEdgePad = 10;        // card side padding
    private const int LogicalTextVPad = 9;        // transcript block vertical padding
    private const int LogicalBarLeft = 14;        // equalizer block x
    private const int LogicalBarSize = 4;         // bar width
    private const int LogicalBarGap = 3;
    private const int LogicalBarBoxHeight = 36;   // equalizer block height (full level)
    private const int LogicalBarMinHeight = 6;    // idle bar
    private const int LogicalBarsWidth = 32;      // 5 bars + 4 gaps
    private const int LogicalGapBarsToText = 12;
    private const int LogicalPillHeight = 16;
    private const int LogicalPillGap = 4;
    private const int LogicalPillPad = 9;         // pill inner side padding
    private const int LogicalPillDot = 6;
    private const int LogicalPillDotGap = 5;
    private const int LogicalGapTextToPills = 8;
    private const int LogicalMaxPillWidth = 150;  // destination pill cap; longer names ellipsize
    private const int LogicalRegionRadius = 12;
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

    private uint _dpi;
    private Font _transcriptFont = null!;
    private Font _pillFont = null!;

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
        EnsureFonts(96);
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
        // WinForms rescales the form AFTER a monitor move (WM_DPICHANGED); snap the
        // whole card — size, fonts, region, position — back to the canonical values
        // for the new DPI. The snap is a pure function of that DPI, so whatever
        // WinForms just multiplied cannot compound into a wrong scale.
        DpiChanged += (_, args) =>
        {
            EnsureFonts((uint)args.DeviceDpiNew);
            Size = CanonicalSize;
            PositionOverlay();
            RebuildRegion();
            Invalidate();
        };
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
        _transcriptFont.Dispose();
        _pillFont.Dispose();
    }

    // ------------------------------------------------------------- scale & fonts

    /// <summary>Logical → device pixels for the monitor the card is presented on.</summary>
    private int S(float logical) => (int)MathF.Round(logical * _dpi / 96f);

    private Size CanonicalSize => new(S(LogicalWidth), S(LogicalHeight));

    private void EnsureFonts(uint dpi)
    {
        if (dpi == _dpi && _transcriptFont != null) return;
        _dpi = dpi;
        // Pixel-sized em heights (10pt ≈ 13.3px, 8.25pt ≈ 11px @96 DPI): a pixel
        // font renders identically on every DC, so measuring and drawing always
        // agree — regardless of which monitor's DPI a GDI call resolves through.
        _transcriptFont?.Dispose();
        _pillFont?.Dispose();
        _transcriptFont = new Font("Segoe UI", MathF.Round(13.3f * _dpi / 96), GraphicsUnit.Pixel);
        _pillFont = new Font("Segoe UI", MathF.Round(11f * _dpi / 96), GraphicsUnit.Pixel);
    }

    /// <summary>Sizes, positions and re-regions the card for the monitor it belongs on:
    /// the paste-target's screen when known, else the cursor's.</summary>
    private void Present()
    {
        var screen = AnchorScreen ?? Screen.FromPoint(Cursor.Position);
        var center = new Point(screen.Bounds.Left + screen.Bounds.Width / 2,
                               screen.Bounds.Top + screen.Bounds.Height / 2);
        EnsureFonts(DpiOfPoint(center));
        Size = CanonicalSize;
        PositionOverlay();
        RebuildRegion();
        ReassertTopMost();
    }

    private void RebuildRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var old = Region;
        Region = new Region(RoundedPath(ClientRectangle, S(LogicalRegionRadius)));
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

    // ------------------------------------------------------------------- layout

    /// <summary>Equalizer block, vertically centered in the card.</summary>
    private Rectangle BarsBox() =>
        new(S(LogicalBarLeft), (ClientRectangle.Height - S(LogicalBarBoxHeight)) / 2,
            S(LogicalBarsWidth), S(LogicalBarBoxHeight));

    private int PillWidth(string text, bool withDot)
    {
        var textSize = TextRenderer.MeasureText(text, _pillFont);
        int width = textSize.Width + (withDot ? S(LogicalPillDot) + S(LogicalPillDotGap) : 0)
            + S(LogicalPillPad) * 2;
        return Math.Min(width, S(LogicalMaxPillWidth));
    }

    /// <summary>Transcript block: strictly inside the card (FitTail measures against
    /// exactly this rectangle, so a fitted transcript can never spill past the
    /// bottom edge) and never under the pill column.</summary>
    private Rectangle TextRect(int pillColumnWidth)
    {
        int textX = S(LogicalBarLeft + LogicalBarsWidth + LogicalGapBarsToText);
        int pillLeft = ClientRectangle.Width - S(LogicalEdgePad) - pillColumnWidth;
        int width = Math.Max(S(60), pillLeft - S(LogicalGapTextToPills) - textX);
        return new Rectangle(textX, S(LogicalTextVPad), width,
            ClientRectangle.Height - 2 * S(LogicalTextVPad));
    }

    // -------------------------------------------------------------------- paint

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

        bool error = _mode == OverlayMode.Error;
        using (var border = new Pen(error ? Theme.DangerStrong : Color.FromArgb(64, 64, 74)))
        {
            using var path = RoundedPath(rect, S(LogicalRegionRadius));
            g.DrawPath(border, path);
        }

        // Equalizer bars, inside BarsBox() — never past the card edge.
        var box = BarsBox();
        int barWidth = S(LogicalBarSize);
        int barGap = S(LogicalBarGap);
        Color barTop = error ? Theme.DangerStrong : Color.FromArgb(168, Theme.AccentBright.R, Theme.AccentBright.G, Theme.AccentBright.B);
        Color barBottom = error ? Theme.Danger : Theme.Accent;
        for (var i = 0; i < BarCount; i++)
        {
            int height = Math.Max(S(LogicalBarMinHeight), (int)(box.Height * Math.Clamp(_barLevels[i], 0, 1)));
            int x = box.Left + i * (barWidth + barGap);
            int y = box.Bottom - height;
            using (var barGradient = new LinearGradientBrush(new Rectangle(x, y, barWidth, height), barTop, barBottom, 90f))
            {
                using var path = RoundedPath(new Rectangle(x, y, barWidth, height), S(2));
                g.FillPath(barGradient, path);
            }
        }

        // Live transcript (realtime) — newest tail shown, oldest trimmed — or the
        // current status while nothing was heard yet. Measured and drawn with the
        // same pixel-sized font; vertically centered in its block.
        string recText = error ? "ERROR" : "REC";
        string destText = "→ " + (_targetApp.Length > 0 ? _targetApp : "clipboard");
        int recWidth = PillWidth(recText, withDot: true); // the status pill always shows its dot
        int destWidth = PillWidth(destText, withDot: false);
        var textRect = TextRect(Math.Max(recWidth, destWidth));
        var primary = _transcript.Length > 0 ? _transcript : _statusText;
        primary = FitTail(primary, textRect, _transcriptFont);
        TextRenderer.DrawText(g, primary, _transcriptFont, textRect,
            error ? Theme.Danger : Theme.TextBright,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        // Right column, two rows: REC on the first, destination under it.
        int pillHeight = S(LogicalPillHeight);
        int right = rect.Right - S(LogicalEdgePad);
        int pillTop = S((LogicalHeight - 2 * LogicalPillHeight - LogicalPillGap) / 2);
        DrawPill(g, recText, right, pillTop, pillHeight, recWidth,
            error ? Theme.Danger : Theme.AccentBright,
            error ? Color.FromArgb(60, 24, 24, 28) : Color.FromArgb(48, 120, 40, 44),
            error ? Theme.DangerStrong : Theme.Danger);
        DrawPill(g, destText, right, pillTop + pillHeight + S(LogicalPillGap), pillHeight, destWidth,
            Theme.TextCaption, Color.FromArgb(38, 38, 46), null);
    }

    /// <summary>Draws a rounded pill with an optional leading dot at a pre-computed
    /// width (see <see cref="PillWidth"/>); longer text ellipsises inside it.</summary>
    private void DrawPill(Graphics g, string text, int right, int top, int height, int width,
        Color textColor, Color backColor, Color? dotColor)
    {
        var rect = new Rectangle(right - width, top, width, height);
        using (var brush = new SolidBrush(backColor))
        using (var path = RoundedPath(rect, height / 2))
        {
            g.FillPath(brush, path);
        }
        int dot = dotColor.HasValue ? S(LogicalPillDot) : 0;
        int textLeft = rect.Left + S(LogicalPillPad);
        if (dotColor.HasValue)
        {
            var dotY = rect.Top + (height - dot) / 2;
            using var dotBrush = new SolidBrush(dotColor.Value);
            g.FillEllipse(dotBrush, new Rectangle(textLeft, dotY, dot, dot));
            textLeft += dot + S(LogicalPillDotGap);
        }
        TextRenderer.DrawText(g, text, _pillFont,
            new Rectangle(textLeft, rect.Top, rect.Right - S(LogicalPillPad) - textLeft, height),
            textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Trims a long string from its START so the newest words stay visible;
    /// search direction and reasoning live in <see cref="OverlayTextFit"/>.</summary>
    private string FitTail(string text, Rectangle rect, Font font) =>
        OverlayTextFit.FitTail(text, rect, font);

    // --------------------------------------------------------------- animation

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

    // --------------------------------------------------------------- public API

    public void ShowRecording()
    {
        if (InvokeRequired) { BeginInvoke(ShowRecording); return; }
        _errorTimer.Stop();
        _hideTimer.Stop();
        _mode = OverlayMode.Recording;
        _level = 0;
        _transcript = "";
        Array.Clear(_barLevels);
        Present();
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
        Present();
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

    /// <summary>Monitor to anchor the card to: the paste-target window's screen
    /// (set by TrayContext at recording start) — NOT the cursor, which may sit on
    /// a different monitor than the one the user is typing on.</summary>
    public Screen? AnchorScreen { get; set; }

    /// <summary>Pins the card to the TOP center of the anchor monitor: the bottom
    /// of the screen is where input fields live — the overlay must never cover them.
    /// Clamped into the monitor's working area so mixed-DPI coordinate surprises
    /// can never push it off-screen.</summary>
    private void PositionOverlay()
    {
        var area = (AnchorScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        int x = area.Width - Width < 16
            ? area.Left
            : Math.Clamp(area.Left + (area.Width - Width) / 2, area.Left + 8, area.Right - Width - 8);
        int y = Math.Clamp(area.Top + 12, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        Location = new Point(x, y);
    }

    // ------------------------------------------------------------------- win32

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>Effective DPI (MDT_EFFECTIVE_DPI) of the monitor containing a point.</summary>
    private static uint DpiOfPoint(Point p)
    {
        const uint tonearest = 2;
        const int effective = 0;
        try
        {
            var hmon = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, tonearest);
            if (hmon != IntPtr.Zero &&
                GetDpiForMonitor(hmon, effective, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX;
        }
        catch (Exception) { }
        return 96;
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
