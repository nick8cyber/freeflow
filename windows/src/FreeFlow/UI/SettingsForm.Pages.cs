using FreeFlow.Core;
using FreeFlow.Services;

namespace FreeFlow.UI;

/// <summary>Section builders for the settings window (one method per sidebar page).</summary>
public sealed partial class SettingsForm
{
    // ------------------------------------------------------------- page shell

    /// <summary>Creates a scrollable page with a title/caption header and a card stack; returns the stack.</summary>
    private TableLayoutPanel MakePage(string title, string caption)
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Window,
            Visible = false,
        };
        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int titleRow = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.FontPageTitle,
            ForeColor = Theme.TextBright,
            Margin = new Padding(1, 0, 0, 2),
            UseMnemonic = false, // titles like "Vocabulary & Prompts" must show the ampersand
        };
        stack.Controls.Add(heading, 0, titleRow);

        int captionRow = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var sub = new Label
        {
            Text = caption,
            AutoSize = true,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextCaption,
            Margin = new Padding(1, 0, 0, Theme.L),
        };
        stack.Controls.Add(sub, 0, captionRow);

        page.Controls.Add(stack);
        _content.Controls.Add(page);
        _pages.Add(page);
        return stack;
    }

    /// <summary>Appends a card to a page stack (cards dock to the top of their full-width cell).</summary>
    private static void AddCard(TableLayoutPanel stack, Card card)
    {
        card.Dock = DockStyle.Top;
        int row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(card, 0, row);
    }

    private static Label ResultLabel() => new()
    {
        Text = "",
        AutoSize = false,
        AutoEllipsis = true,
        Height = 17,
        Font = Theme.FontCaption,
        ForeColor = Theme.TextCaption,
    };

    // ------------------------------------------------------------- providers

    private static readonly string[] GeminiVoiceModels =
        { "gemini-3.5-transcribe", "gemini-3.5-transcribe-live", "gemini-2.5-flash" };

    private static readonly string[] GeminiCleanupModels =
        { "gemini-3.5-flash-lite", "gemini-3.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-flash" };

    private static readonly string[] GroqTranscribeModels = { "whisper-large-v3", "whisper-large-v3-turbo" };

    private static readonly string[] GroqCleanupModels =
        { "openai/gpt-oss-20b", "openai/gpt-oss-120b", "qwen/qwen3.6-27b", "groq/compound", "groq/compound-mini" };

    /// <summary>Curated DropDownList; the stored value always stays selectable (custom values never break).</summary>
    private static ThemeComboBox ModelCombo(string[] catalog, string current)
    {
        var combo = new ThemeComboBox();
        foreach (var model in catalog) combo.Items.Add(model);
        if (!string.IsNullOrWhiteSpace(current) && !catalog.Contains(current)) combo.Items.Add(current);
        combo.SelectedItem = string.IsNullOrWhiteSpace(current) ? catalog[0] : current;
        return combo;
    }

    /// <summary>Capture device picker: System default plus every live MME input, so a stored
    /// name for a temporarily absent device stays selectable instead of breaking the form.</summary>
    private static ThemeComboBox MicDeviceCombo(string current)
    {
        var combo = new ThemeComboBox();
        combo.Items.Add(MicDeviceResolver.SystemDefaultLabel);
        foreach (var name in MicDeviceResolver.DeviceNames())
            if (!combo.Items.Contains(name)) combo.Items.Add(name);
        if (!string.IsNullOrWhiteSpace(current) && !combo.Items.Contains(current)) combo.Items.Add(current);
        combo.SelectedItem = string.IsNullOrWhiteSpace(current) ? MicDeviceResolver.SystemDefaultLabel : current;
        return combo;
    }

    private void BuildProvidersPage()
    {
        var stack = MakePage("Providers", "API keys and models for voice input and cleanup.");

        var gemini = new Card("Google Gemini", badge: new Badge("Primary · required"));
        Grids.Row(gemini.Body, "API key",
            ThemeInputs.Text(out _geminiKey, "Paste your Gemini API key", password: true));
        Grids.Row(gemini.Body, "Voice input model",
            _geminiModel = ModelCombo(GeminiVoiceModels, _settings.TranscriptionModelGemini));
        Grids.Span(gemini.Body, Theme.Caption(
            "live = realtime streaming (Live API); use gemini-3.5-transcribe for the default record-then-transcribe flow."),
            0, Theme.S);
        Grids.Row(gemini.Body, "Cleanup model",
            _geminiCleanupModel = ModelCombo(GeminiCleanupModels, _settings.PostProcessingModelGemini));

        var geminiTest = new ThemeButton(ThemeButtonKind.Neutral, "Test connection");
        geminiTest.Click += async (_, _) => await TestGemini();
        _geminiTestResult = ResultLabel();
        Grids.Row(gemini.Body, "", geminiTest, 6, 2);
        Grids.Span(gemini.Body, _geminiTestResult, 0, 2);
        Grids.Span(gemini.Body, Theme.Caption("Key is stored encrypted (DPAPI) on this device."), 0, 2);
        AddCard(stack, gemini);

        var groq = new Card("Groq", "Optional automatic fallback when Gemini fails.",
            new Badge("Fallback · optional", Badge.BadgeKind.Neutral));
        Grids.Row(groq.Body, "API key",
            ThemeInputs.Text(out _groqKey, "Paste your Groq API key", password: true));
        Grids.Row(groq.Body, "Transcription (audio · fallback)",
            _groqTranscribeModel = ModelCombo(GroqTranscribeModels, _settings.TranscriptionModelGroq));
        Grids.Row(groq.Body, "Cleanup (text · fallback)",
            _groqModel = ModelCombo(GroqCleanupModels, _settings.PostProcessingModelGroq));
        Grids.Row(groq.Body, "Cleanup fallback model",
            _groqFallbackModel = ModelCombo(GroqCleanupModels, _settings.PostProcessingFallbackModelGroq));

        var groqTest = new ThemeButton(ThemeButtonKind.Neutral, "Test connection");
        groqTest.Click += async (_, _) => await TestGroq();
        _groqTestResult = ResultLabel();
        Grids.Row(groq.Body, "", groqTest, 6, 2);
        Grids.Span(groq.Body, _groqTestResult, 0, 2);
        Grids.Span(groq.Body, Theme.Caption("Key is stored encrypted (DPAPI) on this device."), 0, 2);
        AddCard(stack, groq);
    }

    // ------------------------------------------------------------- shortcuts

    private void BuildShortcutsPage()
    {
        var stack = MakePage("Shortcuts", "Global keys — they work in any app.");

        var dictation = new Card("Dictation");
        Grids.Row(dictation.Body, "Hold to talk",
            ThemeInputs.Wrap(_holdBox = new HotkeyBox(new HotkeyBinding(_settings.HoldHotkeyVk, _settings.HoldHotkeyMods))));
        Grids.Row(dictation.Body, "Toggle (tap)",
            ThemeInputs.Wrap(_toggleBox = new HotkeyBox(new HotkeyBinding(_settings.ToggleHotkeyVk, _settings.ToggleHotkeyMods))));
        Grids.Row(dictation.Body, "Paste again",
            ThemeInputs.Wrap(_pasteAgainBox = new HotkeyBox(new HotkeyBinding(_settings.PasteAgainHotkeyVk, _settings.PasteAgainHotkeyMods))));
        Grids.Span(dictation.Body, Theme.Caption(
            "Click a field and press the desired combination. Esc, Delete, or Backspace unbinds the shortcut."), 0, 2);
        AddCard(stack, dictation);

        var options = new Card("Options");
        _suppressKey = Grids.Check(
            "Suppress the bound key so it does nothing else (recommended for Caps Lock)",
            _settings.SuppressBoundKey);
        Grids.Span(options.Body, _suppressKey, 4, 4);
        Grids.Span(options.Body, Theme.Caption(
            "Caps Lock is the default hold key (the Windows analog of the Mac Fn key). " +
            "Ctrl+Caps Lock toggles dictation; press-and-release then press again to stop."), 0, 2);
        AddCard(stack, options);
    }

    // ------------------------------------------------------------- behavior

    private void BuildBehaviorPage()
    {
        var stack = MakePage("Behavior", "How dictation captures context and delivers text.");

        var microphone = new Card("Microphone");
        _micDevice = MicDeviceCombo(_settings.MicDeviceName);
        Grids.Row(microphone.Body, "Input device", _micDevice);
        Grids.Span(microphone.Body, Theme.Caption(
            "System default follows Windows — Bluetooth headsets can hijack it mid-dictation. Pin a device to keep dictation stable."),
            0, Theme.S);
        _realtimeStreaming = Grids.Check(
            "Use Gemini realtime streaming (Live) — transcript appears as you speak, own free-tier quota",
            _settings.RealtimeStreamingEnabled);
        Grids.Span(microphone.Body, _realtimeStreaming, 2, 2);
        _cleanupGroq = Grids.Check(
            "Clean up Groq-mode transcripts with an LLM (Google transcripts always paste as-is)",
            _settings.CleanupGroqTranscripts);
        Grids.Span(microphone.Body, _cleanupGroq, 2, 4);
        AddCard(stack, microphone);

        var context = new Card("Context-aware cleanup");
        _contextEnabled = Grids.Check("Read foreground app name and window title", _settings.ContextEnabled);
        Grids.Span(context.Body, _contextEnabled, 4, 2);
        _includeScreenshot = Grids.Check(
            "Include a screenshot of the foreground window (more private: off)", _settings.IncludeScreenshot);
        Grids.Span(context.Body, _includeScreenshot, 2, 4);
        AddCard(stack, context);

        var text = new Card("Text output");
        _pressEnter = Grids.Check(
            "Spoken \"press enter\" command presses Enter after paste", _settings.PressEnterEnabled);
        Grids.Span(text.Body, _pressEnter, 4, 2);
        _preserveClipboard = Grids.Check(
            "Restore previous clipboard contents after pasting", _settings.PreserveClipboard);
        Grids.Span(text.Body, _preserveClipboard, 2, 2);
        _preserveExactWording = Grids.Check("Preserve exact wording (skip cleanup)", _settings.PreserveExactWording);
        Grids.Span(text.Body, _preserveExactWording, 2, 2);
        _commandMode = Grids.Check(
            "Edit Mode: transform selected text with a spoken command", _settings.CommandModeEnabled);
        Grids.Span(text.Body, _commandMode, 2, 4);
        AddCard(stack, text);

        var storage = new Card("Storage");
        _keepHistory = Grids.Check("Keep a local dictation history (last 20 entries)", _settings.KeepHistory);
        Grids.Span(storage.Body, _keepHistory, 4, 2);
        _keepInClipboardHistory = Grids.Check(
            "Keep dictation in clipboard history (default: hidden)", _settings.KeepInClipboardHistory);
        Grids.Span(storage.Body, _keepInClipboardHistory, 2, 4);
        AddCard(stack, storage);

        var languages = new Card("Languages");
        Grids.Row(languages.Body, "Spoken language",
            _transcriptionLanguage = TranscriptionLanguageCombo(_settings.TranscriptionLanguage));
        Grids.Row(languages.Body, "Output language",
            _outputLanguage = OutputLanguageCombo(_settings.OutputLanguage));
        AddCard(stack, languages);

        var timeouts = new Card("Timeouts (seconds)");
        _transcriptionTimeout = NumericRow(timeouts.Body, "Transcription", _settings.TranscriptionTimeoutSeconds);
        _postProcessingTimeout = NumericRow(timeouts.Body, "Cleanup", _settings.PostProcessingTimeoutSeconds);
        _contextTimeout = NumericRow(timeouts.Body, "Context", _settings.ContextTimeoutSeconds);
        AddCard(stack, timeouts);
    }

    private static NumericUpDown NumericRow(TableLayoutPanel grid, string label, int value)
    {
        var host = ThemeInputs.Numeric(out var numeric, 5, 600, value);
        Grids.Row(grid, label, host);
        return numeric;
    }

    // ------------------------------------------------------------- vocabulary

    private void BuildVocabularyPage()
    {
        var stack = MakePage("Vocabulary & Prompts", "Custom words, macros, and prompt overrides.");

        var vocabulary = new Card("Custom vocabulary", "Comma-separated names and jargon the transcription should prefer.");
        Grids.Span(vocabulary.Body,
            ThemeInputs.Multiline(out _vocabulary, _settings.CustomVocabulary, 76), 2, 2);
        AddCard(stack, vocabulary);

        var systemPrompt = new Card("Cleanup system prompt", "Empty = FreeFlow default.");
        Grids.Span(systemPrompt.Body,
            ThemeInputs.Multiline(out _systemPrompt, _settings.CustomSystemPrompt, 110), 2, 2);
        AddCard(stack, systemPrompt);

        var contextPrompt = new Card("Context prompt", "Empty = FreeFlow default.");
        Grids.Span(contextPrompt.Body,
            ThemeInputs.Multiline(out _contextPrompt, _settings.CustomContextPrompt, 60), 2, 2);
        AddCard(stack, contextPrompt);

        var macros = new Card("Voice macros", "Spoken command → pasted text. Select an entry to edit it.");
        var macroListBox = new ThemeListBox { Height = 108 };
        _macroList = macroListBox;
        Grids.Span(macros.Body, ThemeInputs.List(macroListBox, 110), 2, 4);

        var pair = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var commandHost = ThemeInputs.Text(out _macroCommand, "spoken command");
        commandHost.Margin = new Padding(0, 2, Theme.XS, 2);
        commandHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var payloadHost = ThemeInputs.Text(out _macroPayload, "pasted payload");
        payloadHost.Margin = new Padding(Theme.XS, 2, 0, 2);
        payloadHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        pair.Controls.Add(commandHost, 0, 0);
        pair.Controls.Add(payloadHost, 1, 0);
        Grids.Span(macros.Body, pair);

        var macroButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        var add = new ThemeButton(ThemeButtonKind.Primary, "Add / update");
        add.Margin = new Padding(0, 4, Theme.S, 2);
        add.Click += (_, _) => AddMacro();
        var delete = new ThemeButton(ThemeButtonKind.Danger, "Delete");
        delete.Margin = new Padding(0, 4, 0, 2);
        delete.Click += (_, _) => DeleteMacro();
        macroButtons.Controls.Add(add);
        macroButtons.Controls.Add(delete);
        Grids.Span(macros.Body, macroButtons, 0, 2);

        _macroList.SelectedIndexChanged += (_, _) =>
        {
            if (_macroList.SelectedItem is string selected && _settings.VoiceMacros.TryGetValue(selected, out var payload))
            {
                _macroCommand.Text = selected;
                _macroPayload.Text = payload;
            }
        };
        ReloadMacroList();
        AddCard(stack, macros);
    }

    private void ReloadMacroList()
    {
        _macroList.Items.Clear();
        foreach (var key in _settings.VoiceMacros.Keys) _macroList.Items.Add(key);
    }

    private void AddMacro()
    {
        var command = _macroCommand.Text.Trim();
        if (command.Length == 0) return;
        _settings.VoiceMacros[command] = _macroPayload.Text;
        ReloadMacroList();
        _macroCommand.Text = "";
        _macroPayload.Text = "";
    }

    private void DeleteMacro()
    {
        if (_macroList.SelectedItem is string selected) _settings.VoiceMacros.Remove(selected);
        ReloadMacroList();
    }

    // ------------------------------------------------------------- history

    private void BuildHistoryPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Window,
            Visible = false,
        };

        var card = new Card("Dictation history", "Recent dictations, stored locally on this device.")
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        card.MakeBodyFill();

        var list = new ThemeListBox { Dock = DockStyle.Fill };
        int count = 0;
        foreach (var entry in _history.Load())
        {
            var app = entry.AppName.Length > 0 ? entry.AppName : "unknown app";
            list.Items.Add($"{entry.Timestamp:MM-dd HH:mm:ss} [{app}] {entry.FinalText}");
            count++;
        }

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var summary = new Label
        {
            Text = count == 1 ? "1 dictation retained." : $"{count} dictations retained.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = Theme.FontCaption,
            ForeColor = Theme.TextCaption,
            Margin = new Padding(0, 6, 0, 0),
        };
        var clear = new ThemeButton(ThemeButtonKind.Danger, "Clear history");
        clear.Margin = new Padding(0, 2, 0, 0);
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show("Delete all stored dictation history?", "Clear history",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _history.Clear();
            list.Items.Clear();
            summary.Text = "0 dictations retained.";
        };
        footer.Controls.Add(summary, 0, 0);
        footer.Controls.Add(clear, 1, 0);

        int listRow = card.Body.RowCount++;
        card.Body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Body.Controls.Add(list, 0, listRow);
        card.Body.SetColumnSpan(list, 2);

        int footerRow = card.Body.RowCount++;
        card.Body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Body.Controls.Add(footer, 0, footerRow);
        card.Body.SetColumnSpan(footer, 2);

        page.Controls.Add(card);
        _content.Controls.Add(page);
        _pages.Add(page);
    }
}
