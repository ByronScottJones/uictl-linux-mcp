namespace UICtl.Core;

/// <summary>
/// Public surface CommandDispatcher calls for click/move/scroll/key and
/// synthesized `type` - thin translation over UinputDevice/KeyCodes, mirrors
/// Accessibility.cs's shape (thin public methods over the D-Bus internals).
/// </summary>
public static class InputSynthesis
{
    public static void Click(Point at, MouseButton button, int count) =>
        UinputDevice.Click((int)Math.Round(at.X), (int)Math.Round(at.Y), button, count);

    public static void Move(Point at) =>
        UinputDevice.MoveTo((int)Math.Round(at.X), (int)Math.Round(at.Y));

    public static void Scroll(Point at, int dx, int dy) =>
        UinputDevice.Scroll((int)Math.Round(at.X), (int)Math.Round(at.Y), dx, dy);

    /// <summary>Parses a combo like "ctrl+shift+esc" (Linux modifier vocabulary: ctrl, shift, alt, super - see MCP_INTERFACE.md) and synthesizes it: all modifiers down, the final key tapped, modifiers released in reverse order.</summary>
    public static void SendKeyCombo(string combo)
    {
        var (modifiers, mainCode) = KeyCodes.ParseCombo(combo);
        foreach (int mod in modifiers) UinputDevice.KeyDown(mod);
        UinputDevice.KeyTap(mainCode);
        for (int i = modifiers.Count - 1; i >= 0; i--) UinputDevice.KeyUp(modifiers[i]);
    }

    /// <summary>Synthesizes keystrokes for arbitrary text - US-QWERTY/ASCII only, see KeyCodes' doc comment. Throws naming the first unsupported character rather than silently dropping it.</summary>
    public static void TypeText(string text)
    {
        foreach (char c in text)
        {
            if (!KeyCodes.CharMap.TryGetValue(c, out var entry))
                throw new UiCtlException($"synthesized typing doesn't support character '{c}' (U+{(int)c:X4}) - only ASCII/US-QWERTY is supported this way; a widget with a direct AT-SPI EditableText interface can accept arbitrary Unicode instead");

            if (entry.Shift) UinputDevice.KeyDown(KeyCodes.Modifiers["shift"]);
            UinputDevice.KeyTap(entry.KeyCode);
            if (entry.Shift) UinputDevice.KeyUp(KeyCodes.Modifiers["shift"]);
        }
    }
}
