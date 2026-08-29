namespace UICtl.Core;

/// <summary>
/// Focus-hold state - mirrors WindowStore/ElementStore's "static state,
/// no locking" pattern (the daemon serves one request at a time, see
/// ENGINEERING.md). The actual "pinning" behavior lives in
/// ReassertIfHeld, called by CommandDispatcher before every
/// focus-sensitive command (click/move/scroll/key/type) while a hold is
/// active - re-activating the held window in case a human clicked away
/// since the hold started, not just bookkeeping.
/// </summary>
public static class FocusHoldStore
{
    private sealed record HoldState(string App, int Pid, long AtspiWindowId, long BackendWindowId, BackendWindow? RestoresTo);

    private static HoldState? _current;

    public static bool Held => _current is not null;

    public static Dictionary<string, object?> Hold(string app, int pid, long atspiWindowId)
    {
        // Best-effort - capture what's frontmost *before* activating the
        // target, so `focus.release` has something to restore to. Null if
        // the backend can't determine it (nothing focused, or an
        // unreachable extension) - release just skips restoring in that case.
        BackendWindow? restoresTo = TryGetFocusedWindow();

        BackendWindow target = WindowActivation.ActivateForAppInternal(pid, atspiWindowId);
        _current = new HoldState(app, pid, atspiWindowId, target.Id, restoresTo);

        return StatusPayload(isFrontmostOverride: true);
    }

    public static Dictionary<string, object?> Release()
    {
        if (_current is null)
            return new Dictionary<string, object?> { ["held"] = false, ["restoredFocus"] = false };

        bool restored = false;
        // Id 0/pid 0 shows up for real, harmless edge cases (some window
        // was X11-active but didn't set _NET_WM_PID, e.g. a Shell UI
        // element) - reactivating it would be a pointless no-op send, so
        // just skip rather than report a misleading "restored: true".
        if (_current.RestoresTo is { Id: not 0 } r)
        {
            try { restored = WindowActivation.CurrentBackend().Activate(r.Id); }
            catch { restored = false; }
        }
        _current = null;
        return new Dictionary<string, object?> { ["held"] = false, ["restoredFocus"] = restored };
    }

    public static Dictionary<string, object?> Status() => StatusPayload(isFrontmostOverride: null);

    /// <summary>
    /// Called by CommandDispatcher before click/move/scroll/key/type while
    /// a hold is active - re-activates the held window first, then
    /// returns the compact per-call marker for the response's
    /// "focusHold" field. Returns null when no hold is active (callers
    /// omit the field entirely in that case, matching this codebase's
    /// existing convention - see Elements' "truncated" field).
    /// </summary>
    public static Dictionary<string, object?>? ReassertIfHeld()
    {
        if (_current is null) return null;

        bool reasserted = false;
        try { reasserted = WindowActivation.CurrentBackend().Activate(_current.BackendWindowId); }
        catch { reasserted = false; }

        return new Dictionary<string, object?>
        {
            ["held"] = true,
            ["windowId"] = _current.AtspiWindowId,
            ["reasserted"] = reasserted,
        };
    }

    private static Dictionary<string, object?> StatusPayload(bool? isFrontmostOverride)
    {
        if (_current is null) return new Dictionary<string, object?> { ["held"] = false };

        bool isFrontmost = isFrontmostOverride ?? IsCurrentlyFrontmost();
        return new Dictionary<string, object?>
        {
            ["held"] = true,
            ["app"] = _current.App,
            ["pid"] = _current.Pid,
            ["windowId"] = _current.AtspiWindowId,
            ["isFrontmost"] = isFrontmost,
            ["restoresTo"] = _current.RestoresTo is { } r
                ? new Dictionary<string, object?> { ["pid"] = r.Pid, ["title"] = r.Title }
                : null,
        };
    }

    private static bool IsCurrentlyFrontmost()
    {
        if (_current is null) return false;
        BackendWindow? focused = TryGetFocusedWindow();
        return focused is not null && focused.Pid == _current.Pid;
    }

    private static BackendWindow? TryGetFocusedWindow()
    {
        try { return WindowActivation.GetFocusedWindow(); }
        catch { return null; }
    }
}
