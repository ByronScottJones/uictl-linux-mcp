using UICtl.Core.Interop;

namespace UICtl.Core.Tests;

public class NativeAbiTests
{
    [Fact]
    public void IsSupported_TrueOnTheArchitectureThisSuiteActuallyRunsOn()
    {
        // Can't meaningfully test the false branch without mocking
        // RuntimeInformation.ProcessArchitecture (not injectable) - this
        // just guards against the check itself being inverted/broken,
        // since CI only ever runs on x86_64 or arm64 today.
        Assert.True(NativeAbi.IsSupported);
    }

    [Fact]
    public void EnsureSupported_DoesNotThrowOnASupportedArchitecture()
    {
        NativeAbi.EnsureSupported("test");
    }
}
