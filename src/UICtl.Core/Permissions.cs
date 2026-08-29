namespace UICtl.Core;

/// <summary>
/// `permissions.status` - the gating diagnostic AGENTS.md tells callers to
/// check before assuming input/windowing/AT-SPI will work. Shape is
/// Linux-specific per MCP_INTERFACE.md's uictl_permissions section, not
/// mirrored from macOS/Windows.
/// </summary>
public static class Permissions
{
    public static PermissionsStatus GetStatus()
    {
        // Detected from $WAYLAND_DISPLAY/$DISPLAY, not $XDG_SESSION_TYPE -
        // observed unset in a real session during development, see
        // ENGINEERING.md.
        bool hasWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool hasX11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        string sessionType = hasWayland ? "wayland" : "x11";

        bool uinputWritable = UinputDevice.ProbeWritable();
        bool atspiEnabled = TryProbeAtspi();

        // The companion GNOME Shell extension isn't built yet (a later
        // phase per ENGINEERING.md) - always false on Wayland, not
        // applicable on X11.
        bool? shellExtensionConnected = sessionType == "wayland" ? false : null;

        bool interactive = (hasWayland || hasX11) && Tmds.DBus.Address.Session is not null;

        return new PermissionsStatus(
            SessionType: sessionType,
            InputMethod: "uinput",
            UinputWritable: uinputWritable,
            AtspiEnabled: atspiEnabled,
            ShellExtensionConnected: shellExtensionConnected,
            Interactive: interactive);
    }

    private static bool TryProbeAtspi()
    {
        try
        {
            Accessibility.ListApps(includeBackground: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
