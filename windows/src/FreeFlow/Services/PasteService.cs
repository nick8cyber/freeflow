using System.Runtime.InteropServices;
using FreeFlow.Core;

namespace FreeFlow.Services;

/// <summary>
/// Inserts dictation text at the cursor ported from the macOS paste flow: writes to
/// the clipboard (flagged so clipboard managers and cloud sync skip it), waits for
/// the shortcut keys to be physically released, sends Ctrl+V, optionally presses
/// Enter for the spoken "press enter" command, then restores the previous clipboard
/// — including its privacy markers, so content that was hidden from clipboard
/// history/cloud sync before the dictation stays hidden after the restore.
/// All operations are serialized: a paste racing another paste corrupts both.
/// </summary>
public sealed class PasteService
{
    private const int PasteAfterShortcutReleasePollMs = 25;
    private const int PasteAfterShortcutReleaseMaxAttempts = 24;
    private const int PasteAfterReleaseDelayMs = 30;
    private const int PressEnterAfterPasteDelayMs = 80;
    private const int ClipboardRestoreDelayMs = 1000;
    private const int ClipboardOpenRetries = 10;
    private const int ClipboardOpenRetryDelayMs = 10;

    private readonly Func<bool> _shortcutInputHeld;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PasteService(Func<bool> shortcutInputHeld)
    {
        _shortcutInputHeld = shortcutInputHeld;
    }

