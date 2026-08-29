# uictl

- [uictl-mac-mcp](https://github.com/ByronJones-Elsevier/uictl-mac-mcp) macOS version
- [uictl-win-mcp](https://github.com/ByronJones-Elsevier/uictl-win-mcp) Windows version
- uictl-linux-mcp (this repo) Linux/GNOME version

A Linux command-line tool (and MCP server) for finding, inspecting, and
driving running GUI applications — built so a coding agent (Claude Code or
otherwise) can locate a window, screenshot it, read its UI structure, and
click/type into it without a human at the keyboard.

This is the Linux counterpart to
[`uictl-mac-mcp`](https://github.com/ByronJones-Elsevier/uictl-mac-mcp) (macOS)
and [`uictl-win-mcp`](https://github.com/ByronJones-Elsevier/uictl-win-mcp)
(Windows). All three commit to the **same CLI verbs and MCP tool names** where
the platform allows it, so an agent's workflow doesn't change based on which
OS it's driving. The exact, binding contract is `MCP_INTERFACE.md` — read
that before implementing or extending anything here. Where GNOME/Wayland
genuinely can't support something the other two platforms do (or needs
something they don't), that's called out explicitly in the contract rather
than silently diverging.

## Status

**Under active development — not yet feature-complete.** See
`ENGINEERING.md` for the phased build plan and current progress, and the
"Environment constraints" section below before assuming any given capability
works on your setup.

## What it does (target — see Status)

- **Find & activate** — list running processes and their windows, bring one
  to the front.
- **Screenshot** — capture a window or a whole display, optionally with a
  numbered "set-of-marks" overlay on every accessible element so a
  vision-capable model can say "click element 7" instead of guessing pixel
  coordinates.
- **Inspect** — walk a window's accessibility tree (AT-SPI2): every
  element's role, name, value, and on-screen bounding rectangle.
- **Act** — click (by coordinate or by element id), move, scroll, type
  (direct value-set or synthesized input), send key combos.
- **Wait** — block until an element matching a role/title appears.
- **Read** — OCR a window or image region, sample a pixel's color,
  read/write the clipboard.
- **MCP server** — every capability above also exposed as an MCP tool over
  stdio, so an MCP-aware client can call `uictl_screenshot`, `uictl_click`,
  etc. directly instead of shelling out and parsing JSON.

Every CLI command prints one JSON object to stdout and exits `0`/non-zero on
success/failure, identical contract to the other two platforms.

## Build

Requires the .NET 10 SDK on Ubuntu 26.04 (GNOME). From this directory:

```sh
dotnet build -c Release
```

The binary lands at `src/UICtl.Cli/bin/Release/net10.0/uictl`. Either
invoke it by that full path, or put it on `PATH`.

## Environment constraints — read this before filing a "bug"

Linux/GNOME is not just a third coat of paint over the same primitives macOS
and Windows expose — several things are structurally different:

- **Wayland (GNOME/Mutter) has no public API for an outside process to list,
  activate, or move arbitrary windows** — deliberate, for app isolation.
  `windows`/`activate`/`focus *` on a pure-Wayland GNOME session require the
  companion GNOME Shell extension in `gnome-extension/` to be installed and
  enabled (see that directory's README) — without it, those calls only see
  X11/XWayland windows.
- **Screenshot and remote-input on Wayland go through `xdg-desktop-portal`
  consent dialogs or the kernel**, not a silent OS grant. `screenshot` uses
  the portal (one-time session reuse where possible, not a dialog per call).
  Input synthesis (`click`/`move`/`scroll`/`type`/`key`) uses a virtual
  `/dev/uinput` device instead, which needs a one-time setup step — see
  `uictl permissions` and the Input setup section below.
- **`pixel` is more expensive on Wayland than on macOS/Windows** — there's no
  cheap single-pixel read primitive; it's sampled from a full captured frame.
- **OCR confidence is real here** (Tesseract reports per-word confidence),
  unlike Windows (`Windows.Media.Ocr` reports none) — closer to macOS's
  Vision output.
- Run `uictl permissions` to see your session's actual capabilities
  (`sessionType`, `inputMethod`, `uinputWritable`, `atspiEnabled`,
  `shellExtensionConnected`) before assuming a given command will work.

### Input setup (uinput)

Input synthesis needs write access to `/dev/uinput`. Group membership alone
is not sufficient on a stock Ubuntu 26.04 install — confirmed live during
development: `/dev/uinput` ships `root:root` mode `0600` with no group grant
at all, so without the udev rule below, `usermod -aG input` alone still
leaves `uictl permissions`' `uinputWritable` field `false`.

```sh
sudo usermod -aG input $USER

echo 'KERNEL=="uinput", GROUP="input", MODE="0660"' | sudo tee /etc/udev/rules.d/99-uinput.rules
sudo udevadm control --reload-rules
sudo udevadm trigger --name-match=/dev/uinput   # or reboot if that doesn't pick it up

# then start a fresh login session so your shell's group membership updates
```

Check `uictl permissions`' `uinputWritable` field to confirm before assuming
click/type/scroll/key will work.

### Security note

`/dev/uinput` write access — same as the AT-SPI accessibility bus — lets
any process running as your user synthesize arbitrary keyboard and mouse
input system-wide, not just into windows this tool launched. That's the
whole point (it's what makes an agent able to drive a real GUI), but it
means the trust boundary is "anything that can talk to the daemon's Unix
socket (`~/.uictl/uictl.sock`, user-only permissions) can act as you at the
keyboard/mouse." Don't run the daemon, or grant `/dev/uinput`/AT-SPI
access, on a machine or account you don't trust the caller on. The udev
rule above (`GROUP="input", MODE="0660"`) is scoped to the `input` group
specifically for this reason — it doesn't open `/dev/uinput` to every user
on the box.

## Architecture in one paragraph

Same shape as macOS and Windows: a thin CLI/MCP front end over a small
background daemon, talking over a Unix domain socket
(`~/.uictl/uictl.sock`, no named-pipe translation needed like Windows). The
first command you run auto-spawns the daemon; every subsequent command — CLI
or MCP — is a client that sends one JSON request and gets one JSON response
back. See `ENGINEERING.md` for the details, including the `IWindowBackend`
split between X11 (direct Xlib/EWMH) and Wayland/GNOME (the companion Shell
extension over D-Bus).

## CLI quick reference

Same verbs as macOS/Windows where supported — see `MCP_INTERFACE.md` for the
full table and per-platform notes:

```sh
uictl apps                                   # list running processes
uictl displays                               # list monitors + scale
uictl windows --app gedit                    # list an app's windows
uictl activate --app gedit                   # bring it to front
uictl focus hold --app gedit                 # pin focus for a sequence of actions
uictl screenshot --app gedit --annotate      # numbered element overlay + legend
uictl elements --app gedit --role push-button
uictl click --element 12345-7                # click element 7 from that legend
uictl type --element 12345-9 "hello"         # type into a specific field
uictl key "ctrl+shift+esc"                   # keyboard shortcut
uictl wait-for --app gedit --title "Done"    # poll for an element
uictl ocr --app gedit                        # read on-screen text
uictl pixel --at 100,200                     # sample a pixel's color
uictl clipboard get / set "text"
uictl log show                               # open the live activity log window
uictl log export                             # export it as JSON
uictl feedback create --category issue --title "..." --body "..."
uictl feedback submit <id>                   # checks for duplicates, then opens a pre-filled GitHub issue
uictl daemon status / stop
```

Run `uictl --help` (also `-h`, `-H`, `--HELP`, `-?`) for full option lists.
HTML help (`docs/help.html`) is also included; no man page is planned unless
it turns out to be worth the upkeep alongside the other two platforms.

## Using it as an MCP server

`uictl mcp` runs as an MCP server over stdio — no separate flags or config
file of its own.

**Claude Code:**

```sh
claude mcp add uictl -- /path/to/uictl mcp
```

**Claude Desktop / other MCP clients** that take a JSON config:

```json
{
  "mcpServers": {
    "uictl": {
      "command": "/path/to/uictl",
      "args": ["mcp"]
    }
  }
}
```

Registers the same `uictl_*` tools as the macOS/Windows implementations,
minus anything genuinely not supported on this platform — see
`MCP_INTERFACE.md`.

## Recommended agent workflow

1. `apps` / `windows --app <name>` to find the target window.
2. `activate --app <name>` to bring it to front.
3. `screenshot --app <name> --annotate` — get an image plus a legend mapping
   numbers to element ids/roles/titles.
4. `click --element <id>` / `type --element <id> "..."` to act.
5. Re-run `elements` or `screenshot --annotate` to confirm the result, or
   `wait-for` if the UI update isn't instant.

See `AGENTS.md` for a more detailed walkthrough and Linux/GNOME-specific
gotchas.

## See also

- `AGENTS.md` — the agent playbook.
- `ENGINEERING.md` — architecture, phased build plan, and implementation
  notes.
- `MCP_INTERFACE.md` — the exact, binding tool/parameter contract shared
  with the macOS and Windows implementations.
- `gnome-extension/` — the companion GNOME Shell extension needed for
  window management on Wayland.
