namespace UICtl.Core;

/// <summary>
/// One live window as a window-management backend (X11/EWMH or the
/// Wayland/GNOME Shell extension) reports it - a completely separate id
/// space from AT-SPI's own windowId scheme used by windows.list/elements
/// (see WindowActivation.cs's doc comment for how the two are correlated).
/// </summary>
internal sealed record BackendWindow(long Id, int Pid, string Title, Frame Frame);

/// <summary>
/// Window activation/focus-query primitive - the one thing AT-SPI has no
/// primitive for (Component.GrabFocus returns NotSupported in practice,
/// see ENGINEERING.md). Two implementations, chosen at runtime by
/// SessionDetection: X11WindowBackend (direct Xlib/EWMH) and
/// WaylandWindowBackend (the companion GNOME Shell extension over D-Bus).
/// </summary>
internal interface IWindowBackend
{
    IReadOnlyList<BackendWindow> ListWindows();
    bool Activate(long id);
    BackendWindow? GetFocusedWindow();
}
