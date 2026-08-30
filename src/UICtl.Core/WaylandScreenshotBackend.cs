using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// Wayland screenshot capture via the xdg-desktop-portal Screenshot
/// interface - see Interop/PortalInterfaces.cs's doc comment for the two
/// live-verified, non-obvious requirements this class exists to satisfy
/// (app-id registration via a self-installed .desktop file, and why
/// `interactive: true` - with its real per-call consent dialog - is the
/// only path that actually completes).
///
/// **This means every Wayland screenshot needs a manual click** - there
/// is no zero-dialog path through this API in this GNOME build (the
/// `interactive: false` "reuse a stored grant" variant fails outright
/// with no stored permission ever getting created via this interface, at
/// least for a non-sandboxed host app like this one). ScreenCast+PipeWire
/// is the documented alternative for a true one-time-consent flow -
/// deliberately deferred, see ENGINEERING.md.
/// </summary>
internal static class WaylandScreenshotBackend
{
    private const string ServiceName = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath ServicePath = "/org/freedesktop/portal/desktop";
    private const string AppId = "org.byronscottjones.uictl";

    private static readonly Lazy<Task<(Connection Connection, string LocalNamePrefix)>> LazySetup = new(SetupAsync);

    private static async Task<(Connection, string)> SetupAsync()
    {
        if (Address.Session is null)
            throw new UiCtlException("no D-Bus session address available ($DBUS_SESSION_BUS_ADDRESS unset?) - cannot reach xdg-desktop-portal");
        var connection = new Connection(Address.Session);
        ConnectionInfo info = await connection.ConnectAsync();

        EnsureDesktopFileInstalled();
        var registry = connection.CreateProxy<IPortalRegistry>(ServiceName, ServicePath);
        try
        {
            await registry.RegisterAsync(AppId, new Dictionary<string, object>());
        }
        catch (DBusException ex)
        {
            // Already registered (a prior call on this same connection, or
            // the portal remembering this exact executable path from a
            // previous daemon run) is the common "failure" here - Register
            // has no documented idempotent no-op response, so a thrown
            // DBusException on the *second* call in a process's lifetime
            // is expected, not a real error. A genuinely broken .desktop
            // file (wrong Exec=) surfaces later instead, as the Screenshot
            // call itself failing - see ScreenshotAsync's guidance there.
            //
            // The exact ErrorName the portal uses for "already registered"
            // isn't live-confirmed, so this still swallows broadly rather
            // than risk mis-filtering - but it logs so an unexpected cause
            // (a real permission or malformed-.desktop error) is visible
            // instead of silently disappearing.
            Console.Error.WriteLine($"uictl: portal Register returned {ex.ErrorName} ({ex.Message}) - treating as already-registered and continuing; if screenshots subsequently fail, this may be the real cause");
        }

        string sender = info.LocalName.TrimStart(':').Replace('.', '_');
        return (connection, sender);
    }

    /// <summary>
    /// A "host" (non-Flatpak/Snap) D-Bus client must have a .desktop file
    /// under $XDG_DATA_HOME/applications named "&lt;AppId&gt;.desktop" whose
    /// Exec= resolves to the exact path of the process actually making the
    /// D-Bus call, or org.freedesktop.host.portal.Registry.Register fails
    /// with "App info not found" (live-confirmed - see
    /// Interop/PortalInterfaces.cs). Self-installs/repairs this file from
    /// the daemon's own running path (Environment.ProcessPath) on every
    /// setup rather than assuming a fixed install location, since this
    /// project has none (see README.md's Build section - the binary can
    /// live anywhere on PATH).
    /// </summary>
    private static void EnsureDesktopFileInstalled()
    {
        string exePath = Environment.ProcessPath
            ?? throw new UiCtlException("could not determine this process's own executable path (Environment.ProcessPath is null) - needed to register with xdg-desktop-portal for screenshot capture");

        // Exec= is a bare, unquoted line in .desktop-file syntax - a
        // newline would forge extra keys/sections, and a relative path
        // wouldn't resolve the way the portal (which invokes Exec=
        // directly, not via a shell/$PATH lookup) expects.
        if (exePath.IndexOfAny(['\n', '\r']) >= 0)
            throw new UiCtlException($"this process's executable path contains a newline, can't write a valid .desktop Exec= line: {exePath}");
        if (!Path.IsPathRooted(exePath))
            throw new UiCtlException($"this process's executable path isn't absolute, can't write a reliable .desktop Exec= line: {exePath}");

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string appsDir = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(appsDir);

        string desktopPath = Path.Combine(appsDir, $"{AppId}.desktop");
        string desired =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=uictl\n" +
            "Comment=GUI automation daemon - registered with xdg-desktop-portal for Wayland screenshot capture, see ENGINEERING.md's Wayland screenshot section\n" +
            $"Exec={exePath}\n" +
            "NoDisplay=true\n" +
            "Categories=Utility;\n";

        if (File.Exists(desktopPath) && File.ReadAllText(desktopPath) == desired)
            return;

        // Atomic replace: a writer killed mid-File.WriteAllText would
        // otherwise leave a truncated/corrupt .desktop file that then
        // breaks every future portal Register call, not just this one.
        string tmpPath = Path.Combine(appsDir, $"{AppId}.desktop.tmp.{Guid.NewGuid():N}");
        File.WriteAllText(tmpPath, desired);
        File.Move(tmpPath, desktopPath, overwrite: true);
        Console.Error.WriteLine($"uictl: wrote {desktopPath} (xdg-desktop-portal app registration for Wayland screenshot capture)");
    }

    /// <summary>Captures the whole screen (the portal has no window/region-scoped capture option - see Screenshot.cs for cropping) and returns the raw PNG bytes it saved. Shows a real consent dialog every call - see class doc comment.</summary>
    public static byte[] CapturePng() => AsyncBridge.RunSync(async () =>
    {
        (Connection connection, string sender) = await LazySetup.Value;

        string token = "uictl_" + Guid.NewGuid().ToString("N")[..12];
        var requestPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{sender}/{token}");

        var tcs = new TaskCompletionSource<(uint, IDictionary<string, object>)>();
        var request = connection.CreateProxy<IPortalRequest>(ServiceName, requestPath);
        // Subscribed *before* calling Screenshot, using the precomputed
        // request path per the portal spec's documented race-avoidance
        // pattern - confirmed live to matter: subscribing only after
        // Screenshot() returns lost the Response signal outright when the
        // call resolved fast (see WaylandScreenshotBackend's git history/
        // ENGINEERING.md for the live debugging session that found this).
        using var subscription = await request.WatchResponseAsync(r => tcs.TrySetResult(r));

        var portal = connection.CreateProxy<IScreenshotPortal>(ServiceName, ServicePath);
        var options = new Dictionary<string, object> { ["handle_token"] = token, ["interactive"] = true };
        Console.Error.WriteLine("uictl: requesting Wayland screenshot via xdg-desktop-portal - a \"Take Screenshot\" consent dialog will appear and needs a manual click (see AGENTS.md)");
        await portal.ScreenshotAsync("", options);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (completed != tcs.Task)
            throw new UiCtlException("screenshot portal request timed out waiting for the consent dialog to be answered");

        var (response, results) = tcs.Task.Result;
        if (response == 1)
            throw new UiCtlException("screenshot cancelled (consent dialog was dismissed/denied)");
        if (response != 0 || !results.TryGetValue("uri", out var uriObj))
            throw new UiCtlException($"screenshot portal request failed (response code {response})");

        var uri = new Uri((string)uriObj);
        return await File.ReadAllBytesAsync(uri.LocalPath);
    });
}
