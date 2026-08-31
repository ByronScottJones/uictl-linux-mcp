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
        bool hasWayland = SessionDetection.HasWayland;
        bool hasX11 = SessionDetection.HasX11;
        string sessionType = SessionDetection.SessionType;

        bool uinputWritable = UinputDevice.ProbeWritable();
        bool atspiEnabled = GetCached(ref _atspiCache, TryProbeAtspi);

        // Not applicable on X11 (activate/focus.* use direct Xlib/EWMH
        // there, no Shell extension involved) - only probed on Wayland.
        bool? shellExtensionConnected = sessionType == "wayland" ? GetCached(ref _shellExtensionCache, TryProbeShellExtension) : null;

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
            PreflightManualSteps: preflight.ManualSteps,
            PreflightChecks: preflight.Checks);
    }

    /// <summary>
    /// Both probes below only ever ran with no deadline at all - fine
    /// nearly always (a local D-Bus round trip is normally sub-millisecond),
    /// but live-verified as a real, if intermittent, problem: GNOME Shell's
    /// own D-Bus service occasionally took as long as ~48s to answer a
    /// `WaylandWindowBackend.ListWindows()` call (reason unconfirmed - not
    /// this project's bug to fix, GNOME Shell's own responsiveness is
    /// outside its control) - and since DaemonServer.cs handles one
    /// connection at a time, that single slow probe blocked *every* other
    /// command, including totally unrelated ones, for the same ~48s. A
    /// diagnostic "can I reach X" check should fail fast, not stall the
    /// whole daemon waiting for an answer nobody's holding out for - a much
    /// shorter, probe-specific bound than `AsyncBridge.DefaultDBusTimeout`
    /// (the 60s ceiling every real AT-SPI/D-Bus call - `apps.list`/
    /// `windows.list`/etc. - now has too, added later in the same
    /// investigation; see ENGINEERING.md's "Daemon reliability: AT-SPI/D-Bus call timeouts").
    /// Those callers still want to wait out a real, if slow, answer rather
    /// than fail fast the way a probe should - this is a separate, shorter
    /// bound layered on top for exactly that different purpose, not a
    /// replacement for it.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// On top of the per-call timeout: a successful probe is trusted for 5
    /// minutes rather than re-checked on every single `permissions.status`
    /// call - suggested after the ~48s stall above turned out to be
    /// GNOME Shell's own occasional slowness, not something a timeout alone
    /// fully insulates against if a caller (uictl-gui's toast, an agent
    /// polling permissions in a loop) asks frequently: fewer probes means
    /// fewer chances to hit that slowness at all. A *failed* probe is never
    /// cached - if AT-SPI or the Shell extension is down, the next call
    /// should notice as soon as it's fixed, not wait out the rest of a
    /// stale 5-minute window.
    /// </summary>
    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromMinutes(5);

    private static (bool Value, DateTime CheckedAtUtc)? _atspiCache;
    private static (bool Value, DateTime CheckedAtUtc)? _shellExtensionCache;

    private static bool GetCached(ref (bool Value, DateTime CheckedAtUtc)? cache, Func<bool> probe)
    {
        if (cache is { Value: true } hit && DateTime.UtcNow - hit.CheckedAtUtc < ProbeCacheLifetime)
            return true;

        bool result = probe();
        cache = (result, DateTime.UtcNow);
        return result;
    }

    private static bool TryProbeAtspi() =>
        TryWithTimeout(() => Accessibility.ListApps(includeBackground: false));

    private static bool TryProbeShellExtension() =>
        TryWithTimeout(() => new WaylandWindowBackend().ListWindows());

    /// <summary>
    /// Bounds the *wait*, not the underlying call - Tmds.DBus has no way to
    /// cancel an in-flight method call once sent, so a timed-out probe's
    /// Task keeps running to completion on its own thread-pool thread in
    /// the background rather than actually stopping. Harmless: the daemon's
    /// accept loop isn't blocked by it (CommandDispatcher.Dispatch already
    /// returned by the time this gives up), and it doesn't accumulate
    /// unboundedly since each one eventually finishes on its own.
    /// </summary>
    private static bool TryWithTimeout(Action probe)
    {
        try
        {
            return Task.Run(() => { probe(); return true; }).Wait(ProbeTimeout);
        }
        catch
        {
            return false;
        }
    }

    private static readonly (bool Ready, string? At, IReadOnlyList<string> ManualSteps, IReadOnlyDictionary<string, bool> Checks) EmptyPreflight =
        (false, null, Array.Empty<string>(), new Dictionary<string, bool>());

    /// <summary>
    /// Never seen scripts/preflight.sh run -&gt; all-empty/false. Ran but
    /// not fully ready -&gt; a timestamp, the manual steps still needed, and
    /// which named checks failed (per-check detail requested in PR #2
    /// review, so a caller doesn't have to re-read the raw file itself to
    /// see exactly what's wrong) - so a caller can tell "never run" apart
    /// from "ran, but something's still not ready". A malformed file
    /// (partial write, hand-edited) is treated the same as "never run"
    /// rather than throwing - this is a status hint, not something that
    /// should fail the whole permissions call.
    /// </summary>
    private static (bool Ready, string? At, IReadOnlyList<string> ManualSteps, IReadOnlyDictionary<string, bool> Checks) ReadPreflightStatus()
    {
        if (!File.Exists(PreflightPath)) return EmptyPreflight;
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

            var checks = new Dictionary<string, bool>();
            if (root.TryGetProperty("checks", out var checksProp) && checksProp.ValueKind == JsonValueKind.Object)
                foreach (var check in checksProp.EnumerateObject())
                    if (check.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        checks[check.Name] = check.Value.GetBoolean();

            return (ready, at, steps, checks);
        }
        catch
        {
            return EmptyPreflight;
        }
    }
}
