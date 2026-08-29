using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// AT-SPI2 over D-Bus - the core accessibility subsystem, and the one big
/// low-risk win on this platform: it works identically under X11 and
/// Wayland (unlike window management, see ENGINEERING.md). Owns the daemon's
/// one long-lived AT-SPI connection plus the window/element id caches that
/// need to outlive a single dispatch call.
/// </summary>
public static class Accessibility
{
    private const string RegistryService = "org.a11y.atspi.Registry";
    private static readonly ObjectPath RootPath = "/org/a11y/atspi/accessible/root";

    private static readonly Lazy<Connection> LazyConnection = new(() => AsyncBridge.RunSync(ConnectAsync));

    private static async Task<Connection> ConnectAsync()
    {
        if (Address.Session is null)
            throw new UiCtlException("no D-Bus session address available ($DBUS_SESSION_BUS_ADDRESS unset?) - cannot reach AT-SPI");
        using var sessionConnection = new Connection(Address.Session);
        await sessionConnection.ConnectAsync();
        var bus = sessionConnection.CreateProxy<IA11yBus>("org.a11y.Bus", "/org/a11y/bus");
        string atspiAddress = await bus.GetAddressAsync();

        var atspiConnection = new Connection(atspiAddress);
        await atspiConnection.ConnectAsync();
        return atspiConnection;
    }

    private static Connection Conn => LazyConnection.Value;

    /// <summary>One AT-SPI-registered application: its D-Bus unique name, root accessible, and real OS pid (via GetConnectionUnixProcessID - AT-SPI's own Application.Id is an internal registration counter, not a pid, see AtspiInterfaces.cs).</summary>
    private sealed record AtspiApp(string BusName, string Name, int Pid);

    private static async Task<IReadOnlyList<AtspiApp>> ListAtspiAppsAsync()
    {
        var root = Conn.CreateProxy<IAtspiAccessible>(RegistryService, RootPath);
        var children = await root.GetChildrenAsync();
        var driver = Conn.CreateProxy<IDBusDriver>("org.freedesktop.DBus", "/org/freedesktop/DBus");

        var apps = new List<AtspiApp>();
        foreach (var (busName, _) in children)
        {
            try
            {
                var app = Conn.CreateProxy<IAtspiAccessible>(busName, RootPath);
                string name = await app.GetAsync<string>("Name");
                uint pid = await driver.GetConnectionUnixProcessIDAsync(busName);
                apps.Add(new AtspiApp(busName, name, (int)pid));
            }
            catch (Exception)
            {
                // A registered peer that disappears mid-enumeration (quit
                // between GetChildren and here) or refuses one of these
                // calls shouldn't take the whole listing down.
            }
        }
        return apps;
    }

    public static IReadOnlyList<AppInfo> ListApps(bool includeBackground)
    {
        var atspiApps = AsyncBridge.RunSync(ListAtspiAppsAsync);
        // TryAdd, not ToDictionary: two AT-SPI accessibles can share a pid
        // (a process registering more than one root accessible) - a
        // straight ToDictionary throws on the duplicate key and would take
        // the whole listing down over it.
        var appsByPid = new Dictionary<int, string>();
        foreach (var a in atspiApps) appsByPid.TryAdd(a.Pid, a.Name);

        IEnumerable<int> pids = appsByPid.Keys;
        if (includeBackground)
            pids = pids.Union(ProcEnumeration.ListPids());

        var result = new List<AppInfo>();
        foreach (int pid in pids)
        {
            string name = appsByPid.TryGetValue(pid, out var atspiName) ? atspiName : ProcEnumeration.TryGetProcessName(pid);
            if (name.Length == 0) continue; // process gone by the time we looked
            result.Add(new AppInfo(pid, name, ""));
        }
        return result;
    }

