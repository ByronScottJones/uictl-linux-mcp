using Tmds.DBus;

namespace UICtl.Core.Interop;

/// <summary>
/// Tmds.DBus proxies for org.freedesktop.portal.ScreenCast - the "real"
/// zero-dialog-on-repeat mechanism xdg-desktop-portal offers (unlike
/// Screenshot, see PortalInterfaces.cs's doc comment for why that one has
/// no working zero-dialog path here). ScreenCast's session-based flow:
/// CreateSession -> SelectSources -> Start -> OpenPipeWireRemote, each of
/// the first three going through the same Request/Response pattern
/// IScreenshotPortal already uses (see PortalInterfaces.cs's IPortalRequest -
/// reused here, not redeclared).
///
/// The consent UI here is a source-*picker* dialog (choose which monitor/
/// window to share), not a "take screenshot" confirm - shown once, then
/// skippable on later calls via `restore_token`: SelectSources's options
/// support `persist_mode` (2 = persist until explicitly revoked) and a
/// `restore_token` from a *previous* successful Start's results can be
/// passed back into a later SelectSources call to reuse that grant
/// silently. See WaylandScreenCastBackend.cs for how the token is
/// persisted and reused, and ENGINEERING.md's "Wayland screenshot"
/// section for why this needed its own investigation before Screenshot's
/// simpler interactive:false path was ruled out as a dead end here.
/// </summary>
[DBusInterface("org.freedesktop.portal.ScreenCast")]
public interface IScreenCastPortal : IDBusObject
{
    /// <returns>Object path of a Request - watch its Response signal (IPortalRequest) for the result, which includes "session_handle".</returns>
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);

    /// <returns>Object path of a Request - Response's result is empty on success (just a status code).</returns>
    Task<ObjectPath> SelectSourcesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);

    /// <returns>Object path of a Request - Response's results include "streams" (array of (node_id, properties)) and, if persist_mode was set, "restore_token".</returns>
    Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);

    /// <returns>A UNIX_FD (not a Request - this is a direct method reply) - the PipeWire socket to connect to for the stream(s) named in Start's results.</returns>
    Task<CloseSafeHandle> OpenPipeWireRemoteAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
}

/// <summary>The session object CreateSession's Response hands back as "session_handle" - must be closed explicitly, it isn't cleaned up just by the connection dropping.</summary>
[DBusInterface("org.freedesktop.portal.Session")]
public interface IPortalSession : IDBusObject
{
    Task CloseAsync();
}
