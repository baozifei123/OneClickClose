using System;
using System.Globalization;

namespace OneClickClose
{
    public sealed class HotkeyDefinition
    {
        public uint Modifiers { get; set; }
        public uint VirtualKey { get; set; }
        public bool IsValid { get; set; }

        public static HotkeyDefinition Default
        {
            get
            {
                return new HotkeyDefinition
                {
                    Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                    VirtualKey = (uint)'X',
                    IsValid = true
                };
            }
        }
    }

    public static class HotkeyParser
    {
        public static bool IsSameCombination(HotkeyDefinition first, HotkeyDefinition second)
        {
            return first != null && second != null && first.IsValid && second.IsValid &&
                first.Modifiers == second.Modifiers && first.VirtualKey == second.VirtualKey;
        }

        public static HotkeyDefinition Parse(string text)
        {
            uint modifiers = 0;
            uint virtualKey = 0;

            if (!string.IsNullOrWhiteSpace(text))
            {
                string[] parts = text.Split('+');
                foreach (string part in parts)
                {
                    string token = part.Trim();
                    if (token.Length == 0)
                    {
                        continue;
                    }

                    switch (token.ToLowerInvariant())
                    {
                        case "ctrl":
                        case "control":
                            modifiers |= NativeMethods.MOD_CONTROL;
                            continue;
                        case "alt":
                            modifiers |= NativeMethods.MOD_ALT;
                            continue;
                        case "shift":
                            modifiers |= NativeMethods.MOD_SHIFT;
                            continue;
                        case "win":
                            modifiers |= NativeMethods.MOD_WIN;
                            continue;
                    }

                    string upper = token.ToUpperInvariant();
                    if (upper.Length == 1)
                    {
                        char key = upper[0];
                        if ((key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9'))
                        {
                            virtualKey = (uint)key;
                        }
                    }
                    else if (upper.Length >= 2 && upper[0] == 'F')
                    {
                        int functionNumber;
                        if (int.TryParse(upper.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out functionNumber) &&
                            functionNumber >= 1 && functionNumber <= 24)
                        {
                            virtualKey = (uint)(0x6F + functionNumber);
                        }
                    }
                }
            }

            if (virtualKey == 0 || modifiers == 0)
            {
                HotkeyDefinition fallback = HotkeyDefinition.Default;
                fallback.IsValid = false;
                return fallback;
            }

            return new HotkeyDefinition
            {
                Modifiers = modifiers,
                VirtualKey = virtualKey,
                IsValid = true
            };
        }
    }
}
