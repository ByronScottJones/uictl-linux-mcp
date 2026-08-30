namespace UICtl.Core;

/// <summary>Screen coordinate: top-left origin, y increasing downward. See MCP_INTERFACE.md for the X11-vs-Wayland coordinate-space caveat.</summary>
public readonly record struct Point(double X, double Y);

/// <summary>Bounding rectangle in the same coordinate space as <see cref="Point"/>.</summary>
public readonly record struct Frame(double X, double Y, double W, double H)
{
    public Point Center => new(X + W / 2, Y + H / 2);
}

public enum MouseButton { Left, Right, Center }

public sealed record AppInfo(int Pid, string Name, string BundleId);

public sealed record WindowInfo(long WindowId, int Pid, string Title, Frame Frame, long? DisplayId);

/// <summary>
/// One monitor. <c>Index</c> matches what `screenshot --screen &lt;index&gt;`
/// expects; <c>DisplayId</c> is an opaque per-backend id (X11: RandR output
/// id; Wayland: whatever Mutter.DisplayConfig assigns - see MCP_INTERFACE.md).
/// <c>Scale</c> is the monitor's configured scale factor, which may be
/// fractional under GNOME's fractional-scaling feature.
/// </summary>
public sealed record DisplayInfo(int Index, long DisplayId, Frame Frame, bool IsMain, double Scale);

public sealed record ResolvedWindow(long WindowId, int Pid);

public sealed record ElementInfo(string Id, string Role, string Title, string? Value, Frame Frame);

public sealed record AnnotatedElement(int Number, ElementInfo Element);

public sealed record ElementWalkResult(IReadOnlyList<ElementInfo> Elements, bool Truncated);

public sealed record ElementWalkOptions(string? RoleFilter = null, string? TitleContains = null, int MaxDepth = 25, int MaxElements = 500);

/// <summary>
/// Linux-specific shape (see MCP_INTERFACE.md's uictl_permissions section -
/// deliberately not the same fields as macOS/Windows, since the underlying
/// concepts differ). <c>SessionType</c> is detected from $WAYLAND_DISPLAY/
/// $DISPLAY, not $XDG_SESSION_TYPE (observed unset in a real session during
/// development). <c>ShellExtensionConnected</c> is null on an X11 session.
/// <c>PreflightReady</c>/<c>PreflightAt</c>/<c>PreflightManualSteps</c>/
/// <c>PreflightChecks</c> mirror scripts/preflight.sh's own
/// <c>ready</c>/<c>ranAt</c>/<c>manualStepsRemaining</c>/<c>checks</c>
/// fields (see Permissions.cs) - lets a caller tell "preflight never run"
/// apart from "ran, but something's still not ready", down to exactly
/// which named check(s) failed, without re-reading the raw file itself.
/// </summary>
public sealed record PermissionsStatus(
    string SessionType,
    string InputMethod,
    bool UinputWritable,
    bool AtspiEnabled,
    bool? ShellExtensionConnected,
    bool Interactive,
    bool PreflightReady,
    string? PreflightAt,
    IReadOnlyList<string> PreflightManualSteps,
    IReadOnlyDictionary<string, bool> PreflightChecks);

public readonly record struct PixelColor(byte R, byte G, byte B, byte A);

/// <summary>One recognized line of text. <c>Confidence</c> is a real Tesseract per-word-averaged value on this platform (unlike Windows' always-null) - see MCP_INTERFACE.md.</summary>
public sealed record TextBlock(string Text, Frame Frame, double? Confidence);

/// <summary>
/// One local feedback draft (`~/.uictl/feedback.json`, see FeedbackStore.cs).
/// <c>Category</c> is a free-form string (matches MCP_INTERFACE.md's
/// `category: string`, not a fixed enum) - used as the GitHub issue's
/// label when submitted. <c>UpdatedAt</c> is null until the first
/// `feedback.update` call.
/// </summary>
public sealed record FeedbackEntry(int Id, string Category, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>One GitHub issue returned by a `feedback.checkDuplicates`/`feedback.submit` search - see FeedbackGitHub.cs.</summary>
public sealed record GitHubIssueSummary(int Number, string Title, string Url, string State);

/// <summary>One entry in the daemon's in-memory activity log (`log.export`, and eventually `log.show`'s GTK4 window - see ActivityLog.cs). <c>Params</c>/<c>Result</c> are already redacted where MCP_INTERFACE.md's rules require it, so they're safe to serialize verbatim.</summary>
public sealed record ActivityLogEntry(int Id, DateTimeOffset Timestamp, string Command, object? Params, bool Success, object? Result, string? Error, double DurationMs);
