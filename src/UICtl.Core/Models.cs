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
/// </summary>
public sealed record PermissionsStatus(
    string SessionType,
    string InputMethod,
    bool UinputWritable,
    bool AtspiEnabled,
    bool? ShellExtensionConnected,
    bool Interactive);

public readonly record struct PixelColor(byte R, byte G, byte B, byte A);

/// <summary>One recognized line of text. <c>Confidence</c> is a real Tesseract per-word-averaged value on this platform (unlike Windows' always-null) - see MCP_INTERFACE.md.</summary>
public sealed record TextBlock(string Text, Frame Frame, double? Confidence);
