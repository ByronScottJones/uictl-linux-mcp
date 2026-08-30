using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// `ocr` - runs Tesseract (TesseractEngine.cs/Interop/TesseractInterop.cs)
/// over either a standalone PNG file (<paramref name="imagePath"/>) or a
/// live capture, using the exact same capture pipeline as `screenshot`
/// (Screenshot.CaptureFrame/WholeScreenFrame) - so on Wayland this shows
/// the same real consent dialog every call (see AGENTS.md), and on X11
/// it's the same instant XGetImage path. With no window/app/region given,
/// OCRs the whole screen, mirroring plain `screenshot`'s own default.
///
/// <paramref name="region"/> is always an absolute screen-space rectangle
/// (the same space `elements` frames use), regardless of whether
/// <paramref name="windowId"/>/<paramref name="appSelector"/> also
/// narrowed the capture source - it's applied as a further crop against
/// whichever frame was captured. That one rule naturally covers "region of
/// the whole screen" (no window/app given) and "region within a window"
/// (both given) without two separate code paths.
/// </summary>
public static class Ocr
{
    public static IReadOnlyList<TextBlock> Read(string? imagePath, long? windowId, string? appSelector, Frame? region)
    {
        if (imagePath is not null)
        {
            if (windowId is not null || appSelector is not null || region is not null)
                throw new UiCtlException("\"image\" can't be combined with \"window\"/\"app\"/\"region\" - it OCRs a standalone file, not a live capture");

            var (rgba, w, h) = PngCodec.Decode(File.ReadAllBytes(imagePath));
            return TesseractEngine.Recognize(rgba, w, h, 0, 0);
        }

        WindowInfo? target = null;
        if (windowId is not null || appSelector is not null)
        {
            var resolved = WindowResolver.Resolve(windowId, appSelector);
            target = Accessibility.ListWindows(resolved.Pid).FirstOrDefault(w => w.WindowId == resolved.WindowId)
                ?? throw new UiCtlException($"window {resolved.WindowId} not found (re-run `windows` - ids are re-issued per listing, see AGENTS.md)");
        }

        Frame sourceFrame = target?.Frame ?? Screenshot.WholeScreenFrame();
        int sourceWidth = (int)sourceFrame.W, sourceHeight = (int)sourceFrame.H;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new UiCtlException($"resolved a {sourceWidth}x{sourceHeight} capture area - nothing to OCR");

        byte[] captured = Screenshot.CaptureFrame(target, sourceFrame, sourceWidth, sourceHeight);

        if (region is not { } requested)
            return TesseractEngine.Recognize(captured, sourceWidth, sourceHeight, sourceFrame.X, sourceFrame.Y);

        int cropX = (int)(requested.X - sourceFrame.X), cropY = (int)(requested.Y - sourceFrame.Y);
        int cropW = (int)requested.W, cropH = (int)requested.H;
        if (cropW <= 0 || cropH <= 0)
            throw new UiCtlException($"\"region\" resolved to a {cropW}x{cropH} area - nothing to OCR");

        byte[] cropped = Screenshot.CropRgba(captured, sourceWidth, sourceHeight, cropX, cropY, cropW, cropH);
        return TesseractEngine.Recognize(cropped, cropW, cropH, requested.X, requested.Y);
    }
}
