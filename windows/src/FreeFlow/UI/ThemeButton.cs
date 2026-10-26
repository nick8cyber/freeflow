using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

public enum ThemeButtonKind
{
    /// <summary>Standard surface button (subtle fill + border).</summary>
    Neutral,
    /// <summary>Accent-filled call to action (Save, Start using FreeFlow).</summary>
    Primary,
    /// <summary>Destructive action (Delete, Clear history).</summary>
    Danger,
    /// <summary>Borderless quiet action that blends into its parent surface.</summary>
    Ghost,
}

/// <summary>
/// The one button class used across the app: flat, anti-aliased rounded corners,
/// explicit hover/pressed/disabled states, generous 30px height — labels can
/// never clip (the button AutoSizes to its text).
/// </summary>
public class ThemeButton : Button
{
    private bool _hover;
    private bool _pressed;

    public ThemeButton(ThemeButtonKind kind = ThemeButtonKind.Neutral, string? text = null)
    {
        Kind = kind;
        if (text != null) Text = text;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontBodySemibold;
        BackColor = Theme.Card; // opaque base; OnPaint covers the full rect with state colors
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(0, Theme.ButtonHeight);
        Padding = new Padding(Theme.L, 0, Theme.L, 0);
        Margin = new Padding(0);
        Cursor = Cursors.Hand;
    }

    public ThemeButtonKind Kind { get; }

    protected override bool ShowFocusCues => false;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed) Invalidate();
        _pressed = false;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width, Height);
        var radius = LogicalToDeviceUnits(Theme.RadiusControl);

        var (back, border, fore) = !Enabled ? DisabledColors()
            : _pressed ? PressedColors()
            : _hover ? HoverColors()
            : RestColors();

        // Full-rect base in the parent surface color so the rounded corners blend in.
        using (var baseFill = new SolidBrush(Parent?.BackColor ?? Theme.Window))
        {
            g.FillRectangle(baseFill, rect);
        }
        if (back.A > 0) Theme.FillRounded(g, back, rect, radius);
        if (border.A > 0) Theme.StrokeRounded(g, border, rect, radius);

        TextRenderer.DrawText(g, Text, Font, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private (Color Back, Color Border, Color Fore) RestColors() => Kind switch
    {
        ThemeButtonKind.Primary => (Theme.Accent, Theme.Accent, Theme.OnAccent),
        ThemeButtonKind.Danger => (Theme.DangerFill, Theme.DangerBorder, Theme.Danger),
        ThemeButtonKind.Ghost => (Color.Transparent, Color.Transparent, Theme.Text),
        _ => (Theme.Field, Theme.FieldBorder, Theme.Text),
    };

    private (Color Back, Color Border, Color Fore) HoverColors() => Kind switch
    {
        ThemeButtonKind.Primary => (Color.FromArgb(88, 196, 138), Theme.Accent, Theme.OnAccent),
        ThemeButtonKind.Danger => (Color.FromArgb(66, 38, 34), Color.FromArgb(132, 64, 56), Theme.Danger),
        ThemeButtonKind.Ghost => (Theme.Hover, Color.Transparent, Theme.TextBright),
        _ => (Theme.Hover, Theme.FieldBorderHover, Theme.TextBright),
    };

    private (Color Back, Color Border, Color Fore) PressedColors() => Kind switch
    {
        ThemeButtonKind.Primary => (Color.FromArgb(50, 154, 97), Theme.Accent, Theme.OnAccent),
        ThemeButtonKind.Danger => (Theme.DangerStrong, Theme.DangerStrong, Theme.OnAccent),
        ThemeButtonKind.Ghost => (Theme.Pressed, Color.Transparent, Theme.TextBright),
        _ => (Theme.Pressed, Theme.FieldBorderHover, Theme.TextBright),
    };

    private (Color Back, Color Border, Color Fore) DisabledColors() => Kind switch
    {
        ThemeButtonKind.Primary => (Theme.Field, Theme.FieldBorder, Theme.TextDisabled),
        _ => (Theme.Field, Theme.CardBorder, Theme.TextDisabled),
    };
}
