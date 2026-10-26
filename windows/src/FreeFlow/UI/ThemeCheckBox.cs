using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

/// <summary>
/// Dark check box: rounded box with an accent fill and drawn check glyph
/// (the stock system glyph always renders in light mode and looks wrong here).
/// </summary>
public class ThemeCheckBox : CheckBox
{
    private bool _hover;

    public ThemeCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontBody;
        BackColor = Theme.Card; // opaque: checkbox rows live on cards
        AutoSize = true;
        MinimumSize = new Size(0, 20);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 4, 0, 4);
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

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ButtonBase never erases the background for UserPaint controls — do it here
        // or every row keeps pixels from earlier paints.
        using (var bg = new SolidBrush(BackColor))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        int box = LogicalToDeviceUnits(16);
        var rect = new Rectangle(0, (Height - box) / 2, box, box);
        int radius = LogicalToDeviceUnits(4);

        if (Checked)
        {
            using (var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.TextDisabled))
            using (var path = Theme.RoundedPath(rect, radius))
            {
                g.FillPath(fill, path);
            }
            using var check = new Pen(Theme.OnAccent, Math.Max(1.6f, box / 9f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            float bx = rect.X, by = rect.Y, s = box;
            g.DrawLines(check, new[]
            {
                new PointF(bx + s * 0.26f, by + s * 0.52f),
                new PointF(bx + s * 0.43f, by + s * 0.68f),
                new PointF(bx + s * 0.74f, by + s * 0.32f),
            });
        }
        else
        {
            using (var fill = new SolidBrush(Theme.Field))
            using (var path = Theme.RoundedPath(rect, radius))
            {
                g.FillPath(fill, path);
            }
            Theme.StrokeRounded(g, _hover ? Theme.FieldBorderHover : Theme.FieldBorder, rect, radius);
        }

        var textRect = new Rectangle(rect.Right + LogicalToDeviceUnits(8), 0,
            Width - rect.Right - LogicalToDeviceUnits(8), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.TextDisabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        int boxAndGap = LogicalToDeviceUnits(24);
        return new Size(text.Width + boxAndGap + 2, Math.Max(text.Height, LogicalToDeviceUnits(20)));
    }
}
