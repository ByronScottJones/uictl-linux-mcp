using Tmds.DBus;

namespace UICtl.Core.Interop;

/// <summary>
/// Tmds.DBus proxy for the companion GNOME Shell extension's
/// org.byronscottjones.uictl.WindowManager service (see gnome-extension/) - hand-written
/// against that extension's own D-Bus interface XML
/// (dbusServer.js's WINDOW_MANAGER_INTERFACE), same pattern as
/// AtspiInterfaces.cs. Struct-array marshaling (a(uisiiii) -> a C# tuple
/// array) follows the same convention already proven working there
/// (e.g. IAtspiAccessible.GetChildrenAsync's (string, ObjectPath)[]).
/// Must be public - Tmds.DBus's dynamic proxy assembly cannot implement an
/// internal interface (confirmed in AtspiInterfaces.cs's own doc comment).
/// </summary>
[DBusInterface("org.byronscottjones.uictl.WindowManager")]
public interface IUictlWindowManager : IDBusObject
{
    /// <returns>Array of (stableSeq, pid, title, x, y, width, height).</returns>
    Task<(uint, int, string, int, int, int, int)[]> ListWindowsAsync();

    Task<bool> ActivateWindowAsync(uint stableSeq);

    /// <returns>(found, stableSeq) - stableSeq is meaningless when found is false.</returns>
    Task<(bool, uint)> GetFocusedWindowAsync();
}
