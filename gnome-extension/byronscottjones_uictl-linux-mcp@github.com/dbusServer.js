/* dbusServer.js
 *
 * Exports org.byronscottjones.uictl.WindowManager on the session bus - the primitive
 * uictl's C# WaylandWindowBackend needs for `activate`/`focus.*`, since
 * Mutter gives an outside process no other way to list/raise windows on
 * Wayland (see this repo's ENGINEERING.md "Wayland/GNOME backend" notes).
 * Modeled directly on the Gio.DBusExportedObject.wrapJSObject + own_name
 * pattern used by the snapd-prompting@canonical.com extension shipped with
 * this same GNOME Shell version - see /usr/share/gnome-shell/extensions/
 * snapd-prompting@canonical.com/dbusServer.js on this dev machine.
 *
 * Deliberately synchronous handlers (no "Async" suffix): every call here
 * is an immediate, in-process Mutter/GObject query - wrapJSObject supports
 * returning a plain value directly (marshaled automatically) or throwing
 * (converted to a D-Bus error) for exactly this case, no need for the
 * manual invocation.return_value() dance snapd-prompting's async PromptAsync
 * uses (that extension needs it because it waits on a signal elsewhere).
 */
import Gio from 'gi://Gio';
import GObject from 'gi://GObject';
import Meta from 'gi://Meta';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

const WindowManagerInterface = 'org.byronscottjones.uictl.WindowManager';
const WindowManagerObjectPath = '/org/byronscottjones/uictl/WindowManager';

const WINDOW_MANAGER_INTERFACE = `
<node>
    <interface name="${WindowManagerInterface}">
        <method name="ListWindows">
            <arg type="a(uisiiii)" direction="out" name="windows"/>
        </method>
        <method name="ActivateWindow">
            <arg type="u" direction="in" name="stableSeq"/>
            <arg type="b" direction="out" name="success"/>
        </method>
        <method name="GetFocusedWindow">
            <arg type="b" direction="out" name="found"/>
            <arg type="u" direction="out" name="stableSeq"/>
        </method>
    </interface>
</node>
`;

/** Same filter real GNOME Shell dock/tiling extensions use to skip menus/tooltips/the desktop window - see ubuntu-dock@ubuntu.com/theming.js and tiling-assistant@ubuntu.com/extension.js on this machine. */
function isRelevantWindow(win) {
    return win.get_window_type() !== Meta.WindowType.DESKTOP && !win.is_skip_taskbar();
}

function allRelevantWindows() {
    return global.display.list_all_windows().filter(isRelevantWindow);
}

export const DBusServer = GObject.registerClass(
class DBusServer extends GObject.Object {
    constructor() {
        super();

        this._dbusObject = Gio.DBusExportedObject.wrapJSObject(WINDOW_MANAGER_INTERFACE, this);
        try {
            this._dbusObject.export(Gio.DBus.session, WindowManagerObjectPath);
        } catch (e) {
            logError(e, `Failed to export ${WindowManagerObjectPath}`);
        }

        this._ownName = Gio.DBus.session.own_name(WindowManagerInterface,
            Gio.BusNameOwnerFlags.NONE, null, () => this._lostName());
    }

    destroy() {
        if (this._ownName)
            Gio.DBus.session.unown_name(this._ownName);

        try {
            this._dbusObject.unexport();
        } catch (e) {
            logError(e, `Failed to unexport ${WindowManagerObjectPath}`);
        }

        this._dbusObject.run_dispose();
        delete this._dbusObject;
    }

    _lostName(name) {
        console.error(`Name "${name}" lost`);
        delete this._ownName;
    }

    ListWindows() {
        return allRelevantWindows().map(win => {
            const rect = win.get_frame_rect();
            return [
                win.get_stable_sequence(),
                win.get_pid(),
                win.get_title() ?? '',
                rect.x, rect.y, rect.width, rect.height,
            ];
        });
    }

    ActivateWindow(stableSeq) {
        const win = allRelevantWindows().find(w => w.get_stable_sequence() === stableSeq);
        if (!win)
            return false;
        Main.activateWindow(win);
        return true;
    }

    GetFocusedWindow() {
        const win = global.display.get_focus_window();
        if (!win)
            return [false, 0];
        return [true, win.get_stable_sequence()];
    }
});
