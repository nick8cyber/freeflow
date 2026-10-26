using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

/// <summary>
/// ComboBox with a fully dark, owner-drawn dropdown list and a flat edit surface
/// (DropDownList mode; the stock light-mode list would break the dark theme).
/// A small "cap" control covers the system-drawn drop arrow so even that pixel
/// is dark, and forwards clicks to the combo.
/// </summary>
public class ThemeComboBox : ComboBox
{
    private ArrowCap? _cap;

    public ThemeComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Font = Theme.FontBody;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Font.Height + 8;
        Cursor = Cursors.Hand;
        Margin = new Padding(0);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_cap == null || _cap.IsDisposed)
        {
            _cap = new ArrowCap(this);
            Controls.Add(_cap);
        }
        PositionCap();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionCap();
    }

    private void PositionCap()
    {
        if (_cap == null) return;
        int width = LogicalToDeviceUnits(17);
        _cap.SetBounds(Width - width, 0, width, Height);
        _cap.BringToFront();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items.Count == 0) return;

        // The closed edit box paints with ComboBoxEdit set; only highlight inside the dropdown list.
        bool inList = !e.State.HasFlag(DrawItemState.ComboBoxEdit);
        bool selected = inList && e.State.HasFlag(DrawItemState.Selected);

        using (var bg = new SolidBrush(selected ? Theme.NavSelected : Theme.Field))
        {
            e.Graphics.FillRectangle(bg, e.Bounds);
        }
        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }

        var text = Items[e.Index]?.ToString() ?? "";
        var rect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, rect, selected ? Theme.TextBright : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Covers the system drop-arrow area with a themed chevron; clicks toggle the dropdown.</summary>
    private sealed class ArrowCap : Control
    {
        private readonly ThemeComboBox _owner;
        private bool _hover;

        public ArrowCap(ThemeComboBox owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Field;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

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

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            _owner.DroppedDown = !_owner.DroppedDown;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(_hover ? Theme.Hover : Theme.Field))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            float cx = Width / 2f, cy = Height / 2f;
            float s = Math.Min(Width, Height) * 0.24f;
            using var brush = new SolidBrush(_hover ? Theme.TextBright : Theme.TextCaption);
            g.FillPolygon(brush, new[]
            {
                new PointF(cx - s, cy - s / 2),
                new PointF(cx + s, cy - s / 2),
                new PointF(cx, cy + s / 2),
            });
        }
    }
}

/// <summary>ListBox with dark owner-drawn rows and an accent selection bar.</summary>
public class ThemeListBox : ListBox
{
    public ThemeListBox()
    {
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Font = Theme.FontBody;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Font.Height + 8;
        IntegralHeight = false;
        Padding = new Padding(4, 4, 4, 4);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = e.State.HasFlag(DrawItemState.Selected);

        using (var bg = new SolidBrush(selected ? Theme.NavSelected : BackColor))
        {
            e.Graphics.FillRectangle(bg, e.Bounds);
        }
        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }

        var text = Items[e.Index]?.ToString() ?? "";
        var rect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, rect,
            selected ? Theme.TextBright : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
