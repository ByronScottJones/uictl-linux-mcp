# Engineering notes

This mirrors the structure of the macOS
[`uictl-mcp`](https://github.com/ByronJones-Elsevier/uictl-mac-mcp) and
[`uictl-win-mcp`](https://github.com/ByronJones-Elsevier/uictl-win-mcp)
`ENGINEERING.md` files so all three implementations stay easy to compare.
Linux/GNOME is not a third coat of paint over the same primitives — several
things here are structurally different from both sibling platforms, and
that's called out explicitly rather than glossed over.

## Planned process architecture

```
CLI subcommand ──┐
                  ├──▶ DaemonClient (Unix socket, ~/.uictl/uictl.sock) ──▶ DaemonServer ──▶ CommandDispatcher ──▶ Core/*
MCP tool call  ───┘
```

Same shape as macOS, and simpler than Windows here specifically: Linux has
real Unix domain sockets, so no named-pipe translation layer is needed.

- **CLI** (`src/UICtl.Cli`, `System.CommandLine`): parses flags, builds a
  `{"command": ..., "params": ...}` JSON dict, sends it via `DaemonClient`,
  prints the JSON response, exits 0/1 based on the response's `ok` field.
- **MCP server** (`src/UICtl.Mcp`, `uictl mcp`, official `ModelContextProtocol`
  C# SDK — pure managed code, not Windows-specific, used as-is here): same
  thing, one layer up. Tool definitions here must match `MCP_INTERFACE.md`
  exactly.
- **DaemonClient/DaemonServer** (`src/UICtl.Ipc`): a length-prefixed JSON
  protocol over a Unix domain socket, one request/response per connection —
  ported from `uictl-win-mcp`'s `UICtl.Ipc` largely unchanged, transport
  swapped from named pipe to `UnixDomainSocketEndPoint`.
- **CommandDispatcher** (`src/UICtl.Ipc`): the one switch/pattern-match every
  request goes through. Add a new capability by adding a case here plus a CLI
  subcommand and/or MCP tool that calls it.
- **Core** (`src/UICtl.Core`): the actual Linux/GNOME calls — AT-SPI2 over
  D-Bus (accessibility tree, click/type), Xlib/EWMH (X11 windows/screenshot),
  the companion GNOME Shell extension over D-Bus (Wayland windows/focus),
  Mutter's `DisplayConfig` D-Bus interface (displays), `/dev/uinput` (input
  synthesis), Tesseract (OCR), `wl-copy`/`wl-paste`/`xclip` (clipboard).
- **GUI** (`src/UICtl.Gui`): GTK4 + libadwaita via **Gir.Core** (maintained
  C# GObject-introspection bindings) for the toast and the `uictl log show`
  activity table — the most visibly "native GNOME" surface of the tool,
  replacing WPF/AppKit.

## Why a daemon at all

Same rationale as macOS/Windows: state that needs to outlive one CLI
invocation has to live somewhere — the element-id cache
(`elements`/`screenshot --annotate` hand out synthetic ids a later
`click --element <id>` resolves back to a live AT-SPI accessible), any
reused portal/ScreenCast session for Wayland screenshots, and the D-Bus
connections to AT-SPI's registry and the Shell extension.

## Core subsystem design

| Concern | Design |
|---|---|
| Accessibility (elements/click/type) | **AT-SPI2 over D-Bus** (`org.a11y.Bus` registry; `Accessible`/`Component`/`Action`/`Text`/`EditableText` interfaces) via `Tmds.DBus`. Works identically under X11 and Wayland — build this first, it's most of the value. Confirmed live against a real GNOME desktop: `Component.GetExtents` returns real screen-coordinate geometry for a Wayland-native app's top-level window (`gnome-text-editor`'s Frame accessible: role `window`, `Name` = title, `GetExtents(0)` = real `(x,y,w,h)`) — this is enough to build most of `windows.list` (title/frame/role) and all of `elements`/`click`/`type` **without X11 or the Shell extension**, for any app that registers with AT-SPI. `Component.GrabFocus` looked like a possible general activation mechanism but returns `NotSupported` in practice (tested against the same Frame) — window *activation* still needs X11 or the Shell extension, see below. `apps.list`'s pid can be cross-referenced against AT-SPI's `Accessible.GetApplication`. |
| apps.list | `/proc/[pid]/comm` + `/proc/[pid]/cmdline` enumeration — no permission needed, same on X11/Wayland. |
| windows.list | Primarily AT-SPI (see above) for any app that registers — title, frame, role. Fall back to the `IWindowBackend` split below only to catch windows AT-SPI doesn't know about (an app that doesn't implement the AT-SPI/ATK bridge) or to fill in `displayId`/pid-to-window correlation gaps. |
| activate / focus | AT-SPI has no general activation primitive (`GrabFocus` unsupported in practice) — this is the one area still fully gated on `IWindowBackend`, chosen at runtime by session-type detection. Note: this environment's `$XDG_SESSION_TYPE` was observed **unset** even with `$WAYLAND_DISPLAY` and `$DISPLAY` both present (both WSLg and the real GNOME VM) — detect by checking `$WAYLAND_DISPLAY` first, `$DISPLAY` second, don't rely on `$XDG_SESSION_TYPE` alone. **X11 backend**: direct Xlib P/Invoke against EWMH (`_NET_ACTIVE_WINDOW`, plus `_NET_WM_PID`/`_NET_WM_NAME` where available) — same "raw P/Invoke to the native platform lib" pattern as `NativeMethods.cs` on Windows; note EWMH support can be partial (WSLg's XWayland advertises no `_NET_CLIENT_LIST` at all in `_NET_SUPPORTED`), and **most modern GTK apps run as native Wayland clients even on a real GNOME/Xorg-available desktop** (confirmed: default-launched `gnome-text-editor` is invisible to `xwininfo -root -tree` entirely, X11-only helper windows aside) — so treat the X11 backend as covering genuinely X11-native/forced (`GDK_BACKEND=x11`) apps and Xorg sessions specifically, not the common case. **Wayland/GNOME backend**: D-Bus calls to the companion Shell extension (`gnome-extension/`), since Mutter deliberately exposes no such API to arbitrary outside processes — this is the backend that actually matters for activating a typical modern GNOME app. |
| displays.list | X11: RandR extension. Wayland/GNOME: `org.gnome.Mutter.DisplayConfig` D-Bus interface — exposed by Mutter itself for `gnome-control-center`, **no custom extension needed** for this one. |
| screenshot | X11: `XGetImage`/`XShmGetImage`, no permission prompt. Wayland: `org.freedesktop.portal.Screenshot`, or `ScreenCast` + PipeWire for repeat/window-scoped capture — negotiate the portal session once per daemon lifetime and reuse it rather than popping a dialog every call, the closest Linux analog to macOS's one-time Screen Recording grant. |
| input synthesis | **Implemented** (Phase 2): `/dev/uinput` virtual keyboard+mouse device via P/Invoke `ioctl`/`write` — kernel-level, identical under X11 and Wayland (no `IWindowBackend` split needed, unlike windowing), no per-call dialog. One long-lived absolute-pointer+keyboard device, created once and cached for the daemon's lifetime (`UinputDevice.cs`), ranged to the real X11/XWayland screen pixel size (`Interop/XlibScreenInterop.cs`) rather than a normalized virtual range — see "Coordinate spaces" below. Needs one-time device-permission setup: group membership alone is **not** sufficient on a stock Ubuntu 26.04 install (confirmed live: `/dev/uinput` ships `root:root` mode `0600` with no group grant at all) — a udev rule is also required, see README's Input setup section. Text/key synthesis is US-QWERTY/ASCII-only (`KeyCodes.cs` — `uinput` codes are physical-key codes, not Unicode input); an unsupported character throws rather than silently dropping. XTest remains a documented, not-yet-built zero-setup alternative for a confirmed-X11 session. |
| OCR | **Tesseract** via the `Tesseract` NuGet (libtesseract P/Invoke wrapper), fully local, no network. Reports real per-word confidence — unlike Windows' `null`, closer to macOS's Vision output. |
| pixel | X11: 1×1 `XGetImage`. Wayland: sampled from the same screenshot/screencast frame — no cheap standalone primitive exists here, so this is genuinely more expensive than macOS/Windows on Wayland. |
| clipboard | Shell out to `wl-copy`/`wl-paste` (Wayland) or `xclip` (X11), selected by the same session-type detection as windows. Native-protocol implementation is a possible follow-up if shelling out proves fragile. |
| permissions.status | **Implemented** (Phase 2, `Permissions.cs`): `{"sessionType": "x11"|"wayland", "inputMethod": "uinput"|"xtest", "uinputWritable": bool, "atspiEnabled": bool, "shellExtensionConnected": bool \| null, "interactive": bool}`. `inputMethod` always reports `"uinput"` for now (XTest isn't built, so there's nothing to choose between yet). `shellExtensionConnected` is always `false` on Wayland until the companion extension exists (Phase 3). |

## Coordinate spaces

X11 gives a single global "virtual screen" pixel space (origin top-left of
the bounding box of all monitors, y-down, physical pixels — negative
coordinates possible for a monitor above/left of the primary, same idea as
Windows' virtual screen space) with no separate scale-factor conversion
needed for that backend.

Wayland is a genuine open question this project has to resolve empirically,
not assume: Mutter tracks a global "logical" (post-fractional-scale)
coordinate space internally for its own multi-monitor layout, but a Wayland
client is normally only ever told its own surface-local coordinates — there
is no standard protocol exposing the compositor's global space to an
arbitrary outside process. The companion Shell extension is the thing that
will have to expose this (it runs inside Mutter and can read
`global.display`/monitor layout directly), which means the Wayland
backend's coordinate space is defined by whatever the extension reports —
document the exact space (logical vs. physical, scale factor handling) in
this file once Phase 3 nails it down, the same way macOS's three-space
writeup and Windows' per-monitor-DPI writeup did.

**Input synthesis's pointer range (Phase 2) — live-verified, with a real caveat found along the way:**
`UinputDevice` ranges its `ABS_X`/`ABS_Y` axes to the real X11/XWayland
screen pixel size (`XlibScreenInterop.GetScreenSize`, i.e.
`XDisplayWidth`/`XDisplayHeight` against the default screen — works over
XWayland in a Wayland session, same as any other X11 client) rather than a
normalized virtual range (the `0..65535` convention some uinput tools use).

Confirmed live against `gnome-text-editor` (Wayland-native): clicking at the
window's own top-level frame center, then synthesizing keystrokes, correctly
changed the window title (which the app derives from buffer content) -
proof both that the window-level frame really is real screen-coordinate
geometry, and that the uinput device correctly lands clicks/keystrokes in
the right place. Getting there needed one real fix:
**`UI_SET_PROPBIT`/`INPUT_PROP_POINTER` must be set on the virtual device**
(`Interop/UinputInterop.cs`) - without it, libinput has no signal that an
`EV_ABS` device with buttons is a pointer rather than e.g. a graphics
tablet, and every write/ioctl succeeds with no error while nothing actually
happens on screen. Not documented anywhere obvious in the uinput man page;
found by elimination during live testing.

**Untested: multi-monitor and fractional (HiDPI) scaling.** This dev
machine is single-monitor (`Virtual-1`, 1518x986 via `xrandr`), so
`XDisplayWidth`/`XDisplayHeight`'s single "default screen" reading is
unambiguous here. On a real multi-monitor setup this needs verification,
not assumption: `XDisplayWidth`/`XDisplayHeight` describe the X11/XWayland
**virtual screen** (the bounding box of all monitors, per the "Coordinate
spaces" section above), so a target coordinate should in principle still
land on the right monitor - but whether libinput actually maps a single
absolute-pointer *uinput* device's full range across multiple physical
outputs the same way (rather than pinning it to one output, the common
behavior for tablet-classified absolute devices) is unverified. Fractional
scaling is a separate open question in the same vein - not addressed here,
flagged by PR #2 review, revisit when a multi-monitor/HiDPI machine is
available to test against.

**Separately, a real and *not yet fixed* limitation surfaced along the
way**: AT-SPI `Component.GetExtents(coordType=screen)` returns a real,
correct frame for a window's own top-level accessible, but returns a
**degenerate `(0,0)` origin for every nested child element**, regardless of
that element's actual on-screen position - confirmed on both
`gnome-calculator` and `gnome-text-editor` (every element at every depth,
including sibling buttons laid out side-by-side in a grid, reported
identical `x:0,y:0`; only width/height varied meaningfully with nesting
depth). This means `click --element`/the `type --element` fallback's
click-to-focus step only reliably lands on large, top-anchored elements
(their computed center still falls in a sensible spot, e.g.
`gnome-text-editor`'s main text box) - **not** on precisely-positioned small
widgets like individual buttons in a grid (confirmed: clicking
`gnome-calculator`'s "7" button via `--element` computed target `(32,22)`,
nowhere near the actual button). This is a Phase 1 `Accessibility.cs`/AT-SPI
finding, not an input-synthesis bug - `click --at` with an
independently-known-correct coordinate is unaffected. Root cause not yet
investigated (candidates: GTK4's AT-SPI bridge not propagating
window-position + widget-allocation offsets for Wayland-native surfaces;
worth revisiting once screenshot capability (Phase 4) makes visual
comparison easy, or when the Shell extension (Phase 3) can provide an
independent geometry source to cross-check against). See AGENTS.md's
matching gotcha.

## AT-SPI2 element correlation

Unlike macOS (no public `CGWindowID → AXUIElement` API, matched by
title+frame) or Windows (`AutomationElement.FromHandle(HWND)` direct), the
X11 case here should also be close to direct: an AT-SPI `Accessible`'s
`Application` interface can usually be matched to a specific X11 window via
the app's PID (from `_NET_WM_PID`) plus AT-SPI's own registry of
applications by PID — verify this holds in practice during Phase 1 rather
than assuming, and document a fallback heuristic here if it doesn't (mirror
macOS's Calculator-exception writeup as the template for how to record it).

## GNOME Shell extension

A small GJS extension (`gnome-extension/`), its own D-Bus name (e.g.
`org.uictl.WindowManager` on the session bus), exposing `ListWindows()`,
`GetWindowGeometry(id)`, `ActivateWindow(id)`, `GetFocusedWindow()` —
wrapping Mutter's internal `global.display`/`Meta.Window` APIs the way
community extensions like "Window Calls" do. Versioned/tested against
whatever GNOME release ships with Ubuntu 26.04 specifically — a Shell
major-version bump breaking it is a known, accepted fragility, not something
this project tries to abstract away (same spirit as `uictl-win-mcp` being
scoped to its dev machine's exact OS build). Requires `gnome-extensions
install`/`enable` plus a Wayland re-login (Shell can't restart live under
Wayland the way it could under X11) — documented as a one-time setup step in
the README, the Linux analog of granting Accessibility/Screen Recording on
macOS.

## Adding a new capability

1. Implement the actual Linux/GNOME call in `src/UICtl.Core`.
2. Add a case to `CommandDispatcher.Dispatch`.
3. Add a CLI subcommand (`src/UICtl.Cli`) that builds the params dict and
   calls `DaemonClient.Send`.
4. Add a matching tool definition in `src/UICtl.Mcp` if it should also be
   MCP-callable — **and update `MCP_INTERFACE.md` in the same change**,
   since that file is the contract the macOS and Windows implementations are
   expected to match too (or to explicitly note this platform's divergence
   against).
5. Rebuild (`dotnet build`), restart the daemon (`uictl daemon stop`; it
   auto-restarts on the next command) so it picks up the new binary.

## Project layout

```
uictl-linux-mcp/
  src/
    UICtl.Cli/        # entry point; System.CommandLine subcommands
    UICtl.Core/        # AT-SPI2 / Xlib / GNOME Shell extension client / uinput / Tesseract / clipboard
    UICtl.Ipc/         # DaemonClient, DaemonServer, CommandDispatcher, Unix-socket protocol
    UICtl.Mcp/         # MCP server (ModelContextProtocol SDK), tool definitions
    UICtl.Gui/         # GTK4/libadwaita toast + activity log window (Gir.Core)
  tests/
    UICtl.Core.Tests/  # xunit, referencing UICtl.Core
  gnome-extension/     # companion GNOME Shell extension (GJS), own install/versioning
  uictl.slnx
```

Project references: `UICtl.Ipc` → `UICtl.Core`; `UICtl.Mcp` → `UICtl.Ipc`;
`UICtl.Gui` → `UICtl.Core`; `UICtl.Cli` → all of the above;
`UICtl.Core.Tests` → `UICtl.Core`. `UICtl.Cli`'s `AssemblyName` is set to
`uictl` so the built binary is `uictl`, not `UICtl.Cli`.

## Development filesystem note

This repo's canonical working copy lives in WSL's **native** filesystem
(`~/dev/uictl-linux-mcp` inside Ubuntu 26.04, i.e. `ext4`), not under
`/mnt/c/...`. Building this solution from WSL against a Windows-mounted
DrvFs path fails intermittently with `MSB3374` ("last access/last write
time... cannot be set") — a known DrvFs/9p timestamp-handling reliability
issue, confirmed empirically while scaffolding this repo (the exact same
solution built cleanly once moved to native `ext4`). Edit files from Windows
tooling via the UNC path
(`\\wsl.localhost\Ubuntu-26.04\home\<user>\dev\uictl-linux-mcp`) if needed;
always `dotnet build`/`run`/`test` from inside WSL against the native path,
never `/mnt/c/...`.

## Build plan / phases

See the plan this repo was scaffolded from for the full phase breakdown
(environment verification, **AT-SPI + X11 core (done, Phase 1)**, **input
synthesis (done, Phase 2 — click/move/scroll/key/type, `permissions.status`)**,
the GNOME Shell extension + Wayland window management, Wayland
screenshot/OCR/displays, feedback + activity log GUI, tests/docs/contract
sync). Phases are sequenced
so as much as possible lands and gets live-tested inside a WSL Ubuntu 26.04
session before anything requires a real GNOME/Mutter desktop — window
enumeration/activation on Wayland, the Shell extension, portal consent
dialogs, and `Mutter.DisplayConfig` cannot be meaningfully exercised in
WSLg (it runs a lightweight `weston` compositor, not `gnome-shell`/Mutter)
and need a real GNOME desktop session (a VM is sufficient).

**Live-test every phase against a real running GTK app, not just a clean
build** — this is the standing lesson from `uictl-win-mcp`, where code
"scaffolded and cross-compiled but never run" hid a real bug until it was
finally executed.
