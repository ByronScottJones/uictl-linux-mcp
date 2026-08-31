namespace UICtl.ScreenCapture;

/// <summary>
/// Hand-rolled encoder for the specific SPA POD binary format PipeWire's
/// `pw_stream_connect` needs for its `params` array - no .NET SPA/PipeWire
/// binding exists to generate this from. Struct layouts and enum values
/// below are transcribed from PipeWire 1.6.2's real C headers (spa/pod/pod.h,
/// spa/utils/type.h, spa/param/param.h, spa/param/format.h,
/// spa/param/video/raw.h, spa/utils/defs.h - fetched directly from the
/// PipeWire GitHub repo at the exact installed version, not guessed from
/// memory, given a wrong byte here crashes the whole process rather than
/// throwing a catchable .NET exception), verified against the installed
/// `libpipewire-0.3.so.0` (1.6.2, confirmed via `dpkg -l`).
///
/// Every POD is `{ uint32 size; uint32 type; }` (8 bytes) followed by
/// `size` bytes of body, and the whole thing is padded with zero bytes so
/// the *next* POD starts on an 8-byte boundary (the padding itself isn't
/// counted in `size`). `SpaPodWriter` handles that padding automatically
/// after every top-level Write*/End* call; nested writers pad themselves
/// the same way before returning control to their parent.
/// </summary>
internal static class SpaType
{
    public const uint None = 1, Bool = 2, Id = 3, Int = 4, Long = 5, Float = 6, Double = 7,
        String = 8, Bytes = 9, Rectangle = 10, Fraction = 11, Bitmap = 12, Array = 13,
        Struct = 14, Object = 15, Sequence = 16, Pointer = 17, Fd = 18, Choice = 19, Pod = 20;

    public const uint ObjectFormat = 0x40003;
}

internal static class SpaChoiceType
{
    public const uint None = 0, Range = 1, Step = 2, Enum = 3, Flags = 4;
}

internal static class SpaParamType
{
    public const uint EnumFormat = 3, Format = 4;
}

internal static class SpaFormatKey
{
    public const uint MediaType = 1, MediaSubtype = 2;
    public const uint VideoFormat = 0x20001, VideoSize = 0x20003, VideoFramerate = 0x20004;
}

internal static class SpaMediaType
{
    public const uint Video = 2;
}

internal static class SpaMediaSubtype
{
    public const uint Raw = 1;
}

internal static class SpaVideoFormat
{
    public const uint RGBx = 7, BGRx = 8, RGBA = 11, BGRA = 12;
}

/// <summary>
/// Builds one top-level POD into a growable byte buffer. Each Write*
/// method appends a complete, self-contained, 8-byte-aligned POD (header +
/// body + padding) - callers compose these into an Object's property list
/// by calling WriteProp (key+flags+value POD) for each property.
/// </summary>
internal sealed class SpaPodWriter
{
    private readonly MemoryStream _stream = new();

    public byte[] ToArray() => _stream.ToArray();

    private void Pad()
    {
        int rem = (int)(_stream.Length % 8);
        if (rem != 0) _stream.Write(new byte[8 - rem]);
    }

    private void WriteU32(uint v) => _stream.Write(BitConverter.GetBytes(v));
    private void WriteI32(int v) => _stream.Write(BitConverter.GetBytes(v));

    /// <summary>Writes a bare 4-byte-body POD (Id or Int) plus alignment padding.</summary>
    public void WriteId(uint value)
    {
        WriteU32(4);
        WriteU32(SpaType.Id);
        WriteU32(value);
        Pad();
    }

    public void WriteRectangle(uint width, uint height)
    {
        WriteU32(8);
        WriteU32(SpaType.Rectangle);
        WriteU32(width);
        WriteU32(height);
        // body is already 8 bytes - always aligned, no padding needed.
    }

    public void WriteFraction(uint num, uint denom)
    {
        WriteU32(8);
        WriteU32(SpaType.Fraction);
        WriteU32(num);
        WriteU32(denom);
    }

    /// <summary>
    /// A Choice POD offering a default/min/max Rectangle range
    /// (SPA_CHOICE_Range) - `values` must be exactly [default, min, max].
    /// </summary>
    public void WriteRectangleChoiceRange((uint W, uint H)[] values)
    {
        uint bodySize = 8 + 8 + (uint)(values.Length * 8);
        WriteU32(bodySize);
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Range);
        WriteU32(0);
        WriteU32(8);
        WriteU32(SpaType.Rectangle);
        foreach (var (w, h) in values) { WriteU32(w); WriteU32(h); }
        // 8-byte elements, choice header already 16 bytes - always aligned.
    }

    /// <summary>Same as WriteRectangleChoiceRange but for Fraction values.</summary>
    public void WriteFractionChoiceRange((uint Num, uint Denom)[] values)
    {
        uint bodySize = 8 + 8 + (uint)(values.Length * 8);
        WriteU32(bodySize);
        WriteU32(SpaType.Choice);
        WriteU32(SpaChoiceType.Range);
        WriteU32(0);
        WriteU32(8);
        WriteU32(SpaType.Fraction);
        foreach (var (n, d) in values) { WriteU32(n); WriteU32(d); }
    }

    /// <summary>
    /// Writes one property: `{ uint32 key; uint32 flags; }` followed by the
    /// value POD (already-encoded bytes from one of the WriteXxx helpers
    /// above, called on its own throwaway writer). Not itself padded -
    /// the value POD's own trailing pad already leaves the stream 8-byte
    /// aligned, so a following prop starts cleanly.
    /// </summary>
    public void WriteProp(uint key, byte[] valuePod)
    {
        WriteU32(key);
        WriteU32(0); // flags
        _stream.Write(valuePod);
    }

    /// <summary>
    /// Wraps everything written by `buildBody` in an Object POD header -
    /// `buildBody` should call WriteProp repeatedly on `this` writer
    /// *after* this call returns the body-start marker... in practice,
    /// simplest usage: construct the Format object as a single method
    /// (see PipeWireCapture.BuildEnumFormatParam) that writes the object
    /// header via WriteObjectHeader (size computed up front by building
    /// props into a temporary writer first), not via this wrapper.
    /// </summary>
    public void WriteObjectHeader(uint bodySize, uint objectType, uint objectId)
    {
        WriteU32(bodySize);
        WriteU32(SpaType.Object);
        WriteU32(objectType);
        WriteU32(objectId);
    }

    /// <summary>Appends already-encoded, self-aligned POD bytes verbatim (e.g. a props blob built on a separate writer).</summary>
    public void WriteRaw(byte[] bytes) => _stream.Write(bytes);
}
