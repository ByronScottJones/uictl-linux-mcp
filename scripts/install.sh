#!/usr/bin/env bash
# Installs a published build: copies <publish-dir> to <prefix>/lib/uictl and
# symlinks each executable into <prefix>/bin. The helpers (uictl-gui,
# uictl-screencapture) are looked up on $PATH by the daemon, so they get
# symlinks alongside uictl itself.
# Usage: scripts/install.sh <publish-dir> <prefix>
set -euo pipefail

if [ "$#" -ne 2 ]; then
	echo "usage: install.sh <publish-dir> <prefix>" >&2
	exit 2
fi

publish_dir="$1"
prefix="$2"
lib_dir="$prefix/lib/uictl"
bin_dir="$prefix/bin"

mkdir -p "$lib_dir" "$bin_dir"
cp -R "$publish_dir"/. "$lib_dir"/

for exe in uictl uictl-gui uictl-screencapture; do
	if [ ! -x "$lib_dir/$exe" ]; then
		echo "install.sh: $lib_dir/$exe is missing - did 'make publish' succeed?" >&2
		exit 1
	fi
	ln -sf "$lib_dir/$exe" "$bin_dir/$exe"
	echo "Installed: $bin_dir/$exe -> $lib_dir/$exe"
done

case ":$PATH:" in
	*":$bin_dir:"*) ;;
	*) echo "Note: $bin_dir is not on your PATH - add it to run uictl directly." ;;
esac
