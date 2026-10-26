using System.Diagnostics;
using FreeFlow.Core;
using FreeFlow.Services;

namespace FreeFlow.UI;

/// <summary>Drawn microphone glyph used by the setup hero.</summary>
public sealed class MicGlyph : Control
{
    public MicGlyph()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        int size = 56 * DeviceDpi / 96;
        Size = new Size(size, size);
        BackColor = Theme.Window; // opaque: sits on the hero panel (same color)
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Theme.FillRounded(g, Theme.AccentFill, ClientRectangle, LogicalToDeviceUnits(Theme.RadiusCard));
        Theme.StrokeRounded(g, Theme.AccentBorder, ClientRectangle, LogicalToDeviceUnits(Theme.RadiusCard));
        int inset = LogicalToDeviceUnits(11);
        Icons.Microphone(g, new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2), Theme.AccentBright);
    }
}

/// <summary>First-run setup: welcoming hero, required Gemini key, optional Groq fallback key.</summary>
public sealed class SetupForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _geminiKey;
    private readonly TextBox _groqKey;
    private readonly Label _status = new()
    {
        Text = "",
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = Theme.FontCaption,
        ForeColor = Theme.TextCaption,
        Height = 30,
        BackColor = Theme.Window,
    };
    private bool _saved;

    public SetupForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Welcome to FreeFlow";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        Icon = AppIcon.Create();
        ClientSize = new Size(560 * DeviceDpi / 96, 470 * DeviceDpi / 96);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Window,
            Margin = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ---- hero: glyph, name, one-line pitch
        int S(int value) => value * DeviceDpi / 96;
        var hero = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Window,
            Margin = Padding.Empty,
            Padding = new Padding(0, S(26), 0, S(4)),
        };
        var glyph = new MicGlyph { Top = S(26) };
        var title = new Label
        {
            Text = "FreeFlow",
            AutoSize = true,
            Font = Theme.FontHeading,
            ForeColor = Theme.TextBright,
            BackColor = Theme.Window,
            Top = S(90),
        };
        var pitch = new Label
        {
            Text = "Dictate anywhere with AI voice input.\nHold the shortcut to talk — polished text is pasted into the focused app.",
            AutoSize = true,
            Font = Theme.FontBody,
            ForeColor = Theme.TextCaption,
            BackColor = Theme.Window,
            TextAlign = ContentAlignment.TopCenter,
            MaximumSize = new Size(S(470), 0),
            Top = S(122),
        };
        hero.Controls.Add(glyph);
        hero.Controls.Add(title);
        hero.Controls.Add(pitch);
        hero.Layout += (_, _) =>
        {
            foreach (Control child in hero.Controls)
            {
                child.Left = (hero.ClientSize.Width - child.Width) / 2;
            }
        };

        // ---- key cards
        var cards = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Window,
            Margin = Padding.Empty,
            Padding = new Padding(S(28), S(14), S(28), 0),
        };

        var geminiCard = new Card("Google Gemini", badge: new Badge("Primary · required"))
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, Theme.M),
        };
        Grids.Span(geminiCard.Body,
            MakeLink("https://aistudio.google.com/apikey", "Get a free key — aistudio.google.com/apikey"), 0, 6);
        var geminiHost = ThemeInputs.Text(out _geminiKey, "Paste your Gemini API key", password: true);
        Grids.Span(geminiCard.Body, InputRow(geminiHost, TestButton("Test", async () => await TestGemini())), 2, 2);

        var groqCard = new Card("Groq", badge: new Badge("Fallback · optional", Badge.BadgeKind.Neutral))
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, Theme.M),
        };
        Grids.Span(groqCard.Body,
            MakeLink("https://console.groq.com/keys", "Get a free key — console.groq.com/keys"), 0, 6);
        var groqHost = ThemeInputs.Text(out _groqKey, "Paste your Groq API key (optional)", password: true);
        Grids.Span(groqCard.Body, InputRow(groqHost, TestButton("Test", async () => await TestGroq())), 2, 2);
        // Dock=Top docks the last-added control first, so add Groq before Gemini
        // to land Gemini (primary) on top.
        cards.Controls.Add(groqCard);
        cards.Controls.Add(geminiCard);

        // ---- status + actions
        var statusRow = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Window,
            Margin = Padding.Empty,
            Padding = new Padding(S(28), S(8), S(28), 0),
        };
        _status.Dock = DockStyle.Fill;
        statusRow.Controls.Add(_status);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Window,
            Margin = Padding.Empty,
            Padding = new Padding(S(28), S(10), S(28), S(24)),
        };
        var save = new ThemeButton(ThemeButtonKind.Primary, "Start using FreeFlow");
        save.Margin = new Padding(Theme.S, 0, 0, 0);
        save.Click += (_, _) => Save();
        var exit = new ThemeButton(ThemeButtonKind.Ghost, "Exit");
        exit.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(save); // primary sits rightmost
        buttons.Controls.Add(exit);

        root.Controls.Add(hero);
        root.Controls.Add(cards);
        root.Controls.Add(statusRow);
        root.Controls.Add(buttons);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window }); // stretch row

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = exit;
        Shown += (_, _) => _geminiKey.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private static TableLayoutPanel InputRow(HostPanel host, ThemeButton test)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        host.Margin = new Padding(0, 2, 0, 2);
        host.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        test.Margin = new Padding(Theme.S, 2, 0, 2);
        row.Controls.Add(host, 0, 0);
        row.Controls.Add(test, 1, 0);
        return row;
    }

    private static ThemeButton TestButton(string text, Func<Task> test)
    {
        var button = new ThemeButton(ThemeButtonKind.Neutral, text);
        button.Click += async (_, _) => await test();
        return button;
    }

    private static LinkLabel MakeLink(string url, string text)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = Theme.Link,
            ActiveLinkColor = Theme.LinkHover,
            TabStop = true,
            Margin = new Padding(0),
        };
        link.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return link;
    }

    private async Task TestGemini()
    {
        _status.Text = "Testing Gemini…";
        _status.ForeColor = Theme.TextCaption;
        try
        {
            var client = new GeminiClient(_settings.GeminiBaseUrl, _geminiKey.Text.Trim());
            await client.ValidateKeyAsync(CancellationToken.None);
            _status.Text = "Gemini key works.";
            _status.ForeColor = Theme.Accent;
        }
        catch (ProviderException ex)
        {
            _status.Text = "Gemini: " + FriendlyErrors.Describe(ex);
            _status.ForeColor = Theme.Danger;
        }
    }

    private async Task TestGroq()
    {
        _status.Text = "Testing Groq…";
        _status.ForeColor = Theme.TextCaption;
        try
        {
            var client = new GroqClient(_settings.GroqBaseUrl, _groqKey.Text.Trim());
            await client.ValidateKeyAsync(CancellationToken.None);
            _status.Text = "Groq key works.";
            _status.ForeColor = Theme.Accent;
        }
        catch (ProviderException ex)
        {
            _status.Text = "Groq: " + FriendlyErrors.Describe(ex);
            _status.ForeColor = Theme.Danger;
        }
    }

    private void Save()
    {
        var gemini = _geminiKey.Text.Trim();
        var groq = _groqKey.Text.Trim();
        if (gemini.Length == 0 && groq.Length == 0)
        {
            _status.Text = "Enter a Gemini API key (primary) — or a Groq key to continue with the fallback only.";
            _status.ForeColor = Theme.Danger;
            return;
        }
        if (gemini.Length == 0)
        {
            var proceed = MessageBox.Show(
                "Gemini is the primary provider; without it, dictation quality and reliability depend entirely on Groq.\n\nContinue with Groq only?",
                "No Gemini key",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (proceed != DialogResult.Yes) return;
        }
        _settings.GeminiApiKey = gemini;
        _settings.GroqApiKey = groq;
        SettingsStore.Save(_settings);
        _saved = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!_saved && e.CloseReason == CloseReason.UserClosing)
        {
            DialogResult = DialogResult.Cancel;
        }
    }
}
