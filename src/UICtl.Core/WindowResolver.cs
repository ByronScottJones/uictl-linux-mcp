namespace UICtl.Core;

public static class WindowResolver
{
    public static ResolvedWindow Resolve(long? windowId, string? appSelector)
    {
        if (windowId is { } id)
        {
            // pid is recoverable directly from the windowId encoding
            // ((pid << 12) | frameIndex, see Accessibility.ListWindows) -
            // no need to separately resolve it, but WindowStore must have
            // seen this id from an actual windows.list call first.
            WindowStore.Resolve(id);
            return new ResolvedWindow(id, (int)(id >> 12));
        }

        if (appSelector is { } selector)
        {
            int pid = AppSelector.Resolve(selector);
            var windows = Accessibility.ListWindows(pid);
            if (windows.Count == 0)
                throw new UiCtlException($"no window found for \"{selector}\"");
            // No general way to determine "frontmost" without the GNOME
            // Shell extension (see ENGINEERING.md) - the first AT-SPI
            // window found is a reasonable default until that lands.
            return new ResolvedWindow(windows[0].WindowId, pid);
        }

        throw new UiCtlException("either a window id or an app selector is required");
    }
}
