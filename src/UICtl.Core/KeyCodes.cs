using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// US-QWERTY keycode tables for synthesized input - see the input-synthesis
/// plan's "US-QWERTY/ASCII-only for synthesized text" decision.
/// `uinput`/evdev KEY_* codes are physical-key codes, not Unicode input, so
/// there is no general "insert this codepoint" primitive at this layer.
/// Values verified against this machine's
/// /usr/include/linux/input-event-codes.h (Ubuntu 26.04), not guessed.
/// </summary>
internal static class KeyCodes
{
    /// <summary>char -> (keycode, needs Shift). Covers what a synthesized `type` call can send.</summary>
    public static readonly IReadOnlyDictionary<char, (int KeyCode, bool Shift)> CharMap = BuildCharMap();

    /// <summary>Named key/modifier tokens for `key`'s combo string (e.g. "ctrl+shift+esc") - case-insensitive lookup, see NameMap accessor below.</summary>
    private static readonly Dictionary<string, int> NamedKeys = BuildNamedKeys();

    /// <summary>Linux modifier vocabulary per MCP_INTERFACE.md: ctrl, shift, alt, super (not macOS's cmd/fn or Windows' win).</summary>
    public static readonly IReadOnlyDictionary<string, int> Modifiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = KEY_LEFTCTRL,
        ["shift"] = KEY_LEFTSHIFT,
        ["alt"] = KEY_LEFTALT,
        ["super"] = KEY_LEFTMETA,
    };

    /// <summary>Resolves one combo token (a named key, or a single character falling back to <see cref="CharMap"/>) to a keycode. Throws naming the token if nothing matches.</summary>
    public static int ResolveKeyToken(string token)
    {
        if (NamedKeys.TryGetValue(token, out int code)) return code;
        if (token.Length == 1 && CharMap.TryGetValue(token[0], out var entry)) return entry.KeyCode;
        throw new UiCtlException($"unrecognized key \"{token}\" in combo");
    }

    /// <summary>Pure parse of a combo string like "ctrl+shift+esc" into its modifier keycodes (in the order given) and its one non-modifier key. Kept separate from InputSynthesis.SendKeyCombo so it's testable without touching /dev/uinput.</summary>
    public static (IReadOnlyList<int> Modifiers, int MainKeyCode) ParseCombo(string combo)
    {
        string[] tokens = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            throw new UiCtlException("empty key combo");

        var modifiers = new List<int>();
        string? mainToken = null;
        foreach (string token in tokens)
        {
            if (Modifiers.TryGetValue(token, out int modCode))
                modifiers.Add(modCode);
            else if (mainToken is null)
                mainToken = token;
            else
                throw new UiCtlException($"combo \"{combo}\" has more than one non-modifier key");
        }
        if (mainToken is null)
            throw new UiCtlException($"combo \"{combo}\" has no non-modifier key to send");

        return (modifiers, ResolveKeyToken(mainToken));
    }

    private const int KEY_ESC = 1, KEY_1 = 2, KEY_2 = 3, KEY_3 = 4, KEY_4 = 5, KEY_5 = 6, KEY_6 = 7, KEY_7 = 8, KEY_8 = 9, KEY_9 = 10, KEY_0 = 11;
    private const int KEY_MINUS = 12, KEY_EQUAL = 13, KEY_BACKSPACE = 14, KEY_TAB = 15;
    private const int KEY_Q = 16, KEY_W = 17, KEY_E = 18, KEY_R = 19, KEY_T = 20, KEY_Y = 21, KEY_U = 22, KEY_I = 23, KEY_O = 24, KEY_P = 25;
    private const int KEY_LEFTBRACE = 26, KEY_RIGHTBRACE = 27, KEY_ENTER = 28, KEY_LEFTCTRL = 29;
    private const int KEY_A = 30, KEY_S = 31, KEY_D = 32, KEY_F = 33, KEY_G = 34, KEY_H = 35, KEY_J = 36, KEY_K = 37, KEY_L = 38;
    private const int KEY_SEMICOLON = 39, KEY_APOSTROPHE = 40, KEY_GRAVE = 41, KEY_LEFTSHIFT = 42, KEY_BACKSLASH = 43;
    private const int KEY_Z = 44, KEY_X = 45, KEY_C = 46, KEY_V = 47, KEY_B = 48, KEY_N = 49, KEY_M = 50;
    private const int KEY_COMMA = 51, KEY_DOT = 52, KEY_SLASH = 53, KEY_LEFTALT = 56, KEY_SPACE = 57;
    private const int KEY_F1 = 59, KEY_F2 = 60, KEY_F3 = 61, KEY_F4 = 62, KEY_F5 = 63, KEY_F6 = 64, KEY_F7 = 65, KEY_F8 = 66, KEY_F9 = 67, KEY_F10 = 68;
    private const int KEY_F11 = 87, KEY_F12 = 88;
    private const int KEY_HOME = 102, KEY_UP = 103, KEY_PAGEUP = 104, KEY_LEFT = 105, KEY_RIGHT = 106, KEY_END = 107, KEY_DOWN = 108, KEY_PAGEDOWN = 109;
    private const int KEY_INSERT = 110, KEY_DELETE = 111, KEY_LEFTMETA = 125;

    private static IReadOnlyDictionary<char, (int, bool)> BuildCharMap()
    {
        var map = new Dictionary<char, (int, bool)>();

        void Letter(char lower, int code)
        {
            map[lower] = (code, false);
            map[char.ToUpperInvariant(lower)] = (code, true);
        }

        Letter('q', KEY_Q); Letter('w', KEY_W); Letter('e', KEY_E); Letter('r', KEY_R); Letter('t', KEY_T);
        Letter('y', KEY_Y); Letter('u', KEY_U); Letter('i', KEY_I); Letter('o', KEY_O); Letter('p', KEY_P);
        Letter('a', KEY_A); Letter('s', KEY_S); Letter('d', KEY_D); Letter('f', KEY_F); Letter('g', KEY_G);
        Letter('h', KEY_H); Letter('j', KEY_J); Letter('k', KEY_K); Letter('l', KEY_L);
        Letter('z', KEY_Z); Letter('x', KEY_X); Letter('c', KEY_C); Letter('v', KEY_V); Letter('b', KEY_B);
        Letter('n', KEY_N); Letter('m', KEY_M);

        void Digit(char ch, int code, char shifted) { map[ch] = (code, false); map[shifted] = (code, true); }
        Digit('1', KEY_1, '!'); Digit('2', KEY_2, '@'); Digit('3', KEY_3, '#'); Digit('4', KEY_4, '$');
        Digit('5', KEY_5, '%'); Digit('6', KEY_6, '^'); Digit('7', KEY_7, '&'); Digit('8', KEY_8, '*');
        Digit('9', KEY_9, '('); Digit('0', KEY_0, ')');

        map[' '] = (KEY_SPACE, false);
        map['\n'] = (KEY_ENTER, false);
        map['\t'] = (KEY_TAB, false);
        map['-'] = (KEY_MINUS, false); map['_'] = (KEY_MINUS, true);
        map['='] = (KEY_EQUAL, false); map['+'] = (KEY_EQUAL, true);
        map['['] = (KEY_LEFTBRACE, false); map['{'] = (KEY_LEFTBRACE, true);
        map[']'] = (KEY_RIGHTBRACE, false); map['}'] = (KEY_RIGHTBRACE, true);
        map[';'] = (KEY_SEMICOLON, false); map[':'] = (KEY_SEMICOLON, true);
        map['\''] = (KEY_APOSTROPHE, false); map['"'] = (KEY_APOSTROPHE, true);
        map['`'] = (KEY_GRAVE, false); map['~'] = (KEY_GRAVE, true);
        map['\\'] = (KEY_BACKSLASH, false); map['|'] = (KEY_BACKSLASH, true);
        map[','] = (KEY_COMMA, false); map['<'] = (KEY_COMMA, true);
        map['.'] = (KEY_DOT, false); map['>'] = (KEY_DOT, true);
        map['/'] = (KEY_SLASH, false); map['?'] = (KEY_SLASH, true);

        return map;
    }

    private static Dictionary<string, int> BuildNamedKeys()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["esc"] = KEY_ESC, ["escape"] = KEY_ESC,
            ["tab"] = KEY_TAB,
            ["space"] = KEY_SPACE,
            ["enter"] = KEY_ENTER, ["return"] = KEY_ENTER,
            ["backspace"] = KEY_BACKSPACE,
            ["delete"] = KEY_DELETE, ["del"] = KEY_DELETE,
            ["insert"] = KEY_INSERT,
            ["home"] = KEY_HOME,
            ["end"] = KEY_END,
            ["pageup"] = KEY_PAGEUP, ["pgup"] = KEY_PAGEUP,
            ["pagedown"] = KEY_PAGEDOWN, ["pgdn"] = KEY_PAGEDOWN,
            ["up"] = KEY_UP, ["down"] = KEY_DOWN, ["left"] = KEY_LEFT, ["right"] = KEY_RIGHT,
            ["f1"] = KEY_F1, ["f2"] = KEY_F2, ["f3"] = KEY_F3, ["f4"] = KEY_F4, ["f5"] = KEY_F5, ["f6"] = KEY_F6,
            ["f7"] = KEY_F7, ["f8"] = KEY_F8, ["f9"] = KEY_F9, ["f10"] = KEY_F10, ["f11"] = KEY_F11, ["f12"] = KEY_F12,
        };
        foreach (char c in "abcdefghijklmnopqrstuvwxyz0123456789")
            map[c.ToString()] = CharMap[c].KeyCode;
        return map;
    }
}
