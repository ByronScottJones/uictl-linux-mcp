using Tmds.DBus;

namespace UICtl.Core;

/// <summary>
/// Maps a windowId (see Accessibility.ListWindows' <c>(pid &lt;&lt; 12) | frameIndex</c>
/// scheme - see ENGINEERING.md/MCP_INTERFACE.md's "Window id" note) back to
/// the AT-SPI (busName, path) it came from, so a later `elements --window`/
/// `screenshot --window` doesn't need windows.list to have encoded the AT-SPI
/// reference itself into an opaque integer. Replaced wholesale each time
/// windows.list runs, same spirit as ElementStore.
/// </summary>
internal static class WindowStore
{
    private static readonly Dictionary<long, (string BusName, ObjectPath Path)> ByWindowId = new();

    public static void Register(long windowId, string busName, ObjectPath path) =>
        ByWindowId[windowId] = (busName, path);

    public static (string BusName, ObjectPath Path) Resolve(long windowId) =>
        ByWindowId.TryGetValue(windowId, out var entry)
            ? entry
            : throw new UiCtlException($"window {windowId} is not known - re-run windows.list");
}
