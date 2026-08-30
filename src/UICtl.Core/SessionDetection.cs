namespace UICtl.Core;

/// <summary>
/// The one place the $WAYLAND_DISPLAY-first session-type check lives -
/// previously duplicated inline in Permissions.cs, now also needed by
/// WindowActivation.cs to pick a backend. Detected from
/// $WAYLAND_DISPLAY/$DISPLAY, not $XDG_SESSION_TYPE - observed unset in a
/// real session during development, see ENGINEERING.md.
/// </summary>
internal static class SessionDetection
{
    public static bool HasWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    public static bool HasX11 => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static string SessionType => HasWayland ? "wayland" : "x11";
}
