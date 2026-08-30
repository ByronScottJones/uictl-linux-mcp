using UICtl.Core.Interop;

namespace UICtl.Core.Tests;

public class PngCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsExactPixels()
    {
        const int width = 17, height = 13; // odd sizes - no accidental stride/alignment coincidences
        var rgba = new byte[width * height * 4];
        var rnd = new Random(42);
        rnd.NextBytes(rgba);
        // PNG encode/decode here always treats alpha as opaque data, not blending - keep it, but
        // force full opacity so this test isn't accidentally sensitive to alpha semantics.
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

        byte[] png = PngCodec.Encode(rgba, width, height);
        var (decoded, decodedWidth, decodedHeight) = PngCodec.Decode(png);

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal(rgba, decoded);
    }

    [Fact]
    public void Decode_RejectsNonPngData()
    {
        Assert.Throws<UiCtlException>(() => PngCodec.Decode(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Encode_ProducesValidPngSignatureAndIhdr()
    {
        byte[] png = PngCodec.Encode(new byte[2 * 2 * 4], 2, 2);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
    }
}
