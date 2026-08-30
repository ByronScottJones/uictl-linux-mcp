using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// `pixel` - X11 has a real cheap single-pixel-read primitive
/// (XGetImage of a 1x1 region); Wayland doesn't, so this samples a full
/// screenshot via WaylandScreenshotBackend (the same real consent-dialog
/// cost as `screenshot` - see Screenshot.cs/MCP_INTERFACE.md's own
/// warning not to use this in a tight polling loop on Wayland).
/// </summary>
public static class Pixel
{
    public static PixelColor At(Point at)
    {
        int x = (int)at.X, y = (int)at.Y;
        byte[] rgba = SessionDetection.HasWayland ? SampleWayland(x, y) : SampleX11(x, y);
        return new PixelColor(rgba[0], rgba[1], rgba[2], rgba[3]);
    }

    private static byte[] SampleX11(int x, int y)
    {
        var (rgba, _, _) = X11ScreenCapture.CaptureRootRegion(x, y, 1, 1);
        return rgba;
    }

    private static byte[] SampleWayland(int x, int y)
    {
        byte[] png = WaylandScreenshotBackend.CapturePng();
        var (rgba, w, h) = PngCodec.Decode(png);
        if (x < 0 || y < 0 || x >= w || y >= h)
            throw new UiCtlException($"({x},{y}) is outside the captured {w}x{h} screenshot");
        int i = (y * w + x) * 4;
        return rgba.AsSpan(i, 4).ToArray();
    }
}
