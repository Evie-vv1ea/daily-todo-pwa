#!/usr/bin/env bash
set -euo pipefail

# One-click native macOS builder & installer for to-do-list (Swift AppKit + WKWebView).
# Supports running locally from a cloned repo or remotely via:
#   curl -fsSL https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main/macos/install.sh | bash

REPO_BASE="${REPO_BASE:-https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main}"
APP_DIR="${INSTALL_DIR:-$HOME/Applications/to-do-list.app}"
PLIST_PATH="$HOME/Library/LaunchAgents/com.evie.to-do-list.plist"
NO_AUTOSTART="${NO_AUTOSTART:-0}"

step() { printf '\033[36m[to-do-list]\033[0m %s\n' "$1"; }

if ! command -v swiftc >/dev/null 2>&1; then
  echo "Error: Swift compiler (swiftc) not found."
  echo "Please install Apple Command Line Tools first: xcode-select --install"
  exit 1
fi

STAGE="$(mktemp -d)"
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

SCRIPT_DIR=""
if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fi

mkdir -p "$STAGE/app"
if [[ -n "$SCRIPT_DIR" && -f "$SCRIPT_DIR/DesktopNote.swift" && -f "$SCRIPT_DIR/../index.html" ]]; then
  LOCAL_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
  step "Using local repository sources from $LOCAL_ROOT"
  cp "$LOCAL_ROOT/macos/DesktopNote.swift" "$STAGE/DesktopNote.swift"
  cp "$LOCAL_ROOT/macos/Info.plist" "$STAGE/Info.plist"
  for f in index.html fusion-pixel-sc.woff2 fusion-pixel-license.txt manifest.webmanifest icon-192.png icon-256.png; do
    [[ -f "$LOCAL_ROOT/$f" ]] && cp "$LOCAL_ROOT/$f" "$STAGE/app/$f"
  done
else
  step "Downloading latest sources from GitHub ($REPO_BASE)..."
  curl -fsSL "$REPO_BASE/macos/DesktopNote.swift" -o "$STAGE/DesktopNote.swift"
  curl -fsSL "$REPO_BASE/macos/Info.plist" -o "$STAGE/Info.plist"
  for f in index.html fusion-pixel-sc.woff2 fusion-pixel-license.txt manifest.webmanifest icon-192.png icon-256.png; do
    curl -fsSL "$REPO_BASE/$f" -o "$STAGE/app/$f"
  done
fi

step "Compiling native macOS binary (Swift AppKit + WKWebView)..."
swiftc -O -framework Cocoa -framework WebKit "$STAGE/DesktopNote.swift" -o "$STAGE/to-do-list"

# Stop existing instance gracefully if installed
if [[ -x "$APP_DIR/Contents/MacOS/to-do-list" ]]; then
  step "Stopping existing to-do-list instance..."
  "$APP_DIR/Contents/MacOS/to-do-list" --shutdown || true
  sleep 0.4
fi

step "Installing bundle to $APP_DIR..."
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources/app"
cp "$STAGE/to-do-list" "$APP_DIR/Contents/MacOS/to-do-list"
chmod +x "$APP_DIR/Contents/MacOS/to-do-list"
cp "$STAGE/Info.plist" "$APP_DIR/Contents/Info.plist"
cp -R "$STAGE/app/." "$APP_DIR/Contents/Resources/app/"

if [[ "$NO_AUTOSTART" != "1" ]]; then
  mkdir -p "$(dirname "$PLIST_PATH")"
  cat > "$PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>com.evie.to-do-list</string>
  <key>ProgramArguments</key>
  <array>
    <string>$APP_DIR/Contents/MacOS/to-do-list</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <false/>
</dict>
</plist>
EOF
  launchctl unload "$PLIST_PATH" >/dev/null 2>&1 || true
  launchctl load "$PLIST_PATH" >/dev/null 2>&1 || true
  step "Registered macOS LaunchAgent autostart ($PLIST_PATH)."
fi

open -a "$APP_DIR"
step "Installed and started! Move your cursor to the top-left corner of your screen to peek."
