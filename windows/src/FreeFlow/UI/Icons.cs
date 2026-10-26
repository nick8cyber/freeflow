using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

/// <summary>Vector glyphs drawn on demand (no image assets). All painters take the drawing color.</summary>
public static class Icons
{
    /// <summary>API-key glyph (Providers page).</summary>
    public static void Key(Graphics g, Rectangle r, Color color)
    {
        using var pen = IconPen(g, r, color, 12f);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        g.DrawEllipse(pen, x + w * 0.06f, y + h * 0.30f, w * 0.40f, h * 0.40f);
        g.DrawLine(pen, x + w * 0.46f, y + h * 0.50f, x + w * 0.94f, y + h * 0.50f);
        g.DrawLine(pen, x + w * 0.72f, y + h * 0.50f, x + w * 0.72f, y + h * 0.70f);
        g.DrawLine(pen, x + w * 0.90f, y + h * 0.50f, x + w * 0.90f, y + h * 0.74f);
    }

    /// <summary>Lightning bolt, filled (Shortcuts page).</summary>
    public static void Bolt(Graphics g, Rectangle r, Color color)
    {
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        PointF[] points =
        {
            new(x + w * 0.55f, y + h * 0.05f),
            new(x + w * 0.22f, y + h * 0.55f),
            new(x + w * 0.45f, y + h * 0.55f),
            new(x + w * 0.38f, y + h * 0.95f),
            new(x + w * 0.78f, y + h * 0.42f),
            new(x + w * 0.52f, y + h * 0.42f),
        };
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, points);
    }

    /// <summary>Three sliders (Behavior page).</summary>
    public static void Sliders(Graphics g, Rectangle r, Color color)
    {
        using var pen = IconPen(g, r, color, 11f);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        foreach (var (ty, kx) in new[] { (0.25f, 0.64f), (0.52f, 0.32f), (0.79f, 0.70f) })
        {
            g.DrawLine(pen, x + w * 0.08f, y + h * ty, x + w * 0.92f, y + h * ty);
            var knob = new RectangleF(x + w * kx - w * 0.09f, y + h * ty - h * 0.09f, w * 0.18f, h * 0.18f);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, knob);
        }
    }

    /// <summary>"Aa" word glyph (Vocabulary page).</summary>
    public static void Letters(Graphics g, Rectangle r, Color color) =>
        TextRenderer.DrawText(g, "Aa", Theme.FontBodySemibold, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

    /// <summary>Clock face (History page).</summary>
    public static void Clock(Graphics g, Rectangle r, Color color)
    {
        using var pen = IconPen(g, r, color, 11f);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        g.DrawEllipse(pen, x + w * 0.10f, y + h * 0.10f, w * 0.80f, h * 0.80f);
        g.DrawLine(pen, x + w * 0.50f, y + h * 0.28f, x + w * 0.50f, y + h * 0.52f);
        g.DrawLine(pen, x + w * 0.50f, y + h * 0.52f, x + w * 0.68f, y + h * 0.60f);
    }

    /// <summary>Studio microphone (setup hero and tray icon).</summary>
    public static void Microphone(Graphics g, Rectangle r, Color color)
    {
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;

        var capsule = Rectangle.Round(new RectangleF(x + w * 0.36f, y + h * 0.08f, w * 0.28f, h * 0.42f));
        Theme.FillRounded(g, color, capsule, Math.Max(2, (int)(capsule.Width / 2)));

        using var pen = new Pen(color, Math.Max(1.6f, w / 14f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(pen, x + w * 0.22f, y + h * 0.28f, w * 0.56f, h * 0.52f, 15f, 150f);
        g.DrawLine(pen, x + w * 0.50f, y + h * 0.80f, x + w * 0.50f, y + h * 0.90f);
        g.DrawLine(pen, x + w * 0.32f, y + h * 0.90f, x + w * 0.68f, y + h * 0.90f);
    }

    private static Pen IconPen(Graphics g, Rectangle r, Color color, float divisor)
    {
        return new Pen(color, Math.Max(1.4f, Math.Min(r.Width, r.Height) / divisor))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
    }
}
