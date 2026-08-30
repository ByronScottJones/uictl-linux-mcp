using UICtl.Core.Interop;

namespace UICtl.Core;

public sealed record ScreenshotResult(string Path, int Width, int Height, IReadOnlyList<AnnotatedElement>? Elements);

/// <summary>
/// `screenshot` - dispatches to X11ScreenCapture or WaylandScreenshotBackend
/// by session type (see SessionDetection), same split as activate/focus.*
/// but for a different reason: X11 has a native, instant pixel-capture
/// primitive (XGetImage) while Wayland has none at all - every Wayland
/// screenshot goes through the portal's real capture-then-save-to-disk
/// flow (see WaylandScreenshotBackend.cs), decoded back into raw pixels
/// here only when a crop (--window/--app/--screen) or --annotate needs
/// pixel access; a plain whole-desktop capture skips that round trip.
/// </summary>
public static class Screenshot
{
    public static ScreenshotResult Capture(long? windowId, string? appSelector, int? screenIndex, bool annotate, string? roleFilter, string outPath)
    {
        if (annotate && windowId is null && appSelector is null)
            throw new UiCtlException("--annotate requires --app or --window (elements are walked from a specific window)");

        WindowInfo? target = null;
        if (windowId is not null || appSelector is not null)
        {
            var resolved = WindowResolver.Resolve(windowId, appSelector);
            target = Accessibility.ListWindows(resolved.Pid).FirstOrDefault(w => w.WindowId == resolved.WindowId)
                ?? throw new UiCtlException($"window {resolved.WindowId} not found (re-run `windows` - ids are re-issued per listing, see AGENTS.md)");
        }

        Frame captureFrame = target?.Frame ?? WholeScreenFrame(screenIndex);
        int width = (int)captureFrame.W, height = (int)captureFrame.H;
        if (width <= 0 || height <= 0)
            throw new UiCtlException($"resolved a {width}x{height} capture area - nothing to capture");

        byte[] rgba = SessionDetection.HasWayland
            ? CaptureWaylandCropped(captureFrame, width, height)
            : CaptureX11(target, captureFrame, width, height);

        List<AnnotatedElement>? elements = null;
        if (annotate)
        {
            var options = new ElementWalkOptions(RoleFilter: roleFilter);
            var walk = Accessibility.WalkWindow(target!.WindowId, options);
            elements = new List<AnnotatedElement>(walk.Elements.Count);
            int number = 1;
            foreach (var el in walk.Elements)
            {
                elements.Add(new AnnotatedElement(number, el));
                int ex = (int)(el.Frame.X - captureFrame.X);
                int ey = (int)(el.Frame.Y - captureFrame.Y);
                int ew = (int)el.Frame.W, eh = (int)el.Frame.H;
                var color = ((byte)255, (byte)64, (byte)32, (byte)255);
                ImageDrawing.DrawRectOutline(rgba, width, height, ex, ey, ew, eh, color);
                ImageDrawing.DrawNumberBadge(rgba, width, height, ex, ey, number, color, ((byte)255, (byte)255, (byte)255, (byte)255));
                number++;
            }
        }

        byte[] png = PngCodec.Encode(rgba, width, height);
        string fullOutPath = Path.GetFullPath(outPath);
        string? dir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(fullOutPath, png);

        return new ScreenshotResult(fullOutPath, width, height, elements);
    }

    private static Frame WholeScreenFrame(int? screenIndex)
    {
        if (screenIndex is { } idx)
        {
            var display = DisplayConfig.List().FirstOrDefault(d => d.Index == idx)
                ?? throw new UiCtlException($"no display with index {idx} - see `uictl displays`");
            return display.Frame;
        }
        var (w, h) = XlibScreenInterop.GetScreenSize();
        return new Frame(0, 0, w, h);
    }

    private static byte[] CaptureX11(WindowInfo? target, Frame captureFrame, int width, int height)
    {
        if (target is not null)
        {
            var backendWindow = WindowActivation.ResolveTarget(new X11WindowBackend(), target.Pid, target.WindowId);
            var (rgba, _, _) = X11ScreenCapture.CaptureWindow(backendWindow.Id, width, height);
            return rgba;
        }
        var (fullRgba, _, _) = X11ScreenCapture.CaptureRootRegion((int)captureFrame.X, (int)captureFrame.Y, width, height);
        return fullRgba;
    }

    private static byte[] CaptureWaylandCropped(Frame captureFrame, int width, int height)
    {
        byte[] png = WaylandScreenshotBackend.CapturePng();
        var (fullRgba, fullW, fullH) = PngCodec.Decode(png);
        if ((int)captureFrame.X == 0 && (int)captureFrame.Y == 0 && width == fullW && height == fullH)
            return fullRgba;
        return CropRgba(fullRgba, fullW, fullH, (int)captureFrame.X, (int)captureFrame.Y, width, height);
    }

    /// <summary>Crops a raw RGBA buffer, clipping to the source bounds (a stale/out-of-range AT-SPI frame reports transparent black for whatever falls outside the actual captured image rather than throwing).</summary>
    internal static byte[] CropRgba(byte[] src, int srcW, int srcH, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int sy = y + row;
            if (sy < 0 || sy >= srcH) continue;
            int startX = Math.Max(x, 0);
            int endX = Math.Min(x + w, srcW);
            int copyWidth = endX - startX;
            if (copyWidth <= 0) continue;

            int srcOffset = (sy * srcW + startX) * 4;
            int dstOffset = (row * w + (startX - x)) * 4;
            Buffer.BlockCopy(src, srcOffset, dst, dstOffset, copyWidth * 4);
        }
        return dst;
    }
}
