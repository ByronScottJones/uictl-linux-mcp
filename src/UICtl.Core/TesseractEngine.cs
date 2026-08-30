using System.Runtime.InteropServices;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// One long-lived libtesseract engine handle, created once and cached for
/// the daemon's lifetime - same rationale as UinputDevice.cs's cached
/// uinput device: TessBaseAPIInit3 loads the English trained-data model
/// from disk, a real (if modest) cost not worth repeating on every `ocr`
/// call. Safe to reuse across calls with no locking: DaemonServer.cs
/// handles one connection at a time (see its own doc comment), and
/// Tesseract's own API is explicitly designed for repeated SetImage/
/// Recognize calls on the same handle - only End()/Delete() at process
/// exit, not between individual OCR calls.
/// </summary>
internal static class TesseractEngine
{
    private static readonly Lazy<IntPtr> LazyHandle = new(Create);

    static TesseractEngine()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!LazyHandle.IsValueCreated) return;
            TesseractInterop.TessBaseAPIEnd(LazyHandle.Value);
            TesseractInterop.TessBaseAPIDelete(LazyHandle.Value);
        };
    }

    /// <summary>Runs OCR over a raw RGBA buffer, returning one TextBlock per non-blank text line with its frame offset by (originX, originY) - the on-screen (or in-source-image) origin of that buffer, so callers capturing a cropped region can report frames in the same absolute space as `elements`.</summary>
    public static IReadOnlyList<TextBlock> Recognize(byte[] rgba, int width, int height, double originX, double originY)
    {
        IntPtr api = LazyHandle.Value;
        TesseractInterop.TessBaseAPISetImage(api, rgba, width, height, 4, width * 4);
        TesseractInterop.TessBaseAPIRecognize(api, IntPtr.Zero);

        var blocks = new List<TextBlock>();
        IntPtr iter = TesseractInterop.TessBaseAPIGetIterator(api);
        if (iter == IntPtr.Zero)
            return blocks; // no recognizable text at all - not an error

        try
        {
            do
            {
                IntPtr textPtr = TesseractInterop.TessResultIteratorGetUTF8Text(iter, TesseractInterop.RilTextline);
                string text = (Marshal.PtrToStringUTF8(textPtr) ?? "").Trim();
                TesseractInterop.TessDeleteText(textPtr);
                if (text.Length == 0) continue; // Tesseract can emit whitespace-only lines between real ones

                float confidence = TesseractInterop.TessResultIteratorConfidence(iter, TesseractInterop.RilTextline);
                TesseractInterop.TessPageIteratorBoundingBox(iter, TesseractInterop.RilTextline, out int left, out int top, out int right, out int bottom);

                var frame = new Frame(originX + left, originY + top, right - left, bottom - top);
                blocks.Add(new TextBlock(text, frame, confidence / 100.0));
            } while (TesseractInterop.TessResultIteratorNext(iter, TesseractInterop.RilTextline) != 0);
        }
        finally
        {
            TesseractInterop.TessResultIteratorDelete(iter);
        }
        return blocks;
    }

    private static IntPtr Create()
    {
        IntPtr api = TesseractInterop.TessBaseAPICreate();
        if (api == IntPtr.Zero)
            throw new UiCtlException("libtesseract's TessBaseAPICreate returned null - failed to allocate a Tesseract engine instance");

        if (TesseractInterop.TessBaseAPIInit3(api, null, "eng") != 0)
        {
            TesseractInterop.TessBaseAPIDelete(api);
            throw new UiCtlException("Tesseract engine initialization failed - is tesseract-ocr-eng installed? See scripts/preflight.sh / `uictl permissions`.");
        }
        return api;
    }
}
