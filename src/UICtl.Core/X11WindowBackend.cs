using System.Runtime.InteropServices;
using System.Text;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// Direct Xlib/EWMH window management - X11-native/forced sessions, and
/// (per ENGINEERING.md's own finding) XWayland-backed windows in a
/// Wayland session, which is genuinely the less-common case on modern
/// GNOME but still a real one. Opens/closes a fresh Display* per call,
/// same pattern as XlibScreenInterop - EWMH calls here are infrequent
/// (activate, list windows), not worth the complexity of a persistent
/// connection with its own threading/reentrancy concerns.
///
/// The one Xlib gotcha that actually matters for correctness: a format-32
/// property (used for Window/CARDINAL values, e.g. _NET_CLIENT_LIST,
/// _NET_WM_PID, _NET_ACTIVE_WINDOW) is returned by XGetWindowProperty as
/// an array of native `long` (8 bytes each on x86_64), not 4-byte int32,
/// regardless of the logical 32-bit value it holds - a classic Xlib
/// binding trap. Get this wrong and every pid/window-id read back as
/// garbage.
/// </summary>
internal sealed class X11WindowBackend : IWindowBackend
{
    public IReadOnlyList<BackendWindow> ListWindows()
    {
        IntPtr display = Open();
        try
        {
            nuint root = X11WindowInterop.XRootWindow(display, X11WindowInterop.XDefaultScreen(display));
            nuint clientListAtom = Atom(display, "_NET_CLIENT_LIST");
            nuint pidAtom = Atom(display, "_NET_WM_PID");
            nuint netNameAtom = Atom(display, "_NET_WM_NAME");
            nuint wmNameAtom = Atom(display, "WM_NAME");
            nuint utf8Atom = Atom(display, "UTF8_STRING");

            var result = new List<BackendWindow>();
            foreach (long xid in ReadFormat32Property(display, root, clientListAtom))
            {
                nuint win = (nuint)xid;
                long[] pidProp = ReadFormat32Property(display, win, pidAtom);
                int pid = pidProp.Length > 0 ? (int)pidProp[0] : 0;

                string title = ReadStringProperty(display, win, netNameAtom, utf8Atom)
                    ?? ReadStringProperty(display, win, wmNameAtom, utf8Atom)
                    ?? "";

                Frame frame = ReadFrame(display, root, win);
                result.Add(new BackendWindow(xid, pid, title, frame));
            }
            return result;
        }
        finally
        {
            X11WindowInterop.XCloseDisplay(display);
        }
    }

    public bool Activate(long id)
    {
        IntPtr display = Open();
        try
        {
            nuint root = X11WindowInterop.XRootWindow(display, X11WindowInterop.XDefaultScreen(display));
            nuint activeWindowAtom = Atom(display, "_NET_ACTIVE_WINDOW");
            byte[] message = X11WindowInterop.BuildActiveWindowClientMessage((nuint)id, activeWindowAtom);

            IntPtr eventPtr = Marshal.AllocHGlobal(message.Length);
            try
            {
                Marshal.Copy(message, 0, eventPtr, message.Length);
                int status = X11WindowInterop.XSendEvent(display, root, false,
                    X11WindowInterop.SubstructureRedirectMask | X11WindowInterop.SubstructureNotifyMask, eventPtr);
                X11WindowInterop.XSync(display, false);
                return status != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(eventPtr);
            }
        }
        finally
        {
            X11WindowInterop.XCloseDisplay(display);
        }
    }

