using System.Text.Json;
using UICtl.Core;
using UICtl.Ipc;

namespace UICtl.Core.Tests;

public class InputParsingTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void GetPointOrThrow_ParsesXY()
    {
        var p = Params("""{"at":"12,34"}""");
        Point point = p.GetPointOrThrow("at");
        Assert.Equal(12, point.X);
        Assert.Equal(34, point.Y);
    }

    [Fact]
    public void GetPointOrThrow_AllowsWhitespaceAndDecimals()
    {
        var p = Params("""{"at":" 12.5 , 34.5 "}""");
        Point point = p.GetPointOrThrow("at");
        Assert.Equal(12.5, point.X);
        Assert.Equal(34.5, point.Y);
    }

    [Fact]
    public void GetPointOrThrow_ThrowsOnMissingParam()
    {
        var p = Params("{}");
        Assert.Throws<UiCtlException>(() => p.GetPointOrThrow("at"));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12,34,56")]
    [InlineData("abc,def")]
    public void GetPointOrNull_ThrowsOnMalformedValue(string raw)
    {
        var p = Params($$"""{"at":"{{raw}}"}""");
        Assert.Throws<UiCtlException>(() => p.GetPointOrNull("at"));
    }

    [Fact]
    public void GetPointOrNull_ReturnsNullWhenParamAbsent()
    {
        var p = Params("{}");
        Assert.Null(p.GetPointOrNull("at"));
    }
}
