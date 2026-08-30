namespace UICtl.Core.Interop;

/// <summary>
/// Minimal in-place drawing over a raw RGBA buffer - just enough for
/// `screenshot --annotate`'s numbered element boxes (a rectangle outline
/// plus a small solid-background number badge in the corner). No image
/// library dependency (see PngCodec.cs's doc comment for why) - digits
/// are a hand-rolled 3x5 bitmap font, scaled up for legibility, the same
/// "hand-roll a small bounded thing rather than add a dependency" spirit
/// as PngCodec's own CRC32 table.
/// </summary>
internal static class ImageDrawing
{
    private static readonly Dictionary<char, byte[]> DigitFont = new()
    {
        ['0'] = new byte[] { 0b111, 0b101, 0b101, 0b101, 0b111 },
        ['1'] = new byte[] { 0b010, 0b110, 0b010, 0b010, 0b111 },
        ['2'] = new byte[] { 0b111, 0b001, 0b111, 0b100, 0b111 },
        ['3'] = new byte[] { 0b111, 0b001, 0b111, 0b001, 0b111 },
        ['4'] = new byte[] { 0b101, 0b101, 0b111, 0b001, 0b001 },
        ['5'] = new byte[] { 0b111, 0b100, 0b111, 0b001, 0b111 },
        ['6'] = new byte[] { 0b111, 0b100, 0b111, 0b101, 0b111 },
        ['7'] = new byte[] { 0b111, 0b001, 0b010, 0b010, 0b010 },
        ['8'] = new byte[] { 0b111, 0b101, 0b111, 0b101, 0b111 },
        ['9'] = new byte[] { 0b111, 0b101, 0b111, 0b001, 0b111 },
    };

    public static void SetPixel(byte[] rgba, int width, int height, int x, int y, (byte R, byte G, byte B, byte A) color)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        int i = (y * width + x) * 4;
        rgba[i + 0] = color.R;
        rgba[i + 1] = color.G;
        rgba[i + 2] = color.B;
        rgba[i + 3] = color.A;
    }

    /// <summary>Draws a 2px-thick unfilled rectangle outline, clipped to the buffer.</summary>
    public static void DrawRectOutline(byte[] rgba, int width, int height, int x, int y, int w, int h, (byte R, byte G, byte B, byte A) color)
    {
        for (int t = 0; t < 2; t++)
        {
            for (int i = x; i < x + w; i++)
            {
                SetPixel(rgba, width, height, i, y + t, color);
                SetPixel(rgba, width, height, i, y + h - 1 - t, color);
            }
            for (int i = y; i < y + h; i++)
            {
                SetPixel(rgba, width, height, x + t, i, color);
                SetPixel(rgba, width, height, x + w - 1 - t, i, color);
            }
        }
    }

    public static void FillRect(byte[] rgba, int width, int height, int x, int y, int w, int h, (byte R, byte G, byte B, byte A) color)
    {
        for (int j = y; j < y + h; j++)
            for (int i = x; i < x + w; i++)
                SetPixel(rgba, width, height, i, j, color);
    }

    /// <summary>
    /// Draws a solid-background number badge with its top-left corner at
    /// (x, y) - scale 3 (each font pixel becomes a 3x3 block, 1px gap
    /// between digits) is legible at typical screen DPI without needing
    /// anti-aliasing.
    /// </summary>
    public static void DrawNumberBadge(byte[] rgba, int width, int height, int x, int y, int number,
        (byte R, byte G, byte B, byte A) background, (byte R, byte G, byte B, byte A) foreground, int scale = 3)
    {
        string digits = number.ToString();
        int digitWidth = 3 * scale;
        int digitHeight = 5 * scale;
        int gap = scale;
        int padding = scale;
        int badgeWidth = digits.Length * digitWidth + (digits.Length - 1) * gap + padding * 2;
        int badgeHeight = digitHeight + padding * 2;

        FillRect(rgba, width, height, x, y, badgeWidth, badgeHeight, background);

        int cursorX = x + padding;
        foreach (char c in digits)
        {
            if (DigitFont.TryGetValue(c, out var rows))
            {
                for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                {
                    if ((rows[row] & (1 << (2 - col))) == 0) continue;
                    FillRect(rgba, width, height, cursorX + col * scale, y + padding + row * scale, scale, scale, foreground);
                }
            }
            cursorX += digitWidth + gap;
        }
    }
}
