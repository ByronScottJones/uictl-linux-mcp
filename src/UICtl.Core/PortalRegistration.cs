using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// The xdg-desktop-portal connect/app-registration dance every portal
/// interface (Screenshot, ScreenCast) needs first - extracted from
/// WaylandScreenshotBackend.cs (Phase 4) so WaylandScreenCastBackend.cs
/// (the ScreenCast+PipeWire follow-up, see ENGINEERING.md's "Wayland
/// screenshot" section) doesn't duplicate it. Live-verified findings this
/// embodies, from the original Phase 4 investigation:
///
/// A "host" (non-Flatpak/Snap) D-Bus client needs an app identity before
/// any permission-gated portal interface will do anything at all.
/// `org.freedesktop.host.portal.Registry.Register(app_id, {})` fails with
/// "App info not found for '&lt;id&gt;'" unless a .desktop file named
/// `&lt;app_id&gt;.desktop` exists under $XDG_DATA_HOME/applications whose
/// `Exec=` resolves to the exact path of the calling process's own binary.
/// This project has no fixed install location for the `uictl` binary (see
/// README.md's Build section), so `EnsureDesktopFileInstalled` self-
/// writes/repairs this file from `Environment.ProcessPath` on every setup
/// rather than assuming one.
/// </summary>
internal static class PortalRegistration
{
    public const string ServiceName = "org.freedesktop.portal.Desktop";
    public static readonly ObjectPath ServicePath = "/org/freedesktop/portal/desktop";
    public const string AppId = "org.byronscottjones.uictl";

    private static readonly Lazy<Task<(Connection Connection, string LocalNamePrefix)>> LazySetup = new(SetupAsync);

    /// <returns>A shared D-Bus session connection plus this connection's own local name (sanitized, "_"-joined) - needed to build the per-request object path any portal Request/Response call uses.</returns>
    public static Task<(Connection Connection, string LocalNamePrefix)> ConnectAsync() => LazySetup.Value;

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
            // is expected, not a real error.
            Console.Error.WriteLine($"uictl: portal Register returned {ex.ErrorName} ({ex.Message}) - treating as already-registered and continuing; if the portal call subsequently fails, this may be the real cause");
        }

        string sender = info.LocalName.TrimStart(':').Replace('.', '_');
        return (connection, sender);
    }

    /// <summary>
    /// A "host" (non-Flatpak/Snap) D-Bus client must have a .desktop file
    /// under $XDG_DATA_HOME/applications named "&lt;app_id&gt;.desktop" whose
    /// Exec= resolves to the exact path of the process actually making the
    /// D-Bus call, or org.freedesktop.host.portal.Registry.Register fails
    /// with "App info not found". Self-installs/repairs this file from the
    /// daemon's own running path (Environment.ProcessPath) on every setup
    /// rather than assuming a fixed install location, since this project
    /// has none (see README.md's Build section - the binary can live
    /// anywhere on PATH).
    /// </summary>
    private static void EnsureDesktopFileInstalled()
    {
        string exePath = Environment.ProcessPath
            ?? throw new UiCtlException("could not determine this process's own executable path (Environment.ProcessPath is null) - needed to register with xdg-desktop-portal");

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
            "Comment=GUI automation daemon - registered with xdg-desktop-portal for Wayland screenshot/screencast capture, see ENGINEERING.md's Wayland screenshot section\n" +
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
        Console.Error.WriteLine($"uictl: wrote {desktopPath} (xdg-desktop-portal app registration)");
    }
}
