using Tmds.DBus;

namespace UICtl.Core.Interop;

/// <summary>
/// Tmds.DBus proxy for org.gnome.Mutter.DisplayConfig - exposed by Mutter
/// itself (the compositor/WM in both X11 and Wayland GNOME sessions, same
/// process either way) for gnome-control-center's display panel, so unlike
/// activate/focus.* this needs no companion Shell extension and no
/// X11-vs-Wayland backend split - one code path for both session types
/// (see DisplayConfig.cs). Signature hand-verified against the real
/// service on this machine via `gdbus introspect --session --dest
/// org.gnome.Mutter.DisplayConfig --object-path
/// /org/gnome/Mutter/DisplayConfig` and a throwaway Tmds.DBus test
/// program (a{sv} marshals to IDictionary&lt;string, object&gt; with
/// values already unboxed to their native CLR type - confirmed live,
/// e.g. a mode's "is-current" comes back as a real `bool`, not a boxed
/// Variant wrapper). Must be public - Tmds.DBus's dynamic proxy assembly
/// cannot implement an internal interface (see UictlWindowManagerInterface.cs).
/// </summary>
[DBusInterface("org.gnome.Mutter.DisplayConfig")]
public interface IDisplayConfig : IDBusObject
{
    /// <returns>
    /// (serial, monitors, logicalMonitors, properties).
    /// monitors: ((connector, vendor, product, serial) spec, modes, properties) -
    /// modes: (modeId, width, height, refreshRate, preferredScale, supportedScales, properties),
    /// where a mode's properties carries "is-current"/"is-preferred" (bool).
    /// logicalMonitors: (x, y, scale, transform, isPrimary, monitors, properties) -
    /// x/y/scale are Mutter's own global "logical" coordinate space, the
    /// same space Meta.Window/get_frame_rect() places windows in (see
    /// ENGINEERING.md's "Coordinate spaces" section) - width/height aren't
    /// given directly, derive them from the matching physical monitor's
    /// current mode (width/height) divided by this scale.
    /// </returns>
    Task<(uint serial,
          ((string connector, string vendor, string product, string serial) spec,
           (string modeId, int width, int height, double refreshRate, double preferredScale, double[] supportedScales, IDictionary<string, object> properties)[] modes,
           IDictionary<string, object> properties)[] monitors,
          (int x, int y, double scale, uint transform, bool isPrimary, (string, string, string, string)[] monitors, IDictionary<string, object> properties)[] logicalMonitors,
          IDictionary<string, object> properties)> GetCurrentStateAsync();
}
