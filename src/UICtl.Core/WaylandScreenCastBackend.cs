using System.Runtime.InteropServices;
using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// The real, documented zero-dialog-on-repeat Wayland capture mechanism -
/// org.freedesktop.portal.ScreenCast, piping frames out over PipeWire
/// (PipeWireCapture.cs) - built after Screenshot's own `interactive:
/// false` path was confirmed dead (Interop/PortalInterfaces.cs), and after
/// a real `parent_window` handle for Screenshot turned out to be
/// untestable due to an unrelated GirCore binding bug (ENGINEERING.md's
/// "Wayland screenshot" section has the full story).
///
/// Session flow (each of the first three goes through the same
/// Request/Response pattern WaylandScreenshotBackend.cs already uses):
/// CreateSession -> SelectSources -> Start -> OpenPipeWireRemote. The
/// consent UI here is GNOME's source-*picker* (choose a monitor/window to
/// share) - shown once, then skippable via a `restore_token`: SelectSources
/// requests `persist_mode: 2` (persist until explicitly revoked) and,
/// once Start's results hand back a fresh `restore_token`, that token is
/// saved to `~/.uictl/screencast-restore-token` and replayed on every
/// later SelectSources call. Whether GNOME's implementation actually
/// honors this and skips the picker on a second run is exactly what this
/// class exists to prove out - Screenshot's own interactive:true path
/// showed a dialog on literally every call regardless of what was passed,
/// so this is not assumed to just work.
/// </summary>
internal static class WaylandScreenCastBackend
{
    private static readonly string RestoreTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".uictl", "screencast-restore-token");

    private const uint SourceTypeMonitor = 1;

    /// <summary>
    /// SPA/xdg-desktop-portal defines Hidden=1, Embedded=2, Metadata=4 for
    /// cursor_mode. Embedded (cursor drawn directly into the captured
    /// frame) is used here - matches what most screenshot tools show by
    /// default. A fixed choice, not queried from ScreenCast's own
    /// AvailableCursorModes property first - see ENGINEERING.md's "Wayland
    /// screenshot" section for a real, live-diagnosed false alarm this
    /// caused: mid-development, *every* cursor_mode value started failing
    /// with "Unavailable cursor mode N", including ones confirmed working
    /// minutes earlier, and the actual cause turned out to be
    /// `xdg-desktop-portal.service` (the main dispatcher, not the GNOME
    /// backend) caching a stale `AvailableCursorModes=0` after the backend
    /// crashed and respawned during rapid repeated testing - restarting
    /// the dispatcher itself (not just the backend) restored
    /// `AvailableCursorModes=7`, and this exact code then worked again
    /// with no changes. Not a reason to add capability-querying here
    /// preemptively; a hard failure with this specific error message is
    /// the signal to check `busctl --user get-property
    /// org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop
    /// org.freedesktop.portal.ScreenCast AvailableCursorModes` before
    /// assuming the code regressed.
    /// </summary>
    private const uint CursorModeEmbedded = 2;

    private const uint PersistModePersistent = 2;

    public sealed record Session(SafeHandle PipeWireFd, uint NodeId, ObjectPath SessionHandle, Connection Connection);

    /// <summary>
    /// Runs the full CreateSession/SelectSources/Start/OpenPipeWireRemote
    /// sequence. Shows GNOME's source-picker dialog on the first call (or
    /// any call where the saved restore_token has been revoked/expired) -
    /// callers should warn a human the same way WaylandScreenshotBackend.cs
    /// does before invoking this.
    /// </summary>
    public static async Task<Session> StartSessionAsync()
    {
        (Connection connection, string sender) = await PortalRegistration.ConnectAsync();
        var screenCast = connection.CreateProxy<IScreenCastPortal>(PortalRegistration.ServiceName, PortalRegistration.ServicePath);

        var sessionHandle = await CreateSessionAsync(connection, sender, screenCast);
        try
        {
            string? restoreToken = TryReadRestoreToken();
            string? newRestoreToken = await SelectSourcesAsync(connection, sender, screenCast, sessionHandle, restoreToken);

            (uint nodeId, string? startRestoreToken) = await StartAsync(connection, sender, screenCast, sessionHandle);

            // Start's own restore_token (if present) is the authoritative
            // one to save - SelectSources only ever echoes back what it
            // was given, Start is what actually mints a fresh token for a
            // brand-new or renewed grant.
            string? tokenToSave = startRestoreToken ?? newRestoreToken;
            if (!string.IsNullOrEmpty(tokenToSave))
                SaveRestoreToken(tokenToSave);

            CloseSafeHandle pipeWireFd = await screenCast.OpenPipeWireRemoteAsync(sessionHandle, new Dictionary<string, object>());
            return new Session(pipeWireFd, nodeId, sessionHandle, connection);
        }
        catch
        {
            await TryCloseSessionAsync(connection, sessionHandle);
            throw;
        }
    }

    public static async Task CloseSessionAsync(Session session) =>
        await TryCloseSessionAsync(session.Connection, session.SessionHandle);

    private static async Task TryCloseSessionAsync(Connection connection, ObjectPath sessionHandle)
    {
        try
        {
            var session = connection.CreateProxy<IPortalSession>(PortalRegistration.ServiceName, sessionHandle);
            await session.CloseAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"uictl: failed to close ScreenCast portal session ({ex.Message}) - it will linger until this process exits or the portal cleans it up itself");
        }
    }

    private static async Task<ObjectPath> CreateSessionAsync(Connection connection, string sender, IScreenCastPortal screenCast)
    {
        string sessionHandleToken = "uictl_sess_" + Guid.NewGuid().ToString("N")[..12];
        var options = new Dictionary<string, object>
        {
            ["session_handle_token"] = sessionHandleToken,
        };
        var (response, results) = await CallRequestAsync(connection, sender, options,
            handleToken => screenCast.CreateSessionAsync(WithHandleToken(options, handleToken)));

        if (response != 0 || !results.TryGetValue("session_handle", out var sessionHandleObj))
            throw new UiCtlException($"ScreenCast CreateSession failed (response code {response})");
        return new ObjectPath((string)sessionHandleObj);
    }

    private static async Task<string?> SelectSourcesAsync(Connection connection, string sender, IScreenCastPortal screenCast, ObjectPath sessionHandle, string? restoreToken)
    {
        var options = new Dictionary<string, object>
        {
            ["types"] = SourceTypeMonitor,
            ["multiple"] = false,
            ["cursor_mode"] = CursorModeEmbedded,
            ["persist_mode"] = PersistModePersistent,
        };
        if (!string.IsNullOrEmpty(restoreToken))
            options["restore_token"] = restoreToken;

        var (response, results) = await CallRequestAsync(connection, sender, options,
            handleToken => screenCast.SelectSourcesAsync(sessionHandle, WithHandleToken(options, handleToken)));

        if (response != 0)
            throw new UiCtlException($"ScreenCast SelectSources failed (response code {response}) - consent may have been denied");
        return results.TryGetValue("restore_token", out var t) ? (string)t : null;
    }

    private static async Task<(uint NodeId, string? RestoreToken)> StartAsync(Connection connection, string sender, IScreenCastPortal screenCast, ObjectPath sessionHandle)
    {
        var options = new Dictionary<string, object>();
        var (response, results) = await CallRequestAsync(connection, sender, options,
            handleToken => screenCast.StartAsync(sessionHandle, "", WithHandleToken(options, handleToken)));

        if (response != 0)
            throw new UiCtlException($"ScreenCast Start failed (response code {response})");
        if (!results.TryGetValue("streams", out var streamsObj))
            throw new UiCtlException("ScreenCast Start succeeded but returned no streams");

        // streams is a(ua{sv}) - array of (node_id, properties). Only one
        // stream is ever requested (multiple:false above), so just take
        // the first. Tmds.DBus decodes each struct as a real
        // ValueTuple<uint, IDictionary<string,object>>, not a boxed
        // object[] the way a{sv} values usually come back - confirmed
        // live (an (object[]) cast throws InvalidCastException naming the
        // actual runtime type).
        var streams = (ValueTuple<uint, IDictionary<string, object>>[])streamsObj;
        var (nodeId, _) = streams[0];
        string? restoreToken = results.TryGetValue("restore_token", out var t) ? (string)t : null;
        return (nodeId, restoreToken);
    }

    private static Dictionary<string, object> WithHandleToken(Dictionary<string, object> options, string handleToken)
    {
        options["handle_token"] = handleToken;
        return options;
    }

    /// <summary>
    /// Subscribes to the Request's Response signal *before* calling the
    /// method that creates it, using the precomputed request path - same
    /// race-avoidance pattern as WaylandScreenshotBackend.cs's Screenshot
    /// call (confirmed load-bearing there: subscribing only after the
    /// call returns lost the signal outright when it resolved fast).
    /// </summary>
    private static async Task<(uint Response, IDictionary<string, object> Results)> CallRequestAsync(
        Connection connection, string sender, Dictionary<string, object> options, Func<string, Task<ObjectPath>> call)
    {
        string handleToken = "uictl_req_" + Guid.NewGuid().ToString("N")[..12];
        var requestPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{sender}/{handleToken}");

        var tcs = new TaskCompletionSource<(uint, IDictionary<string, object>)>();
        var request = connection.CreateProxy<IPortalRequest>(PortalRegistration.ServiceName, requestPath);
        using var subscription = await request.WatchResponseAsync(r => tcs.TrySetResult(r));

        await call(handleToken);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (completed != tcs.Task)
            throw new UiCtlException("ScreenCast portal request timed out waiting for a response");
        return tcs.Task.Result;
    }

    private static string? TryReadRestoreToken()
    {
        try { return File.Exists(RestoreTokenPath) ? File.ReadAllText(RestoreTokenPath).Trim() : null; }
        catch { return null; }
    }

    private static void SaveRestoreToken(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestoreTokenPath)!);
            File.WriteAllText(RestoreTokenPath, token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"uictl: failed to save ScreenCast restore token ({ex.Message}) - the source picker will show again next time");
        }
    }
}
