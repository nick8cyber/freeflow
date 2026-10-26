using System.Diagnostics;
using System.IO;
using FreeFlow.Core;
using FreeFlow.Services;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace FreeFlow.UI;

/// <summary>
/// Tray application context: owns the hotkey engine and pipeline, shows the
/// recording overlay, and provides the tray menu (settings, paste again, exit).
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly HotkeyEngine _hotkeys = new();
    private readonly ProviderRouter _router;
    private readonly Pipeline _pipeline;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;
    private readonly HistoryStore _history;
    private SettingsForm? _settingsForm;

    public TrayContext(AppSettings settings)
    {
        _settings = settings;
        _history = new HistoryStore();
        _router = new ProviderRouter(() => _settings);

        // The overlay must exist before the pipeline: it both hosts the marshaling
        // control the pipeline posts hotkey work onto and receives its UI events.
        _overlay = new OverlayForm();
        _ = _overlay.Handle; // force handle creation so BeginInvoke works immediately

        _pipeline = new Pipeline(
            () => _settings,
            _router,
            _hotkeys,
            _history,
            marshal: action => _overlay.BeginInvoke((Delegate)action));

        ApplyHotkeySettings();

        // Session begin/end perform device work (microphone open with format-ladder
        // retries, Live socket setup) — marshal them off the hook thread: a stalled
        // WH_KEYBOARD_LL callback gets the hook silently removed by Windows and all
        // hotkeys die until restart.
        _hotkeys.HoldActivated += () => _overlay.BeginInvoke((Delegate)(Action)_pipeline.OnHoldActivated);
        _hotkeys.HoldDeactivated += () => _overlay.BeginInvoke((Delegate)(Action)_pipeline.OnHoldDeactivated);
        _hotkeys.ToggleActivated += () => _overlay.BeginInvoke((Delegate)(Action)_pipeline.OnToggleActivated);
        _hotkeys.PasteAgainTriggered += () => _pipeline.PasteAgain(_settings);
        _hotkeys.EscapeRequested += () => _pipeline.OnEscapeRequested();
        HotkeyBox.CaptureFocusChanged += capturing =>
        {
            _hotkeys.CaptureMode = capturing;
            if (!capturing) _hotkeys.ResetCaptureState();
        };
        _hotkeys.KeyCaptured += HotkeyBox.HandleExternalCapture;
        if (!_hotkeys.Install())
        {
            // Mirror the macOS hotkeyMonitoringErrorMessage surfacing.
            _overlay.ShowError("Global shortcut monitoring could not start — hotkeys are disabled. Restart the app or check security software.");
        }

        _pipeline.RecordingStarted += _ => _overlay.ShowRecording();
        _pipeline.LevelChanged += _overlay.UpdateLevel;
        _pipeline.PartialTranscript += _overlay.UpdateTranscript;
        _pipeline.TargetAppChanged += _overlay.UpdateTargetApp;
        _pipeline.StatusChanged += status => { _overlay.ShowStatus(status); UpdateTrayTooltip(); };
        _pipeline.Finished += () => { _overlay.HideAfterDelay(); UpdateTrayTooltip(); };
        _pipeline.Cancelled += () => { _overlay.HideAfterDelay(); UpdateTrayTooltip(); };
        _pipeline.ErrorOccurred += message =>
        {
            _overlay.ShowError(message);
            UpdateTrayTooltip();
            _lastErrorItem.Text = "Last error: " + (message.Length > 90 ? message[..90] + "…" : message);
            _lastErrorItem.Visible = true;
            if (_settings.AlertSounds) System.Media.SystemSounds.Hand.Play();
        };

        // A throwing marshaled action surfaces here instead of WinForms' crash dialog.
        Application.ThreadException += (_, e) =>
        {
            _overlay.ShowError("Internal error: " + e.Exception.Message);
            if (_settings.AlertSounds) System.Media.SystemSounds.Hand.Play();
        };

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "FreeFlow",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        // Hook health readout: the tooltip exposes the live hook event counter so a
        // silently removed hook (Windows does it after any slow callback) is visible
        // from outside the process instead of presenting as "the hotkey is dead".
        // The same counter lands in a tiny state file (counter only, no user data)
        // for external diagnostics. An uninstalled hook is retried here every tick —
        // whatever blocked the install at startup, hotkeys come back on their own.
        var statePath = Path.Combine(SettingsStore.DirectoryPath, "hookstate.json");
        var healthTimer = new WinFormsTimer { Interval = 2000 };
        var lastEvents = 0L;
        healthTimer.Tick += (_, _) =>
        {
            if (!_hotkeys.Installed) _hotkeys.Install();
            var installed = _hotkeys.Installed;
            var events = _hotkeys.EventsSeen;
            var moving = events != lastEvents;
            lastEvents = events;
            _tray.Text = installed
                ? $"FreeFlow | hook {(moving ? "ok" : "idle")} ({events})"
                : "FreeFlow | HOTKEYS UNAVAILABLE";
            try
            {
                File.WriteAllText(statePath,
                    $"{{\"installed\":{(installed ? "true" : "false")},\"events\":{events},\"moving\":{(moving ? "true" : "false")},\"lastVk\":{_hotkeys.LastKeyDownVk},\"holdVk\":{_hotkeys.Hold.VirtualKey},\"holdActive\":{(_pipeline.IsBusy ? "true" : "false")},\"recent\":\"{_hotkeys.RecentKeyDownVks}\"}}");
            }
            catch (Exception) { }
        };
        healthTimer.Start();
    }

    private void SetTrayIcon(bool recording)
    {
        // Icons are cached per state inside AppIcon (shared with the forms) — never disposed here.
        _tray.Icon = AppIcon.Create(recording);
    }

    private void ApplyHotkeySettings()
    {
        _hotkeys.Hold = new HotkeyBinding(_settings.HoldHotkeyVk, _settings.HoldHotkeyMods);
        _hotkeys.Toggle = new HotkeyBinding(_settings.ToggleHotkeyVk, _settings.ToggleHotkeyMods);
        _hotkeys.PasteAgain = new HotkeyBinding(_settings.PasteAgainHotkeyVk, _settings.PasteAgainHotkeyMods);
        _hotkeys.SuppressBoundKey = _settings.SuppressBoundKey;
        if (_holdHintItem != null)
        {
            _holdHintItem.Text = $"Hold {HotkeyNaming.Describe(_settings.HoldHotkeyVk, _settings.HoldHotkeyMods)} to dictate";
        }
    }

    private ToolStripMenuItem _holdHintItem = null!;
    private ToolStripMenuItem _lastErrorItem = null!;

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        _holdHintItem = new ToolStripMenuItem(
            $"Hold {HotkeyNaming.Describe(_settings.HoldHotkeyVk, _settings.HoldHotkeyMods)} to dictate")
        {
            Enabled = false,
        };
        menu.Items.Add(_holdHintItem);
        menu.Items.Add(new ToolStripSeparator());

        var startStop = new ToolStripMenuItem("Start / stop dictation");
        startStop.Click += (_, _) => _pipeline.OnToggleActivated();
        menu.Items.Add(startStop);

        var pasteAgain = new ToolStripMenuItem("Paste again");
        pasteAgain.Click += (_, _) => _pipeline.PasteAgain(_settings);
        menu.Items.Add(pasteAgain);

        _lastErrorItem = new ToolStripMenuItem("Last error: none") { Enabled = false, Visible = false };
        menu.Items.Add(_lastErrorItem);

        menu.Items.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        var autostart = new ToolStripMenuItem("Start with Windows");
        autostart.Checked = _settings.StartWithWindows;
        autostart.Click += (_, _) =>
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
            autostart.Checked = _settings.StartWithWindows;
            Autostart.Set(_settings.StartWithWindows);
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>Opens (or activates) the settings window; also used by the --settings launch flag.</summary>
    public void ShowSettingsWindow() => ShowSettings();

    private void ShowSettings()
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _history, ApplyHotkeySettings);
        _settingsForm.Show();
    }

    private void UpdateTrayTooltip()
    {
        _tray.Text = _pipeline.IsBusy ? "FreeFlow — listening…" : "FreeFlow";
        SetTrayIcon(_pipeline.IsBusy); // subtle accent dot on the glyph while recording
    }

    private void ExitApp()
    {
        _pipeline.CancelNow(); // synchronous: posted cancels would never run after Exit
        _hotkeys.Uninstall();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FreeFlow";

    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (enabled)
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
        }
    }
}

