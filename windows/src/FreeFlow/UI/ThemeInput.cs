using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

/// <summary>
/// Rounded bordered panel framing a borderless child input (TextBox, numeric,
/// list...). Forwards clicks to the child and highlights its border while the
/// child has focus — one reusable host for every styled input in the app.
/// </summary>
public sealed class HostPanel : Panel
{
    public Control Child { get; }
    private bool _hover;

    public HostPanel(Control child, Padding padding)
    {
        Child = child;
        BackColor = Theme.Field;
        Padding = padding;
        Margin = new Padding(0);
        if (child is TextBox textBox) textBox.BorderStyle = BorderStyle.None;
        child.Dock = DockStyle.Fill;
        Controls.Add(child);

        child.GotFocus += (_, _) => Invalidate();
        child.LostFocus += (_, _) => Invalidate();
        child.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        child.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        Click += (_, _) => child.Focus();
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var border = Child.Focused ? Theme.Accent
            : _hover ? Theme.FieldBorderHover
            : Theme.FieldBorder;
        Theme.StrokeRounded(e.Graphics, border, ClientRectangle, LogicalToDeviceUnits(Theme.RadiusControl));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}

/// <summary>Factories pairing themed inputs with their bordered hosts.</summary>
public static class ThemeInputs
{
    private static Padding FieldPad(Control host)
    {
        int pad = host.LogicalToDeviceUnits(10);
        int padV = host.LogicalToDeviceUnits(Theme.FieldVPad);
        return new Padding(pad, padV, pad, padV);
    }

    /// <summary>Single-line text input; assign <paramref name="box"/>.Text after creation.</summary>
    public static HostPanel Text(out TextBox box, string? placeholder = null, bool password = false)
    {
        box = new TextBox
        {
            PlaceholderText = placeholder ?? "",
            UseSystemPasswordChar = password,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Theme.FontBody,
        };
        return Wrap(box);
    }

    /// <summary>Multiline text input with a fixed logical-pixel height and a vertical scrollbar.</summary>
    public static HostPanel Multiline(out TextBox box, string text, int height)
    {
        box = new TextBox
        {
            Text = text,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Theme.FontBody,
        };
        var host = Wrap(box);
        host.Height = height * host.DeviceDpi / 96;
        return host;
    }

    /// <summary>Numeric input (dark spinner arrows drawn over the system buttons).</summary>
    public static HostPanel Numeric(out NumericUpDown numeric, int min, int max, int value)
    {
        numeric = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Theme.FontBody,
        };
        var target = numeric;
        target.HandleCreated += (_, _) => StyleSpinButtons(target);
        if (target.IsHandleCreated) StyleSpinButtons(target);
        return Wrap(target);
    }

    /// <summary>Hosts any prebuilt input (e.g. HotkeyBox), sizing the host to it.</summary>
    public static HostPanel Wrap(Control child)
    {
        var host = new HostPanel(child, FieldPad(child));
        int innerHeight = child switch
        {
            TextBox textBox => textBox.PreferredHeight,
            NumericUpDown numeric => numeric.PreferredHeight,
            _ => child.Height,
        };
        host.Height = innerHeight + Theme.FieldVPad * 2 * host.DeviceDpi / 96;
        return host;
    }

    /// <summary>Hosts a list with an explicit logical-pixel height.</summary>
    public static HostPanel List(ThemeListBox list, int height)
    {
        var host = new HostPanel(list, new Padding(2));
        host.Height = height * host.DeviceDpi / 96;
        return host;
    }

    /// <summary>Repaints the system-drawn up/down buttons dark (their glyphs would be near-invisible).</summary>
    private static void StyleSpinButtons(NumericUpDown numeric)
    {
        foreach (Control control in numeric.Controls)
        {
            if (control.GetType().Name != "UpDownButtons") continue;
            control.Paint += (sender, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = ((Control)sender!).ClientRectangle;
                using var bg = new SolidBrush(Theme.Field);
                g.FillRectangle(bg, r);
                using var chevron = new Pen(Theme.TextCaption, 1.6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                float cx = r.Width / 2f;
                float aw = Math.Max(4f, r.Width * 0.26f);
                float ay = r.Height * 0.30f, by = r.Height * 0.70f;
                g.DrawLines(chevron, new[]
                {
                    new PointF(cx - aw, ay + 2), new PointF(cx, ay - 2), new PointF(cx + aw, ay + 2),
                });
                g.DrawLines(chevron, new[]
                {
                    new PointF(cx - aw, by - 2), new PointF(cx, by + 2), new PointF(cx + aw, by - 2),
                });
            };
            control.Invalidate();
        }
    }
}
