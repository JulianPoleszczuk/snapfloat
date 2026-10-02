using SnapFloat.Core.Input;
using SnapFloat.Core.Settings;

namespace SnapFloat.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+4", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, '4')]
    [InlineData("ctrl + shift + s", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'S')]
    [InlineData("Win+Alt+F9", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x78)]
    [InlineData("Control+PrtSc", HotkeyModifiers.Ctrl, 0x2C)]
    [InlineData("F13", HotkeyModifiers.None, 0x7C)]
    public void Parses_valid_shortcuts(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(mods, vk), hk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData(null)]
    public void Empty_text_is_no_shortcut(string? text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.True(hk.IsEmpty);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+NotAKey")]
    public void Rejects_malformed_shortcuts(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Formats_in_canonical_modifier_order()
    {
        var hk = new Hotkey(HotkeyModifiers.Win | HotkeyModifiers.Shift | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'K');
        Assert.Equal("Ctrl+Shift+Alt+Win+K", hk.ToString());
        Assert.Equal(hk, Hotkey.Parse(hk.ToString()));
    }

    [Fact]
    public void Win_Shift_S_is_reserved_for_Windows()
    {
        var error = SettingsValidator.ValidateHotkey(Hotkey.Parse("Win+Shift+S"));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("Shift+S")]
    public void Requires_a_real_modifier(string text) =>
        Assert.NotNull(SettingsValidator.ValidateHotkey(Hotkey.Parse(text)));

    [Fact]
    public void F13_to_F24_may_be_used_alone() =>
        Assert.Null(SettingsValidator.ValidateHotkey(Hotkey.Parse("F15")));

    [Fact]
    public void Warns_about_AltGr_combinations() =>
        Assert.NotNull(SettingsValidator.HotkeyWarning(Hotkey.Parse("Ctrl+Alt+S")));

    [Fact]
    public void Detects_duplicate_shortcuts()
    {
        var errors = SettingsValidator.ValidateHotkeys("Ctrl+Shift+4", "Ctrl+Shift+4", "None");
        Assert.True(errors.ContainsKey(nameof(AppSettings.FullScreenHotkey)));
        Assert.False(errors.ContainsKey(nameof(AppSettings.RegionHotkey)));
    }

    [Fact]
    public void Default_shortcuts_are_valid()
    {
        var s = new AppSettings();
        Assert.Empty(SettingsValidator.ValidateHotkeys(s.RegionHotkey, s.FullScreenHotkey, s.WindowHotkey));
    }
}
