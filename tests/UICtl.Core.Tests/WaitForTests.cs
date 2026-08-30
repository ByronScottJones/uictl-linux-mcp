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

    /// <summary>
    /// Real (not mocked) happy-path test - requires a live desktop session
    /// with at least one AT-SPI-registered window already on screen, same
    /// category as ClipboardTests.cs/OcrTests.cs. Deliberately doesn't
    /// assume which window: picks whatever `windows.list` already reports
    /// rather than a specific app, since that's the only thing this test
    /// can rely on on any real GNOME desktop this project targets.
    /// </summary>
    [Fact]
    public void Poll_FindsAnElementImmediatelyWhenAlreadyPresent()
    {
        var windows = Accessibility.ListWindows(null);
        Assert.True(windows.Count > 0, "this test requires at least one AT-SPI window already on screen");

        var sw = Stopwatch.StartNew();
        var (found, element) = WaitFor.Poll(windows[0].WindowId, null, null, null, timeoutSeconds: 5);
        sw.Stop();

        Assert.True(found);
        Assert.NotNull(element);
        // No filter narrows the match, so this should resolve on the very
        // first poll - not wait out any meaningful fraction of the timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"expected an immediate match, took {sw.Elapsed}");
    }
}
