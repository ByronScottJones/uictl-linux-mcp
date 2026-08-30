using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// X11/XWayland pixel capture backing `screenshot`/`pixel` - see
/// X11ImageInterop.cs for the live-verified XImage struct handling and
/// its important caveat: root-window capture (used here for the
/// whole-desktop case) throws a raw X BadMatch error under XWayland,
/// confirmed on this dev machine, not independently verified against a
/// genuine X11 session. Capturing a specific window (the common case -
/// screenshot --app/--window) is confirmed live-working under XWayland
/// and does not depend on root capture at all.
/// </summary>
internal static class X11ScreenCapture
{
    /// <summary>Captures a specific X11/XWayland window's own content directly by its backend window id (see IWindowBackend.cs) - the robust, live-confirmed path.</summary>
    public static (byte[] Rgba, int Width, int Height) CaptureWindow(long backendWindowId, int width, int height)
    {
        IntPtr display = Open();
        try
        {
            byte[] rgba = X11ImageInterop.CaptureRgba(display, (nuint)backendWindowId, 0, 0, width, height);
            return (rgba, width, height);
        }
        finally
        {
            X11WindowInterop.XCloseDisplay(display);
        }
    }

    /// <summary>Captures a rectangle of the root window (the X11 virtual-screen space, see ENGINEERING.md's "Coordinate spaces") - the whole-desktop / --screen N case. See class doc comment: not usable under XWayland.</summary>
    public static (byte[] Rgba, int Width, int Height) CaptureRootRegion(int x, int y, int width, int height)
    {
        IntPtr display = Open();
        try
        {
            nuint root = X11WindowInterop.XRootWindow(display, X11WindowInterop.XDefaultScreen(display));
            byte[] rgba = X11ImageInterop.CaptureRgba(display, root, x, y, width, height);
            return (rgba, width, height);
        }
        finally
        {
            X11WindowInterop.XCloseDisplay(display);
        }
    }

    private static IntPtr Open()
    {
        IntPtr display = X11WindowInterop.XOpenDisplay(null);
        if (display == IntPtr.Zero)
            throw new UiCtlException("could not open the X11 display ($DISPLAY unset, or no X server/XWayland reachable)");
        return display;
    }
}
