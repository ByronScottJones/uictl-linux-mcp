#!/usr/bin/env bash
# One-time dev-environment setup for uictl-linux-mcp on Ubuntu 26.04 (GNOME).
# Idempotent — safe to re-run.
set -euo pipefail

echo "==> Updating apt package lists"
sudo apt-get update

echo "==> Installing .NET 10 SDK (build/run/test)"
# Ubuntu 26.04's own repos carry dotnet-sdk-10.0 directly — no Microsoft
# package feed needed. If you need a newer patch than apt has, use
# https://dot.net/install.sh instead.
sudo apt-get install -y dotnet-sdk-10.0

echo "==> Installing build tools"
sudo apt-get install -y build-essential git

echo "==> Installing GitHub CLI (used by 'uictl feedback submit')"
sudo apt-get install -y gh

echo "==> Installing AT-SPI2 + X11 runtime libs (accessibility tree, X11 backend)"
# Present by default on a real GNOME desktop session; explicit here so a
# minimal/headless Ubuntu box (VM, container) still has them.
sudo apt-get install -y at-spi2-core libatspi2.0-0t64 libx11-6 libxtst6

echo "==> Installing clipboard tools (uictl clipboard get/set)"
sudo apt-get install -y wl-clipboard xclip

echo "==> Installing Tesseract OCR (uictl ocr)"
sudo apt-get install -y tesseract-ocr tesseract-ocr-eng

echo "==> (Optional) Installing gnome-screenshot for visual verification during development"
# Not a uictl runtime dependency - useful for a human or agent to visually
# confirm click/type targeting landed where expected, until 'uictl
# screenshot' (Phase 4) lands.
sudo apt-get install -y gnome-screenshot

echo "==> Adding \$USER to the 'input' group (needed for /dev/uinput write access)"
if id -nG "$USER" | grep -qw input; then
  echo "    already in 'input' group"
else
  sudo usermod -aG input "$USER"
  echo "    added — you must log out and back in (a fresh session) for this to take effect"
fi

echo
echo "==> Done. Verify with:"
echo "    dotnet --version"
echo "    dotnet build -c Release   # from the repo root"
echo "    uictl permissions         # after building, checks uinputWritable/atspiEnabled/etc."
echo
echo "Note: gnome-extensions CLI and gnome-shell itself are part of the GNOME"
echo "desktop session and aren't installed by this script — this must be run"
echo "on/from a real GNOME desktop (or VM), not a headless box, for the"
echo "windowing/screenshot/input features to work at all."
