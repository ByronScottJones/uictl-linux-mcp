using System.Text.Json;

namespace UICtl.Core;

/// <summary>
/// `permissions.status` - the gating diagnostic AGENTS.md tells callers to
/// check before assuming input/windowing/AT-SPI will work. Shape is
/// Linux-specific per MCP_INTERFACE.md's uictl_permissions section, not
/// mirrored from macOS/Windows.
/// </summary>
public static class Permissions
{
    /// <summary>
    /// Written by scripts/preflight.sh - deliberately outside UICtl.Ipc's
    /// DaemonPaths (Core has no reference to Ipc), same "~/.uictl" base
    /// directory convention.
    /// </summary>
    private static readonly string PreflightPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".uictl", "preflight.json");

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

        var preflight = ReadPreflightStatus();

        return new PermissionsStatus(
            SessionType: sessionType,
            InputMethod: "uinput",
            UinputWritable: uinputWritable,
            AtspiEnabled: atspiEnabled,
            ShellExtensionConnected: shellExtensionConnected,
            Interactive: interactive,
            PreflightReady: preflight.Ready,
            PreflightAt: preflight.At,
            PreflightManualSteps: preflight.ManualSteps);
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

    /// <summary>
    /// Never seen scripts/preflight.sh run -&gt; (false, null, []). Ran but
    /// not fully ready -&gt; (false, &lt;timestamp&gt;, &lt;steps&gt;), so a
    /// caller can tell "never run" apart from "ran, but something's still
    /// not ready" - and what to actually do about it. A malformed file
    /// (partial write, hand-edited) is treated the same as "never run"
    /// rather than throwing - this is a status hint, not something that
    /// should fail the whole permissions call.
    /// </summary>
    private static (bool Ready, string? At, IReadOnlyList<string> ManualSteps) ReadPreflightStatus()
    {
        if (!File.Exists(PreflightPath)) return (false, null, Array.Empty<string>());
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(PreflightPath));
            var root = doc.RootElement;
            bool ready = root.TryGetProperty("ready", out var readyProp) && readyProp.GetBoolean();
            string? at = root.TryGetProperty("ranAt", out var atProp) ? atProp.GetString() : null;

            var steps = new List<string>();
            if (root.TryGetProperty("manualStepsRemaining", out var stepsProp) && stepsProp.ValueKind == JsonValueKind.Array)
                foreach (var step in stepsProp.EnumerateArray())
                    if (step.GetString() is { } s) steps.Add(s);

            return (ready, at, steps);
        }
        catch
        {
            return (false, null, Array.Empty<string>());
        }
    }
}
