using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

/// <summary>
/// Regression tests for the stuck-modifier bug: while a dictation session is
/// active the hook consumes modifier events, but a keyup whose keydown reached
/// the OS before the session started must pass through — otherwise Windows keeps
/// the modifier logically held and every keystroke becomes a shortcut.
/// </summary>
public class HotkeyEngineModifierTests
{
    private const bool Down = true;
    private const bool Up = false;

    [Fact]
    public void KeydownDuringActiveSessionIsSwallowed()
    {
        Assert.Equal(
            HotkeyEngine.ModifierSwallow.Swallow,
            HotkeyEngine.ModifierSwallowDecision(sessionActive: true, isDown: Down, keydownWasSwallowed: false));
    }

    [Fact]
    public void KeyupOfInSessionSwallowedKeydownIsSwallowed()
    {
        // Habitual Shift pressed and released entirely inside a dictation session.
        Assert.Equal(
            HotkeyEngine.ModifierSwallow.Swallow,
            HotkeyEngine.ModifierSwallowDecision(sessionActive: true, isDown: Up, keydownWasSwallowed: true));
    }

    [Fact]
    public void KeyupOfModifierHeldBeforeSessionPassesThrough()
    {
        // Ctrl was held when the session started (Ctrl+V paste, then dictate);
        // swallowing its keyup would leave Ctrl stuck down system-wide.
        Assert.Equal(
            HotkeyEngine.ModifierSwallow.PassThrough,
            HotkeyEngine.ModifierSwallowDecision(sessionActive: true, isDown: Up, keydownWasSwallowed: false));
    }

    [Fact]
    public void ModifierEventsOutsideSessionAlwaysPassThrough()
    {
        Assert.Equal(
            HotkeyEngine.ModifierSwallow.PassThrough,
            HotkeyEngine.ModifierSwallowDecision(sessionActive: false, isDown: Down, keydownWasSwallowed: false));
        Assert.Equal(
            HotkeyEngine.ModifierSwallow.PassThrough,
            HotkeyEngine.ModifierSwallowDecision(sessionActive: false, isDown: Up, keydownWasSwallowed: true));
    }
}
