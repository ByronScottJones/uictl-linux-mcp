using UICtl.Core;

namespace UICtl.Core.Tests;

/// <summary>
/// Covers AsyncBridge.RunSync's opt-in timeout parameter (added in the
/// AT-SPI/D-Bus timeout audit, see ENGINEERING.md's "Daemon reliability:
/// AT-SPI/D-Bus call timeouts" section) - a real D-Bus call isn't needed to
/// exercise this: any Task that outlives the timeout demonstrates the same
/// bound/throw behavior AsyncBridge applies to a slow AT-SPI/D-Bus call.
/// </summary>
public class AsyncBridgeTests
{
    [Fact]
    public void RunSync_NoTimeout_WaitsForSlowTask()
    {
        int result = AsyncBridge.RunSync(async () =>
        {
            await Task.Delay(50);
            return 42;
        });
        Assert.Equal(42, result);
    }

    [Fact]
    public void RunSync_WithinTimeout_ReturnsResult()
    {
        int result = AsyncBridge.RunSync(async () =>
        {
            await Task.Delay(10);
            return 7;
        }, TimeSpan.FromSeconds(5));
        Assert.Equal(7, result);
    }

    [Fact]
    public void RunSync_ExceedsTimeout_ThrowsUiCtlException()
    {
        var ex = Assert.Throws<UiCtlException>(() =>
            AsyncBridge.RunSync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return 0;
            }, TimeSpan.FromMilliseconds(50)));

        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public void RunSync_Void_ExceedsTimeout_ThrowsUiCtlException()
    {
        Assert.Throws<UiCtlException>(() =>
            AsyncBridge.RunSync(async () => await Task.Delay(TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void RunSync_WithinTimeout_PropagatesOriginalException()
    {
        // Confirms Wait(timeout) completing doesn't swallow/wrap the real
        // exception into an AggregateException - GetAwaiter().GetResult()
        // must still unwrap it to the original type.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AsyncBridge.RunSync<int>(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5)));
        Assert.Equal("boom", ex.Message);
    }
}
