# uictl Window Manager (companion GNOME Shell extension)

Exposes `org.byronscottjones.uictl.WindowManager` on the session D-Bus so `uictl`'s
`activate`/`focus.*` commands can list and raise windows on Wayland, where
Mutter deliberately gives an outside process no other way to do this (see
the root `ENGINEERING.md`'s "GNOME Shell extension" section). No UI, no
preferences — it exists purely to run inside the Shell process and answer
three D-Bus calls: `ListWindows`, `ActivateWindow`, `GetFocusedWindow`.

Not needed on an X11 session — `uictl activate` uses direct Xlib/EWMH there
instead (see `src/UICtl.Core/X11WindowBackend.cs`). `uictl permissions`'
`shellExtensionConnected` field is `null` on X11 for this reason.

## Install

```sh
cd gnome-extension/byronscottjones_uictl-linux-mcp@github.com
gnome-extensions pack --force --extra-source=dbusServer.js --out-dir=/tmp
gnome-extensions install --force /tmp/byronscottjones_uictl-linux-mcp@github.com.shell-extension.zip
```

The `--extra-source=dbusServer.js` flag is required — confirmed live during
development: `gnome-extensions pack` only includes `metadata.json` and
`extension.js` by default, silently dropping any other `.js` file unless
it's named explicitly. Forgetting this flag produces an extension that
installs and "enables" without error but does nothing (the actual D-Bus
server code is just missing from the package).

## Enable — **a logout/login (or reboot) is required first**

Confirmed live on this project's dev machine (GNOME Shell 50, Wayland):
installing a **brand-new** extension does not make the running Shell
process aware of it — `gnome-extensions list`/`info` report nothing for it
even immediately after a successful `install`, and the Shell's own
`org.gnome.Shell.Extensions.ReloadExtension` D-Bus method exists in the
introspected interface but returns `UnknownMethod` when actually called
(not implemented on this build). A full Shell restart is the only way
observed to work — under Wayland that means logging out and back in (or
rebooting), since Shell can't restart in place the way `Alt+F2, r` does on
X11.

**This is a one-time cost per machine**, not per `uictl` invocation or per
extension update — see below.

```sh
# after logging back in:
gnome-extensions enable byronscottjones_uictl-linux-mcp@github.com
uictl permissions   # shellExtensionConnected should now be true
```

## Updating the extension after a code change

Re-run the `pack`/`install` commands above, **then fully restart Shell**
(log out/in, or reboot) — the same one-time cost as a first-ever enable,
paid again on every code change, not just the first.

An earlier version of this doc claimed `disable`/`enable` alone was enough.
Confirmed live on this project's dev machine (GNOME Shell 50, Wayland) that
this is **wrong** for anything beyond `extension.js` itself: a
`disable`/`enable` cycle done specifically to pick up a bugfix in
`dbusServer.js` (a file loaded via `--extra-source`, not `extension.js`
proper) did not take — the old, buggy code kept running until a full Shell
restart. Treat any change to a non-`extension.js` source file as requiring
a restart; only `extension.js` itself may reload live via toggle, and that
hasn't been separately re-verified since.

```sh
gnome-extensions disable byronscottjones_uictl-linux-mcp@github.com
gnome-extensions install --force /tmp/byronscottjones_uictl-linux-mcp@github.com.shell-extension.zip
# log out/in (or reboot), then:
gnome-extensions enable byronscottjones_uictl-linux-mcp@github.com
```

## Debugging

`console.log`/`console.error`/`logError` calls in this extension's code go
to the systemd journal:

```sh
journalctl --user -f -o cat /usr/bin/gnome-shell
```

`gnome-extensions info byronscottjones_uictl-linux-mcp@github.com` (once discovered)
reports its enabled/error state; `GetExtensionErrors` over
`org.gnome.Shell.Extensions` on the session bus gives any exception
GNOME Shell caught while loading it.
