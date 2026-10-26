using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace FreeFlow.UI;

/// <summary>
/// Borderless, non-activating recording overlay (Windows analog of the macOS
/// RecordingOverlay): a dark rounded card pinned to the TOP of the screen — the
/// bottom, where input fields live, stays clear — with an animated level
/// equalizer, the live interim transcript (tail shown first) and a status line.
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
    private OverlayMode _mode;
    private int _animTick;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Window;
        // The level bar repaints up to 30×/s; without double buffering each repaint
        // erases the background first and the whole card visibly flickers.
        DoubleBuffered = true;
        Size = new Size(520 * DeviceDpi / 96, 132 * DeviceDpi / 96);
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
        Region = new Region(Theme.RoundedPath(ClientRectangle, LogicalToDeviceUnits(16)));
        old?.Dispose();
        Invalidate();
    }

    private Rectangle BarsRect()
    {
        int pad = LogicalToDeviceUnits(20);
        int barWidth = LogicalToDeviceUnits(6);
        int gap = LogicalToDeviceUnits(4);
        return new Rectangle(pad, LogicalToDeviceUnits(18),
            BarCount * barWidth + (BarCount - 1) * gap, LogicalToDeviceUnits(72));
    }

    private Rectangle TextRect()
    {
        var bars = BarsRect();
        int pad = LogicalToDeviceUnits(20);
        return new Rectangle(bars.Right + LogicalToDeviceUnits(14), LogicalToDeviceUnits(14),
            ClientRectangle.Width - pad - bars.Right - LogicalToDeviceUnits(14),
            LogicalToDeviceUnits(84));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int radius = LogicalToDeviceUnits(16);

        Theme.StrokeRounded(g, _mode == OverlayMode.Error ? Theme.DangerStrong : Theme.CardBorder,
            ClientRectangle, radius);

        // Animated equalizer bars (recording): eased toward the live level with
        // per-bar phase, never fully empty so the row reads as one instrument.
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
            using var fill = new SolidBrush(_mode == OverlayMode.Error ? Theme.DangerStrong : Theme.Accent);
            Theme.FillRounded(g, fill.Color, new Rectangle(x, y, barWidth, height), corner);
        }

        // Live transcript (realtime) — newest tail shown, oldest trimmed — or the
        // current status when nothing was heard yet. The fine status line sits under
        // the transcript in the caption color.
        var textRect = TextRect();
        var primary = _transcript.Length > 0 ? _transcript : _statusText;
        primary = FitTail(primary, textRect, Theme.FontPageTitle);
        TextRenderer.DrawText(g, primary, Theme.FontPageTitle, textRect,
            _mode == OverlayMode.Error ? Theme.Danger : Theme.TextBright,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        if (_transcript.Length > 0 && _statusText.Length > 0)
        {
            var statusRect = new Rectangle(textRect.Left, textRect.Bottom + LogicalToDeviceUnits(2),
                textRect.Width, LogicalToDeviceUnits(20));
            var status = FitTail(_statusText, statusRect, Theme.FontCaption);
            TextRenderer.DrawText(g, status, Theme.FontCaption, statusRect,
                Theme.TextCaption, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
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
        Invalidate(BarsRect());
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

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpShowWindow = 0x40;
}
