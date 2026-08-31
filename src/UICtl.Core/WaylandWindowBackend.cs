using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// Talks to the companion GNOME Shell extension's org.byronscottjones.uictl.WindowManager
/// service over the regular session bus (unlike AT-SPI, which needs an
/// extra hop via org.a11y.Bus to find its own dedicated bus - see
/// Accessibility.cs - the Shell extension exports itself directly on
/// Gio.DBus.session). Lazy connection, same rationale as Accessibility's:
/// one long-lived connection for the daemon's lifetime.
/// </summary>
internal sealed class WaylandWindowBackend : IWindowBackend
{
    private const string ServiceName = "org.byronscottjones.uictl.WindowManager";
    private static readonly ObjectPath ServicePath = "/org/byronscottjones/uictl/WindowManager";

    private static readonly Lazy<Connection> LazyConnection = new(() => AsyncBridge.RunSync(ConnectAsync, AsyncBridge.DefaultDBusTimeout));

    private static async Task<Connection> ConnectAsync()
    {
        if (Address.Session is null)
            throw new UiCtlException("no D-Bus session address available ($DBUS_SESSION_BUS_ADDRESS unset?) - cannot reach the uictl Shell extension");
        var connection = new Connection(Address.Session);
        await connection.ConnectAsync();
        return connection;
    }

    private static IUictlWindowManager Proxy =>
        LazyConnection.Value.CreateProxy<IUictlWindowManager>(ServiceName, ServicePath);

    public IReadOnlyList<BackendWindow> ListWindows() => AsyncBridge.RunSync(async () =>
    {
        var windows = await GuardedCallAsync(() => Proxy.ListWindowsAsync());
        return (IReadOnlyList<BackendWindow>)windows
            .Select(w => new BackendWindow(w.Item1, w.Item2, w.Item3, new Frame(w.Item4, w.Item5, w.Item6, w.Item7)))
            .ToList();
    }, AsyncBridge.DefaultDBusTimeout);

    public bool Activate(long id) => AsyncBridge.RunSync(() => GuardedCallAsync(() => Proxy.ActivateWindowAsync((uint)id)), AsyncBridge.DefaultDBusTimeout);

    public BackendWindow? GetFocusedWindow() => AsyncBridge.RunSync(async () =>
    {
        (bool found, uint stableSeq) = await GuardedCallAsync(() => Proxy.GetFocusedWindowAsync());
        if (!found) return null;
        // The focused window's title/pid/frame aren't part of
        // GetFocusedWindow's own return - re-resolve from ListWindows by
        // id rather than growing the D-Bus interface for a rarely-needed
        // combination (focus.status is the only caller that wants more
        // than just "which id").
        var all = await GuardedCallAsync(() => Proxy.ListWindowsAsync());
        foreach (var w in all)
            if (w.Item1 == stableSeq)
                return new BackendWindow(w.Item1, w.Item2, w.Item3, new Frame(w.Item4, w.Item5, w.Item6, w.Item7));
        return null;
    }, AsyncBridge.DefaultDBusTimeout);

    /// <summary>
    /// A D-Bus call to a service name nobody owns throws Tmds.DBus's own
    /// connection-level exception, not a helpful message - translate it
    /// into the same "is the extension installed/enabled" guidance
    /// AGENTS.md/gnome-extension/README.md already give.
    /// </summary>
    private static async Task<T> GuardedCallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex)
        {
            throw new UiCtlException(
                $"could not reach the uictl GNOME Shell extension ({ex.Message}) - is it installed and enabled? " +
                "See gnome-extension/README.md. `uictl permissions`' shellExtensionConnected field reports this.");
        }
    }
}
