namespace UICtl.Core.Tests;

/// <summary>
/// Exercises the real wl-copy/wl-paste (or xclip) via Clipboard - no
/// mocking, since the whole point is the external-process interop (see
/// Clipboard.cs's doc comment for the forked-child/pipe-EOF gotcha this
/// is really guarding against). Requires a real desktop session with
/// wl-clipboard/xclip installed (scripts/preflight.sh) - same as every
/// other native/session-dependent test in this project (see
/// ENGINEERING.md's build-plan note on testing against the genuine
/// article rather than approximating). Saves and restores whatever was
/// on the clipboard before the test, best-effort, so running the suite
/// doesn't clobber something the developer had actually copied.
/// </summary>
public class ClipboardTests
{
    [Fact]
    public void SetThenGet_RoundTripsExactText()
    {
        string? original = TryGetCurrent();
        try
        {
            const string testText = "uictl clipboard round-trip test — unicode ✓, trailing space ";
            Clipboard.Set(testText);
            Assert.Equal(testText, Clipboard.Get());
        }
        finally
        {
            // Best-effort restore. There's no "clear"/no-owner primitive in
            // the public Clipboard API (not part of MCP_INTERFACE.md's
            // contract), so a clipboard that started empty/non-text is
            // restored to an empty string rather than left holding this
            // test's own text - as close to the original state as this
            // class can produce, not a perfect match.
            Clipboard.Set(original ?? "");
        }
    }

    private static string? TryGetCurrent()
    {
        try { return Clipboard.Get(); }
        catch (UiCtlException) { return null; } // empty/non-text clipboard - nothing to restore verbatim
    }
}
