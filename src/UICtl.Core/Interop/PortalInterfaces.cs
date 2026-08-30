using Tmds.DBus;

namespace UICtl.Core.Interop;

/// <summary>
/// Tmds.DBus proxies for the three xdg-desktop-portal interfaces the
/// Wayland screenshot backend needs - hand-verified live against the real
/// portal on this machine (org.freedesktop.portal.Desktop), not guessed
/// from the spec alone. See WaylandScreenshotBackend.cs for the full
/// story; the two load-bearing, non-obvious findings from that live
/// testing:
///
/// (1) `org.freedesktop.host.portal.Registry.Register(app_id, {})` is
/// required before Screenshot() will do anything at all for a plain
/// (non-Flatpak/Snap) "host" D-Bus client - it fails with
/// "App info not found for '&lt;id&gt;'" unless a .desktop file named
/// `&lt;app_id&gt;.desktop` exists under $XDG_DATA_HOME/applications
/// *and* that file's `Exec=` resolves to the exact path of the calling
/// process's own binary (verified by comparing against the connecting
/// process's real executable, not just the desktop file's existence -
/// confirmed by testing with a mismatched Exec= first, which still
/// failed the same way).
///
/// (2) `Screenshot(..., {"interactive": false})` does NOT fall back to
/// showing a one-time "remember this" consent prompt the way some other
/// portals do - it fails immediately (Request.Response code 2) when no
/// permission is already stored, and nothing in this GNOME build's
/// screenshot portal ever seems to grant that stored permission via this
/// interface. Only `{"interactive": true}` completes successfully - but
/// it shows a real modal "Take Screenshot" dialog requiring a manual
/// click on *every* call, not just the first. There is no zero-dialog
/// path through this specific interface - see Screenshot.cs's doc
/// comment and MCP_INTERFACE.md for how this is surfaced to callers, and
/// ENGINEERING.md for the ScreenCast+PipeWire alternative this project
/// has deliberately deferred rather than build in the same pass.
/// </summary>
[DBusInterface("org.freedesktop.portal.Screenshot")]
public interface IScreenshotPortal : IDBusObject
{
    /// <returns>Object path of a Request - watch its Response signal (IPortalRequest) for the result.</returns>
    Task<ObjectPath> ScreenshotAsync(string parentWindow, IDictionary<string, object> options);
}

[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    /// <returns>(response, results) - response 0 = success (results has "uri"), 1 = user cancelled, 2 = other error.</returns>
    Task<IDisposable> WatchResponseAsync(Action<(uint response, IDictionary<string, object> results)> handler);
}

/// <summary>
/// Lets a non-sandboxed ("host") process register an application identity
/// with xdg-desktop-portal, required before Screenshot (and presumably
/// other permission-gated portals) will serve it - see this file's class
/// doc comment finding (1).
/// </summary>
[DBusInterface("org.freedesktop.host.portal.Registry")]
public interface IPortalRegistry : IDBusObject
{
    Task RegisterAsync(string appId, IDictionary<string, object> options);
}
