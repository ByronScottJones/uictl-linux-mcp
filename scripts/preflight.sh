#!/usr/bin/env bash
# Preflight check/install for uictl-linux-mcp. Idempotent. Writes
# ~/.uictl/preflight.json so the daemon/MCP server can tell at startup
# whether this has ever been run and whether it fully succeeded - read by
# `uictl permissions`' preflightReady/preflightAt/preflightManualSteps
# fields (see src/UICtl.Core/Permissions.cs, MCP_INTERFACE.md). Safe to run
# standalone or alongside scripts/setup-ubuntu.sh (narrated, human-facing
# walkthrough) - this one is meant to be re-run any time to self-heal and
# report current status, e.g. at the start of an agent session.
set -uo pipefail

STATE_DIR="$HOME/.uictl"
STATE_FILE="$STATE_DIR/preflight.json"
mkdir -p "$STATE_DIR"

declare -A CHECKS
MANUAL_STEPS=()

check() { # name, test-command
	local name="$1"
	shift
	if "$@" >/dev/null 2>&1; then CHECKS["$name"]=true; else CHECKS["$name"]=false; fi
}

apt_install() { sudo apt-get install -y "$@"; }

echo "==> apt update"
sudo apt-get update -y

echo "==> .NET 10 SDK"
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
	apt_install dotnet-sdk-10.0
fi
check dotnet bash -c "command -v dotnet && dotnet --list-sdks | grep -q '^10\.'"

echo "==> build tools / git / gh"
apt_install build-essential git gh
check buildTools command -v gcc
check git command -v git
check gh command -v gh

echo "==> AT-SPI2 + X11 runtime libs"
apt_install at-spi2-core libatspi2.0-0t64 libx11-6 libxtst6
check atspiPackage dpkg -s at-spi2-core
check libx11Package dpkg -s libx11-6

echo "==> clipboard tools"
apt_install wl-clipboard xclip
check clipboardTools bash -c "command -v wl-copy && command -v xclip"

echo "==> Tesseract OCR"
apt_install tesseract-ocr tesseract-ocr-eng
check tesseract command -v tesseract

echo "==> xdg-utils (feedback submit's browser-open)"
apt_install xdg-utils
check xdgOpen command -v xdg-open

echo "==> GTK4 / libadwaita (uictl-gui's toast + log show window)"
apt_install libgtk-4-1 libadwaita-1-0
check gtk4Runtime dpkg -s libgtk-4-1
check libadwaitaRuntime dpkg -s libadwaita-1-0

echo "==> 'input' group membership"
if id -nG "$USER" | grep -qw input; then
	check inputGroup true
else
	sudo usermod -aG input "$USER"
	check inputGroup false # true only after a fresh login re-reads the group
	MANUAL_STEPS+=("log out and back in (or reboot) so your session picks up the new 'input' group membership")
fi

echo "==> /dev/uinput udev grant"
if [ -f /etc/udev/rules.d/99-uinput.rules ]; then
	check uinputUdevRule true
else
	echo 'KERNEL=="uinput", GROUP="input", MODE="0660"' | sudo tee /etc/udev/rules.d/99-uinput.rules >/dev/null
	sudo udevadm control --reload-rules
	sudo udevadm trigger --name-match=/dev/uinput || true
	check uinputUdevRule true
fi
UINPUT_PERMS="$(stat -c '%a %G' /dev/uinput 2>/dev/null || echo 'missing')"
if [ "$UINPUT_PERMS" != "660 input" ]; then
	MANUAL_STEPS+=("/dev/uinput is not group-writable yet (got: $UINPUT_PERMS) - a reboot may be needed for the udev rule to apply")
fi

echo "==> AT-SPI bus reachable"
check atspiBus bash -c "[ -n \"${DBUS_SESSION_BUS_ADDRESS:-}\" ]"

ALL_OK=true
for k in "${!CHECKS[@]}"; do [ "${CHECKS[$k]}" = false ] && ALL_OK=false; done
[ "${#MANUAL_STEPS[@]}" -gt 0 ] && ALL_OK=false

# Emit JSON by hand (no jq dependency assumed)
{
	echo "{"
	echo "  \"version\": 1,"
	echo "  \"ranAt\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\","
	echo "  \"ready\": $ALL_OK,"
	echo "  \"checks\": {"
	first=true
	for k in "${!CHECKS[@]}"; do
		$first || echo ","
		first=false
		printf "    \"%s\": %s" "$k" "${CHECKS[$k]}"
	done
	echo ""
	echo "  },"
	echo "  \"manualStepsRemaining\": ["
	for i in "${!MANUAL_STEPS[@]}"; do
		[ "$i" -gt 0 ] && echo ","
		printf "    \"%s\"" "${MANUAL_STEPS[$i]}"
	done
	echo ""
	echo "  ]"
	echo "}"
} >"$STATE_FILE"

echo "==> wrote $STATE_FILE"
if $ALL_OK; then
	echo "==> preflight PASSED - ready to build/run uictl"
else
	echo "==> preflight completed with manual steps remaining:"
	printf '    - %s\n' "${MANUAL_STEPS[@]}"
fi
