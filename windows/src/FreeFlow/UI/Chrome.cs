using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

/// <summary>Small tinted pill ("Primary · required") shown next to card titles.</summary>
public sealed class Badge : Control
{
    public enum BadgeKind { Accent, Neutral }

    public Badge(string text, BadgeKind kind = BadgeKind.Accent)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontCaption;
        AutoSize = true;
        BackColor = Theme.Card; // opaque: avoids transparent-sibling repaint artifacts
        TabStop = false;
    }

    public BadgeKind Kind { get; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(text.Width + LogicalToDeviceUnits(14), LogicalToDeviceUnits(18));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width, Height);
        int radius = LogicalToDeviceUnits(5);

        var (fill, border, fore) = Kind == BadgeKind.Accent
            ? (Theme.AccentFill, Theme.AccentBorder, Theme.AccentBright)
            : ((Color)Color.FromArgb(40, 40, 48), Theme.CardBorder, Theme.TextCaption);

        Theme.FillRounded(g, fill, rect, radius);
        Theme.StrokeRounded(g, border, rect, radius);
        TextRenderer.DrawText(g, Text, Font, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Sidebar navigation entry: icon + label with a selected pill and accent bar.</summary>
public sealed class NavButton : Button
{
    private readonly Action<Graphics, Rectangle, Color> _icon;
    private bool _hover;
    private bool _selected;

    public NavButton(string text, Action<Graphics, Rectangle, Color> icon)
    {
        _icon = icon;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontNav;
        Height = 40 * DeviceDpi / 96; // logical 40px, scaled for the current monitor DPI
        Dock = DockStyle.Top;
        BackColor = Theme.Chrome; // opaque: matches the sidebar, avoids repaint artifacts
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

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
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int dpiScale = LogicalToDeviceUnits(1);

        // ButtonBase.OnPaintBackground is a no-op for UserPaint buttons — erase the
        // full rect ourselves or every earlier layout pass leaves pixels behind.
        using (var bgBrush = new SolidBrush(BackColor))
        {
            g.FillRectangle(bgBrush, ClientRectangle);
        }

        var pill = new Rectangle(4, 2, Width - 8, Height - 4);
        if (_selected)
        {
            Theme.FillRounded(g, Theme.NavSelected, pill, Theme.RadiusControl);
            using var bar = new SolidBrush(Theme.Accent);
            g.FillRectangle(bar, 12 * dpiScale, Height / 2 - 8 * dpiScale, 3, 16 * dpiScale);
        }
        else if (_hover)
        {
            Theme.FillRounded(g, Color.FromArgb(38, 38, 46), pill, Theme.RadiusControl);
        }

        var iconColor = _selected ? Theme.TextBright : _hover ? Theme.Text : Theme.TextCaption;
        var iconRect = new Rectangle(24 * dpiScale, Height / 2 - 9 * dpiScale, 18 * dpiScale, 18 * dpiScale);
        _icon(g, iconRect, iconColor);

        var textRect = new Rectangle(iconRect.Right + 12 * dpiScale, 0,
            Width - iconRect.Right - 16 * dpiScale, Height);
        TextRenderer.DrawText(g, Text, Font, textRect,
            _selected ? Theme.TextBright : _hover ? Theme.Text : Theme.TextCaption,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// Rounded section card: optional title/badge/subtitle header above a
/// two-column form grid. Auto-sizes to its content and never clips rows.
/// </summary>
public sealed class Card : TableLayoutPanel
{
    /// <summary>Two-column grid (auto-width label column, fill column) for this card's fields.</summary>
    public TableLayoutPanel Body { get; }

    public Card(string? title = null, string? subtitle = null, Badge? badge = null)
    {
        BackColor = Theme.Card;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(Theme.L), LogicalToDeviceUnits(Theme.M + 2),
            LogicalToDeviceUnits(Theme.L), LogicalToDeviceUnits(Theme.L));
        Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(Theme.M));
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (title != null) AddRow(HeaderRow(title, badge));
        if (subtitle != null)
        {
            var sub = Theme.Caption(subtitle);
            sub.Margin = new Padding(0, 0, 0, Theme.S);
            AddRow(sub);
        }
        Body = Grids.TwoColumn();
        AddRow(Body);

        Resize += (_, _) => UpdateRegion();
    }

    private static Control HeaderRow(string title, Badge? badge)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, Theme.S),
            Padding = Padding.Empty,
        };
        var heading = Theme.SectionTitle(title);
        heading.Margin = new Padding(0, 0, Theme.S, 0);
        flow.Controls.Add(heading);
        if (badge != null)
        {
            badge.Margin = new Padding(0, 1, 0, 0);
            flow.Controls.Add(badge);
        }
        return flow;
    }

    /// <summary>Appends an AutoSize row to the card.</summary>
    public void AddRow(Control control)
    {
        int row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(control, 0, row);
    }

    /// <summary>Makes the body grid expand to fill the card (used by the History page).</summary>
    public void MakeBodyFill()
    {
        AutoSize = false;
        RowStyles[^1] = new RowStyle(SizeType.Percent, 100);
        Body.Dock = DockStyle.Fill;
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var old = Region;
        Region = new Region(Theme.RoundedPath(new Rectangle(0, 0, Width, Height),
            LogicalToDeviceUnits(Theme.RadiusCard)));
        old?.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Theme.StrokeRounded(e.Graphics, Theme.CardBorder, ClientRectangle,
            LogicalToDeviceUnits(Theme.RadiusCard));
    }
}

/// <summary>Form-grid helpers shared by the settings pages: aligned label column + fill column.</summary>
internal static class Grids
{
    public static TableLayoutPanel TwoColumn()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    /// <summary>Label + input row; the input fills the remaining width, rows AutoSize (nothing clips).</summary>
    public static void Row(TableLayoutPanel grid, string label, Control input, int topPad = 2, int bottomPad = 2)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
        {
            var l = Theme.BodyLabel(label);
            l.Anchor = AnchorStyles.Left; // horizontal left + vertical center inside the row
            grid.Controls.Add(l, 0, row);
        }
        input.Margin = new Padding(0, topPad, 0, bottomPad);
        // Buttons keep their natural width; everything else fills the column.
        input.Anchor = input is ThemeButton ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(input, 1, row);
    }

    /// <summary>Full-width row (checkbox, caption, multiline editor) spanning both columns.</summary>
    public static void Span(TableLayoutPanel grid, Control control, int topPad = 2, int bottomPad = 2)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, topPad, 0, bottomPad);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
    }

    public static ThemeCheckBox Check(string text, bool @checked) => new()
    {
        Text = text,
        Checked = @checked,
    };
}