    public BackendWindow? GetFocusedWindow()
    {
        IntPtr display = Open();
        try
        {
            nuint root = X11WindowInterop.XRootWindow(display, X11WindowInterop.XDefaultScreen(display));
            nuint activeWindowAtom = Atom(display, "_NET_ACTIVE_WINDOW");
            long[] active = ReadFormat32Property(display, root, activeWindowAtom);
            if (active.Length == 0 || active[0] == 0)
                return null;

            nuint win = (nuint)active[0];
            nuint pidAtom = Atom(display, "_NET_WM_PID");
            nuint netNameAtom = Atom(display, "_NET_WM_NAME");
            nuint wmNameAtom = Atom(display, "WM_NAME");
            nuint utf8Atom = Atom(display, "UTF8_STRING");

            long[] pidProp = ReadFormat32Property(display, win, pidAtom);
            int pid = pidProp.Length > 0 ? (int)pidProp[0] : 0;
            string title = ReadStringProperty(display, win, netNameAtom, utf8Atom)
                ?? ReadStringProperty(display, win, wmNameAtom, utf8Atom)
                ?? "";
            Frame frame = ReadFrame(display, root, win);
            return new BackendWindow(active[0], pid, title, frame);
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

    private static nuint Atom(IntPtr display, string name) => X11WindowInterop.XInternAtom(display, name, false);

    private static long[] ReadFormat32Property(IntPtr display, nuint window, nuint propertyAtom)
    {
        int status = X11WindowInterop.XGetWindowProperty(
            display, window, propertyAtom, 0, 4096, false, (nuint)X11WindowInterop.AnyPropertyType,
            out _, out int actualFormat, out nuint nitems, out _, out IntPtr propReturn);

        if (status != 0 || propReturn == IntPtr.Zero)
            return Array.Empty<long>();
        try
        {
            if (actualFormat != 32 || nitems == 0) return Array.Empty<long>();
            var result = new long[(int)nitems];
            for (int i = 0; i < result.Length; i++)
                result[i] = Marshal.ReadInt64(propReturn, i * 8); // format-32 properties are packed as native `long`, not int32 - see class doc comment
            return result;
        }
        finally
        {
            if (propReturn != IntPtr.Zero) X11WindowInterop.XFree(propReturn);
        }
    }

    private static string? ReadStringProperty(IntPtr display, nuint window, nuint propertyAtom, nuint utf8Atom)
    {
        int status = X11WindowInterop.XGetWindowProperty(
            display, window, propertyAtom, 0, 4096, false, (nuint)X11WindowInterop.AnyPropertyType,
            out nuint actualType, out int actualFormat, out nuint nitems, out _, out IntPtr propReturn);

        if (status != 0 || propReturn == IntPtr.Zero)
            return null;
        try
        {
            if (actualFormat != 8 || nitems == 0) return null;
            var bytes = new byte[(int)nitems];
            Marshal.Copy(propReturn, bytes, 0, bytes.Length);
            return actualType == utf8Atom ? Encoding.UTF8.GetString(bytes) : Encoding.ASCII.GetString(bytes);
        }
        finally
        {
            if (propReturn != IntPtr.Zero) X11WindowInterop.XFree(propReturn);
        }
    }

    private static Frame ReadFrame(IntPtr display, nuint root, nuint window)
    {
        IntPtr attrs = Marshal.AllocHGlobal(X11WindowInterop.WindowAttributesBufferSize);
        try
        {
            int status = X11WindowInterop.XGetWindowAttributes(display, window, attrs);
            if (status == 0) return default;

            var buffer = new byte[X11WindowInterop.WindowAttributesBufferSize];
            Marshal.Copy(attrs, buffer, 0, buffer.Length);
            // XWindowAttributes' x/y are relative to the window's parent
            // (not necessarily root - a reparenting WM interposes a
            // decoration frame), so only width/height are used from it;
            // position comes from XTranslateCoordinates below instead,
            // giving root-relative (screen) coords directly - the same
            // space AT-SPI/the Wayland backend report.
            (_, _, int width, int height) = X11WindowInterop.ReadWindowAttributesXYWH(buffer);
            X11WindowInterop.XTranslateCoordinates(display, window, root, 0, 0, out int rootX, out int rootY, out _);
            return new Frame(rootX, rootY, width, height);
        }
        finally
        {
            Marshal.FreeHGlobal(attrs);
        }
    }
}