    private static async Task<IReadOnlyList<(WindowInfo Info, string BusName, ObjectPath Path)>> ListWindowsInnerAsync(int? pidFilter)
    {
        var atspiApps = await ListAtspiAppsAsync();
        var windows = new List<(WindowInfo, string, ObjectPath)>();

        foreach (var app in atspiApps)
        {
            if (pidFilter is { } filter && app.Pid != filter) continue;

            var appRoot = Conn.CreateProxy<IAtspiAccessible>(app.BusName, RootPath);
            var children = await appRoot.GetChildrenAsync();

            int frameIndex = 0;
            foreach (var (busName, path) in children)
            {
                var child = Conn.CreateProxy<IAtspiAccessible>(busName, path);
                string role;
                try { role = await child.GetRoleNameAsync(); }
                catch { continue; }
                bool isWindowLike = role.Equals("window", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("frame", StringComparison.OrdinalIgnoreCase)
                    || role.Equals("dialog", StringComparison.OrdinalIgnoreCase);
                if (!isWindowLike) continue;

                string title = "";
                try { title = await child.GetAsync<string>("Name"); } catch { /* leave blank */ }

                Frame frame = default;
                try
                {
                    var comp = Conn.CreateProxy<IAtspiComponent>(busName, path);
                    var (x, y, w, h) = await comp.GetExtentsAsync(0);
                    // A degenerate/garbage extents value has been observed
                    // live (a hidden secondary tab-panel reporting a huge
                    // negative width) - skip rather than hand out nonsense
                    // geometry a caller would compute a click point from.
                    if (w > 0 && h > 0 && w < 100_000 && h < 100_000)
                        frame = new Frame(x, y, w, h);
                }
                catch { /* Component not implemented for this role - leave zeroed */ }

                long windowId = ((long)app.Pid << 12) | (uint)frameIndex;
                windows.Add((new WindowInfo(windowId, app.Pid, title, frame, null), busName, path));
                frameIndex++;
            }
        }
        return windows;
    }

    public static IReadOnlyList<WindowInfo> ListWindows(int? pidFilter)
    {
        var windows = AsyncBridge.RunSync(() => ListWindowsInnerAsync(pidFilter));
        foreach (var (info, busName, path) in windows)
            WindowStore.Register(info.WindowId, busName, path);
        return windows.Select(w => w.Info).ToList();
    }

    private static async Task<ElementWalkResult> WalkInnerAsync(string busName, ObjectPath path, long windowId, ElementWalkOptions options)
    {
        ElementStore.Reset(windowId);
        var elements = new List<ElementInfo>();
        bool truncated = false;

        async Task VisitAsync(string bn, ObjectPath p, int depth)
        {
            if (truncated || depth > options.MaxDepth) return;

            var acc = Conn.CreateProxy<IAtspiAccessible>(bn, p);
            string role = await acc.GetRoleNameAsync();
            string name = "";
            try { name = await acc.GetAsync<string>("Name"); } catch { /* leave blank */ }

            string[] ifaces = await acc.GetInterfacesAsync();
            Frame frame = default;
            if (ifaces.Contains("org.a11y.atspi.Component"))
            {
                try
                {
                    var comp = Conn.CreateProxy<IAtspiComponent>(bn, p);
                    var (x, y, w, h) = await comp.GetExtentsAsync(0);
                    if (w > 0 && h > 0 && w < 100_000 && h < 100_000)
                        frame = new Frame(x, y, w, h);
                }
                catch { /* leave zeroed */ }
            }

            string? value = null;
            if (ifaces.Contains("org.a11y.atspi.Text"))
            {
                try
                {
                    var text = Conn.CreateProxy<IAtspiText>(bn, p);
                    int count = await text.GetCharacterCountAsync();
                    // AT-SPI Text.GetText(startOffset, endOffset) treats
                    // endOffset as exclusive, so (0, count) is the full string.
                    if (count > 0) value = await text.GetTextAsync(0, count);
                }
                catch { /* leave null */ }
            }

            bool roleMatches = options.RoleFilter is null || role.Equals(options.RoleFilter, StringComparison.OrdinalIgnoreCase);
            bool titleMatches = options.TitleContains is null || name.Contains(options.TitleContains, StringComparison.OrdinalIgnoreCase);
            if (roleMatches && titleMatches)
            {
                if (elements.Count >= options.MaxElements) { truncated = true; return; }
                string id = ElementStore.Register(windowId, bn, p);
                elements.Add(new ElementInfo(id, role, name, value, frame));
            }

            var children = await acc.GetChildrenAsync();
            foreach (var (childBus, childPath) in children)
            {
                if (truncated) break;
                await VisitAsync(childBus, childPath, depth + 1);
            }
        }

        await VisitAsync(busName, path, 0);
        return new ElementWalkResult(elements, truncated);
    }

    public static ElementWalkResult WalkWindow(long windowId, ElementWalkOptions options)
    {
        var (busName, path) = WindowStore.Resolve(windowId);
        return AsyncBridge.RunSync(() => WalkInnerAsync(busName, path, windowId, options));
    }

    public static string SetElementText(string elementId, string text)
    {
        var (busName, path) = ElementStore.Resolve(elementId);
        bool ok = AsyncBridge.RunSync(async () =>
        {
            var editable = Conn.CreateProxy<IAtspiEditableText>(busName, path);
            return await editable.SetTextContentsAsync(text);
        });
        if (!ok)
            throw new UiCtlException($"element {elementId} did not accept a direct text value set (no synthesized-keystroke fallback yet - see ENGINEERING.md's build plan)");
        return "atspiValue";
    }
}
