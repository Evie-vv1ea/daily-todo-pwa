#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${INSTALL_DIR:-$HOME/Applications/to-do-list.app}"
PLIST_PATH="$HOME/Library/LaunchAgents/com.evie.to-do-list.plist"

if [[ -x "$APP_DIR/Contents/MacOS/to-do-list" ]]; then
  "$APP_DIR/Contents/MacOS/to-do-list" --shutdown || true
  sleep 0.4
fi

if [[ -f "$PLIST_PATH" ]]; then
  launchctl unload "$PLIST_PATH" >/dev/null 2>&1 || true
  rm -f "$PLIST_PATH"
fi

rm -rf "$APP_DIR"
echo "[to-do-list] Uninstalled app and LaunchAgent. Local WebKit storage is preserved."
