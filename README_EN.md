<div align="center">

<img src="./assets/logo.png" alt="to-do-list logo" width="76" height="76" />

# to-do-list

**Minimalist Cream-Pixel Ins Desktop Sticky Note · Zero Electron Bloat · 60fps Top-Left Corner Peek**

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=flat-square&logo=windows&logoColor=white)](#windows-10--11)
[![macOS](https://img.shields.io/badge/macOS-12%2B%20Native%20Swift-000000?style=flat-square&logo=apple&logoColor=white)](#macos-12)
[![Linux](https://img.shields.io/badge/Linux-GTK3%20%2B%20WebKit2-FCC624?style=flat-square&logo=linux&logoColor=black)](#linux-x11--wayland)
[![PWA](https://img.shields.io/badge/Web%20PWA-Live%20Demo-B86B82?style=flat-square&logo=googlechrome&logoColor=white)](https://evie-vv1ea.github.io/daily-todo-pwa/)
[![Zero Electron](https://img.shields.io/badge/Native%20Shell-~84%20KB-5A8560?style=flat-square)](#architecture)
[![License](https://img.shields.io/badge/License-MIT-7E64A8?style=flat-square)](./LICENSE)

[简体中文](./README.md) · **English** · [Live PWA Demo](https://evie-vv1ea.github.io/daily-todo-pwa/)

<br />

<img src="./assets/hero-banner.png" alt="to-do-list desktop sticky note showcase" width="100%" />

</div>

---

## ✨ Highlights

- 🎨 **Cream-Pixel Ins Aesthetic** — Native 12px [Fusion Pixel SC](https://github.com/TakWolf/fusion-pixel-font) bitmap typography, 6 curated low-saturation cream palettes, crisp 1px pixel frames, dotted memo ruling, tiny `7×7px` pixel checkboxes (`5×5px` checkmarks), and 3 translucency levels (`80% / 88% / 95%`).
- 🖱️ **60fps Top-Left Corner Peek** — Stays completely invisible in the background (zero desktop icon, zero taskbar/Dock clutter). Glide your pointer to the top-left corner for a smooth 60fps ease-out slide & fade; move away to dismiss, with seamless mid-animation reversal.
- 🧩 **3-Level Non-Covering Side Flyouts** — `Sticky Note (L1) → Themed Context Menu (L2) → Side Pixel Calendar / Multiline Note Editor (L3)`. Browse months, reschedule tasks, or jot down notes right beside the menu without ever covering or resizing the sticky note.
- ✍️ **Direct Inline Editing & Auto-Grow** — Shows 2 minimal blank rows when empty and grows row-by-row up to 12 rows as you press `Enter`. Click any task title to edit inline with instant auto-save, navigate rows via `↑ / ↓`, or press `Backspace` on an empty task to delete it.
- 🧰 **Built-in Micro-Tools** — Right-click access to a daily auto-clearing Clipboard (resets at 20:00), Markup Calculator (`+30`), and USD Converter (`×1.3405`) with optional live clipboard watching.
- ⚡ **Zero Electron & 100% Local-First** — Native C# WinForms + Edge WebView2 on Windows (**~84 KB** executable), native Swift AppKit + WKWebView on macOS, and native GTK 3 + WebKit2GTK on Linux (**~54 KB** single script). No accounts, no cloud sync, no telemetry.

---

## 🚀 Quick Start

### Windows 10 / 11

No prerequisites required — builds directly with Windows' built-in `.NET Framework csc.exe`, fetches Microsoft's official WebView2 bindings, registers auto-start on login, and preserves existing local data.

```powershell
# One-line install
irm https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main/windows/install.ps1 | iex

# Or install from a local clone
git clone https://github.com/Evie-vv1ea/daily-todo-pwa.git
cd daily-todo-pwa
powershell -ExecutionPolicy Bypass -File .\windows\install.ps1

# Uninstall (keeps local tasks by default; pass -RemoveData to delete all data)
powershell -ExecutionPolicy Bypass -File .\windows\uninstall.ps1
```

### macOS 12+

Compiles a native Swift AppKit + WKWebView `.app` bundle (`LSUIElement=true`, no Dock icon) and sets up a `LaunchAgent` for login auto-start. Requires Apple Command Line Tools (`xcode-select --install`).

```bash
# One-line install
curl -fsSL https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main/macos/install.sh | bash

# Or install from a local clone
git clone https://github.com/Evie-vv1ea/daily-todo-pwa.git
cd daily-todo-pwa
bash ./macos/install.sh

# Uninstall
bash ./macos/uninstall.sh
```

### Linux (X11 / Wayland)

Native GTK 3 + WebKit2GTK shell (automatically supports both `WebKit2 4.1` and `4.0`, zero Electron overhead, no compiler needed), installs XDG app launcher and `~/.config/autostart` login entry.
- **X11**: Native top-left hot-corner peek with 60fps slide & fade.
- **Wayland** (GNOME / KDE / Hyprland): Supports single-instance `to-do-list --toggle` IPC so you can bind it to any desktop shortcut (e.g. `Super+T`).

```bash
# Install runtime dependencies if not already present:
# Ubuntu / Debian / Mint (use gir1.2-webkit2-4.0 on older 20.04/22.04)
sudo apt install -y python3-gi python3-gi-cairo gir1.2-gtk-3.0 gir1.2-webkit2-4.1
# Fedora: sudo dnf install -y python3-gobject python3-cairo gtk3 webkit2gtk4.1
# Arch:   sudo pacman -S --needed python-gobject python-cairo gtk3 webkit2gtk-4.1

# One-line install
curl -fsSL https://raw.githubusercontent.com/Evie-vv1ea/daily-todo-pwa/main/linux/install.sh | bash

# Or install from a local clone
git clone https://github.com/Evie-vv1ea/daily-todo-pwa.git
cd daily-todo-pwa
bash ./linux/install.sh

# Uninstall (keeps local tasks by default; pass --remove-data to delete all data)
bash ./linux/uninstall.sh
```

### Web / PWA

Try it directly in your browser (installable as an offline PWA):

👉 **[https://evie-vv1ea.github.io/daily-todo-pwa/](https://evie-vv1ea.github.io/daily-todo-pwa/)**

---

## ⌨️ Interactions & Shortcuts

| Action | Behavior |
| :--- | :--- |
| **Hover top-left corner (120ms)** | Smoothly slides & fades in the sticky note; auto-hides 260ms after cursor leaves |
| **Type in blank row → `Enter`** | Creates a task, auto-grows the note by one row, and focuses the next blank line |
| **Click any task title** | Edits inline with instant auto-save; `Enter` moves to next row, `Esc` reverts |
| **`Backspace` on cleared task** | Deletes the task cleanly and moves focus to the previous row |
| **`↑` / `↓` arrow keys** | Navigate smoothly across all tasks and blank draft rows |
| **Right-click task / empty area** | Opens themed native menu with 3rd-level side calendar, note editor, themes, opacity, pin, clipboard & tools |

---

## 🏗️ Architecture

Windows (`windows/DesktopNote.cs`), macOS (`macos/DesktopNote.swift`), Linux (`linux/desktop_note.py`), and Web PWA all share the exact same single-file frontend ([index.html](./index.html)) via a lightweight string-message bridge (`window.chrome?.webview || window.desktopNote`), with built-in 250ms live hot-reload when `app/index.html` changes.

---

## 🙏 Credits & License

- Bundled pixel font: **[Fusion Pixel Font](https://github.com/TakWolf/fusion-pixel-font)** (12px Proportional SC), licensed under [SIL OFL 1.1](./fusion-pixel-license.txt).
- Source code licensed under the **[MIT License](./LICENSE)**.
