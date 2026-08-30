# Using uictl as an agent

This tool exists so an agent (Claude Code or similar) can drive a GUI app
during development without a human moving the mouse. This file is the
practical playbook — see `README.md` for setup and `ENGINEERING.md` for
implementation details. It mirrors the macOS
[`uictl-mcp`](https://github.com/ByronJones-Elsevier/uictl-mac-mcp) and
Windows [`uictl-win-mcp`](https://github.com/ByronJones-Elsevier/uictl-win-mcp)
playbooks verb-for-verb where the platform allows it; see `MCP_INTERFACE.md`
for the exact cross-platform contract and where Linux/GNOME genuinely
diverges.

## The core loop

1. **Find the window.**
   ```sh
   uictl windows --app <AppName>
   ```
   If the app isn't running yet, launch it yourself, then retry — there's no
   "launch" command in uictl on purpose. On a pure-Wayland GNOME session this
   requires the companion GNOME Shell extension (`gnome-extension/`) to be
   installed and enabled — without it, only X11/XWayland windows are visible.
   `uictl permissions` tells you which window backend is active.

2. **Bring it to front.**
   ```sh
   uictl activate --app <AppName>
   ```
   Do this before screenshotting or clicking — otherwise you may be looking
   at (or clicking into) a window that's behind something else.

3. **Look at it — with element numbers, not a blank screenshot.**
   ```sh
   uictl screenshot --app <AppName> --annotate --out /tmp/shot.png
   ```
   Read the returned JSON `elements` legend (each has a `number`, `id`,
   `role`, `title`, `frame`) alongside the image. This is deliberately *not*
   just a plain screenshot — a vision model reading raw pixels has to guess
   coordinates, which is where most GUI-automation flakiness comes from.

4. **Act using the element id, not raw coordinates.**
   ```sh
   uictl click --element <id>
   uictl type --element <id> "some text"
   ```
   Prefer `--element` over `--at x,y` whenever you have an id: it's
   resilient to the window having moved or resized since you looked at it.

5. **Confirm the result, don't assume it.**
   ```sh
   uictl elements --app <AppName> --title "expected label"
   ```
   or `wait-for` if the UI updates asynchronously:
   ```sh
   uictl wait-for --app <AppName> --title "Done" --timeout 10
   ```
   or re-screenshot and OCR/look at it again.

## Holding focus across a human's own clicks

If a human at the machine clicks into another window mid-task while you're
automating something else, that steals frontmost status. Same pattern as
macOS/Windows:

```sh
uictl focus hold --app <AppName>
# ... do the automation ...
uictl focus release
```

`uictl focus status` shows what's held and what `release` will restore
focus to. See `MCP_INTERFACE.md` for the exact response shape and the
`focusHold` field carried on focus-sensitive commands while a hold is
active.

## When accessibility elements aren't enough

Some UIs (canvas-drawn, game engines, custom-rendered text, apps that don't
implement an ATK/AT-SPI bridge properly) don't expose useful nodes via
`elements`. Fall back to:

- `uictl ocr --app <AppName>` — reads on-screen text (Tesseract) and returns
  each block's bounding box in the same coordinate space as `elements`
  frames.
- `uictl pixel --at x,y` — cheap state checks. Note: on a Wayland session
  this is *not* actually cheap here (see the Environment constraints section
  of `README.md`) — it's sampled from a full captured frame, so don't lean
  on it in a tight polling loop the way you might on macOS/Windows.

## Gotchas specific to this tool (Linux/GNOME)

- **Element ids expire.** Every `elements`/`screenshot --annotate` call
  re-walks the tree and re-numbers it. Re-list before acting if any time (or
  any other action) has passed.
- **Wayland `activate`/`focus.*` need the companion Shell extension.**
  (`windows`/`elements` don't — those stay AT-SPI-based on every session
  type.) If `uictl_activate`/`uictl_focus_hold` fail on a Wayland session,
  check `uictl permissions`' `shellExtensionConnected` field — `false` or
  `null` means either you're on X11 (expected, no extension needed there)
  or the extension isn't installed/enabled (see `gnome-extension/README.md`).
  A **brand-new** install of the extension needs a logout/login (or
  reboot) before GNOME Shell is even aware it exists at all — confirmed
  live, this isn't optional/skippable. Toggling an already-known extension
  (e.g. after a code update) is live, no relogin needed.
- **Input synthesis needs `/dev/uinput` access.** If clicks/keystrokes
  silently no-op, check `uictl permissions`' `uinputWritable` field — you
  likely need `sudo usermod -aG input $USER` plus a fresh login session (see
  `README.md`).
- **AT-SPI role names, not AX/UIA vocabulary.** `--role` filters against
  AT-SPI role strings (e.g. `push button`, `frame`, `text`) — different from
  macOS's `AXButton` or Windows' `Button`. A script written for one platform
  needs its `--role` value translated for this one; see `MCP_INTERFACE.md`.
- **`type` without `--element`** sends keystrokes to whatever currently has
  keyboard focus, system-wide — make sure you've clicked into the right
  field first.
- **Synthesized keystrokes are ASCII/US-QWERTY only.** This covers `type`
  without `--element`, and the fallback `type --element` takes when a
  widget doesn't expose AT-SPI `EditableText` directly. `uinput`/evdev
  key codes are physical-key codes, not a Unicode-insertion primitive — a
  character outside US-QWERTY (accents, non-Latin scripts, emoji) throws
  rather than silently dropping it. If you need to type non-ASCII text,
  target a widget that accepts a direct AT-SPI value set instead (the
  `atspiValue` method tier has no such limit) — or fall back to
  `clipboard set` + a paste keystroke once clipboard support lands.
- **Nested elements' frames are unreliable — only trust large/top-anchored
  ones for `click --element`.** Confirmed live: AT-SPI reports a real,
  correct on-screen frame for a window's own top-level accessible, but a
  **degenerate `(0,0)` origin for every nested child element**, regardless
  of its true position (every button in a `gnome-calculator` keypad grid
  reports the same `x:0,y:0` as every other one). `click --element` /
  `type --element`'s click-to-focus fallback compute a target from the
  element's frame center, so this only works for elements that dominate the
  window (a full-width text box, say) — clicking a specific small button
  by `--element` id is **not** currently reliable. Prefer `click --at` with
  a coordinate you've confirmed some other way until this is root-caused
  (see `ENGINEERING.md`'s "Coordinate spaces" section).
- **`/dev/uinput` needs both group membership and a udev grant.** Being in
  the `input` group (see the Input setup section below) is necessary but
  not always sufficient — confirmed live on this project's dev machine,
  `/dev/uinput` shipped `root:root` mode `0600` with no group grant at all,
  so `usermod -aG input` alone left `uictl permissions`' `uinputWritable`
  false. Check that field before assuming click/type/scroll/key will work.
  `scripts/preflight.sh` runs (and re-runs) every setup check at once and
  records the result at `~/.uictl/preflight.json` — `uictl permissions`'
  `preflightReady`/`preflightAt`/`preflightManualSteps` fields surface that
  same status without you having to re-derive it check by check.
- **`elements`' `value` field is not always populated even when `type`
  worked.** It's read from AT-SPI's `Text` interface, but some widgets'
  outer accessible (e.g. GtkSourceView's `text box` role in GNOME Text
  Editor) don't expose `Text` directly even though `EditableText.SetTextContents`
  on that same object succeeds — confirmed live: the window title (which
  GNOME Text Editor derives from buffer content) updated correctly after
  `type` even though the immediately-following `elements` call showed
  `"value": null`. Don't treat a null `value` as proof `type` failed;
  cross-check some other visible effect (title, a re-render, `ocr`) instead.
- **`screenshot`/`pixel` show a real consent dialog on every call on
  Wayland.** X11 is instant. On Wayland there is currently no zero-dialog
  path (`org.freedesktop.portal.Screenshot`'s documented "reuse a stored
  grant" mode doesn't work here - see `ENGINEERING.md`'s "Wayland
  screenshot" section) - warn a human before calling either on a Wayland
  session, the same way you'd warn before synthesizing input, and don't
  call `pixel` in a polling loop there.
- **The daemon caches state.** If you rebuild `uictl` during development,
  run `uictl daemon stop` before your next command.
- **Screenshots/OCR/elements output isn't redacted.** Unlike `type` and
  `clipboard set`/`get` text, on-screen content read by `ocr`/`elements`/
  `screenshot` is not redacted anywhere it's logged or exported.

## Letting a human see what you're doing

`uictl log show` opens a live table of every CLI/MCP call the daemon has
handled (`uictl log export` writes the same data to a JSON file), same as
macOS/Windows.

## Reporting feedback about uictl itself

Same flow as the other two platforms:

```sh
uictl feedback create --category error --title "..." --body "..."
uictl feedback submit <id>
```

`create` only writes to `~/.uictl/feedback.json`. `submit` checks for
GitHub duplicates first, then opens a pre-filled "new issue" page — a human
still has to review and click "Create." Calling `uictl_feedback_submit`
over MCP uses elicitation to let the human review first; see
`MCP_INTERFACE.md`.

## Remote testing over SSH

Same trap as the other two platforms, plus a Linux-specific wrinkle: a
daemon auto-spawned from a cold, non-desktop SSH session has no display/bus
connection to work with at all (no `$DISPLAY`/`$WAYLAND_DISPLAY`, and often
no working `$DBUS_SESSION_BUS_ADDRESS` for AT-SPI either), so this fails
even more bluntly than on Windows (which at least gets a window station,
just a non-interactive one). Start the daemon from an already-logged-in
desktop session before any SSH-driven command can auto-spawn it cold —
`uictl permissions`' `interactive` field reports this.

## MCP mode

If your harness supports MCP tools directly, prefer that over shelling out:

```sh
claude mcp add uictl -- /path/to/uictl mcp
```

Then call `uictl_screenshot`, `uictl_elements`, `uictl_click`, etc. as
structured tool calls instead of parsing CLI stdout.