/// <summary>
/// Runtime-drawn mic tray icon (no .ico asset needed): dark rounded tile with a
/// crisp white microphone; while recording, a small accent dot marks live state.
/// </summary>
public static class AppIcon
{
    private static Icon? _idle;
    private static Icon? _recording;

    /// <summary>Returns the cached idle/recording icon (shared process-wide; never dispose).</summary>
    public static Icon Create(bool recording = false)
    {
        if (recording)
        {
            _recording ??= Build(recording: true);
            return _recording;
        }
        _idle ??= Build(recording: false);
        return _idle;
    }

    private static Icon Build(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var tile = new Rectangle(1, 1, 30, 30);
            Theme.FillRounded(g, Color.FromArgb(30, 30, 36), tile, 8);
            Theme.StrokeRounded(g, Color.FromArgb(56, 56, 64), tile, 8);

            using var glyph = new SolidBrush(Color.FromArgb(235, 235, 240));
            using (var path = Theme.RoundedPath(new Rectangle(13, 5, 6, 12), 3))
            {
                g.FillPath(glyph, path); // microphone capsule
            }
            using var pen = new Pen(Color.FromArgb(235, 235, 240), 2.1f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawArc(pen, 10, 11, 12, 12, 0, 180); // cradle
            g.DrawLine(pen, 16, 23, 16, 26);        // stem
            g.DrawLine(pen, 12, 26, 20, 26);        // base

            if (recording)
            {
                using var ring = new Pen(Theme.Chrome, 2f);
                using var dot = new SolidBrush(Theme.Accent);
                g.FillEllipse(dot, 22, 22, 8, 8);
                g.DrawEllipse(ring, 21, 21, 10, 10);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
