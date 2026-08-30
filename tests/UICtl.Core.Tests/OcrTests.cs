using UICtl.Core.Interop;

namespace UICtl.Core.Tests;

/// <summary>
/// Exercises the real libtesseract.so.5 via TesseractEngine - no mocking,
/// since the whole point of this feature is the native interop (see
/// Interop/TesseractInterop.cs's doc comment for why the Tesseract NuGet
/// package couldn't be used instead). Requires tesseract-ocr/
/// tesseract-ocr-eng installed (scripts/preflight.sh) - same precondition
/// this feature has in production, not something worth faking a fallback
/// for in tests.
/// </summary>
public class OcrTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-sample.png");

    [Fact]
    public void Recognize_ReadsRealTextFromAKnownFixtureImage()
    {
        var (rgba, width, height) = PngCodec.Decode(File.ReadAllBytes(FixturePath));
        var blocks = TesseractEngine.Recognize(rgba, width, height, originX: 0, originY: 0);

        var block = Assert.Single(blocks);
        Assert.Equal("UICTL OCR TEST", block.Text);
        Assert.InRange(block.Confidence!.Value, 0.0, 1.0);
        // The rendered text starts a few pixels in, not flush with the image edge.
        Assert.True(block.Frame.X > 0 && block.Frame.X < 30, $"unexpected left offset {block.Frame.X}");
        Assert.True(block.Frame.W > 100, $"unexpected width {block.Frame.W}");
    }

    [Fact]
    public void Recognize_OffsetsFrameByTheGivenOrigin()
    {
        var (rgba, width, height) = PngCodec.Decode(File.ReadAllBytes(FixturePath));
        var atOrigin = TesseractEngine.Recognize(rgba, width, height, originX: 0, originY: 0);
        var offset = TesseractEngine.Recognize(rgba, width, height, originX: 100, originY: 200);

        Assert.Equal(atOrigin[0].Frame.X + 100, offset[0].Frame.X);
        Assert.Equal(atOrigin[0].Frame.Y + 200, offset[0].Frame.Y);
        Assert.Equal(atOrigin[0].Frame.W, offset[0].Frame.W);
    }

    [Fact]
    public void Recognize_ReturnsNoBlocksForABlankImage()
    {
        var blank = new byte[40 * 40 * 4];
        Array.Fill(blank, (byte)255); // opaque white

        var blocks = TesseractEngine.Recognize(blank, 40, 40, 0, 0);

        Assert.Empty(blocks);
    }
}
