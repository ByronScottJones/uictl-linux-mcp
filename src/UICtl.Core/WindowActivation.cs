namespace UICtl.Core;

/// <summary>
/// Public entry point CommandDispatcher calls for `activate` and
/// FocusHoldStore's reassertion - selects the right IWindowBackend by
/// session type (same split as AppSelector/Accessibility use for other
/// concerns) and correlates by pid to a live window. Deliberately does
/// NOT touch windows.list's own AT-SPI windowId scheme - see
/// IWindowBackend.cs's doc comment on why these are separate id spaces.
/// </summary>
public static class WindowActivation
{
    internal static IWindowBackend CurrentBackend() =>
        SessionDetection.HasWayland ? new WaylandWindowBackend() : new X11WindowBackend();

    /// <summary>
    /// Resolves pid (already resolved by the caller via AppSelector/
    /// WindowResolver) and activates the matching window - CommandDispatcher's
    /// entry point for `activate`. The caller already has the pid it
    /// passed in, so there's nothing further worth returning (and
    /// BackendWindow is internal - can't appear in a public signature);
    /// see <see cref="ActivateForAppInternal"/> for the same-assembly
    /// version FocusHoldStore needs the resolved BackendWindow from.
    /// </summary>
    public static void ActivateForApp(int pid, long? atspiWindowId) => ActivateForAppInternal(pid, atspiWindowId);

    /// <summary>
    /// Resolves pid (already resolved by the caller via AppSelector/
    /// WindowResolver) to a live backend window and activates it.
    /// <paramref name="atspiWindowId"/>, when given, disambiguates among
    /// several windows sharing a pid by title-matching against AT-SPI's
    /// own windows.list for that pid - reuses Accessibility.ListWindows
    /// rather than adding new cross-referencing state.
    /// </summary>
    internal static BackendWindow ActivateForAppInternal(int pid, long? atspiWindowId)
    {
        var backend = CurrentBackend();
        BackendWindow chosen = ResolveTarget(backend, pid, atspiWindowId);

        if (!backend.Activate(chosen.Id))
            throw new UiCtlException($"backend reported activation failure for window {chosen.Id} (pid {pid})");

        return chosen;
    }

    internal static BackendWindow ResolveTarget(IWindowBackend backend, int pid, long? atspiWindowId)
    {
        var candidates = backend.ListWindows().Where(w => w.Pid == pid).ToList();
        if (candidates.Count == 0)
        {
            throw new UiCtlException(
                $"no window backend entry found for pid {pid} - if this is a Wayland session, is the companion " +
                "GNOME Shell extension installed and enabled? See gnome-extension/README.md / `uictl permissions`.");
        }
        if (candidates.Count == 1 || atspiWindowId is not { } wid)
            return candidates[0];

        string? atspiTitle = Accessibility.ListWindows(pid).FirstOrDefault(w => w.WindowId == wid)?.Title;
        return PickByTitle(candidates, atspiTitle);
    }

    /// <summary>Pure matching logic split out from ResolveTarget so it's testable without a live AT-SPI/Accessibility dependency - falls back to the first candidate when there's no title to match or nothing matches.</summary>
    internal static BackendWindow PickByTitle(IReadOnlyList<BackendWindow> candidates, string? title) =>
        (title is not null ? candidates.FirstOrDefault(c => c.Title == title) : null) ?? candidates[0];

    internal static BackendWindow? GetFocusedWindow() => CurrentBackend().GetFocusedWindow();
}
