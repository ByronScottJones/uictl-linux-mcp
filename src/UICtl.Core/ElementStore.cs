using Tmds.DBus;

namespace UICtl.Core;

/// <summary>
/// Maps synthetic element ids (handed out by Accessibility.WalkWindow) back
/// to the live AT-SPI (busName, path) they came from, so a later
/// click/type by id doesn't need to re-walk the tree. Scoped per window;
/// replaced wholesale each time that window's elements are re-listed. The
/// daemon serves one request at a time (see ENGINEERING.md), so this needs
/// no locking.
/// </summary>
internal static class ElementStore
{
    private static readonly Dictionary<string, (string BusName, ObjectPath Path)> ByElementId = new();
    private static readonly Dictionary<long, int> CounterByWindow = new();

    public static void Reset(long windowId)
    {
        CounterByWindow[windowId] = 0;
        string prefix = $"{windowId}-";
        foreach (var key in ByElementId.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            ByElementId.Remove(key);
    }

    public static string Register(long windowId, string busName, ObjectPath path)
    {
        int next = CounterByWindow.GetValueOrDefault(windowId) + 1;
        CounterByWindow[windowId] = next;
        string id = $"{windowId}-{next}";
        ByElementId[id] = (busName, path);
        return id;
    }

    public static (string BusName, ObjectPath Path) Resolve(string elementId) =>
        ByElementId.TryGetValue(elementId, out var entry)
            ? entry
            : throw new UiCtlException($"element {elementId} is not known - re-run elements or screenshot --annotate");
}
