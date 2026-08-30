using System.Diagnostics;

namespace UICtl.Core.Tests;

public class WaitForTests
{
    [Fact]
    public void Poll_ThrowsImmediatelyWhenNeitherWindowNorAppGiven()
    {
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<UiCtlException>(() => WaitFor.Poll(null, null, null, null, timeoutSeconds: 5));
        sw.Stop();

        Assert.Contains("window id or an app selector", ex.Message);
        // Should fail fast, not poll out the full 5s timeout for a
        // selector combination that can never succeed.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"expected an immediate throw, took {sw.Elapsed}");
    }

    [Fact]
    public void Poll_ReturnsNotFoundAfterTimeoutForANonexistentApp()
    {
        // AppSelector.Resolve only scans /proc (no display/AT-SPI needed -
        // see AppSelector.cs), so this exercises the "keep polling on
        // resolution failure" path without needing a real desktop session.
        var sw = Stopwatch.StartNew();
        var (found, element) = WaitFor.Poll(null, "definitely-not-a-real-app-xyz123", null, null, timeoutSeconds: 1);
        sw.Stop();

        Assert.False(found);
        Assert.Null(element);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1), $"expected to poll for roughly the full 1s timeout, took {sw.Elapsed}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"expected to return near the 1s timeout, took {sw.Elapsed}");
    }
}
