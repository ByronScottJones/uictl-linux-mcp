using UICtl.Core;

namespace UICtl.Ipc;

internal static class Parsing
{
    public static Point ParsePoint(string text)
    {
        var parts = text.Split(',').Select(s => s.Trim()).ToArray();
        if (parts.Length != 2 || !double.TryParse(parts[0], out var x) || !double.TryParse(parts[1], out var y))
            throw new UiCtlException($"expected \"x,y\", got \"{text}\"");
        return new Point(x, y);
    }

    public static Frame ParseFrame(string text)
    {
        var parts = text.Split(',').Select(s => s.Trim()).ToArray();
        if (parts.Length != 4
            || !double.TryParse(parts[0], out var x) || !double.TryParse(parts[1], out var y)
            || !double.TryParse(parts[2], out var w) || !double.TryParse(parts[3], out var h))
            throw new UiCtlException($"expected \"x,y,w,h\", got \"{text}\"");
        return new Frame(x, y, w, h);
    }
}
