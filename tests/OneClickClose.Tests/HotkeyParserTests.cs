using Xunit;

namespace OneClickClose.Tests
{
    public sealed class HotkeyParserTests
    {
        [Fact]
        public void ParsesControlAltLetter()
        {
            HotkeyDefinition definition = HotkeyParser.Parse("Ctrl+Alt+X");
            Assert.True(definition.IsValid);
            Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, definition.Modifiers);
            Assert.Equal((uint)'X', definition.VirtualKey);
        }

        [Fact]
        public void ParsesShiftFunctionKey()
        {
            HotkeyDefinition definition = HotkeyParser.Parse("Shift+F12");
            Assert.True(definition.IsValid);
            Assert.Equal(NativeMethods.MOD_SHIFT, definition.Modifiers);
            Assert.Equal((uint)0x7B, definition.VirtualKey);
        }

        [Fact]
        public void ParsesCaseInsensitively()
        {
            HotkeyDefinition definition = HotkeyParser.Parse("ctrl+shift+q");
            Assert.True(definition.IsValid);
            Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, definition.Modifiers);
            Assert.Equal((uint)'Q', definition.VirtualKey);
        }

        [Fact]
        public void InvalidTextFallsBackToControlAltX()
        {
            HotkeyDefinition definition = HotkeyParser.Parse("Ctrl");
            Assert.False(definition.IsValid);
            Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, definition.Modifiers);
            Assert.Equal((uint)'X', definition.VirtualKey);
        }

        [Fact]
        public void UnsupportedKeyFallsBackToControlAltX()
        {
            HotkeyDefinition definition = HotkeyParser.Parse("Win+Space");
            Assert.False(definition.IsValid);
            Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, definition.Modifiers);
            Assert.Equal((uint)'X', definition.VirtualKey);
        }
    }
}
