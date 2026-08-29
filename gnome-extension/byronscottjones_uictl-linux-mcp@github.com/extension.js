/* extension.js
 *
 * No UI, no preferences - this extension exists solely to export
 * org.byronscottjones.uictl.WindowManager on the session bus for uictl's Wayland window
 * backend. See dbusServer.js and this repo's ENGINEERING.md.
 */
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import {DBusServer} from './dbusServer.js';

export default class UictlWindowManagerExtension extends Extension {
    enable() {
        this._dbusServer = new DBusServer();
    }

    disable() {
        this._dbusServer?.destroy();
        this._dbusServer = null;
    }
}
