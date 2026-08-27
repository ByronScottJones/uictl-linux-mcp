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
| Accessibility (elements/click/type) | **AT-SPI2 over D-Bus** (`org.a11y.Bus` registry; `Accessible`/`Component`/`Action`/`Text`/`EditableText` interfaces) via `Tmds.DBus`. Works identically under X11 and Wayland — the one big low-risk win on this platform, build it first. |
| apps.list | `/proc/[pid]/comm` + `/proc/[pid]/cmdline` enumeration — no permission needed, same on X11/Wayland. |
| windows.list / activate / focus | `IWindowBackend`, chosen at runtime by session-type detection. Note: this environment's `$XDG_SESSION_TYPE` was observed **unset** even with `$WAYLAND_DISPLAY` and `$DISPLAY` both present (WSLg) — detect by checking `$WAYLAND_DISPLAY` first, `$DISPLAY` second, don't rely on `$XDG_SESSION_TYPE` alone. **X11 backend**: direct Xlib P/Invoke against EWMH (`_NET_CLIENT_LIST`, `_NET_ACTIVE_WINDOW`, `_NET_WM_PID`, `_NET_WM_NAME`) — same "raw P/Invoke to the native platform lib" pattern as `NativeMethods.cs` on Windows. **Wayland/GNOME backend**: D-Bus calls to the companion Shell extension (`gnome-extension/`), since Mutter deliberately exposes no such API to arbitrary outside processes. |
| displays.list | X11: RandR extension. Wayland/GNOME: `org.gnome.Mutter.DisplayConfig` D-Bus interface — exposed by Mutter itself for `gnome-control-center`, **no custom extension needed** for this one. |
| screenshot | X11: `XGetImage`/`XShmGetImage`, no permission prompt. Wayland: `org.freedesktop.portal.Screenshot`, or `ScreenCast` + PipeWire for repeat/window-scoped capture — negotiate the portal session once per daemon lifetime and reuse it rather than popping a dialog every call, the closest Linux analog to macOS's one-time Screen Recording grant. |
| input synthesis | Primary: `/dev/uinput` virtual keyboard+mouse device via P/Invoke `ioctl`/`write` — kernel-level, identical under X11 and Wayland, no per-call dialog, needs one-time device-permission setup (see README). XTest is available as a zero-setup alternative when the session is confirmed X11. |
| OCR | **Tesseract** via the `Tesseract` NuGet (libtesseract P/Invoke wrapper), fully local, no network. Reports real per-word confidence — unlike Windows' `null`, closer to macOS's Vision output. |
| pixel | X11: 1×1 `XGetImage`. Wayland: sampled from the same screenshot/screencast frame — no cheap standalone primitive exists here, so this is genuinely more expensive than macOS/Windows on Wayland. |
| clipboard | Shell out to `wl-copy`/`wl-paste` (Wayland) or `xclip` (X11), selected by the same session-type detection as windows. Native-protocol implementation is a possible follow-up if shelling out proves fragile. |
| permissions.status | New shape (the contract already anticipates per-platform divergence here): `{"sessionType": "x11"|"wayland", "inputMethod": "uinput"|"xtest", "uinputWritable": bool, "atspiEnabled": bool, "shellExtensionConnected": bool \| null, "interactive": bool}`. |

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
  uictl.sln
```

Project references: `UICtl.Ipc` → `UICtl.Core`; `UICtl.Mcp` → `UICtl.Ipc`;
`UICtl.Gui` → `UICtl.Core`; `UICtl.Cli` → all of the above;
`UICtl.Core.Tests` → `UICtl.Core`. `UICtl.Cli`'s `AssemblyName` is set to
`uictl` so the built binary is `uictl`, not `UICtl.Cli`.

## Build plan / phases

See the plan this repo was scaffolded from for the full phase breakdown
(environment verification, AT-SPI + X11 core, input synthesis, the GNOME
Shell extension + Wayland window management, Wayland screenshot/OCR/displays,
feedback + activity log GUI, tests/docs/contract sync). Phases are sequenced
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
