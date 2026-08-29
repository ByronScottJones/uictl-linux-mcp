# uictl MCP interface — cross-platform contract

This is the single source of truth for the tool names, CLI verbs, arguments, and
response shape that **all three** implementations of `uictl` commit to exposing
identically, where the platform allows it:

- macOS (Swift): [`uictl-mac-mcp`](https://github.com/ByronJones-Elsevier/uictl-mac-mcp)
- Windows (.NET/C#): [`uictl-win-mcp`](https://github.com/ByronJones-Elsevier/uictl-win-mcp)
- Linux/GNOME (.NET/C#): this repo, `uictl-linux-mcp`

The goal is that an agent's tool-calling code (or shell script) doesn't need to
branch on OS — `uictl_click`, `uictl_screenshot`, etc. take the same arguments
and return the same shape everywhere. Where a platform genuinely can't support
something, or must add something, that's called out explicitly below rather
than silently diverging. Treat this file as the spec to implement against; if
an implementation and this file disagree, the file wins — fix the code.

Changes to tool names, argument names, or the response envelope must be made
in this file first, then mirrored into every implementation affected in the
same change (`Sources/uictl/MCP/MCPServer.swift` on macOS; `src/UICtl.Mcp` in
`uictl-win-mcp` and in this repo).

**Note on this file's provenance:** it was forked from `uictl-win-mcp`'s copy
of the same name. `uictl-mac-mcp` does not currently have its own copy of this
file at all, despite being named as a party to the contract in both other
repos' copies — worth reconciling at some point, not fixed here.

## Response envelope

Every command — CLI or MCP — returns exactly one of:

```json
{"ok": true, "data": { /* command-specific */ }}
```
```json
{"ok": false, "error": "<human-readable message>"}
```

The CLI prints this as one JSON object to stdout and exits `0` on `ok: true`,
non-zero otherwise. The MCP tool call returns the same JSON as its text
content, with `isError` set to `!ok`.

## Common types

- **Point** — `{"x": number, "y": number}`. Screen coordinates, top-left
  origin, y increasing downward.
  - macOS: Quartz global point-space (points, not raw pixels).
  - Windows: virtual-screen device pixels (per-monitor-DPI-aware process).
  - Linux, X11 backend: root-window virtual-screen pixel space (the bounding
    box of all monitors via RandR) — same shape as Windows' virtual screen
    space, physical pixels.
  - Linux, Wayland/GNOME backend: **open question, resolve empirically in
    the phase that builds this backend, don't assume.** Wayland has no
    standard protocol exposing a compositor's global coordinate space to an
    arbitrary client; this backend's space is whatever the companion GNOME
    Shell extension reports (likely Mutter's internal "logical",
    post-fractional-scale space) — document the final answer here, including
    how/whether it's convertible to physical pixels, once decided. Until
    then, treat Wayland-backend points/frames as **not necessarily
    comparable** to X11-backend ones within the same process.
  - See each repo's `ENGINEERING.md` for the platform-specific pixel-vs-point
    discussion — the contract here is just "top-left origin, y-down, and
    self-consistent with every other frame/point this tool returns on that
    platform/backend."
- **Frame/Rect** — `{"x": number, "y": number, "w": number, "h": number}`.
  Same coordinate space as Point.
- **Element id** — an opaque string, scoped to the window it was listed from.
  Callers must treat it as opaque and not parse it.
  - macOS/Windows: `"{windowId}-{n}"` (an incrementing counter per window).
  - Linux: same scheme (`"{windowId}-{n}"`) unless implementation finds a
    reason to diverge — if so, update this file and explain why.
  - All platforms: ids are invalidated the next time that window's elements
    are re-listed (`uictl_elements` / `uictl_screenshot --annotate`). Callers
    must re-list before acting on a stale id.
- **Window id** — an integer window handle.
  - macOS: `CGWindowID` (`UInt32`).
  - Windows: `HWND` value (may exceed 32 bits).
  - Linux, X11 backend: the X11 `Window` (`XID`, an unsigned 32-bit value).
  - Linux, Wayland/GNOME backend: whatever id scheme the companion GNOME
    Shell extension assigns internally to a `Meta.Window` (Mutter has no
    stable public numeric window id of its own) — document the exact scheme
    here once Phase 3 defines it. Because a single Linux process may have
    two live backends (X11 windows via XWayland *and* Wayland-native windows
    on the same GNOME session), a Linux `windowId` is **not guaranteed
    unique across backends** the way it is on macOS/Windows — callers
    should treat it as opaque and always re-resolve via the same call that
    gave it to them (`windows.list`), never assume cross-backend uniqueness.

## App/window selectors

Every `app` parameter below is a single string matched, in order, against:

- macOS: name substring → bundle id → pid.
- Windows: name substring (process image name) → package family name → pid.
- Linux: name substring (process/`comm` name, case-insensitive) → pid. There
  is no bundle-id/package-family-name equivalent in the general case; see
  `uictl_apps` below for how that field is populated.

## Tools

Twenty-nine tools total (20 core + 7 feedback + 2 log), one per row below.
"Command" is the internal dispatcher command string; "CLI" is the subcommand
a human/script would type; "MCP tool" is the name an MCP client calls.

| Command | CLI | MCP tool | Required args | Optional args |
|---|---|---|---|---|
| `permissions.status` | `permissions` | `uictl_permissions` | — | `app: string` |
| `apps.list` | `apps` | `uictl_apps` | — | `all: bool` |
| `displays.list` | `displays` | `uictl_displays` | — | — |
| `windows.list` | `windows` | `uictl_windows` | — | `app: string` |
| `activate` | `activate` | `uictl_activate` | `app: string` | `window: int` |
| `focus.hold` | `focus hold` | `uictl_focus_hold` | — (`app` or `window`) | `app: string`, `window: int` |
| `focus.release` | `focus release` | `uictl_focus_release` | — | — |
| `focus.status` | `focus status` | `uictl_focus_status` | — | — |
| `screenshot` | `screenshot` | `uictl_screenshot` | — | `window: int`, `app: string`, `screen: int`, `out: string`, `annotate: bool`, `role: string` |
| `elements` | `elements` | `uictl_elements` | — | `window: int`, `app: string`, `role: string`, `title: string`, `maxDepth: int`, `maxElements: int` |
| `click` | `click` | `uictl_click` | — (`at` or `element`) | `at: string`, `element: string`, `button: string`, `double: bool`, `count: int` |
| `move` | `move` | `uictl_move` | `at: string` | — |
| `scroll` | `scroll` | `uictl_scroll` | `at: string` | `dx: int`, `dy: int` |
| `type` | `type` | `uictl_type` | `text: string` | `element: string` |
| `key` | `key` | `uictl_key` | `combo: string` | — |
| `waitFor` | `wait-for` | `uictl_wait_for` | — | `window: int`, `app: string`, `role: string`, `title: string`, `timeout: number` |
| `ocr` | `ocr` | `uictl_ocr` | — | `image: string`, `window: int`, `app: string`, `region: string` |
| `pixel` | `pixel` | `uictl_pixel` | `at: string` | — |
| `clipboard.get` | `clipboard get` | `uictl_clipboard_get` | — | — |
| `clipboard.set` | `clipboard set` | `uictl_clipboard_set` | `text: string` | — |
| `feedback.create` | `feedback create` | `uictl_feedback_create` | `category: string`, `title: string`, `body: string` | — |
| `feedback.list` | `feedback list` | `uictl_feedback_list` | — | — |
| `feedback.get` | `feedback get` | `uictl_feedback_get` | `id: int` | — |
| `feedback.update` | `feedback update` | `uictl_feedback_update` | `id: int` | `category: string`, `title: string`, `body: string` |
| `feedback.delete` | `feedback delete` | `uictl_feedback_delete` | `id: int` | — |
| `feedback.checkDuplicates` | `feedback check-duplicates` | `uictl_feedback_check_duplicates` | `id: int` | `repo: string`, `token: string` |
| `feedback.submit` | `feedback submit` | `uictl_feedback_submit` | `id: int` | `repo: string`, `token: string` |
| `log.show` | `log show` | `uictl_log_show` | — | — |
| `log.export` | `log export` | `uictl_log_export` | — | `out: string` |

`permissions.request` (CLI-only) is macOS-only — it triggers macOS's TCC
consent dialogs interactively. Neither Windows nor Linux has an equivalent
OS consent dialog to trigger (Linux's closest analogs — the uinput
group-membership grant and the Wayland portal dialogs — aren't something a
single CLI flag can "request" the way TCC can); `permissions --request` on
Linux, like Windows, is a no-op that returns the same payload as
`permissions` plus a `"requested": false` note.

### `uictl_permissions`

Checks the platform-specific preconditions this tool needs to function.
Response `data` shape is intentionally *not* identical between platforms,
since the underlying concepts differ:

- macOS: `{"accessibility": bool, "screenRecording": bool}`.
- Windows: `{"elevated": bool, "targetProcessElevated": bool | null, "interactive": bool}`.
- Linux: `{"sessionType": "x11" | "wayland", "inputMethod": "uinput" | "xtest", "uinputWritable": bool, "atspiEnabled": bool, "shellExtensionConnected": bool | null, "interactive": bool, "preflightReady": bool, "preflightAt": string | null, "preflightManualSteps": string[], "preflightChecks": {[name: string]: bool}}`.
  `sessionType` is detected from `$WAYLAND_DISPLAY`/`$DISPLAY` (**not**
  `$XDG_SESSION_TYPE` alone — observed unset even in a real Wayland session
  during development). `shellExtensionConnected` is `null` on an X11
  session (not applicable there), `false` if Wayland and the companion
  Shell extension isn't reachable over D-Bus, `true` if it is.
  `uinputWritable` reflects whether the daemon's own process can currently
  open `/dev/uinput` for writing. `interactive` is `false` when the daemon's
  own process isn't attached to a real display/bus session (see "Remote
  testing over SSH" below) — every other Linux-specific command that
  touches the screen, input, or the clipboard silently no-ops or fails when
  this is `false`, rather than raising a distinct error of its own.
  `preflightReady`/`preflightAt`/`preflightManualSteps`/`preflightChecks`
  mirror `scripts/preflight.sh`'s own `ready`/`ranAt`/
  `manualStepsRemaining`/`checks` fields, read from
  `~/.uictl/preflight.json` (which that script writes) — `preflightReady:
  false, preflightAt: null` means the script has never been run at all;
  `false` with a non-null `preflightAt` means it ran but isn't fully ready
  yet, with `preflightManualSteps` naming what's left (e.g. "log out and
  back in") and `preflightChecks` giving the full per-check breakdown
  (`{"dotnet": true, "inputGroup": false, ...}`) so a caller can see
  exactly which check(s) failed without re-reading the raw file itself.
  This lets a caller (human or agent) distinguish "never set up" from "set
  up, but something's still not ready" — and what to do about it.

### `uictl_apps`

`data`: `{"apps": [{"pid": int, "name": string, "bundleId": string}, ...]}`.

- Linux: `bundleId` is `""` for every app unless a future implementation
  finds a reliable way to correlate a running process back to a
  `.desktop` entry id — same empty-string-for-N/A convention Windows uses
  for classic Win32 apps.

### `uictl_displays`

`data`: `{"displays": [{"index": int, "displayId": int, "frame": Frame, "isMain": bool, "scale": number}, ...]}`.
`index` matches what `screenshot`'s `screen` argument expects.

- Linux: `displayId` is an opaque per-backend monitor id (X11: RandR output
  id; Wayland: whatever `Mutter.DisplayConfig` assigns). `scale` is the
  monitor's configured scale factor — an integer under classic X11 scaling,
  potentially fractional under GNOME's fractional-scaling feature; report
  the real value either way, don't round.

### `uictl_windows`

`data`: `{"windows": [{"windowId": int, "pid": int, "title": string, "frame": Frame, "displayId": int | null}, ...]}`.
On Linux, only windows visible to the currently-active backend (X11 or
Wayland/GNOME-extension) are listed — see "Window id" above.

### `uictl_activate`

`data`: `{"pid": int, ...}` — resolved pid at minimum, mirroring
macOS/Windows.

### `uictl_focus_hold` / `uictl_focus_status` / `uictl_focus_release`

Same shape as macOS/Windows — see those repos' copies of this file for the
full field list (`held`, `app`, `pid`, `windowId`, `isFrontmost`,
`restoresTo`, `restoredFocus`) and the `focusHold` field carried by
`uictl_click`/`uictl_move`/`uictl_scroll`/`uictl_type`/`uictl_key` while a
hold is active. On Linux, "activate"/"raise" for the Wayland backend goes
through the companion Shell extension the same way `windows.list` does.

### `uictl_screenshot`

`data`: `{"path": string, "width": int, "height": int}`, plus
`"elements"` when `annotate: true` (same legend shape as macOS/Windows).
`role` is a platform-native string — on Linux, an AT-SPI role name (see
`uictl_elements` below).

### `uictl_elements`

`data`: `{"windowId": int, "count": int, "elements": [...]}`, plus
`"truncated": true` if `maxElements` cut the walk short.

**Role strings are not unified across platforms**, same as the
macOS/Windows divergence: Linux uses AT-SPI2 role name strings (e.g.
`push button`, `frame`, `text`, `check box` — note the spaces, unlike
macOS's `AXButton` or Windows' `Button`). A script written for one platform
needs its `--role` value translated for this one.

### `uictl_click`

`data`: `{"clicked": Point}`, plus `"focusHold"` per the note above while a
hold is active.

### `uictl_move` / `uictl_scroll`

`data`: `{"moved": true}` / `{"scrolled": true}`, plus `"focusHold"` while a
hold is active.

- Linux: `scroll`'s sign convention (not yet pinned down elsewhere in this
  file) - `dy > 0` scrolls content **down** (matches DOM `wheel` event
  `deltaY`), `dx > 0` scrolls **right**. Translated internally to the
  kernel's inverted `REL_WHEEL` convention (positive = up) - see
  `ENGINEERING.md`'s input synthesis notes. Other platforms should match
  this or explicitly document a divergence.

### `uictl_type`

`data`: `{"method": "atspiValue" | "synthesizedKeystrokes", "element": string?}`, plus `"focusHold"` while a hold is active.

- Linux: synthesized keystrokes (`method: "synthesizedKeystrokes"`, whether
  from omitting `--element` or from the `EditableText`-fails fallback) only
  support ASCII characters typeable on a US-QWERTY layout - `uinput`/evdev
  `KEY_*` codes are physical-key codes, not a Unicode-insertion primitive.
  An unsupported character throws naming it, rather than silently dropping
  it. Arbitrary Unicode still works via the `atspiValue` tier on any widget
  that implements `EditableText`. The element fallback additionally
  synthesizes a left click at the element's center first, to give it
  keyboard focus (there's no reliable AT-SPI focus primitive here -
  `Component.GrabFocus` returns `NotSupported` in practice, see
  `ENGINEERING.md`'s activate/focus row), before synthesizing the text.
- Linux: `method` values are `"atspiValue"` (AT-SPI `EditableText`/`Text`
  interface direct value set, the equivalent of macOS's AX value set or
  Windows' `ValuePattern`) or `"synthesizedKeystrokes"` (uinput/XTest, the
  fallback). Same two-tier strategy, platform-native names.

### `uictl_key`

`data`: `{"sent": string}` (echoes the combo), plus `"focusHold"` while a
hold is active. Modifier vocabulary is platform-native: macOS uses
`cmd, shift, alt/option, ctrl/control, fn`; Windows uses `ctrl, shift, alt,
win`; **Linux uses `ctrl, shift, alt, super`** (the "Super"/"Meta" key,
GNOME's own name for what Windows calls `win`). There is no shared modifier
name for "the OS accelerator key" across all three platforms — scripts
crossing platforms must translate this themselves.

### `uictl_wait_for`

`data`: `{"found": bool, "element": {...}?}` (element present only when found).

### `uictl_ocr`

`data`: `{"textBlocks": [{"text": string, "frame": Frame, "confidence": number | null}, ...]}`.

- macOS: Vision framework, real per-observation `confidence` (0–1).
- Windows: `Windows.Media.Ocr`, no confidence score at all — always `null`.
- Linux: **Tesseract**, which reports real per-word confidence (0–1,
  normalized the same way macOS's is) — closer to macOS's behavior than
  Windows'. Each block corresponds to one Tesseract text line, `frame` as
  the union of that line's word bounding boxes — same granularity the other
  two platforms give.
- All platforms return per-block bounding boxes in the same coordinate
  space as `elements` frames — a hard requirement, not a suggestion.

### `uictl_pixel`

`data`: `{"r": int, "g": int, "b": int, "a": int}` (0–255 each).

- Linux, X11 backend: a direct 1×1 `XGetImage` read, cheap, same cost class
  as macOS/Windows.
- Linux, Wayland backend: **genuinely more expensive than the other
  platforms** — there is no cheap single-pixel read primitive under
  Wayland; the value is sampled from a full captured screenshot/screencast
  frame. Document this in caller-facing docs (`AGENTS.md`) rather than
  quietly making `pixel` slow and unexplained; don't use it in a tight
  polling loop on a Wayland session the way you might on macOS/Windows.

### `uictl_clipboard_get` / `uictl_clipboard_set`

`data`: `{"text": string}` / `{"set": true}`.

- Linux: implemented via shelling out to `wl-copy`/`wl-paste` (Wayland) or
  `xclip` (X11), selected by the same session-type detection as the window
  backend. Purely an implementation detail — the contract shape is
  identical to macOS/Windows.

### Feedback (`uictl_feedback_*`)

Same shape and flow as macOS/Windows — local-first storage
(`~/.uictl/feedback.json`), `submit` checks GitHub for duplicates then opens
a pre-filled "new issue" page rather than filing anything itself. See
`uictl-win-mcp`'s copy of this file for the full `FeedbackEntry` shape and
per-tool `data` details, which this repo matches exactly.

- Linux: default repo is `ByronScottJones/uictl-linux-mcp` (this repo lives
  under the personal account, not the `ByronJones-Elsevier` org the other
  two platforms use — the WSL `gh` session hit an SSO-authorization gap
  against that org when this repo was created; revisit if/when that's
  sorted out). Token
  resolution (`--token` / `token` param → `$GITHUB_TOKEN` → `gh auth token`)
  and the duplicate-check REST call are the same shape as the other two
  platforms, via `HttpClient`/`System.Diagnostics.Process` (same as
  Windows' implementation, since both are .NET).

**MCP elicitation.** `uictl_feedback_submit` is the one tool on all three
platforms whose logic isn't a thin forward to the daemon — same rationale
and flow as macOS/Windows (review via form-mode elicitation, then hand off
a pre-filled GitHub URL via URL-mode elicitation, falling back to a direct
open if the client doesn't support elicitation or doesn't respond within
120s). The Linux implementation uses the same `ModelContextProtocol` C# SDK
as Windows, so the mechanics should carry over directly rather than needing
Windows' original SDK-quirk workarounds re-derived from scratch — but
verify, don't assume, since this repo hasn't built that far yet.

### Activity log (`uictl_log_show` / `uictl_log_export`)

Same behavior and redaction rules as macOS/Windows (toast per call, capped
~2000-entry in-memory log, `text` redacted only for `type`/`clipboard.set`/
`clipboard.get`, `ocr`/`elements`/`screenshot` output never redacted).

- Linux: the window is a GTK4/libadwaita `Adw.Window` with a
  `Gtk.ColumnView` (via Gir.Core), the GNOME-native equivalent of macOS's
  `NSTableView`/Windows' `DataGrid`.

**Commands-enabled kill switch.** Same escape-hatch semantics as
macOS/Windows: the toggle only takes effect while the window is open, and
there is no command to disable commands, only the on-screen checkbox.

### Remote testing over SSH (Linux)

Same trap as macOS/Windows, with a Linux-specific wrinkle: a daemon
auto-spawned from a cold, non-desktop SSH session may have **no**
`$DISPLAY`/`$WAYLAND_DISPLAY` at all, and often no working
`$DBUS_SESSION_BUS_ADDRESS` either — which breaks AT-SPI (a D-Bus service)
as well as any windowing/input call, more bluntly than Windows (which at
least gets a window station, just a non-interactive one). Start the daemon
from an already-logged-in desktop session before any SSH-driven command can
auto-spawn it cold. `uictl_permissions`' `interactive` field reports this
the same way it does on Windows.

## Deliberately platform-specific, not part of this contract

- `daemon start|stop|status` (CLI-only, all platforms — see each repo's
  `ENGINEERING.md` for the IPC transport, which differs: Unix domain socket
  on macOS and Linux, named pipe on Windows).
- `mcp` (the subcommand that runs the MCP server itself over stdio) — its
  existence is shared, its registration/config-file examples are OS-specific
  (see each repo's README).
- Exact permission/consent-model behavior (see `uictl_permissions` above).
- The companion GNOME Shell extension (`gnome-extension/` in this repo) and
  its install/versioning story — a Linux-only artifact with no macOS/Windows
  analog.
