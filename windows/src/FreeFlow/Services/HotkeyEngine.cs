using System.Runtime.InteropServices;
using FreeFlow.Core;

namespace FreeFlow.Services;

public enum DictationSessionState
{
    None,
    Hold,
    Toggle,
}

/// <summary>
/// Global hotkey engine ported from the macOS HotkeyManager/ShortcutMatcher: a
/// WH_KEYBOARD_LL hook tracks exact key+modifier bindings, distinguishes hold
/// (keydown→keyup) from tap semantics, suppresses the bound key so it never
/// reaches other apps, and ignores injected events (including our own pastes).
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int LlkhfInjected = 0x10;
    private const int LlkhfUp = 0x80;

    public HotkeyBinding Hold { get; set; }
    public HotkeyBinding Toggle { get; set; }
    public HotkeyBinding PasteAgain { get; set; }
    public bool SuppressBoundKey { get; set; } = true;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// While a hotkey capture box owns focus the engine switches to capture mode
    /// (macOS suspendHotkeyMonitoringForShortcutCapture + capture-through-tap):
    /// dictation events are suppressed, Escape stops meaning "cancel session", and
    /// every bindable keydown is surfaced via <see cref="KeyCaptured"/> so the box can
    /// rebind reliably — including Caps Lock, which the hook keeps swallowing so the
    /// system caps state never flips mid-capture.
    /// </summary>
    public bool CaptureMode { get; set; }

    /// <summary>Raised in capture mode for each bindable keydown (virtual key + held mods).</summary>
    public event Action<int, HotkeyMods>? KeyCaptured;

    public event Action? HoldActivated;
    public event Action? HoldDeactivated;
    public event Action? ToggleActivated;
    public event Action? ToggleDeactivated;
    public event Action? PasteAgainTriggered;

    /// <summary>Return true when the escape was consumed (cancels a session) — the hook
    /// then swallows the keystroke so the focused app never sees it (macOS parity:
    /// GlobalShortcutBackend returns .consume from the escape branch).</summary>
    public Func<bool>? EscapeRequested;

    private IntPtr _hook = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<int> _pressedVks = [];
    private readonly HashSet<int> _suppressedKeyDownVks = [];
    // Modifier keydowns swallowed by the CURRENT active session; a modifier keyup
    // may only be swallowed when its keydown is in here (see ModifierSwallowDecision).
    private readonly HashSet<int> _sessionSwallowedModifiers = [];
    private bool _escapeConsumed;
    private readonly HashSet<int> _captureDownModifiers = [];
    private bool _holdActive;
    private bool _toggleActive;
    private bool _pasteAgainActive;

    public HotkeyEngine()
    {
        _proc = HookProc;
    }

    /// <summary>Installs the keyboard hook. Returns false when Windows refused
    /// (hook exhaustion / policy) so the app can tell the user hotkeys are dead
    /// instead of failing silently (Swift surfaces eventTapUnavailable).</summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module.FileName), 0);
        return _hook != IntPtr.Zero;
    }

    /// <summary>True while a hook handle exists (Install succeeded and not uninstalled).</summary>
    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>Total keyboard events the hook has seen since install. A counter that
    /// stops moving while the user types means Windows silently removed the hook.</summary>
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);
    private long _eventsSeen;

    /// <summary>Last non-injected keydown's virtual key (diagnostics: proves whether
    /// a physical key press reaches the hook at all).</summary>
    public int LastKeyDownVk => (int)Interlocked.Read(ref _lastKeyDownVk);
    private long _lastKeyDownVk = -1;

    private readonly long[] _recentKeyDownVks = new long[16];
    private int _recentVksPos;

    /// <summary>Ring of the last 16 non-injected keydown VKs (diagnostics).</summary>
    public string RecentKeyDownVks
    {
        get
        {
            var pos = Math.Min(Volatile.Read(ref _recentVksPos), _recentKeyDownVks.Length);
            var parts = new string[pos];
            for (var i = 0; i < pos; i++)
                parts[i] = _recentKeyDownVks[i].ToString();
            return string.Join(",", parts);
        }
    }

    private void RecordRecentVk(int vk)
    {
        var pos = Interlocked.Increment(ref _recentVksPos) - 1;
        _recentKeyDownVks[pos % _recentKeyDownVks.Length] = vk;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>True while any enabled shortcut's physical keys are still held down.</summary>
    public bool IsAnyShortcutInputHeld()
    {
        foreach (var binding in new[] { Hold, Toggle, PasteAgain })
        {
            if (!binding.IsEnabled) continue;
            if (IsInputHeld(binding)) return true;
        }
        return false;
    }

    private bool IsInputHeld(HotkeyBinding binding)
    {
        if (!binding.IsEnabled) return false;
        if (!IsKeyDown(binding.VirtualKey)) return false;
        if ((binding.Mods & HotkeyMods.Ctrl) != 0 && !IsKeyDown(0xA2) && !IsKeyDown(0xA3)) return false;
        if ((binding.Mods & HotkeyMods.Alt) != 0 && !IsKeyDown(0xA4) && !IsKeyDown(0xA5)) return false;
        if ((binding.Mods & HotkeyMods.Shift) != 0 && !IsKeyDown(0xA0) && !IsKeyDown(0xA1)) return false;
        if ((binding.Mods & HotkeyMods.Win) != 0 && !IsKeyDown(0x5B) && !IsKeyDown(0x5C)) return false;
        return true;
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref _eventsSeen);
            if (Enabled)
            {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & LlkhfInjected) != 0;
            var isUp = (info.flags & LlkhfUp) != 0;
            var msg = wParam.ToInt64();

            if (CaptureMode && !injected)
            {
                return HandleCaptureKey(nCode, wParam, lParam, (int)info.vkCode, isUp);
            }

            if (!injected)
            {
                var isDownEvent = msg is WmKeydown or WmSyskeydown;
                var isUpEvent = msg is WmKeyup or WmSyskeyup;
                if (isDownEvent)
                {
                    Interlocked.Exchange(ref _lastKeyDownVk, (int)info.vkCode);
                    RecordRecentVk((int)info.vkCode);
                }

                if (isDownEvent || isUpEvent)
                {
                    try
                    {
                        ProcessKey((int)info.vkCode, isDownEvent);
                    }
                    catch (Exception)
                    {
                        // A hotkey handler error must never break keyboard input.
                    }

                    // Suppress both edges of an active binding press (Caps Lock in
                    // particular toggles on keyup, so a swallowed keydown must swallow
                    // its keyup too, or the user's caps state silently flips).
                    var vk = (int)info.vkCode;
                    if (isUpEvent && vk == 0x1B && _escapeConsumed)
                    {
                        _escapeConsumed = false;
                        return (IntPtr)1; // the consumed escape's keyup stays swallowed too
                    }
                    if (SuppressBoundKey)
                    {
                        if (isUpEvent && _suppressedKeyDownVks.Remove(vk))
                        {
                            return (IntPtr)1;
                        }
                        if (isDownEvent && ShouldSuppress(vk))
                        {
                            // Pair the marker only when the OS has not already got the
                            // key down (auto-repeat after a leaked keydown, or a stale
                            // state from an older run): its keyup must stay free to
                            // pass, or the key strands logically held system-wide.
                            if (!IsKeyDown(vk)) _suppressedKeyDownVks.Add(vk);
                            return (IntPtr)1;
                        }
                        // A keydown that passes through clears any stale suppressed
                        // marker so its matching keyup is never swallowed by mistake.
                        if (isDownEvent)
                        {
                            _suppressedKeyDownVks.Remove(vk);
                        }
                        // While a dictation binding is active, modifier key events are
                        // consumed (Swift ShortcutMatcher modifierEvent consume) so a
                        // habitual Shift for the next sentence never reaches the target.
                        // A keyup is swallowed only when its keydown was swallowed by
                        // the same session: a modifier already held when the session
                        // started (Ctrl released after a Ctrl+V paste, or released
                        // first in a Ctrl+CapsLock toggle combo) must still deliver
                        // its keyup, or the OS holds the modifier down and every
                        // later keystroke becomes an unintended shortcut.
                        if (_holdActive || _toggleActive || _pasteAgainActive)
                        {
                            if (vk is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C)
                            {
                                if (isDownEvent)
                                {
                                    _sessionSwallowedModifiers.Add(vk);
                                    return (IntPtr)1;
                                }
                                var keydownWasSwallowed = _sessionSwallowedModifiers.Remove(vk);
                                if (ModifierSwallowDecision(sessionActive: true, isDown: false, keydownWasSwallowed)
                                    == ModifierSwallow.Swallow)
                                {
                                    return (IntPtr)1;
                                }
                                // Modifier held since before the session: its keyup
                                // passes so the OS modifier state clears.
                            }
                        }
                    }
                }
            }
        }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Capture-mode hook path. Caps Lock is swallowed on both edges (so rebinding it
    /// never toggles the system caps state) and delivered through KeyCaptured.
    /// Modifier keys are ALSO bindable as primary keys (macOS .modifierKey bindings /
    /// the "Right Option" preset): pressing Alt alone offers "Left Alt"/"Right Alt"
    /// as the hold key. Everything else passes through to the focused box. Dictation
    /// events and escape-cancellation are fully suppressed here.
    /// </summary>
    private IntPtr HandleCaptureKey(int nCode, IntPtr wParam, IntPtr lParam, int vk, bool isUp)
    {
        // Esc/Delete/Backspace pass through untouched so the focused box unbinds.
        if (vk == 0x1B) return CallNextHookEx(_hook, nCode, wParam, lParam);

        if (IsModifierVk(vk))
        {
            if (isUp)
            {
                _captureDownModifiers.Remove(vk);
            }
            else if (_captureDownModifiers.Add(vk))
            {
                // Deliver the modifier as the primary key; its own flag is excluded
                // from the modifier set ("Right Alt" binds as key=VK_RMENU, mods=None;
                // Ctrl+Alt binds as key=VK_LMENU, mods=Ctrl).
                var mods = HeldModifiersFromKeyboard() & ~ModifierFlagOf(vk);
                KeyCaptured?.Invoke(vk, mods);
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (vk == 0x14) // Caps Lock: deliver + swallow both edges (no system caps flip)
        {
            if (isUp) _captureDownModifiers.Remove(vk);
            else if (_captureDownModifiers.Add(vk))
            {
                KeyCaptured?.Invoke(vk, HeldModifiersFromKeyboard());
            }
            return (IntPtr)1;
        }

        if (!isUp && IsBindablePrimary(vk))
        {
            KeyCaptured?.Invoke(vk, HeldModifiersFromKeyboard());
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsModifierVk(int vk) =>
        vk is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    private static HotkeyMods ModifierFlagOf(int vk) => vk switch
    {
        0xA0 or 0xA1 => HotkeyMods.Shift,
        0xA2 or 0xA3 => HotkeyMods.Ctrl,
        0xA4 or 0xA5 => HotkeyMods.Alt,
        _ => HotkeyMods.Win,
    };

    /// <summary>Clears capture keydown tracking when capture mode ends.</summary>
    public void ResetCaptureState() => _captureDownModifiers.Clear();

    /// <summary>Primary keys the capture box accepts (mirrors HotkeyBox.CaptureKey).</summary>
    private static bool IsBindablePrimary(int vk) =>
        char.IsLetterOrDigit((char)vk) ||
        vk is >= 0x70 and <= 0x87 || // F1..F24
        vk is 0x14 or 0x91 or 0x2D or 0x13 or 0x21 or 0x22 or 0x23 or 0x24; // Caps, Scroll, Ins, Pause, PgUp, PgDn, End, Home

    /// <summary>Suppress the bound key only while a matching binding is actually active,
    /// so unrelated modifier combos that merely include the key still reach other apps.
    /// Always called for keydown edges, so ComputeActive treats the edge itself as proof
    /// the key is down (see ComputeActive's downEdgeVk).</summary>
    private bool ShouldSuppress(int vk)
    {
        return (Hold.IsEnabled && Hold.VirtualKey == vk && ComputeActive(Hold, vk)) ||
               (Toggle.IsEnabled && Toggle.VirtualKey == vk && ComputeActive(Toggle, vk)) ||
               (PasteAgain.IsEnabled && PasteAgain.VirtualKey == vk && ComputeActive(PasteAgain, vk));
    }

    private void ProcessKey(int vk, bool isDown)
    {
        if (CaptureMode) return; // capture mode never drives sessions

        var changed = false;
        if (isDown)
        {
            if (!_pressedVks.Add(vk)) return; // auto-repeat
            changed = true;
        }
        else
        {
            changed = _pressedVks.Remove(vk);
        }

        if (vk == 0x1B && isDown && changed)
        {
            _escapeConsumed = EscapeRequested?.Invoke() ?? false;
        }

        if (!changed) return;

        // Activations are computed with the down-edge trust: at a fresh keydown the
        // OS async state does not yet include this press (GetAsyncKeyState updates
        // only after the event leaves the hook chain), so asking it there ALWAYS
        // answers "not held" for a brand-new press — the session would start late
        // (on auto-repeat) and the first keydown would leak to the focused app,
        // activating menu mode for Alt-based hold keys.
        var holdActive = ComputeActive(Hold, isDown ? vk : null);
        var toggleActive = ComputeActive(Toggle, isDown ? vk : null);
        var pasteActive = ComputeActive(PasteAgain, isDown ? vk : null);

        // Activations first (most specific — toggle before hold), then deactivations.
        // Each activation starts a fresh modifier-swallow window: a modifier held
        // since before the session keeps passing its keyup through.
        if (toggleActive && !_toggleActive)
        {
            _toggleActive = true;
            _sessionSwallowedModifiers.Clear();
            ToggleActivated?.Invoke();
        }
        if (holdActive && !_holdActive)
        {
            _holdActive = true;
            _sessionSwallowedModifiers.Clear();
            HoldActivated?.Invoke();
        }
        if (pasteActive && !_pasteAgainActive)
        {
            _pasteAgainActive = true;
            _sessionSwallowedModifiers.Clear();
            PasteAgainTriggered?.Invoke();
        }
        if (!toggleActive && _toggleActive)
        {
            _toggleActive = false;
            ToggleDeactivated?.Invoke();
        }
        if (!holdActive && _holdActive)
        {
            _holdActive = false;
            HoldDeactivated?.Invoke();
        }
        if (!pasteActive && _pasteAgainActive)
        {
            _pasteAgainActive = false;
        }
    }

    /// <summary>
    /// Port of ShortcutBinding.isActive: a binding is active while its primary key is
    /// held and the physically held modifiers are a SUPERSET of the binding's modifiers.
    /// (Extending a hold shortcut with extra modifiers must not end the dictation — it
    /// latches toggle mode instead.) Modifier state is read from GetAsyncKeyState, so a
    /// keyup missed on a secure desktop or swallowed by another hook self-heals instead
    /// of leaving a stuck bit that silently disables all shortcuts.
    /// </summary>
    private bool ComputeActive(HotkeyBinding binding, int? downEdgeVk = null)
    {
        if (!binding.IsEnabled) return false;
        // Double-source the primary key against the physical state: a keyup missed on
        // the secure desktop or swallowed by another hook would otherwise leave a stale
        // pressed flag and start ghost recordings on the next unrelated keypress.
        // downEdgeVk exempts the key whose fresh keydown edge is being processed —
        // the OS cannot have applied that event yet.
        if (!_pressedVks.Contains(binding.VirtualKey)) return false;
        if (binding.VirtualKey != downEdgeVk && !IsKeyDown(binding.VirtualKey)) return false;
        var mods = HeldModifiersFromKeyboard();
        return (mods & binding.Mods) == binding.Mods;
    }

    private static HotkeyMods HeldModifiersFromKeyboard()
    {
        var mods = HotkeyMods.None;
        if (IsKeyDown(0xA0) || IsKeyDown(0xA1)) mods |= HotkeyMods.Shift;
        if (IsKeyDown(0xA2) || IsKeyDown(0xA3)) mods |= HotkeyMods.Ctrl;
        if (IsKeyDown(0xA4) || IsKeyDown(0xA5)) mods |= HotkeyMods.Alt;
        if (IsKeyDown(0x5B) || IsKeyDown(0x5C)) mods |= HotkeyMods.Win;
        return mods;
    }

    public void Dispose() => Uninstall();

    /// <summary>Whether the hook may swallow a modifier key event while a dictation
    /// session is active. A keydown may always be swallowed; a keyup only when its
    /// keydown was swallowed by the same session — otherwise the OS keeps the
    /// modifier logically held (stuck Ctrl/Shift after a paste-then-dictate or a
    /// Ctrl-first Ctrl+CapsLock toggle release) and every keystroke becomes a
    /// system-wide shortcut.</summary>
    public static ModifierSwallow ModifierSwallowDecision(bool sessionActive, bool isDown, bool keydownWasSwallowed) =>
        (sessionActive, isDown, keydownWasSwallowed) switch
        {
            (false, _, _) => ModifierSwallow.PassThrough,
            (_, true, _) => ModifierSwallow.Swallow,
            (_, false, true) => ModifierSwallow.Swallow,
            _ => ModifierSwallow.PassThrough,
        };

    public enum ModifierSwallow
    {
        Swallow,
        PassThrough,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr extraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);
}
