using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FreeFlow.UI;

/// <summary>
/// The FreeFlow design system: dark palette, typography scale, spacing scale,
/// and shared drawing helpers. Every custom control and form pulls its look
/// from here so the whole app reads as one product.
/// </summary>
public static class Theme
{
    // ------------------------------------------------------------------ palette

    /// <summary>App background (window content area).</summary>
    public static readonly Color Window = Color.FromArgb(24, 24, 28);          // #18181C

    /// <summary>Chrome surfaces: sidebar, footer, menus.</summary>
    public static readonly Color Chrome = Color.FromArgb(19, 19, 24);          // #131318

    /// <summary>Raised surfaces: section cards.</summary>
    public static readonly Color Card = Color.FromArgb(30, 30, 36);            // #1E1E24

    public static readonly Color CardBorder = Color.FromArgb(47, 47, 55);      // #2F2F37
    public static readonly Color Divider = Color.FromArgb(38, 38, 45);         // #26262D

    /// <summary>Input / field background.</summary>
    public static readonly Color Field = Color.FromArgb(37, 37, 44);           // #25252C
    public static readonly Color FieldBorder = Color.FromArgb(64, 64, 74);     // #40404A
    public static readonly Color FieldBorderHover = Color.FromArgb(84, 84, 95);

    public static readonly Color Text = Color.Gainsboro;
    public static readonly Color TextBright = Color.FromArgb(240, 240, 244);
    public static readonly Color TextCaption = Color.FromArgb(139, 139, 151);
    public static readonly Color TextDisabled = Color.FromArgb(98, 98, 108);

    /// <summary>Accent: primary actions, success states, live recording.</summary>
    public static readonly Color Accent = Color.MediumSeaGreen;
    public static readonly Color AccentBright = Color.FromArgb(128, 205, 158);
    public static readonly Color AccentFill = Color.FromArgb(28, 58, 42);
    public static readonly Color AccentBorder = Color.FromArgb(58, 100, 78);
    public static readonly Color OnAccent = Color.FromArgb(10, 26, 18);

    public static readonly Color Link = Color.FromArgb(128, 205, 158);
    public static readonly Color LinkHover = Color.FromArgb(160, 224, 184);

    /// <summary>Danger: errors, destructive actions.</summary>
    public static readonly Color Danger = Color.Tomato;
    public static readonly Color DangerStrong = Color.FromArgb(206, 70, 54);
    public static readonly Color DangerFill = Color.FromArgb(52, 30, 27);
    public static readonly Color DangerBorder = Color.FromArgb(106, 52, 46);

    public static readonly Color Hover = Color.FromArgb(48, 48, 56);
    public static readonly Color Pressed = Color.FromArgb(58, 58, 67);
    public static readonly Color NavSelected = Color.FromArgb(42, 42, 50);
    public static readonly Color LevelTrack = Color.FromArgb(54, 54, 62);

    // ------------------------------------------------------------------ spacing

    public const int XS = 4;
    public const int S = 8;
    public const int M = 12;
    public const int L = 16;
    public const int XL = 24;

    public const int RadiusCard = 10;
    public const int RadiusControl = 6;

    public const int ButtonHeight = 30;
    /// <summary>Vertical padding added around a single-line input inside its host.</summary>
    public const int FieldVPad = 8;

    // ------------------------------------------------------------------ typography

    public static readonly Font FontHeading = new("Segoe UI Semibold", 15f);
    public static readonly Font FontPageTitle = new("Segoe UI Semibold", 12.25f);
    public static readonly Font FontSection = new("Segoe UI Semibold", 10f);
    public static readonly Font FontNav = new("Segoe UI", 9.75f);
    public static readonly Font FontBody = new("Segoe UI", 9f);
    public static readonly Font FontBodySemibold = new("Segoe UI Semibold", 9f);
    public static readonly Font FontCaption = new("Segoe UI", 8.25f);
    public static readonly Font FontOverlay = new("Segoe UI", 9.25f);

    /// <summary>Standard body label (anchor Left in a TableLayoutPanel row = vertically centered).</summary>
    public static Label BodyLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = FontBody,
            ForeColor = Text,
        };
        int pad = label.LogicalToDeviceUnits(M);
        label.Margin = new Padding(0, 2, pad, 2);
        return label;
    }

    public static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = FontSection,
        ForeColor = TextBright,
        Margin = new Padding(0),
    };

    public static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = FontCaption,
        ForeColor = TextCaption,
        Margin = new Padding(0),
    };

    // ------------------------------------------------------------------ drawing

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(new Rectangle(r.X, r.Y, 1, 1)); return path; }
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, Rectangle r, int radius)
    {
        using var brush = new SolidBrush(color);
        using var path = RoundedPath(r, radius);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, Color color, Rectangle r, int radius, float width = 1f)
    {
        using var pen = new Pen(color, width);
        using var path = RoundedPath(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
        g.DrawPath(pen, path);
    }

    // ------------------------------------------------------------------ platform chrome

    /// <summary>Requests a dark title bar from DWM (best effort, silently ignored on old Windows).</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            var on = 1;
            // Attribute 20 is the documented DWMWA_USE_IMMERSIVE_DARK_MODE; 19 worked on early builds.
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }
        catch (Exception)
        {
            // purely cosmetic; never let it break form creation
        }
    }

    /// <summary>Applies the dark palette to a context menu.</summary>
    public static void StyleMenu(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
        menu.BackColor = Chrome;
        menu.ForeColor = Text;
        menu.ShowImageMargin = false;
        menu.Font = FontBody;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>Dark color table backing <see cref="Theme.StyleMenu"/>.</summary>
internal sealed class DarkMenuColors : ProfessionalColorTable
{
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    public override Color ToolStripDropDownBackground => Theme.Chrome;
    public override Color ToolStripBorder => Theme.Chrome;
    public override Color ImageMarginGradientBegin => Theme.Chrome;
    public override Color ImageMarginGradientMiddle => Theme.Chrome;
    public override Color ImageMarginGradientEnd => Theme.Chrome;
    public override Color MenuBorder => Theme.CardBorder;
    public override Color MenuItemBorder => Transparent;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color MenuItemPressedGradientBegin => Theme.Chrome;
    public override Color MenuItemPressedGradientEnd => Theme.Chrome;
    public override Color SeparatorDark => Theme.Divider;
    public override Color SeparatorLight => Theme.Divider;
    public override Color CheckBackground => Transparent;
    public override Color CheckSelectedBackground => Transparent;
    public override Color CheckPressedBackground => Transparent;
    public override Color RaftingContainerGradientBegin => Theme.Chrome;
    public override Color RaftingContainerGradientEnd => Theme.Chrome;
    public override Color OverflowButtonGradientBegin => Theme.Chrome;
    public override Color OverflowButtonGradientEnd => Theme.Chrome;
}
