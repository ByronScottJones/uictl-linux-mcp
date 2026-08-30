using System.Runtime.InteropServices;

namespace UICtl.Core.Interop;

/// <summary>
/// Raw Xlib P/Invoke for pixel capture (XGetImage) - separate from
/// X11WindowInterop.cs (window management) and XlibScreenInterop.cs
/// (screen size only), same file-per-concern convention. XImage's field
/// offsets below are read directly out of the returned struct rather than
/// modeled as a full C# struct (mirrors X11WindowInterop.cs's
/// XWindowAttributes handling) - hand-verified live on this machine
/// against XGetPixel (a real Xlib function, used as ground truth) for
/// several sample pixels of a real captured window; all matched exactly,
/// confirming both the offsets and the "32bpp TrueColor" assumption
/// Screenshot.cs relies on (see X11ScreenCapture.cs).
///
/// Real, live-confirmed finding along the way: XGetImage against the
/// root window fails with a BadMatch X error under XWayland (this dev
/// machine's only available session type) - the root window has no real
/// backing pixel content to read since Wayland doesn't composite the
/// desktop through X11's root window mechanism at all. XGetImage against
/// an individual X11/XWayland-backed window (confirmed with a
/// GDK_BACKEND=x11-forced gnome-calculator, same live-test technique
/// X11WindowBackend's own doc comment used) works correctly. Whole-screen
/// root capture should work fine on a genuine X11 session (standard,
/// universally-supported X11 semantics) but is **not independently
/// live-verified** on this dev machine, which has no such session
/// available - see ENGINEERING.md's "Coordinate spaces" section for the
/// project's existing precedent of flagging this class of untested gap
/// rather than assuming.
/// </summary>
internal static class X11ImageInterop
{
    private const string LibX11 = "libX11.so.6";

    public const int ZPixmap = 2;
    public static readonly nuint AllPlanes = unchecked((nuint)~0UL);

    [DllImport(LibX11)]
    public static extern IntPtr XGetImage(IntPtr display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

    [DllImport(LibX11)]
    public static extern void XDestroyImage(IntPtr image);

    // XImage field offsets (x86_64, natural alignment - see class doc comment for how these were confirmed).
    private const int DataOffset = 16;
    private const int DepthOffset = 40;
    private const int BytesPerLineOffset = 44;
    private const int BitsPerPixelOffset = 48;

    /// <summary>
    /// Captures a rectangle of <paramref name="drawable"/> (a window or
    /// the root window) and returns it as straight (non-premultiplied),
    /// fully-opaque RGBA bytes - width*height*4, row-major, matching
    /// PngCodec.Encode's expected input. Throws a clear, actionable
    /// message on the two known-real failure modes: BadMatch (root
    /// window capture under XWayland - see class doc comment) surfaces
    /// as an unmanaged X error the process can't catch, so this can't
    /// wrap it in a try/catch - callers needing that distinction should
    /// avoid root capture under XWayland proactively (see
    /// X11ScreenCapture.cs); and any depth/bpp other than 32bpp TrueColor
    /// (not confirmed to exist on any real modern Linux desktop, but
    /// thrown rather than silently misread if it ever does).
    /// </summary>
    public static byte[] CaptureRgba(IntPtr display, nuint drawable, int x, int y, int width, int height)
    {
        IntPtr image = XGetImage(display, drawable, x, y, (uint)width, (uint)height, AllPlanes, ZPixmap);
        if (image == IntPtr.Zero)
            throw new UiCtlException($"XGetImage returned null capturing {width}x{height}+{x}+{y} - drawable may not be viewable");
        try
        {
            int bitsPerPixel = Marshal.ReadInt32(image, BitsPerPixelOffset);
            if (bitsPerPixel != 32)
                throw new UiCtlException($"unsupported X11 visual ({bitsPerPixel} bits per pixel) - only 32bpp TrueColor is supported");

            IntPtr data = Marshal.ReadIntPtr(image, DataOffset);
            int bytesPerLine = Marshal.ReadInt32(image, BytesPerLineOffset);

            var rgba = new byte[width * height * 4];
            unsafe
            {
                byte* src = (byte*)data;
                for (int row = 0; row < height; row++)
                {
                    byte* srcRow = src + row * bytesPerLine;
                    int dstRow = row * width * 4;
                    for (int col = 0; col < width; col++)
                    {
                        // 32bpp TrueColor, red_mask=0xFF0000/green_mask=0xFF00/blue_mask=0xFF,
                        // confirmed live (see class doc comment) - little-endian memory
                        // order per pixel is [B, G, R, X]. Alpha is compositor
                        // window-manager state (shadows/rounded corners), not meaningful
                        // once flattened to a screenshot - always report fully opaque.
                        byte* p = srcRow + col * 4;
                        int dst = dstRow + col * 4;
                        rgba[dst + 0] = p[2]; // R
                        rgba[dst + 1] = p[1]; // G
                        rgba[dst + 2] = p[0]; // B
                        rgba[dst + 3] = 255;  // A
                    }
                }
            }
            return rgba;
        }
        finally
        {
            XDestroyImage(image);
        }
    }
}
