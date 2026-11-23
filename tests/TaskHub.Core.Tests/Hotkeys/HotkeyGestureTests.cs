using TaskHub.Core.Hotkeys;

namespace TaskHub.Core.Tests.Hotkeys;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "T", "Ctrl+Alt+T")]
    [InlineData("ctrl + alt + t", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "T", "Ctrl+Alt+T")]
    [InlineData("Alt+Ctrl+T", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "T", "Ctrl+Alt+T")]
    [InlineData("Control+Shift+5", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "5", "Ctrl+Shift+5")]
    [InlineData("Win+Shift+N", HotkeyModifiers.Win | HotkeyModifiers.Shift, "N", "Shift+Win+N")]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "Space", "Ctrl+Alt+Space")]
    [InlineData("Ctrl+F12", HotkeyModifiers.Ctrl, "F12", "Ctrl+F12")]
    [InlineData("F9", HotkeyModifiers.None, "F9", "F9")]
    [InlineData("Alt+esc", HotkeyModifiers.Alt, "Escape", "Alt+Escape")]
    [InlineData("Ctrl+PgDn", HotkeyModifiers.Ctrl, "PageDown", "Ctrl+PageDown")]
    public void Parses_valid_gestures(string text, HotkeyModifiers mods, string key, string canonical)
    {
        var g = HotkeyGesture.Parse(text);
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
        Assert.Equal(canonical, g.ToString());
        Assert.Equal(g, HotkeyGesture.Parse(g.ToString())); // round-trips
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T")]              // needs a modifier
    [InlineData("Shift")]          // modifier alone isn't a key
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++T")]
    [InlineData("Hyper+T")]
    [InlineData("Ctrl+Ctrl+T")]
    [InlineData("Ctrl+Alt+NotAKey")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    public void Rejects_invalid_gestures(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Modifier_values_match_win32_MOD_flags()
    {
        Assert.Equal(0x1, (int)HotkeyModifiers.Alt);
        Assert.Equal(0x2, (int)HotkeyModifiers.Ctrl);
        Assert.Equal(0x4, (int)HotkeyModifiers.Shift);
        Assert.Equal(0x8, (int)HotkeyModifiers.Win);
    }
}