    /// <summary>Pastes the text into the focused app. Returns false when the clipboard or the
    /// synthetic keys failed, so the caller can surface an error instead of silently pasting
    /// stale clipboard content into the user's document.</summary>
    public bool Paste(string text, bool pressEnterAfter, bool preserveClipboard, bool keepInClipboardHistory)
    {
        _gate.Wait();
        try
        {
            return PasteCore(text, pressEnterAfter, preserveClipboard, keepInClipboardHistory);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Presses Enter without pasting (spoken "press enter" with nothing else to paste).</summary>
    public void PressEnterOnly()
    {
        _gate.Wait();
        try
        {
            WaitForShortcutRelease();
            _ = SendKey(0x0D); // VK_RETURN
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool PasteCore(string text, bool pressEnterAfter, bool preserveClipboard, bool keepInClipboardHistory)
    {
        // Full-format snapshot (macOS parity): text AND images/files/HTML are captured,
        // so restoring can never destroy a copied screenshot or file. The snapshot also
        // carries the previous content's privacy markers, which are restored as-is.
        var previous = preserveClipboard ? Win32Clipboard.CaptureSnapshot() : null;

        // macOS writes the transcript to the pasteboard immediately, THEN waits for the
        // shortcut keys to be physically released. Same order here.
        var clipboardText = TextUtil.WithTrailingSpace(text);
        if (!Win32Clipboard.TrySetText(clipboardText, includeInHistory: keepInClipboardHistory))
        {
            return false;
        }
        if (Win32Clipboard.GetText() != clipboardText)
        {
            // The write silently lost the race with another clipboard writer.
            return false;
        }

        WaitForShortcutRelease();

        var pasted = SendPaste();

        if (pressEnterAfter)
        {
            Thread.Sleep(PressEnterAfterPasteDelayMs);
            _ = SendKey(0x0D); // VK_RETURN — pressed unconditionally (macOS parity)
        }

        if (preserveClipboard && previous != null)
        {
            // Restore whenever the clipboard was written — including failed pastes —
            // so the dictation text never lingers for clipboard managers to pick up.
            // The re-check below makes sure anything the user copied in the last
            // second is not clobbered.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(ClipboardRestoreDelayMs);
                try
                {
                    var (current, readOk) = ReadClipboardText();
                    if (readOk && current == clipboardText)
                    {
                        if (previous.Items.Count > 0) Win32Clipboard.RestoreSnapshot(previous);
                        else Win32Clipboard.TryClear();
                    }
                }
                catch (Exception)
                {
                    // Best-effort restore.
                }
            });
        }

        return pasted;
    }

    private void WaitForShortcutRelease()
    {
        // Wait until the dictation shortcut keys are physically released (port of the
        // 25 ms × 24 attempts poll + settle delay in the macOS pasteAtCursor).
        for (var attempt = 0; attempt < PasteAfterShortcutReleaseMaxAttempts && _shortcutInputHeld(); attempt++)
        {
            Thread.Sleep(PasteAfterShortcutReleasePollMs);
        }
        Thread.Sleep(PasteAfterReleaseDelayMs);
    }

    private static (string Text, bool Ok) ReadClipboardText()
    {
        try
        {
            var text = Win32Clipboard.GetText();
            return (text, true);
        }
        catch (Exception)
        {
            // Clipboard locked: an unreadable previous value must never turn the
            // restore into Clipboard.Clear().
            return ("", false);
        }
    }

    private static bool SendPaste()
    {
        return SendInputs(
            KeyInput(0x11, true), KeyInput(0x56, true), KeyInput(0x56, false), KeyInput(0x11, false));
    }

    private static bool SendKey(int vk)
    {
        return SendInputs(KeyInput(vk, true), KeyInput(vk, false));
    }

    /// <summary>Sends the inputs and reports whether the system accepted them all
    /// (SendInput fails, e.g. via UIPI, when the target window is elevated).</summary>
    private static bool SendInputs(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, INPUT.Size) == (uint)inputs.Length;

    private static INPUT KeyInput(int vk, bool down) => new()
    {
        type = 1u, // INPUT_KEYBOARD
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = 0,
                dwFlags = down ? 0u : 2u, // KEYEVENTF_KEYUP
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    /// <summary>
    /// Raw clipboard access: writes CF_UNICODETEXT plus the real DWORD-0
    /// canIncludeInClipboardHistory / canUploadToCloudClipboard markers and the
    /// presence-based ExcludeClipboardContentFromMonitorProcessing marker — the
    /// Windows analog of the macOS transient pasteboard types. WinForms DataObject
    /// cannot produce the DWORD payloads these formats require.
    /// </summary>
    private static class Win32Clipboard
    {
        private const uint CfUnicodeText = 13;

        /// <summary>Formats stored as GDI/handle references, not global memory: their
        /// bytes cannot be round-tripped via GetClipboardData/GlobalLock.</summary>
        private static readonly HashSet<uint> SkipFormats = [2, 3, 9, 14]; // BITMAP, METAFILEPICT, PALETTE, ENHMETAFILE

        private static readonly uint FmtExcludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        private static readonly uint FmtCanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        private static readonly uint FmtCanUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");

        public static bool TrySetText(string text, bool includeInHistory)
        {
            if (!TryOpenClipboard()) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                if (!SetString(text)) return false;
                if (!includeInHistory)
                {
                    // Presence marker: any non-null handle counts. Marker failures are
                    // best-effort: the transcript IS on the clipboard, and reporting a
                    // failure here would show a paste error while the text sits unused.
                    if (FmtExcludeMonitor != 0) _ = SetRawZeroData(FmtExcludeMonitor, 1);
                    if (FmtCanIncludeInHistory != 0) _ = SetRawZeroData(FmtCanIncludeInHistory, 4);
                    if (FmtCanUploadToCloud != 0) _ = SetRawZeroData(FmtCanUploadToCloud, 4);
                }
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        public static bool TryClear()
        {
            if (!TryOpenClipboard()) return false;
            try { return EmptyClipboard(); }
            finally { CloseClipboard(); }
        }

        public static string GetText()
        {
            if (!TryOpenClipboard()) throw new InvalidOperationException("Clipboard is busy");
            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero) return "";
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return "";
                try
                {
                    return Marshal.PtrToStringUni(pointer) ?? "";
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// Captures every global-memory clipboard format as raw bytes (macOS snapshots
        /// every pasteboard item/type). Handle-based formats that cannot be round-tripped
        /// as bytes (CF_BITMAP, CF_METAFILEPICT, CF_ENHMETAFILE, CF_PALETTE) are skipped —
        /// their content is covered by the DIB equivalent for bitmaps.
        /// </summary>
        public static ClipboardSnapshot CaptureSnapshot()
        {
            var snapshot = new ClipboardSnapshot();
            if (!TryOpenClipboard()) return snapshot;
            try
            {
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    if (SkipFormats.Contains(format)) continue;
                    var handle = GetClipboardData(format);
                    if (handle == IntPtr.Zero) continue;
                    var pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        var size = (int)GlobalSize(handle);
                        if (size <= 0) continue;
                        var data = new byte[size];
                        Marshal.Copy(pointer, data, 0, size);
                        snapshot.Items.Add((format, data));
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                var textHandle = GetClipboardData(CfUnicodeText);
                if (textHandle != IntPtr.Zero)
                {
                    var pointer = GlobalLock(textHandle);
                    if (pointer != IntPtr.Zero)
                    {
                        try { snapshot.Text = Marshal.PtrToStringUni(pointer) ?? ""; }
                        finally { GlobalUnlock(textHandle); }
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
            return snapshot;
        }

        /// <summary>Writes every captured format back verbatim — including the previous
        /// content's privacy-marker formats, which restores its history/cloud exclusion.</summary>
        public static bool RestoreSnapshot(ClipboardSnapshot snapshot)
        {
            if (!TryOpenClipboard()) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                foreach (var (format, data) in snapshot.Items)
                {
                    var handle = GlobalAlloc(GmemMoveable, (uint)data.Length);
                    if (handle == IntPtr.Zero) continue;
                    var pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero)
                    {
                        GlobalFree(handle);
                        continue;
                    }
                    var wrote = false;
                    try
                    {
                        Marshal.Copy(data, 0, pointer, data.Length);
                        wrote = true;
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                        if (!wrote) GlobalFree(handle);
                    }
                    if (SetClipboardData(format, handle) == IntPtr.Zero) GlobalFree(handle);
                }
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>True when the current clipboard is marked excluded from clipboard
        /// history / cloud sync (either marker format present in its "no" state).</summary>
        public static bool IsExcludedFromHistory()
        {
            if (!TryOpenClipboard()) return false;
            try
            {
                if (FmtExcludeMonitor != 0 && GetClipboardData(FmtExcludeMonitor) != IntPtr.Zero) return true;
                if (FmtCanIncludeInHistory != 0 && IsDwordZeroPresent(FmtCanIncludeInHistory)) return true;
                if (FmtCanUploadToCloud != 0 && IsDwordZeroPresent(FmtCanUploadToCloud)) return true;
                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static bool IsDwordZeroPresent(uint format)
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return false;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                return GlobalSize(handle) >= 4 && Marshal.ReadInt32(pointer) == 0;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        private static bool TryOpenClipboard()
        {
            // Clipboard managers hold the clipboard in short bursts; a single attempt
            // would fail real pastes. Retry briefly before giving up.
            for (var attempt = 0; attempt < ClipboardOpenRetries; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(ClipboardOpenRetryDelayMs);
            }
            return false;
        }

        private static bool SetString(string text)
        {
            var bytes = (text.Length + 1) * 2;
            var handle = GlobalAlloc(GmemMoveable, (uint)bytes);
            if (handle == IntPtr.Zero) return false;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            var copied = false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * 2, 0);
                copied = true;
            }
            finally
            {
                GlobalUnlock(handle);
                if (!copied) GlobalFree(handle); // ownership never transferred
            }
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            return true;
        }

        private static bool SetRawZeroData(uint format, int size)
        {
            var handle = GlobalAlloc(GmemMoveable, (uint)size);
            if (handle == IntPtr.Zero) return false;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            var wrote = false;
            try
            {
                for (var i = 0; i < size; i++) Marshal.WriteByte(pointer, i, 0);
                wrote = true;
            }
            finally
            {
                GlobalUnlock(handle);
                if (!wrote) GlobalFree(handle);
            }
            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            return true;
        }

        private const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, uint dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern uint GlobalSize(IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern uint EnumClipboardFormats(uint format);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static readonly int Size = Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}

/// <summary>A point-in-time copy of every clipboard format we can round-trip.</summary>
public sealed class ClipboardSnapshot
{
    public List<(uint Format, byte[] Data)> Items { get; } = [];
    public string Text { get; internal set; } = "";
}
