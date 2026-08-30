namespace UICtl.Core.Tests;

public class ScreenshotCropTests
{
    private static byte[] MakeGradient(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            rgba[i + 0] = (byte)x;
            rgba[i + 1] = (byte)y;
            rgba[i + 2] = 0;
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    [Fact]
    public void CropRgba_ExtractsExactSubregion()
    {
        byte[] src = MakeGradient(10, 10);
        byte[] cropped = Screenshot.CropRgba(src, 10, 10, 2, 3, 4, 5);

        Assert.Equal(4 * 5 * 4, cropped.Length);
        // Top-left of the crop should match source (2,3).
        Assert.Equal(2, cropped[0]);
        Assert.Equal(3, cropped[1]);
        // Bottom-right of the crop (local col 3, row 4) should match source (2+3, 3+4) = (5,7).
        int lastPixel = (4 * 4 + 3) * 4;
        Assert.Equal(5, cropped[lastPixel]);
        Assert.Equal(7, cropped[lastPixel + 1]);
    }

    [Fact]
    public void CropRgba_ClipsRegionPartlyOutsideSource_LeavingZerosOutsideBounds()
    {
        byte[] src = MakeGradient(5, 5);
        // Crop starting 2px before the source's left edge - should not throw, and the
        // out-of-bounds columns should stay zeroed rather than reading garbage.
        byte[] cropped = Screenshot.CropRgba(src, 5, 5, -2, 0, 4, 4);

        Assert.Equal(4 * 4 * 4, cropped.Length);
        Assert.Equal(0, cropped[0]); // out-of-bounds column
        Assert.Equal(0, cropped[2 * 4]); // in-bounds: source x=0 lands at crop column 2
    }
}
