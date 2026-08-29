using Tmds.DBus;

namespace UICtl.Core.Interop;

/// <summary>
/// Hand-written D-Bus proxy interfaces for AT-SPI2 (org.a11y.*), verified
/// live against a real GNOME/Mutter desktop - see ENGINEERING.md's AT-SPI2
/// section. Tmds.DBus generates a dynamic proxy implementing each of these
/// at runtime; every interface must be <c>public</c> or the dynamic proxy
/// assembly cannot implement it (confirmed: an internal interface throws
/// TypeLoadException "attempting to implement an inaccessible interface").
/// </summary>

/// <summary>The session-bus broker that hands out the address of the separate, dedicated AT-SPI bus.</summary>
[DBusInterface("org.a11y.Bus")]
public interface IA11yBus : IDBusObject
{
    Task<string> GetAddressAsync();
}

/// <summary>
/// Every AT-SPI object (application, window, widget) implements this.
/// <c>GetAsync&lt;T&gt;</c> covers the interface's handful of read-only
/// properties (Name, Description, ...) without needing a full properties
/// dictionary type per interface.
/// </summary>
[DBusInterface("org.a11y.atspi.Accessible")]
public interface IAtspiAccessible : IDBusObject
{
    Task<(string, ObjectPath)[]> GetChildrenAsync();
    Task<(string, ObjectPath)> GetChildAtIndexAsync(int index);
    Task<string> GetRoleNameAsync();
    Task<string[]> GetInterfacesAsync();
    Task<T> GetAsync<T>(string prop);
}

/// <summary>
/// On-screen geometry. <c>coordType</c>: 0 = screen, 1 = window, 2 = parent
/// (ATSPI_COORD_TYPE_* - screen confirmed live against a real window).
/// </summary>
[DBusInterface("org.a11y.atspi.Component")]
public interface IAtspiComponent : IDBusObject
{
    Task<(int X, int Y, int Width, int Height)> GetExtentsAsync(uint coordType);
}

/// <summary>Direct value set - the "atspiValue" method tier in MCP_INTERFACE.md's uictl_type contract.</summary>
[DBusInterface("org.a11y.atspi.EditableText")]
public interface IAtspiEditableText : IDBusObject
{
    Task<bool> SetTextContentsAsync(string newContents);
}

[DBusInterface("org.a11y.atspi.Text")]
public interface IAtspiText : IDBusObject
{
    Task<string> GetTextAsync(int startOffset, int endOffset);
    Task<int> GetCharacterCountAsync();
}

/// <summary>
/// Every D-Bus daemon (the session bus, and AT-SPI's own dedicated bus)
/// implements this on its bus driver name - the reliable way to resolve an
/// AT-SPI application's real OS pid, since AT-SPI's own
/// <c>Application.Id</c> property is an internal registration counter, not
/// a pid (confirmed live: Id=13 for a process whose real pid was 8464).
/// </summary>
[DBusInterface("org.freedesktop.DBus")]
public interface IDBusDriver : IDBusObject
{
    Task<uint> GetConnectionUnixProcessIDAsync(string busName);
}
