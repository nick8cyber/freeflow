using FreeFlow.Core;

namespace FreeFlow.UI;

/// <summary>TextBox that captures a key + modifier combination as a hotkey binding.
/// While any capture box has focus the global engine must be suspended (macOS
/// suspendHotkeyMonitoringForShortcutCapture): otherwise the hook swallows the very
/// key being rebound and starts live dictations behind the settings window.</summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>True while any HotkeyBox across the app has keyboard focus. The tray
    /// context switches the engine's CaptureMode accordingly and feeds keys back via
    /// <see cref="HandleExternalCapture"/> (the hook path is the only way to see
    /// Caps Lock without flipping the system caps state).</summary>
    public static event Action<bool>? CaptureFocusChanged;

    private static HotkeyBox? _activeCaptureBox;

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _activeCaptureBox = this;
        CaptureFocusChanged?.Invoke(true);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        if (_activeCaptureBox == this) _activeCaptureBox = null;
        CaptureFocusChanged?.Invoke(false);
    }

    /// <summary>Receives key captures routed from the hotkey engine (UI thread).</summary>
    public static void HandleExternalCapture(int virtualKey, HotkeyMods mods)
    {
        _activeCaptureBox?.Set(new HotkeyBinding(virtualKey, mods));
    }

    public HotkeyBinding Binding { get; private set; }

    public HotkeyBox(HotkeyBinding binding)
    {
        ReadOnly = true;
        TextAlign = HorizontalAlignment.Center;
        BorderStyle = BorderStyle.None; // framed by the shared HostPanel
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Font = Theme.FontBody;
        Set(binding);
    }

    public void Set(HotkeyBinding binding)
    {
        Binding = binding;
        Text = HotkeyNaming.Describe(binding.VirtualKey, binding.Mods);
        ForeColor = binding.IsEnabled ? Theme.TextBright : Theme.TextCaption;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Let Tab navigate between fields instead of being captured.
        if (keyData is Keys.Tab or (Keys.Tab | Keys.Shift)) return base.ProcessCmdKey(ref msg, keyData);
        // Escape, Delete, or Backspace unbinds the shortcut (the model supports
        // disabled bindings — e.g. Paste Again defaults to Disabled).
        if (keyData is Keys.Escape or Keys.Delete or Keys.Back)
        {
            Set(new HotkeyBinding(0, HotkeyMods.None));
            return true;
        }
        CaptureKey(keyData);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Escape or Keys.Delete or Keys.Back)
        {
            Set(new HotkeyBinding(0, HotkeyMods.None));
        }
        else
        {
            CaptureKey(e.KeyData);
        }
        e.Handled = true;
        base.OnKeyDown(e);
    }

    private void CaptureKey(Keys keyData)
    {
        var vk = keyData & Keys.KeyCode;
        if (vk == Keys.None) return;

        // Primary key candidates: any letter/digit, F-keys, and the classic utility keys.
        var capture = char.IsLetterOrDigit((char)vk) ||
                      vk is >= Keys.F1 and <= Keys.F24 ||
                      vk is Keys.Capital or Keys.Scroll or Keys.Insert or Keys.Pause or
                          Keys.PageUp or Keys.PageDown or Keys.End or Keys.Home;

        if (!capture) return;

        var mods = HotkeyMods.None;
        if (keyData.HasFlag(Keys.Control)) mods |= HotkeyMods.Ctrl;
        if (keyData.HasFlag(Keys.Alt)) mods |= HotkeyMods.Alt;
        if (keyData.HasFlag(Keys.Shift)) mods |= HotkeyMods.Shift;
        if (keyData.HasFlag(Keys.LWin) || keyData.HasFlag(Keys.RWin)) mods |= HotkeyMods.Win;

        // Modifier-only combos cannot be primary keys here.
        if (vk is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin) return;

        Set(new HotkeyBinding((int)vk, mods));
    }
}
