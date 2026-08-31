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
| activate / focus | **Implemented** (Phase 3: `IWindowBackend.cs`/`WindowActivation.cs`/`FocusHoldStore.cs`). AT-SPI has no general activation primitive (`GrabFocus` unsupported in practice) — this is the one area gated on `IWindowBackend`, chosen at runtime by `SessionDetection` (`$WAYLAND_DISPLAY` first, `$DISPLAY` second — `$XDG_SESSION_TYPE` observed unset in this environment, don't rely on it alone). **X11 backend** (`X11WindowBackend.cs`): direct Xlib P/Invoke against EWMH (`_NET_CLIENT_LIST`/`_NET_WM_PID`/`_NET_WM_NAME`/`_NET_ACTIVE_WINDOW`) — live-verified against a `GDK_BACKEND=x11`-forced app on this Wayland machine's XWayland (the best available approximation without a genuine X11 session; a daemon spawned with `$WAYLAND_DISPLAY` unset correctly falls back to this backend). One real Xlib gotcha, easy to get wrong: `XGetWindowProperty` returns a format-32 property (Window/CARDINAL values) as an array of native `long` (8 bytes on x86_64), not 4-byte `int32`, regardless of the logical 32-bit value it holds. **Wayland/GNOME backend** (`WaylandWindowBackend.cs`): D-Bus calls to the companion Shell extension (`gnome-extension/`, see below) — the backend that matters for a typical modern GNOME app, since most GTK apps run as native Wayland clients even on a real GNOME/Xorg-available desktop (confirmed in Phase 1: default-launched `gnome-text-editor` invisible to X11 entirely). `windows.list` stays 100% AT-SPI-based, unchanged — `IWindowBackend` is used *only* for activation, correlated to AT-SPI's own windowId scheme by pid (see `MCP_INTERFACE.md`'s "Window id" section for why these are deliberately separate id spaces). |
| displays.list | **Implemented** (Phase 4: `DisplayConfig.cs`). Not X11-RandR-vs-Wayland-split as originally planned below — `org.gnome.Mutter.DisplayConfig` turned out to answer for **both** session types (Mutter is the compositor/WM either way, exposing this for `gnome-control-center`'s own display panel regardless of X11/Wayland), so one D-Bus code path covers both, no XRandR P/Invoke needed. `logicalMonitors`' x/y/scale is Mutter's own global "logical" coordinate space — width/height aren't given directly, derived from the matching physical monitor's current mode divided by that scale. `displayId` is an opaque hash of Mutter's connector-name string (e.g. "Virtual-1") — Mutter's API has no raw XID-equivalent even on an X11 session, unlike RandR's RROutput. |
| screenshot | **Implemented** (Phase 4: `Screenshot.cs`/`X11ScreenCapture.cs`/`WaylandScreenshotBackend.cs`). X11: `XGetImage` (32bpp TrueColor only — confirmed universal on this class of machine, throws naming the bpp otherwise), no permission prompt, live-verified against a `GDK_BACKEND=x11`-forced window. Wayland: `org.freedesktop.portal.Screenshot` — see "Wayland screenshot" section below for what this actually took to get working and its real, unresolved limitation (a consent dialog on **every** call, not just the first). PNG encode/decode is hand-rolled (`Interop/PngCodec.cs`) rather than a new dependency — see its own doc comment. |
| pixel | **Implemented** (Phase 4: `Pixel.cs`). X11: `XGetImage` of a 1x1 region via the same code screenshot uses. Wayland: samples a full portal screenshot — see MCP_INTERFACE.md, this is genuinely expensive here (full capture + consent dialog per call), not a cheap primitive. |
| input synthesis | **Implemented** (Phase 2): `/dev/uinput` virtual keyboard+mouse device via P/Invoke `ioctl`/`write` — kernel-level, identical under X11 and Wayland (no `IWindowBackend` split needed, unlike windowing), no per-call dialog. One long-lived absolute-pointer+keyboard device, created once and cached for the daemon's lifetime (`UinputDevice.cs`), ranged to the real X11/XWayland screen pixel size (`Interop/XlibScreenInterop.cs`) rather than a normalized virtual range — see "Coordinate spaces" below. Needs one-time device-permission setup: group membership alone is **not** sufficient on a stock Ubuntu 26.04 install (confirmed live: `/dev/uinput` ships `root:root` mode `0600` with no group grant at all) — a udev rule is also required, see README's Input setup section. Text/key synthesis is US-QWERTY/ASCII-only (`KeyCodes.cs` — `uinput` codes are physical-key codes, not Unicode input); an unsupported character throws rather than silently dropping. XTest remains a documented, not-yet-built zero-setup alternative for a confirmed-X11 session. |
| OCR | **Implemented** (Phase 4, `Ocr.cs`/`TesseractEngine.cs`/`Interop/TesseractInterop.cs`). **Tesseract**, via hand-rolled P/Invoke against the system libtesseract (`tesseract-ocr`/`tesseract-ocr-eng`, already in `scripts/preflight.sh`) — not the `Tesseract` NuGet package: that package's managed assembly P/Invokes fixed Windows DLL names ("tesseract50", "leptonica-1.82.0") and ships only Windows-native `.dll` binaries, no Linux `.so` at all, confirmed via `strings` on the assembly — it cannot resolve to this machine's installed native libs on any Linux distro. A `NativeLibrary.SetDllImportResolver` hook tries `libtesseract.so.5` then `.so.4` (older LTS releases' `tesseract-ocr` package resolves to Tesseract 4.x) rather than pinning one SONAME the way `libX11.so.6` is pinned elsewhere in this codebase — only `.so.5` is live-verified (no 4.x machine available). `TessBaseAPISetImage` takes a raw RGBA buffer directly, so this needs zero Leptonica/Pix P/Invoke of its own despite Tesseract linking against liblept internally. Reuses `screenshot`'s exact capture pipeline (`Screenshot.CaptureFrame`/`WholeScreenFrame`) for the window/app/whole-screen/region cases, so it inherits the same X11-instant-vs-Wayland-real-consent-dialog-every-call split — see "Wayland screenshot" below. `region` is always an absolute screen-space rectangle, applied as a further crop on top of whatever window/app/whole-screen was captured. One text block per Tesseract text line (`RIL_TEXTLINE`), frame from `TessPageIteratorBoundingBox` (already the union of that line's word boxes, matching MCP_INTERFACE.md's documented contract), confidence normalized from Tesseract's native 0–100 scale to 0–1 — real per-line confidence, unlike Windows' always-`null`, closer to macOS's Vision output. Fully local, no network. The engine handle is cached for the daemon's lifetime (`TesseractEngine.cs`, same rationale as `UinputDevice.cs`'s cached device) since `TessBaseAPIInit3` loading the trained-data model isn't free — safe with no locking since `DaemonServer.cs` handles one connection at a time. |
| pixel | X11: 1×1 `XGetImage`. Wayland: sampled from the same screenshot/screencast frame — no cheap standalone primitive exists here, so this is genuinely more expensive than macOS/Windows on Wayland. |
| clipboard | **Implemented** (Phase 4, `Clipboard.cs`). Shell out to `wl-copy`/`wl-paste` (Wayland) or `xclip` (X11), selected by the same session-type detection as windows. `wl-copy`/`xclip` (setting) fork into the background to keep serving the clipboard after the invoked process exits, on success only - live-verified the hard way: draining that process's stdout/stderr past its own exit waits on the forked child's inherited pipe and never returns, so `Clipboard.cs` skips reading output entirely on a successful set rather than trying to bound the wait. `wl-paste` also always appends a trailing newline regardless of what was actually copied (confirmed live) - `--no-newline` is required for byte-exact round-tripping; `xclip -o` needs no equivalent. Native-protocol implementation is a possible follow-up if shelling out proves fragile. |
| wait-for | **Implemented** (Phase 4, `WaitFor.cs`). Polls `Accessibility.WalkWindow` (the same call `elements` makes) every 250ms until an element matches `--role`/`--title`, or `--timeout` (default 5s) elapses - no new AT-SPI surface needed. Re-resolves the window/app fresh on every poll rather than once up front, so it also covers "wait for this app to even launch": a resolution failure mid-poll (app not running yet) is treated as "not found yet" and retried, not a hard error - only a missing window *and* app selector fails immediately, since no amount of polling could ever fix that. |
| feedback | **Implemented** (Phase 5, `FeedbackStore.cs`/`FeedbackGitHub.cs`). Local-first CRUD (`~/.uictl/feedback.json`, monotonically increasing ids never reused) - `create`/`list`/`get`/`update`/`delete` never touch the network. `checkDuplicates`/`submit` search GitHub's Search Issues API (best-effort free-text match on the draft's title, not exact-match dedup) via `HttpClient`, token resolved `--token`/`token` param → `$GITHUB_TOKEN` → shelling out to `gh auth token`. `submit` never files anything via the API - it only opens a pre-filled "new issue" page (`xdg-open`) for a human to review and click "Create" themselves; uictl has no way to learn whether they actually did, so there's no "submitted" flag anywhere - a human deletes the local draft once they've filed it for real. |
| activity log | **Implemented** (Phase 5, `Ipc/ActivityLog.cs` + `UICtl.Gui`). An in-memory queue capped at ~2000 entries, populated from `CommandDispatcher.Dispatch` itself (one hook point, not per-command wiring) - every command except double-underscore-prefixed daemon lifecycle ones (`__ping__`, `__log_list__`, `__gate_set__`) is recorded with its params, result, success/failure, and duration. Redaction is narrow and exact, matching MCP_INTERFACE.md: `text` is replaced only in `type`'s/`clipboard.set`'s params and `clipboard.get`'s result - `ocr`/`elements`/`screenshot` output is deliberately left alone. `log.export` dumps the current queue to JSON. `log.show` opens `uictl-gui` (Gir.Core, this project's first use of GTK4/libadwaita) - a persistent toast (updates and resets a 5s fade timer on every new command, distinct from a native desktop notification - see its own doc comment for why) plus an on-demand text-based activity window hosting the "commands-enabled" kill switch. `uictl-gui` is a single GApplication instance across its whole lifetime - a second `uictl-gui`/`uictl-gui --show-log` invocation registers, discovers `IsRemote`, forwards via a named GAction (`show-log`), and exits in ~0.3s rather than starting redundant work (confirmed live). Every `DaemonClient.Send` call in `UICtl.Gui` runs on a background thread with results marshaled back via `GLib.Functions.IdleAdd` - calling it directly from a GTK event handler or timeout callback blocks the whole window on any slow daemon response, confirmed live twice (the poll loop, then separately the kill-switch checkbox) as a real, user-visible freeze severe enough to need a force-quit, not a theoretical concern. See "Daemon reliability: AT-SPI/D-Bus call timeouts" below for the pre-existing issue that made this fix necessary in the first place (fixed everywhere else in a later follow-up audit). |
| permissions.status | **Implemented** (Phase 2, `Permissions.cs`): `{"sessionType": "x11"|"wayland", "inputMethod": "uinput"|"xtest", "uinputWritable": bool, "atspiEnabled": bool, "shellExtensionConnected": bool \| null, "interactive": bool}`. `inputMethod` always reports `"uinput"` for now (XTest isn't built, so there's nothing to choose between yet). `shellExtensionConnected` is always `false` on Wayland until the companion extension exists (Phase 3). Its two probes (`TryProbeAtspi`/`TryProbeShellExtension`) are bounded to a 3s timeout and a 5-minute success-only cache (Phase 5 addition) - see "Daemon reliability: AT-SPI/D-Bus call timeouts" below for why, including why this stayed a separate, shorter bound rather than folding into the later 60s fix applied everywhere else. |

## Daemon reliability: AT-SPI/D-Bus call timeouts (audited/fixed 2026-08-31)

Found while building Phase 5's `log show`/toast, not caused by it:
**no AT-SPI or D-Bus call anywhere in this codebase ever had a
client-side timeout**, and `DaemonServer.cs` processes one connection at
a time by design. If GNOME Shell's own D-Bus service is ever slow to
answer (confirmed live: one call took ~48s, cause not diagnosed -
outside this project's control), that single call blocks *every other
command from every other client* for the same duration - not just the
one that triggered it. From the outside this looks exactly like the
whole daemon hanging.

**Fixed in a follow-up audit** (`Permissions.cs`'s two probes were fixed
first, in the same PR as `log show` - see below for why those needed a
different, shorter bound): `AsyncBridge.RunSync` (`src/UICtl.Core/
AsyncBridge.cs`), the shared sync-over-async bridge every AT-SPI/D-Bus
call in this codebase already went through, gained an opt-in `TimeSpan?
timeout` parameter (`null` default = unbounded, the original behavior).
Every real AT-SPI/D-Bus call site now passes `AsyncBridge.
DefaultDBusTimeout` (60s - well above the one confirmed-live ~48s case,
so this essentially never fires under legitimate slowness, only bounds
the absolute worst case to something finite instead of forever):
`Accessibility.cs` (`ListApps`/`ListWindows`/`WalkWindow`/
`SetElementText`/`GetElementFrame`, plus its own lazy AT-SPI bus
connect), `WaylandWindowBackend.cs` (`ListWindows`/`Activate`/
`GetFocusedWindow`, plus its lazy Shell-extension connect), and
`DisplayConfig.cs` (`List`, plus its lazy Mutter connect) - i.e. every
call site named as unfixed in the original version of this section.
`windows.list`'s hang (the specific case confirmed live at the time) is
now bounded by this.

**`WaylandScreenshotBackend.cs` deliberately excluded** - its dominant
wait isn't GNOME Shell being slow, it's a human deciding whether to click
"Allow" on the portal's consent dialog, already bounded by its own
explicit `Task.WhenAny(..., Task.Delay(TimeSpan.FromMinutes(2)))` (a
genuinely different, much longer, human-paced wait that a uniform 60s
AT-SPI/D-Bus bound would wrongly cut short). The narrower gap that
remains there - the initial portal connect/register/`ScreenshotAsync`
call itself hanging, before that 2-minute wait even starts - is lower
risk (a different D-Bus service than the one confirmed slow) and not
fixed here.

**Why `Permissions.cs`'s two probes stayed on their own separate,
shorter 3s bound** rather than switching to the new 60s
`AsyncBridge.DefaultDBusTimeout`: a probe's whole job is "can I reach X
right now", a fast yes/no a human/GUI is waiting on synchronously
(`permissions.status`) - it should fail fast, not wait out a legitimate
but slow 48s answer the way `windows.list` should. Both bounds are now
real, coexisting `AsyncBridge.RunSync` timeout parameters used for
different purposes at different layers (`Permissions.cs` wraps its own
outer `Task.Run(...).Wait(3s)` around calls into `Accessibility.cs`/
`WaylandWindowBackend.cs`, which now *also* carry the inner 60s bound -
the outer, shorter one fires first in practice).

**Caveat on the `Permissions.cs` fix itself** (raised in PR #10 review):
"bounds the wait, not the call" means a timed-out probe's orphaned task
keeps running the real D-Bus call to completion in the background. Fine
for an *occasional* slow probe (one extra background task, gone within
seconds). Under *sustained* slowness (GNOME Shell consistently >3s for
minutes at a stretch) while something polls `permissions.status`
frequently, these orphaned tasks would accumulate - each stuck on its own
thread-pool thread until GNOME Shell eventually answers. `uictl-gui`'s
own poll loop doesn't call `permissions.status` (only `__log_list__`), so
this needs a caller that repeatedly polls `permissions` specifically
(scripted, or an agent in a loop) to actually manifest, and hasn't been
observed live. If it ever is: the fix is a `SemaphoreSlim`-style cap on
concurrently in-flight probes per probe type, not a shorter timeout
(which wouldn't stop them from stacking up, just make each one smaller).

## CPU architecture (x86_64/arm64)

The project has no build-level architecture restriction (no
`RuntimeIdentifier` pin, publishes as a normal portable .NET app) — the
only real architecture dependency is in this codebase's hand-rolled
native struct offsets and ioctl encoding: `X11ImageInterop.cs` (XImage),
`X11WindowInterop.cs`'s `BuildActiveWindowClientMessage`
(XClientMessageEvent), and `UinputInterop.cs`/`UinputDevice.cs`
(`input_event`/`uinput_user_dev`/ioctl direction bits). All of it assumes
the standard 64-bit Linux LP64 data model with natural (unpacked) struct
alignment and little-endian byte order.

x86_64's SysV ABI and arm64's Linux ABI (AAPCS64) share that description
exactly, so these offsets are **reasoned to also work on arm64** — but
only the x86_64 offsets have been hand-verified live (against `XGetPixel`
as ground truth, see `X11ImageInterop.cs`'s doc comment); there is no
arm64 machine available to this project to independently confirm against.
`Interop/NativeAbi.cs` centralizes this reasoning and throws a clear error
on any architecture outside `{x86_64, arm64} × little-endian` rather than
silently misreading a struct offset or misencoding an ioctl request — the
same "flag the untested gap rather than assume" precedent as the XWayland
root-capture note below. If someone runs this on real arm64 hardware, that
would be the thing worth live-verifying and folding back into this note
(and `X11ImageInterop.cs`'s/`NativeAbi.cs`'s doc comments) rather than
just deleting the caveat.

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

## Wayland screenshot (Phase 4 — implemented, with a real open limitation)

`org.freedesktop.portal.Screenshot`, live-debugged against this machine's
real `xdg-desktop-portal`/`xdg-desktop-portal-gnome` (including a
temporary `G_MESSAGES_DEBUG=all` restart of both to see past a bare,
undetailed `Request.Response` error code) rather than assumed from the
spec. Two non-obvious, load-bearing findings from that session, both now
baked into `WaylandScreenshotBackend.cs`:

1. **A "host" (non-Flatpak/Snap) D-Bus client needs an app identity before
   Screenshot will do anything at all.** `org.freedesktop.host.portal.Registry.Register(app_id, {})`
   must be called first, and it fails with `"App info not found for '<id>'"`
   unless a `.desktop` file named `<app_id>.desktop` exists under
   `$XDG_DATA_HOME/applications`. This project has no fixed install
   location for the `uictl` binary (see README.md's Build section), so
   `WaylandScreenshotBackend.EnsureDesktopFileInstalled` self-writes/repairs
   this file from `Environment.ProcessPath` on every daemon startup rather
   than assuming one.
2. **There is no working zero-dialog path through this interface.**
   `{"interactive": false}` (documented elsewhere as "reuse a previously
   granted permission, fail if none exists") fails immediately
   (`Request.Response` code 2) every time in this GNOME build — nothing
   observed grants that stored permission via this interface, for a host
   app at least. Only `{"interactive": true}` completes successfully, but
   it shows a real modal "Take Screenshot" dialog needing a manual click —
   **confirmed live, repeatedly, that this happens on every call**, not
   just a first-time grant (one anomalous pair of instant, dialog-free
   calls was observed once during testing and never reproduced again
   across several follow-up attempts — most likely a leftover
   request/dialog from a rapid daemon-restart testing sequence, not a real
   silent-repeat path; treat "every call needs a click" as the behavior to
   design and document against).

**Deliberately deferred, not attempted this pass:** `org.freedesktop.portal.ScreenCast`
+ PipeWire is the actual "one-time consent, then silent repeat capture"
mechanism GNOME apps use (a `restore_token` skips the picker on later
session creation) — this is the real fix for the per-call dialog above,
matching macOS's one-time Screen Recording grant the way the original
plan for this row intended. Not built here: it needs a live PipeWire
stream negotiation (SPA format/buffer negotiation, frame pulling) via
P/Invoke against `libpipewire` — no .NET binding exists for this, and
it's a substantially larger and riskier body of work than the rest of
Phase 4. Revisit as its own follow-up phase once screenshot's current
click-per-call behavior is confirmed to actually be a problem in
practice (it may be fine for supervised/manual use and only matter for
tight unattended automation loops).

**Investigated 2026-08-31, blocked by an upstream binding bug, not a
GNOME limitation:** a real `parent_window` handle (instead of the empty
`""` this code sends) was suspected as the missing piece for
`interactive: false`'s stored-permission reuse - all prior testing here
always sent an empty parent_window, even on a successful
`interactive: true` call, so the portal may have had nothing to key a
persisted grant to. Getting a real handle needs GTK4/Wayland's
`xdg_foreign` export (`gdk_wayland_toplevel_export_handle`, exposed by
the `GirCore.GdkWayland-4.0` NuGet package - same 0.8.1 version as this
project's other GirCore packages) from a live, mapped top-level window.

This turned out to be **untestable with the current toolkit**: calling
`WaylandToplevel.ExportHandle(...)` and invoking the resulting
`WaylandToplevelExported` callback crashes the process outright -
`System.Runtime.InteropServices.MarshalDirectiveException: Cannot
marshal 'parameter #2': SafeHandles cannot be marshaled from unmanaged
to managed`, thrown from `Gio.Application.Run`'s own argv marshaling on
return, not from anything in application code. Isolated across three
independent minimal repros (a throwaway spike using this project's real
`Interop/PortalInterfaces.cs`; a bare `Adw.Application` + `ExportHandle`
harness with no D-Bus/portal code at all; the same harness using the
*legitimate* `WaylandToplevel.NewFromPointer` construction path instead
of a reflection-based workaround for GirCore's object-wrapper cache) -
all three crash identically, at the exact moment the native side invokes
the callback delegate, ruling out an application-level mistake. This
looks like a genuine bug in `GirCore.GdkWayland-4.0` 0.8.1's marshaling
of that specific reverse callback, not anything about GNOME's actual
`xdg_foreign`/portal behavior - the original "no stored-permission
reuse" finding above is neither confirmed nor refuted by this
investigation; it's simply still untested. Not pursued further: working
around a broken third-party binding (hand-rolled `xdg_foreign`
Wayland-protocol client via raw `libwayland-client` P/Invoke, bypassing
`GirCore.GdkWayland-4.0` entirely) is a body of work comparable to the
already-deferred ScreenCast+PipeWire alternative above, not a quick
follow-up. Revisit if a newer GirCore release fixes this, or if
ScreenCast+PipeWire gets built instead (it doesn't need a
`parent_window`/`xdg_foreign` handle at all, sidestepping this bug
entirely).

**ScreenCast+PipeWire: started 2026-08-31, portal session layer
confirmed live - this is the real fix.** `PortalRegistration.cs`
(extracted from `WaylandScreenshotBackend.cs` - both need the same
app-id registration dance), `Interop/ScreenCastInterfaces.cs`, and
`WaylandScreenCastBackend.cs` implement
`org.freedesktop.portal.ScreenCast`'s session flow: `CreateSession` ->
`SelectSources` (`persist_mode: 2`, plus a saved `restore_token` if one
exists) -> `Start` -> `OpenPipeWireRemote`. **Live-verified, user-
confirmed two-run test**: a fresh session (no restore token) showed
GNOME's source-picker dialog and required a real click to choose a
monitor and share; the very next run, using the `restore_token` saved
from the first (`~/.uictl/screencast-restore-token`), completed **with
no dialog at all** - confirmed by the user watching the screen, not
inferred from timing. This is the deferred "one-time consent, then
silent repeat capture" mechanism actually working, unlike Screenshot's
`interactive:false` (which never granted a stored permission through
that interface at all) and unlike the blocked `parent_window`
investigation above.

One real gotcha found getting this far: the `streams` result from
`Start` (D-Bus type `a(ua{sv})`) decodes via Tmds.DBus as a real
`ValueTuple<uint, IDictionary<string,object>>[]`, not the `object[]`
every other portal result in this codebase has needed so far - confirmed
live via the exact `InvalidCastException` naming the actual runtime
type; fixed by casting to the correct ValueTuple array type directly.

**Not yet built**: the PipeWire side (`node_id`/fd -> an actual decoded
frame -> PNG). This is the harder, riskier remaining piece the original
"substantially larger and riskier" assessment was mainly about - hand-
rolled P/Invoke against `libpipewire-0.3.so.0` (confirmed installed,
v1.6.2, no .NET binding exists), including SPA POD format negotiation
and a native `process` callback for buffer delivery. In progress.

## GNOME Shell extension (Phase 3 — implemented)

`gnome-extension/byronscottjones_uictl-linux-mcp@github.com/` — a small GJS
extension exposing `org.byronscottjones.uictl.WindowManager` on the session
bus (`ListWindows()`, `ActivateWindow(stableSeq)`, `GetFocusedWindow()` —
no separate `GetWindowGeometry`, `ListWindows()` already returns full
geometry per window, cheap in-process GObject calls with no reason to
split it out). Wraps Mutter's `global.display`/`Meta.Window` APIs the same
way community extensions do. **Design grounded in real, currently-running
extensions found on this exact GNOME Shell 50 install**
(`/usr/share/gnome-shell/extensions/`), not guessed from training
knowledge: `snapd-prompting@canonical.com/dbusServer.js` for the
`Gio.DBusExportedObject.wrapJSObject` + `own_name` D-Bus-export pattern;
`ding@rastersoft.com`/`tiling-assistant@ubuntu.com`/`ubuntu-dock@ubuntu.com`
confirmed live: `global.display.list_all_windows()`,
`window.get_stable_sequence()` (the Wayland-backend windowId, see
`MCP_INTERFACE.md`'s "Window id" section), `window.get_pid()`,
`window.get_title()`, `window.get_frame_rect()`,
`global.display.get_focus_window()`, `Main.activateWindow(window)`, and
the `window.get_window_type() !== Meta.WindowType.DESKTOP &&
!window.is_skip_taskbar()` filter real dock/tiling extensions use.

Versioned/tested against whatever GNOME release ships with Ubuntu 26.04
specifically (`shell-version: ["50"]`) — a Shell major-version bump
breaking it is a known, accepted fragility, not something this project
tries to abstract away (same spirit as `uictl-win-mcp` being scoped to its
dev machine's exact OS build).

**Two things confirmed live, not assumed** (see `gnome-extension/README.md`
for the full detail): (1) `gnome-extensions pack` silently drops any
source file other than `extension.js` unless named via
`--extra-source=<file>` — a fresh install with the flag omitted "enables"
with no error while doing nothing, since the actual D-Bus server code
never made it into the package. (2) A **brand-new** extension genuinely
needs a full Shell restart (logout/login, or reboot — Wayland has no live
`Alt+F2, r`) before Shell is even aware it exists at all —
`gnome-extensions list`/`info` report nothing for it immediately after a
successful `install`, and `org.gnome.Shell.Extensions.ReloadExtension` is
introspectable but returns `UnknownMethod` when called (not implemented on
this build). This confirms (doesn't overturn) the original assumption
below. **Toggling an already-known extension *is* live**, though — no
relogin needed after the first time, `gnome-extensions disable`/`enable`
picks up code changes immediately.

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

The following section relates only to development under WSL, and not Ubuntu or other full Linux systems.

**Deprecated**
```
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
```

## Build plan / phases

See the plan this repo was scaffolded from for the full phase breakdown
(environment verification, **AT-SPI + X11 core (done, Phase 1)**, **input
synthesis (done, Phase 2 — click/move/scroll/key/type, `permissions.status`)**,
**the GNOME Shell extension + Wayland window management (done, Phase 3 —
activate/focus.hold/focus.release/focus.status)**, **Wayland
screenshot/displays/pixel/OCR/clipboard/wait-for (done, Phase 4 —
screenshot/displays.list/pixel/ocr/clipboard.get/clipboard.set/waitFor
all implemented and live-verified; only ScreenCast+PipeWire, a
hypothetical zero-dialog screenshot follow-up, remains unbuilt - see
"Wayland screenshot" section above)**, **feedback + activity log (Phase 5,
done — `feedback.*` (`FeedbackStore.cs`/`FeedbackGitHub.cs`), the activity
log's data/redaction/export (`ActivityLog.cs`), and `log.show`'s GTK4/
libadwaita toast + activity window with the commands-enabled kill switch
(`UICtl.Gui`, this project's first use of that toolkit) are all
implemented and live-verified; see "Daemon reliability: AT-SPI/D-Bus call
timeouts" above for a real, pre-existing issue found along the way -
`Permissions.cs`'s two probes were fixed in this same phase, the rest of
the call sites in a later follow-up audit, now also done)**,
tests/docs/contract sync). Development runs directly against a real GNOME/Mutter desktop
session (not WSL/WSLg, which only runs a lightweight `weston` compositor —
window enumeration/activation on Wayland, the Shell extension, portal
consent dialogs, and `Mutter.DisplayConfig` genuinely need real
`gnome-shell`/Mutter, confirmed hands-on building Phase 3), so every phase
lands and gets live-tested against the genuine article, not approximated.

**Live-test every phase against a real running GTK app, not just a clean
build** — this is the standing lesson from `uictl-win-mcp`, where code
"scaffolded and cross-compiled but never run" hid a real bug until it was
finally executed.
