using System.Runtime.InteropServices;

namespace UICtl.Core.Interop;

/// <summary>
/// Raw P/Invoke against the system's libtesseract (the C API declared in
/// tesseract's own capi.h) - the `Tesseract` NuGet package (charlesw/
/// tesseract, the usual choice) was tried first and rejected: its
/// netstandard2.0 managed assembly P/Invokes fixed Windows DLL base names
/// ("tesseract50", "leptonica-1.82.0" - confirmed via `strings` on the
/// managed assembly) and the package ships only Windows-native .dll
/// binaries, no Linux .so and no runtimes/linux-x64/native folder - it
/// cannot resolve to this machine's installed libtesseract.so.5/
/// libleptonica.so.6 at all, on any Linux distro, not just this one.
///
/// Hand-rolling the ~10-function subset this project needs (SetImage +
/// per-text-line iteration) is simpler and more transparent than fighting
/// an unmaintained Windows-oriented wrapper, and matches this project's
/// existing X11/uinput interop convention of a narrow, purpose-built P/
/// Invoke surface over a system library rather than a NuGet wrapper.
///
/// <see cref="TessBaseAPISetImage"/> takes a raw RGBA pixel buffer
/// directly - no Leptonica Pix loading needed, so despite Tesseract
/// linking against liblept internally, this file has zero direct liblept
/// P/Invoke surface of its own.
///
/// Live-verified on this machine (Ubuntu 26.04, tesseract-ocr 5.5.0,
/// libtesseract.so.5, tessdata at /usr/share/tesseract-ocr/5/tessdata):
/// <see cref="TessBaseAPIInit3"/> with a null datapath resolves the
/// correct tessdata directory via libtesseract's own built-in default
/// search - the same zero-config behavior the `tesseract` CLI itself
/// relies on - rather than reading $TESSDATA_PREFIX (unset in this
/// environment) or hand-coding a path, which would be wrong for whatever
/// tessdata directory a different Tesseract major version uses.
/// </summary>
internal static class TesseractInterop
{
    private const string LibTesseract = "libtesseract.so.5";

    /// <summary>TessPageIteratorLevel's RIL_TEXTLINE (publictypes.h) - the granularity MCP_INTERFACE.md's `uictl_ocr` documents: one block per text line, its frame the union of that line's word boxes (which is exactly what a Tesseract text-line's own bounding box already is).</summary>
    public const int RilTextline = 2;

    [DllImport(LibTesseract)]
    public static extern IntPtr TessBaseAPICreate();

    [DllImport(LibTesseract, CharSet = CharSet.Ansi)]
    public static extern int TessBaseAPIInit3(IntPtr handle, string? datapath, string language);

    [DllImport(LibTesseract)]
    public static extern void TessBaseAPISetImage(IntPtr handle, byte[] imageData, int width, int height, int bytesPerPixel, int bytesPerLine);

    [DllImport(LibTesseract)]
    public static extern void TessBaseAPIRecognize(IntPtr handle, IntPtr monitor);

    [DllImport(LibTesseract)]
    public static extern IntPtr TessBaseAPIGetIterator(IntPtr handle);

    [DllImport(LibTesseract)]
    public static extern void TessBaseAPIEnd(IntPtr handle);

    [DllImport(LibTesseract)]
    public static extern void TessBaseAPIDelete(IntPtr handle);

    /// <summary>Returns non-zero while another text line remains; 0 once iteration is exhausted. Declared as `int`, not `bool`, deliberately - capi.h's own BOOL is a plain `int` typedef for C-ABI stability, not a 1-byte C++ bool, so this avoids relying on the CLR's default bool-marshaling assumption matching that.</summary>
    [DllImport(LibTesseract)]
    public static extern int TessResultIteratorNext(IntPtr iterator, int level);

    /// <summary>Returns a heap-allocated UTF-8 C string the caller must free with <see cref="TessDeleteText"/>.</summary>
    [DllImport(LibTesseract)]
    public static extern IntPtr TessResultIteratorGetUTF8Text(IntPtr iterator, int level);

    /// <summary>0-100 scale (MCP_INTERFACE.md documents uictl_ocr's confidence as 0-1, normalized like macOS - callers divide by 100).</summary>
    [DllImport(LibTesseract)]
    public static extern float TessResultIteratorConfidence(IntPtr iterator, int level);

    /// <summary>A TessResultIterator* is safely passable where a TessPageIterator* is expected - ResultIterator is a PageIterator subclass in Tesseract's C++ API, and capi.h's C wrapper functions are designed around that. left/top/right/bottom are pixel coordinates in the image passed to SetImage.</summary>
    [DllImport(LibTesseract)]
    public static extern int TessPageIteratorBoundingBox(IntPtr iterator, int level, out int left, out int top, out int right, out int bottom);

    [DllImport(LibTesseract)]
    public static extern void TessDeleteText(IntPtr text);

    [DllImport(LibTesseract)]
    public static extern void TessResultIteratorDelete(IntPtr iterator);
}
