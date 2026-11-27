#!/usr/bin/env bash
set -euo pipefail

# Uninstaller for to-do-list on Linux.
# Preserves local WebKit storage by default; pass --remove-data to delete everything.

REMOVE_DATA=0
for arg in "$@"; do
  case "$arg" in
    --remove-data|-RemoveData) REMOVE_DATA=1 ;;
  esac
done

XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
INSTALL_DIR="${INSTALL_DIR:-$XDG_DATA_HOME/to-do-list}"
BIN_PATH="${BIN_DIR:-$HOME/.local/bin}/to-do-list"
ICON_PATH="$XDG_DATA_HOME/icons/hicolor/256x256/apps/to-do-list.png"
APP_DESKTOP="$XDG_DATA_HOME/applications/to-do-list.desktop"
AUTOSTART_DESKTOP="$XDG_CONFIG_HOME/autostart/to-do-list.desktop"

if [[ -x "$BIN_PATH" ]]; then
  "$BIN_PATH" --shutdown || true
  sleep 0.3
fi

rm -f "$BIN_PATH" "$ICON_PATH" "$APP_DESKTOP" "$AUTOSTART_DESKTOP"

if [[ "$REMOVE_DATA" == "1" ]]; then
  rm -rf "$INSTALL_DIR"
  echo "[to-do-list] Uninstalled app, autostart entry, and all local data."
else
  rm -rf "$INSTALL_DIR/app" "$INSTALL_DIR/desktop_note.py"
  echo "[to-do-list] Uninstalled app and autostart entry. Local data in $INSTALL_DIR/webkit-data is preserved."
fi
