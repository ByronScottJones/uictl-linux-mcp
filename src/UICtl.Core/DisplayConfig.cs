using Tmds.DBus;
using UICtl.Core.Interop;

namespace UICtl.Core;

/// <summary>
/// `displays.list` - org.gnome.Mutter.DisplayConfig over D-Bus, one code
/// path for both X11 and Wayland GNOME sessions (see IDisplayConfig's doc
/// comment for why no backend split is needed here, unlike activate/
/// focus.*). Live-verified against this machine's real session bus.
/// </summary>
public static class DisplayConfig
{
    private const string ServiceName = "org.gnome.Mutter.DisplayConfig";
    private static readonly ObjectPath ServicePath = "/org/gnome/Mutter/DisplayConfig";

    private static readonly Lazy<Connection> LazyConnection = new(() => AsyncBridge.RunSync(ConnectAsync));

    private static async Task<Connection> ConnectAsync()
    {
        if (Address.Session is null)
            throw new UiCtlException("no D-Bus session address available ($DBUS_SESSION_BUS_ADDRESS unset?) - cannot reach Mutter's DisplayConfig service");
        var connection = new Connection(Address.Session);
        await connection.ConnectAsync();
        return connection;
    }

    private static IDisplayConfig Proxy =>
        LazyConnection.Value.CreateProxy<IDisplayConfig>(ServiceName, ServicePath);

    public static IReadOnlyList<DisplayInfo> List() => AsyncBridge.RunSync(async () =>
    {
        var state = await Proxy.GetCurrentStateAsync();

        // Physical monitors keyed by connector name (the first spec tuple
        // element - "Virtual-1" etc.) so a logical monitor's own
        // "monitors" list (which references physical monitors by the same
        // (connector,vendor,product,serial) tuple, not by index) can look
        // up the current mode's pixel dimensions.
        var byConnector = state.monitors.ToDictionary(m => m.spec.connector);

        var result = new List<DisplayInfo>();
        int index = 0;
        foreach (var lm in state.logicalMonitors)
        {
            string connector = lm.monitors.Length > 0 ? lm.monitors[0].Item1 : "";
            if (!byConnector.TryGetValue(connector, out var physical))
            {
                index++;
                continue;
            }

            int currentModeWidth = 0, currentModeHeight = 0;
            foreach (var mode in physical.modes)
            {
                bool isCurrent = mode.properties.TryGetValue("is-current", out var v) && v is true;
                if (isCurrent || (currentModeWidth == 0 && currentModeHeight == 0))
                {
                    currentModeWidth = mode.width;
                    currentModeHeight = mode.height;
                }
                if (isCurrent) break;
            }

            double width = currentModeWidth / lm.scale;
            double height = currentModeHeight / lm.scale;

            result.Add(new DisplayInfo(
                Index: index,
                DisplayId: StableConnectorId(connector),
                Frame: new Frame(lm.x, lm.y, width, height),
                IsMain: lm.isPrimary,
                Scale: lm.scale));
            index++;
        }
        return (IReadOnlyList<DisplayInfo>)result;
    });

    /// <summary>
    /// Mutter's D-Bus API identifies monitors by connector name string
    /// (e.g. "Virtual-1", "HDMI-1"), not a numeric id the way X11 RandR's
    /// RROutput XID is - derive a stable opaque long from it (FNV-1a) so
    /// DisplayInfo.DisplayId still fits MCP_INTERFACE.md's "opaque
    /// per-backend id" contract without inventing a second, X11-only code
    /// path just to get a real XID (see ENGINEERING.md).
    /// </summary>
    private static long StableConnectorId(string connector)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(connector))
        {
            hash ^= b;
            hash *= prime;
        }
        return unchecked((long)hash);
    }
}
