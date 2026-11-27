#!/usr/bin/env bash
set -euo pipefail

# One-click native Linux installer for to-do-list (GTK 3 + WebKit2GTK).
# Supports running locally from a cloned repo or remotely via:
#   curl -fsSL https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main/linux/install.sh | bash

REPO_BASE="${REPO_BASE:-https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main}"
XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
INSTALL_DIR="${INSTALL_DIR:-$XDG_DATA_HOME/to-do-list}"
BIN_DIR="${BIN_DIR:-$HOME/.local/bin}"
BIN_PATH="$BIN_DIR/to-do-list"
ICON_PATH="$XDG_DATA_HOME/icons/hicolor/256x256/apps/to-do-list.png"
APP_DESKTOP="$XDG_DATA_HOME/applications/to-do-list.desktop"
AUTOSTART_DESKTOP="$XDG_CONFIG_HOME/autostart/to-do-list.desktop"
NO_AUTOSTART="${NO_AUTOSTART:-0}"

step() { printf '\033[36m[to-do-list]\033[0m %s\n' "$1"; }
warn() { printf '\033[33m[to-do-list]\033[0m %s\n' "$1"; }
err()  { printf '\033[31m[to-do-list]\033[0m %s\n' "$1" >&2; }

if ! command -v python3 >/dev/null 2>&1; then
  err "Error: python3 is required but not found."
  exit 1
fi

step "Checking GTK 3 + WebKit2GTK runtime dependencies..."
if ! python3 - <<'PY'
import sys
try:
    import gi
    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    ok = False
    for ver in ("4.1", "4.0"):
        try:
            gi.require_version("WebKit2", ver)
            ok = True
            break
        except ValueError:
            pass
    if not ok:
        sys.exit(2)
    from gi.repository import Gtk, WebKit2  # noqa: F401
    import cairo  # noqa: F401
except Exception:
    sys.exit(1)
PY
then
  err "Missing GTK 3 / WebKit2GTK Python bindings."
  echo "Please install them first using your distribution's package manager:"
  echo "  Ubuntu / Debian / Mint: sudo apt install -y python3-gi python3-gi-cairo gir1.2-gtk-3.0 gir1.2-webkit2-4.1"
  echo "    (On older Debian/Ubuntu 20.04/22.04 use gir1.2-webkit2-4.0 instead)"
  echo "  Fedora / RHEL:          sudo dnf install -y python3-gobject python3-cairo gtk3 webkit2gtk4.1"
  echo "  Arch / Manjaro:         sudo pacman -S --needed python-gobject python-cairo gtk3 webkit2gtk-4.1"
  echo "  openSUSE:               sudo zypper install -y python3-gobject python3-gobject-cairo typelib-1_0-Gtk-3_0 typelib-1_0-WebKit2-4_1"
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
if [[ -n "$SCRIPT_DIR" && -f "$SCRIPT_DIR/desktop_note.py" && -f "$SCRIPT_DIR/../index.html" ]]; then
  LOCAL_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
  step "Using local repository sources from $LOCAL_ROOT"
  cp "$LOCAL_ROOT/linux/desktop_note.py" "$STAGE/desktop_note.py"
  for f in index.html fusion-pixel-sc.woff2 fusion-pixel-license.txt manifest.webmanifest icon-192.png icon-256.png; do
    [[ -f "$LOCAL_ROOT/$f" ]] && cp "$LOCAL_ROOT/$f" "$STAGE/app/$f"
  done
else
  step "Downloading latest sources from GitHub ($REPO_BASE)..."
  curl -fsSL "$REPO_BASE/linux/desktop_note.py" -o "$STAGE/desktop_note.py"
  for f in index.html fusion-pixel-sc.woff2 fusion-pixel-license.txt manifest.webmanifest icon-192.png icon-256.png; do
    curl -fsSL "$REPO_BASE/$f" -o "$STAGE/app/$f"
  done
fi

# Stop existing instance gracefully if running
if [[ -x "$BIN_PATH" ]]; then
  step "Stopping existing to-do-list instance..."
  "$BIN_PATH" --shutdown || true
  sleep 0.3
fi

step "Installing files to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR/app" "$BIN_DIR" "$(dirname "$ICON_PATH")" "$(dirname "$APP_DESKTOP")"
cp "$STAGE/desktop_note.py" "$INSTALL_DIR/desktop_note.py"
chmod +x "$INSTALL_DIR/desktop_note.py"
cp -R "$STAGE/app/." "$INSTALL_DIR/app/"

if [[ -f "$INSTALL_DIR/app/icon-256.png" ]]; then
  cp "$INSTALL_DIR/app/icon-256.png" "$ICON_PATH"
fi

cat > "$BIN_PATH" <<EOF
#!/usr/bin/env bash
export TODO_LIST_APP_DIR="$INSTALL_DIR/app"
exec python3 "$INSTALL_DIR/desktop_note.py" "\$@"
EOF
chmod +x "$BIN_PATH"

cat > "$APP_DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=to-do-list
GenericName=Desktop Sticky Note
Comment=Minimalist Cream-Pixel Desktop Sticky Note
Exec=$BIN_PATH --toggle
Icon=$ICON_PATH
Terminal=false
Categories=Utility;Office;
StartupNotify=false
EOF

if [[ "$NO_AUTOSTART" != "1" ]]; then
  mkdir -p "$(dirname "$AUTOSTART_DESKTOP")"
  cat > "$AUTOSTART_DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=to-do-list
Comment=Minimalist Cream-Pixel Desktop Sticky Note
Exec=$BIN_PATH
Icon=$ICON_PATH
Terminal=false
X-GNOME-Autostart-enabled=true
StartupNotify=false
EOF
  step "Registered XDG autostart ($AUTOSTART_DESKTOP)."
fi

if [[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]]; then
  nohup "$BIN_PATH" >/dev/null 2>&1 &
  disown || true
  if [[ "${XDG_SESSION_TYPE:-}" == "wayland" || -n "${WAYLAND_DISPLAY:-}" ]]; then
    step "Installed and started (Wayland mode)!"
    warn "Tip: Bind '$BIN_PATH --toggle' to a desktop shortcut (e.g. Super+T) for instant show/hide on Wayland."
  else
    step "Installed and started! Move your cursor to the top-left corner of your screen to peek."
  fi
else
  step "Installed! Run '$BIN_PATH' from your desktop session to start."
fi
