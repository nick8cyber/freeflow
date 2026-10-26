using System.Drawing.Drawing2D;
using FreeFlow.Core;
using FreeFlow.Services;

namespace FreeFlow.UI;

/// <summary>
/// Settings window: sidebar navigation (Providers, Shortcuts, Behavior,
/// Vocabulary, History) over scrollable card pages, with a footer action bar.
/// The section builders live in SettingsForm.Pages.cs.
/// </summary>
public sealed partial class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly Action _hotkeysChanged;

    // Provider fields
    private TextBox _geminiKey = null!;
    private ThemeComboBox _geminiModel = null!;
    private ThemeComboBox _geminiCleanupModel = null!;
    private TextBox _groqKey = null!;
    private ThemeComboBox _groqTranscribeModel = null!;
    private ThemeComboBox _groqModel = null!;
    private ThemeComboBox _groqFallbackModel = null!;
    private Label _geminiTestResult = null!;
    private Label _groqTestResult = null!;

    // Shortcut fields
    private HotkeyBox _holdBox = null!;
    private HotkeyBox _toggleBox = null!;
    private HotkeyBox _pasteAgainBox = null!;
    private CheckBox _suppressKey = null!;

    // Behavior fields
    private CheckBox _contextEnabled = null!;
    private ComboBox _micDevice = null!;
    private CheckBox _realtimeStreaming = null!;
    private CheckBox _cleanupGroq = null!;
    private CheckBox _includeScreenshot = null!;
    private CheckBox _preserveClipboard = null!;
    private CheckBox _keepHistory = null!;
    private CheckBox _keepInClipboardHistory = null!;
    private CheckBox _pressEnter = null!;
    private CheckBox _preserveExactWording = null!;
    private CheckBox _commandMode = null!;
    private ComboBox _transcriptionLanguage = null!;
    private ComboBox _outputLanguage = null!;
    private NumericUpDown _transcriptionTimeout = null!;
    private NumericUpDown _postProcessingTimeout = null!;
    private NumericUpDown _contextTimeout = null!;

    // Vocabulary/prompt fields
    private TextBox _vocabulary = null!;
    private TextBox _systemPrompt = null!;
    private TextBox _contextPrompt = null!;
    private TextBox _macroCommand = null!;
    private TextBox _macroPayload = null!;
    private ListBox _macroList = null!;

    private readonly Panel _content = new();
    private readonly List<NavButton> _navButtons = new();
    private readonly List<Panel> _pages = new();

    public SettingsForm(AppSettings settings, HistoryStore history, Action hotkeysChanged)
    {
        _settings = settings;
        _history = history;
        _hotkeysChanged = hotkeysChanged;

        Text = "FreeFlow Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(620, 480);
        Size = Scale(880, 640);
        BackColor = Theme.Window;
        Icon = AppIcon.Create();

        BuildShell();
        BuildProvidersPage();
        BuildShortcutsPage();
        BuildBehaviorPage();
        BuildVocabularyPage();
        BuildHistoryPage();
        SelectPage(0);

        // Show the stored keys (masked) so a routine Save cannot silently wipe them
        // and the Test buttons validate what is actually configured.
        _geminiKey.Text = _settings.GeminiApiKey;
        _groqKey.Text = _settings.GroqApiKey;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private Size Scale(int width, int height) => new(width * DeviceDpi / 96, height * DeviceDpi / 96);
    private int Scale(int value) => value * DeviceDpi / 96;

    // ------------------------------------------------------------------ shell

    private void BuildShell()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = Scale(208),
            BackColor = Theme.Chrome,
            Padding = new Padding(Scale(Theme.M), Scale(20), Scale(Theme.M), Scale(Theme.M)),
        };

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty,
            Padding = new Padding(Scale(Theme.S), 0, 0, Scale(Theme.L)),
        };
        header.Controls.Add(new Label
        {
            Text = "FreeFlow",
            AutoSize = true,
            Font = Theme.FontHeading,
            ForeColor = Theme.TextBright,
            Margin = Padding.Empty,
        });
        header.Controls.Add(new Label
        {
            Text = "Version " + VersionText,
            AutoSize = true,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextCaption,
            Margin = new Padding(1, 2, 0, 0),
        });

        var navItems = new (string Title, Action<Graphics, Rectangle, Color> Icon)[]
        {
            ("Providers", Icons.Key),
            ("Shortcuts", Icons.Bolt),
            ("Behavior", Icons.Sliders),
            ("Vocabulary", Icons.Letters),
            ("History", Icons.Clock),
        };
        // Dock=Top controls dock from the last-added backwards, so add nav buttons
        // bottom-up; the header is added last and stays on top. _navButtons stays in
        // page order (index 0 = Providers) so SelectPage's indexes line up.
        for (int i = navItems.Length - 1; i >= 0; i--)
        {
            var index = i;
            var button = new NavButton(navItems[i].Title, (g, r, c) => navItems[index].Icon(g, r, c));
            button.Click += (_, _) => SelectPage(index);
            _navButtons.Insert(0, button);
            sidebar.Controls.Add(button);
        }
        sidebar.Controls.Add(header);

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Window;
        _content.Padding = new Padding(Scale(18), Scale(16), Scale(18), Scale(Theme.M));

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Scale(52),
            BackColor = Theme.Chrome,
        };
        footer.Paint += (_, e) => e.Graphics.DrawLine(
            new Pen(Theme.Divider), 0, 0, footer.Width, 0);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(Scale(Theme.L), Scale(11), Scale(Theme.L), Scale(11)),
        };
        var save = new ThemeButton(ThemeButtonKind.Primary, "Save");
        save.Click += (_, _) => Save();
        var close = new ThemeButton(ThemeButtonKind.Neutral, "Close");
        close.Click += (_, _) => Close();
        save.Margin = new Padding(Theme.S, 0, 0, 0);
        actions.Controls.Add(save);   // primary sits rightmost
        actions.Controls.Add(close);
        footer.Controls.Add(actions);

        Controls.Add(_content);
        Controls.Add(sidebar);
        Controls.Add(footer);

        CancelButton = close;
    }

    private static string VersionText
    {
        get
        {
            // ProductVersion can carry a commit suffix ("1.0.0+4c6a..."); show the clean semver.
            var core = (Application.ProductVersion ?? "").Split('+')[0];
            return Version.TryParse(core, out var version) ? "v" + version.ToString(3) : "v" + core;
        }
    }

    private void SelectPage(int index)
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visible = i == index;
            _navButtons[i].Selected = i == index;
        }
    }

    // ------------------------------------------------------------------ save

    private void Save()
    {
        _settings.GeminiApiKey = _geminiKey.Text.Trim();
        _settings.TranscriptionModelGemini = Combo(_geminiModel);
        _settings.PostProcessingModelGemini = Combo(_geminiCleanupModel);
        _settings.GroqApiKey = _groqKey.Text.Trim();
        _settings.TranscriptionModelGroq = Combo(_groqTranscribeModel);
        _settings.PostProcessingModelGroq = Combo(_groqModel);
        _settings.PostProcessingFallbackModelGroq = Combo(_groqFallbackModel);

        _settings.HoldHotkeyVk = _holdBox.Binding.VirtualKey;
        _settings.HoldHotkeyMods = _holdBox.Binding.Mods;
        _settings.ToggleHotkeyVk = _toggleBox.Binding.VirtualKey;
        _settings.ToggleHotkeyMods = _toggleBox.Binding.Mods;
        _settings.PasteAgainHotkeyVk = _pasteAgainBox.Binding.VirtualKey;
        _settings.PasteAgainHotkeyMods = _pasteAgainBox.Binding.Mods;
        _settings.SuppressBoundKey = _suppressKey.Checked;

        _settings.ContextEnabled = _contextEnabled.Checked;
        _settings.MicDeviceName =
            _micDevice.SelectedItem is string mic && mic != MicDeviceResolver.SystemDefaultLabel ? mic : "";
        _settings.RealtimeStreamingEnabled = _realtimeStreaming.Checked;
        _settings.CleanupGroqTranscripts = _cleanupGroq.Checked;
        _settings.IncludeScreenshot = _includeScreenshot.Checked;
        _settings.PreserveClipboard = _preserveClipboard.Checked;
        _settings.KeepHistory = _keepHistory.Checked;
        _settings.KeepInClipboardHistory = _keepInClipboardHistory.Checked;
        _settings.PressEnterEnabled = _pressEnter.Checked;
        _settings.PreserveExactWording = _preserveExactWording.Checked;
        _settings.CommandModeEnabled = _commandMode.Checked;
        _settings.TranscriptionLanguage = TranscriptionCode(_transcriptionLanguage.SelectedItem as string);
        _settings.OutputLanguage = _outputLanguage.SelectedItem as string ?? "";
        _settings.TranscriptionTimeoutSeconds = (int)_transcriptionTimeout.Value;
        _settings.PostProcessingTimeoutSeconds = (int)_postProcessingTimeout.Value;
        _settings.ContextTimeoutSeconds = (int)_contextTimeout.Value;

        _settings.CustomVocabulary = _vocabulary.Text;
        _settings.CustomSystemPrompt = _systemPrompt.Text;
        _settings.CustomContextPrompt = _contextPrompt.Text;

        SettingsStore.Save(_settings);
        _hotkeysChanged();
        Close();
    }

    private static string Combo(ComboBox combo) => combo.SelectedItem as string ?? "";

    // ------------------------------------------------------------- providers

    private async Task TestGemini()
    {
        _geminiTestResult.Text = "Testing…";
        _geminiTestResult.ForeColor = Theme.TextCaption;
        try
        {
            var client = new GeminiClient(
                _settings.GeminiBaseUrl,
                _geminiKey.Text.Trim());
            await client.ValidateKeyAsync(CancellationToken.None);
            _geminiTestResult.Text = "OK — the Gemini key works.";
            _geminiTestResult.ForeColor = Theme.Accent;
        }
        catch (ProviderException ex)
        {
            _geminiTestResult.Text = ex.Kind == ProviderFailureKind.Http ? ex.Message : FriendlyErrors.Describe(ex);
            _geminiTestResult.ForeColor = Theme.Danger;
        }
    }

    private async Task TestGroq()
    {
        _groqTestResult.Text = "Testing…";
        _groqTestResult.ForeColor = Theme.TextCaption;
        try
        {
            var client = new GroqClient(
                _settings.GroqBaseUrl,
                _groqKey.Text.Trim());
            await client.ValidateKeyAsync(CancellationToken.None);
            _groqTestResult.Text = "OK — the Groq key works.";
            _groqTestResult.ForeColor = Theme.Accent;
        }
        catch (ProviderException ex)
        {
            _groqTestResult.Text = ex.Kind == ProviderFailureKind.Http ? ex.Message : FriendlyErrors.Describe(ex);
            _groqTestResult.ForeColor = Theme.Danger;
        }
    }

    // ------------------------------------------------------------- languages

    private static ThemeComboBox TranscriptionLanguageCombo(string current)
    {
        var combo = new ThemeComboBox();
        foreach (var (_, display) in AppSettings.TranscriptionLanguages) combo.Items.Add(display);
        var index = Array.FindIndex(AppSettings.TranscriptionLanguages, t => t.Code == current);
        combo.SelectedIndex = index >= 0 ? index : 0;
        return combo;
    }

    private static ThemeComboBox OutputLanguageCombo(string current)
    {
        var combo = new ThemeComboBox();
        foreach (var name in AppSettings.OutputLanguageNames) combo.Items.Add(name);
        var index = Array.IndexOf(AppSettings.OutputLanguageNames, current);
        combo.SelectedIndex = index >= 0 ? index : 0;
        return combo;
    }

    /// <summary>Maps the selected display name back to its ISO code (transcription language stores codes).</summary>
    private static string TranscriptionCode(string? display) =>
        AppSettings.TranscriptionLanguages.FirstOrDefault(t => t.Display == (display ?? "Auto-detect")).Code;
}
