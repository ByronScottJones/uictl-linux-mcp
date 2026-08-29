using System.Runtime.InteropServices;

namespace UICtl.Core.Interop;

/// <summary>
/// Minimal Xlib P/Invoke - real screen pixel dimensions only, so the uinput
/// absolute pointer device (see UinputInterop/UinputDevice) can be ranged to
/// exactly the coordinate space AT-SPI's Component.GetExtents(screen)
/// already reports (see PLAN's "Coordinate spaces" discussion in
/// ENGINEERING.md). Works over XWayland in a Wayland session, same as any
/// other X11 client - deliberately NOT the fuller EWMH window-activation
/// backend ENGINEERING.md describes; that stays its own later phase.
/// </summary>
internal static class XlibScreenInterop
{
    // The unversioned "libX11.so" name only exists on a machine with the
    // libx11-dev package installed (it provides the dev symlink) - .NET's
    // DllImport probing on Linux does not fall back to versioned sonames on
    // its own. Every Ubuntu install has the runtime package (libx11-6),
    // which only ships libX11.so.6 - link against that exact soname
    // instead, confirmed via `ldconfig -p` on this machine, so this doesn't
    // silently depend on a dev package being present at runtime.
    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11)]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XDisplayWidth(IntPtr display, int screenNumber);

    [DllImport(LibX11)]
    private static extern int XDisplayHeight(IntPtr display, int screenNumber);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    /// <summary>Real screen pixel size via the default X11/XWayland display (`$DISPLAY`). Throws if no X server is reachable.</summary>
    public static (int Width, int Height) GetScreenSize()
    {
        IntPtr display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
            throw new UiCtlException("could not open the X11 display ($DISPLAY unset, or no X server/XWayland reachable) - needed to size the virtual input device");
        try
        {
            int screen = XDefaultScreen(display);
            int width = XDisplayWidth(display, screen);
            int height = XDisplayHeight(display, screen);
            return (width, height);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }
}
