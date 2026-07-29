namespace FlowType.Input;

/// <summary>Readable names for virtual-key codes (for the detect-key UI and diagnostics).</summary>
public static class VkNames
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [0x04] = "Middle mouse", [0x05] = "Mouse 4 (back)", [0x06] = "Mouse 5 (forward)",
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter",
        [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        [0x13] = "Pause", [0x14] = "Caps Lock", [0x1B] = "Esc", [0x20] = "Space",
        [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "Print Screen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "Left Win", [0x5C] = "Right Win", [0x5D] = "Menu",
        [0x90] = "Num Lock", [0x91] = "Scroll Lock",
        [0xA0] = "Left Shift", [0xA1] = "Right Shift",
        [0xA2] = "Left Ctrl", [0xA3] = "Right Ctrl",
        [0xA4] = "Left Alt", [0xA5] = "Right Alt",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    public static string Name(int vk)
    {
        if (Known.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();            // 0-9
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();            // A-Z
        if (vk is >= 0x60 and <= 0x69) return $"Numpad {vk - 0x60}";
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";              // F1-F24
        return $"Key 0x{vk:X2}";
    }
}
