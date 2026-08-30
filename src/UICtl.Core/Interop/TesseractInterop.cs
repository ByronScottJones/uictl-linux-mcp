using System.Reflection;
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
/// P/Invoke surface of its own. Tesseract's SetImage copies the pixel
/// data into its own internal Leptonica Pix synchronously during the
/// call rather than retaining the caller's pointer (documented API
/// behavior, precisely so callers in GC'd languages don't have to keep
/// the buffer alive past the call) - confirmed empirically too, in
/// OcrTests.cs's origin-offset test, which reuses the same managed
/// byte[] across two separate Recognize() calls with no corruption. No
/// manual pinning beyond the CLR's own automatic per-call array pinning
/// is needed as a result.
///
/// Live-verified on this machine (Ubuntu 26.04, tesseract-ocr 5.5.0,
/// libtesseract.so.5, tessdata at /usr/share/tesseract-ocr/5/tessdata):
/// <see cref="TessBaseAPIInit3"/> with a null datapath resolves the
/// correct tessdata directory via libtesseract's own built-in default
/// search - the same zero-config behavior the `tesseract` CLI itself
/// relies on - rather than reading $TESSDATA_PREFIX (unset in this
/// environment) or hand-coding a path, which would be wrong for whatever
/// tessdata directory a different Tesseract major version uses.
///
/// The DllImport target below is a logical name ("uictl-tesseract"), not
/// a real library - <see cref="ResolveLibrary"/> intercepts it and tries
/// each real SONAME in <see cref="CandidateLibraryNames"/> in turn, since
/// `preflight.sh`'s plain `apt install tesseract-ocr` can hand a caller
/// either libtesseract.so.5 (this machine, and current Ubuntu/Debian) or
/// libtesseract.so.4 (older LTS releases still in the wild) depending on
/// what the distro's default `tesseract-ocr` package resolves to. Only
/// .so.5 is live-verified here (no .so.4 machine available to this
/// project) - the capi.h surface used below has been stable since
/// Tesseract 3.x, so .so.4 is reasoned, not verified, to work the same
/// way; revisit if that turns out wrong on a real 4.x machine.
/// </summary>
internal static class TesseractInterop
{
    private const string LogicalLibName = "uictl-tesseract";
    private static readonly string[] CandidateLibraryNames = { "libtesseract.so.5", "libtesseract.so.4" };

    /// <summary>TessPageIteratorLevel's RIL_TEXTLINE (publictypes.h) - the granularity MCP_INTERFACE.md's `uictl_ocr` documents: one block per text line, its frame the union of that line's word boxes (which is exactly what a Tesseract text-line's own bounding box already is).</summary>
    public const int RilTextline = 2;

    static TesseractInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(TesseractInterop).Assembly, ResolveLibrary);
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LogicalLibName)
            return IntPtr.Zero; // not ours - let the default resolver handle this assembly's other DllImports (X11, uinput, ...)

        foreach (string candidate in CandidateLibraryNames)
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        }
        throw new DllNotFoundException($"none of [{string.Join(", ", CandidateLibraryNames)}] could be loaded - is tesseract-ocr installed? See scripts/preflight.sh / `uictl permissions`.");
    }

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessBaseAPICreate();

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int TessBaseAPIInit3(IntPtr handle, string? datapath, string language);

    /// <summary>imageData is straight (non-premultiplied) RGBA, row-major, bytesPerPixel=4, bytesPerLine=width*4 - the same layout PngCodec.Decode/X11ImageInterop.CaptureRgba already produce, so callers never need to convert.</summary>
    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPISetImage(IntPtr handle, byte[] imageData, int width, int height, int bytesPerPixel, int bytesPerLine);

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPIRecognize(IntPtr handle, IntPtr monitor);

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessBaseAPIGetIterator(IntPtr handle);

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPIEnd(IntPtr handle);

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPIDelete(IntPtr handle);

    /// <summary>Returns non-zero while another text line remains; 0 once iteration is exhausted. Declared as `int`, not `bool`, deliberately - capi.h's own BOOL is a plain `int` typedef for C-ABI stability, not a 1-byte C++ bool, so this avoids relying on the CLR's default bool-marshaling assumption matching that.</summary>
    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessResultIteratorNext(IntPtr iterator, int level);

    /// <summary>Returns a heap-allocated UTF-8 C string the caller must free with <see cref="TessDeleteText"/>.</summary>
    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessResultIteratorGetUTF8Text(IntPtr iterator, int level);

    /// <summary>0-100 scale (MCP_INTERFACE.md documents uictl_ocr's confidence as 0-1, normalized like macOS - callers divide by 100).</summary>
    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float TessResultIteratorConfidence(IntPtr iterator, int level);

    /// <summary>A TessResultIterator* is safely passable where a TessPageIterator* is expected - ResultIterator is a PageIterator subclass in Tesseract's C++ API, and capi.h's C wrapper functions are designed around that. left/top/right/bottom are pixel coordinates in the image passed to SetImage.</summary>
    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessPageIteratorBoundingBox(IntPtr iterator, int level, out int left, out int top, out int right, out int bottom);

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessDeleteText(IntPtr text);

    [DllImport(LogicalLibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessResultIteratorDelete(IntPtr iterator);
}
