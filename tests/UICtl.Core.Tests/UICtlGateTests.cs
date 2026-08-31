using System.Text.Json;
using UICtl.Ipc;

namespace UICtl.Core.Tests;

/// <summary>
/// UICtlGate/its CommandDispatcher wiring are `internal` to UICtl.Ipc
/// (InternalsVisibleTo already covers this test project). UICtlGate is
/// process-wide static state (matches its own production usage - one
/// daemon process, one gate), so every test restores it to enabled
/// afterward rather than leaving it disabled for whichever test runs next.
///
/// Deliberately doesn't dispatch any *non*-internal command through
/// CommandDispatcher here: `Dispatch` calls `GuiLauncher.EnsureStarted()`
/// for every one of those, which would spawn a real `uictl-gui` process
/// as a side effect of running the test suite - not appropriate for a
/// unit test. The "a real command is blocked while disabled" half of
/// Dispatch's gate check is a single `if (!isInternal && !UICtlGate.Enabled)
/// throw ...` line, verified by inspection rather than a live dispatch
/// call; the "internal commands bypass the gate" half (proven below via
/// `__ping__`/`__gate_set__`, both internal, so `EnsureStarted()` is
/// never reached) is the half worth a real, no-mocking test - it's what
/// lets a human re-enable commands at all once disabled.
/// </summary>
public class UICtlGateTests
{
    [Fact]
    public void DefaultsEnabled()
    {
        Assert.True(UICtlGate.Enabled);
    }

    [Fact]
    public void SetChangesEnabled()
    {
        try
        {
            UICtlGate.Set(false);
            Assert.False(UICtlGate.Enabled);
            UICtlGate.Set(true);
            Assert.True(UICtlGate.Enabled);
        }
        finally
        {
            UICtlGate.Set(true);
        }
    }

    [Fact]
    public void Dispatch_InternalCommandsBypassTheGate()
    {
        try
        {
            UICtlGate.Set(false);

            string pingResponse = CommandDispatcher.Dispatch("__ping__", ParamsExtensions.Empty);
            using var pingDoc = JsonDocument.Parse(pingResponse);
            Assert.True(pingDoc.RootElement.GetProperty("ok").GetBoolean());

            // __gate_set__ itself must work even while disabled, or a human
            // could never re-enable commands once the checkbox turned them off.
            using var enableParams = JsonDocument.Parse("""{"enabled":true}""");
            string gateResponse = CommandDispatcher.Dispatch("__gate_set__", enableParams.RootElement);
            using var gateDoc = JsonDocument.Parse(gateResponse);
            Assert.True(gateDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(UICtlGate.Enabled);
        }
        finally
        {
            UICtlGate.Set(true);
        }
    }
}
