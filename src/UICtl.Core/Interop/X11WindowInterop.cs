using System.Runtime.InteropServices;

namespace UICtl.Core.Interop;

/// <summary>
/// Raw Xlib/EWMH P/Invoke for the X11 window-management backend
/// (list/activate/focused-window) - a well-documented, ~25-year-old,
/// stable protocol (freedesktop.org's Extended Window Manager Hints).
/// Deliberately separate from Interop/XlibScreenInterop.cs, whose doc
/// comment explicitly scopes it to screen-size-only. Predefined atom
/// values (XA_*) are from &lt;X11/Xatom.h&gt; - fixed, stable constants
/// across every X11 implementation, not looked up via XInternAtom.
/// </summary>
internal static class X11WindowInterop
{
    public const nuint XaCardinal = 6;
    public const nuint XaWindow = 33;
    public const nuint XaAtom = 4;

    public const int AnyPropertyType = 0;
    public const int ClientMessageType = 33; // X.h ClientMessage event type
    public const long SubstructureRedirectMask = 1L << 20;
    public const long SubstructureNotifyMask = 1L << 19;

    [DllImport(LibX11)]
    public static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    public static extern int XDefaultScreen(IntPtr display);

    [DllImport(LibX11)]
    public static extern nuint XRootWindow(IntPtr display, int screenNumber);

    [DllImport(LibX11)]
    public static extern nuint XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(
        IntPtr display, nuint w, nuint property,
        nint longOffset, nint longLength, [MarshalAs(UnmanagedType.Bool)] bool delete,
        nuint reqType, out nuint actualTypeReturn, out int actualFormatReturn,
        out nuint nitemsReturn, out nuint bytesAfterReturn, out IntPtr propReturn);

    [DllImport(LibX11)]
    public static extern int XFree(IntPtr data);

    [DllImport(LibX11)]
    public static extern int XSendEvent(IntPtr display, nuint w, [MarshalAs(UnmanagedType.Bool)] bool propagate, long eventMask, IntPtr eventSend);

    [DllImport(LibX11)]
    public static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [DllImport(LibX11)]
    public static extern int XGetWindowAttributes(IntPtr display, nuint w, IntPtr attributesReturn);

    [DllImport(LibX11)]
    public static extern int XTranslateCoordinates(IntPtr display, nuint srcW, nuint destW, int srcX, int srcY, out int destXReturn, out int destYReturn, out nuint childReturn);

    // The unversioned "libX11.so" name only exists with libx11-dev
    // installed - not guaranteed on a production machine. Same finding as
    // XlibScreenInterop.cs (verified via `ldconfig -p` there).
    private const string LibX11 = "libX11.so.6";

    /// <summary>
    /// struct XWindowAttributes is large (~25 fields) but x/y/width/height
    /// are the first four ints, which is all this project needs - read
    /// them directly out of a buffer sized generously for the whole
    /// struct (Xlib never writes past its own definition; over-allocating
    /// is just wasted stack, not a correctness risk).
    /// </summary>
    public const int WindowAttributesBufferSize = 512;

    public static (int X, int Y, int Width, int Height) ReadWindowAttributesXYWH(byte[] buffer) =>
    (
        BitConverter.ToInt32(buffer, 0),  // x
        BitConverter.ToInt32(buffer, 4),  // y
        BitConverter.ToInt32(buffer, 8),  // width
        BitConverter.ToInt32(buffer, 12)  // height
    );

    /// <summary>
    /// Builds the raw ~96-byte XClientMessageEvent payload (within the
    /// standard 192-byte XEvent union padding - `long pad[24]` on 64-bit)
    /// needed to send an EWMH _NET_ACTIVE_WINDOW request - the documented
    /// way any EWMH window manager (including Mutter's X11 mode) accepts
    /// an external activation request. Field offsets verified against
    /// standard x86_64 struct layout (natural alignment, no explicit
    /// packing) for XClientMessageEvent as declared in X11/Xlib.h.
    /// </summary>
    public static byte[] BuildActiveWindowClientMessage(nuint target, nuint netActiveWindowAtom)
    {
        var buf = new byte[192];
        BitConverter.GetBytes(ClientMessageType).CopyTo(buf, 0);   // type
        // serial (8, offset 8), send_event (4, offset 16), display ptr (8, offset 24) - left zero, Xlib fills serial/send_event itself on send
        BitConverter.GetBytes((ulong)target).CopyTo(buf, 32);      // window: the window we want activated
        BitConverter.GetBytes((ulong)netActiveWindowAtom).CopyTo(buf, 40); // message_type
        BitConverter.GetBytes(32).CopyTo(buf, 48);                 // format = 32 (data.l is the active union member)
        // data.l[0] = source indication (1 = normal application), data.l[1] = timestamp (0 = unknown/CurrentTime, acceptable per spec for a non-interactive requestor), data.l[2] = requestor's currently active window (0 = unknown)
        BitConverter.GetBytes(1L).CopyTo(buf, 56);
        BitConverter.GetBytes(0L).CopyTo(buf, 64);
        BitConverter.GetBytes(0L).CopyTo(buf, 72);
        return buf;
    }
}
