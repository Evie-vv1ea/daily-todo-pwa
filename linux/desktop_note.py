#!/usr/bin/env python3
"""
to-do-list — Native Linux Desktop Sticky Note Shell (GTK 3 + WebKit2GTK)

Mirrors the Windows (WinForms + WebView2) and macOS (Swift AppKit + WKWebView)
native shells with zero Electron overhead:
- Custom URI scheme (to-do-list://local/index.html) for stable localStorage origin
- Top-left hot-corner hover detection + 60fps cubic ease-out slide/fade animation
- Wayland-friendly fallback & single-instance IPC (--toggle / --show / --shutdown)
- Themed native GTK right-click menu (Level 2) + 3rd-level side flyouts:
  * PixelCalendarWindow (216x196 Cairo pixel month calendar with pending dots)
  * PixelNoteWindow (216x112 live auto-saving multiline note editor)
- Live 250ms debounced HTML hot-reload via Gio.FileMonitor
"""

from __future__ import annotations

import base64
import datetime
import json
import os
import socket
import sys
import threading
import time
from pathlib import Path
from typing import Callable, Optional, Set, Tuple

# ---------------------------------------------------------------------------
# Dependency check (GTK 3 + WebKit2GTK 4.1 or 4.0)
# ---------------------------------------------------------------------------

def _load_gtk_webkit():
    try:
        import gi  # type: ignore
    except ImportError:
        sys.stderr.write(
            "[to-do-list] Missing PyGObject (python3-gi).\n"
            "Install dependencies for your distro:\n"
            "  Ubuntu/Debian: sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-webkit2-4.1\n"
            "  Fedora:        sudo dnf install python3-gobject gtk3 webkit2gtk4.1\n"
            "  Arch:          sudo pacman -S python-gobject gtk3 webkit2gtk-4.1\n"
        )
        sys.exit(1)

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")

    webkit_version = None
    for ver in ("4.1", "4.0"):
        try:
            gi.require_version("WebKit2", ver)
            webkit_version = ver
            break
        except ValueError:
            continue

    if webkit_version is None:
        sys.stderr.write(
            "[to-do-list] Missing WebKit2GTK GIR typelib (4.1 or 4.0).\n"
            "Install dependencies for your distro:\n"
            "  Ubuntu/Debian: sudo apt install gir1.2-webkit2-4.1  (or gir1.2-webkit2-4.0)\n"
            "  Fedora:        sudo dnf install webkit2gtk4.1\n"
            "  Arch:          sudo pacman -S webkit2gtk-4.1\n"
        )
        sys.exit(1)

    from gi.repository import Gdk, Gio, GLib, Gtk, Pango, PangoCairo, WebKit2  # type: ignore
    import cairo  # type: ignore

    return Gdk, Gio, GLib, Gtk, Pango, PangoCairo, WebKit2, cairo


# ---------------------------------------------------------------------------
# IPC Socket for single-instance control (--shutdown / --toggle / --show)
# ---------------------------------------------------------------------------

def _socket_path() -> str:
    runtime = os.environ.get("XDG_RUNTIME_DIR")
    if runtime and os.path.isdir(runtime):
        return os.path.join(runtime, "to-do-list.sock")
    return f"/tmp/to-do-list-{os.getuid()}.sock"


def _send_ipc(command: str) -> bool:
    sock_path = _socket_path()
    if not os.path.exists(sock_path):
        return False
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(0.8)
            client.connect(sock_path)
            client.sendall(command.encode("utf-8"))
        return True
    except OSError:
        return False


# ---------------------------------------------------------------------------
# Color & Palette Helpers
# ---------------------------------------------------------------------------

RGB = Tuple[int, int, int]


def parse_rgb(text: str) -> Optional[RGB]:
    parts = [p.strip() for p in text.split(",")]
    if len(parts) != 3:
        return None
    try:
        vals = tuple(int(p) for p in parts)
    except ValueError:
        return None
    if all(0 <= v <= 255 for v in vals):
        return vals  # type: ignore
    return None


def parse_hex(text: str) -> Optional[RGB]:
    s = text.strip()
    if len(s) != 7 or not s.startswith("#"):
        return None
    try:
        v = int(s[1:], 16)
    except ValueError:
        return None
    return ((v >> 16) & 255, (v >> 8) & 255, v & 255)


def blend(a: RGB, b: RGB, weight_b: float) -> RGB:
    w = max(0.0, min(1.0, weight_b))
    return (
        int(round(a[0] * (1.0 - w) + b[0] * w)),
        int(round(a[1] * (1.0 - w) + b[1] * w)),
        int(round(a[2] * (1.0 - w) + b[2] * w)),
    )


def to_hex(c: RGB) -> str:
    return f"#{c[0]:02x}{c[1]:02x}{c[2]:02x}"


def to_cairo(c: RGB) -> Tuple[float, float, float]:
    return (c[0] / 255.0, c[1] / 255.0, c[2] / 255.0)


class ThemePalette:
    def __init__(
        self,
        paper: RGB = (255, 240, 243),
        border: RGB = (223, 178, 190),
        ink: RGB = (82, 58, 66),
        accent: RGB = (184, 107, 130),
    ):
        self.paper = paper
        self.border = border
        self.ink = ink
        self.accent = accent

    @property
    def highlight(self) -> RGB:
        return blend(self.paper, self.accent, 0.2)

    @property
    def muted(self) -> RGB:
        return blend(self.ink, self.paper, 0.5)

    @property
    def input_bg(self) -> RGB:
        return blend(self.paper, (255, 255, 255), 0.2)


# ---------------------------------------------------------------------------
# Hot Corner Tracker & 60fps Cubic Ease-Out Motion
# ---------------------------------------------------------------------------

class HotCornerTracker:
    def __init__(self) -> None:
        self._entered_at: Optional[float] = None
        self._left_at: Optional[float] = None

    def should_show(
        self, cx: int, cy: int, sx: int, sy: int, edge: int, now: float
    ) -> bool:
        in_corner = (sx <= cx < sx + edge) and (sy <= cy < sy + edge)
        if not in_corner:
            self._entered_at = None
            return False
        if self._entered_at is None:
            self._entered_at = now
        return (now - self._entered_at) >= 0.12

    def should_hide(
        self,
        cx: int,
        cy: int,
        panel_rect: Tuple[int, int, int, int],
        sx: int,
        sy: int,
        edge: int,
        keep_open: bool,
        now: float,
    ) -> bool:
        px, py, pw, ph = panel_rect
        in_panel = (px <= cx < px + pw) and (py <= cy < py + ph)
        in_corner = (sx <= cx < sx + edge) and (sy <= cy < sy + edge)
        if keep_open or in_panel or in_corner:
            self._left_at = None
            return False
        if self._left_at is None:
            self._left_at = now
        return (now - self._left_at) >= 0.26

    def reset(self) -> None:
        self._entered_at = None
        self._left_at = None


class NoteMotion:
    def __init__(self) -> None:
        self.progress: float = 0.0
        self.target: float = 0.0
        self._start_progress: float = 0.0
        self._started_at: float = 0.0
        self._duration: float = 0.16

    def begin(self, target: float, now: float) -> None:
        self.target = target
        self._start_progress = self.progress
        self._started_at = now
        base = 0.16 if target > 0.5 else 0.18
        self._duration = max(0.016, base * abs(target - self.progress))

    def advance(self, now: float) -> bool:
        raw = min(1.0, max(0.0, (now - self._started_at) / self._duration))
        eased = 1.0 - pow(1.0 - raw, 3.0)
        self.progress = self._start_progress + (self.target - self._start_progress) * eased
        if raw >= 1.0:
            self.progress = self.target
            return True
        return False

    def snap(self, value: float) -> None:
        self.progress = value
        self.target = value


# ---------------------------------------------------------------------------
# MIME Helper
# ---------------------------------------------------------------------------

def mime_type_for(filename: str) -> str:
    ext = Path(filename).suffix.lower()
    return {
        ".html": "text/html; charset=utf-8",
        ".woff2": "font/woff2",
        ".png": "image/png",
        ".webmanifest": "application/manifest+json",
        ".json": "application/json",
        ".js": "application/javascript; charset=utf-8",
        ".css": "text/css; charset=utf-8",
        ".txt": "text/plain; charset=utf-8",
    }.get(ext, "application/octet-stream")


def parse_iso_date(text: str) -> Optional[datetime.date]:
    try:
        return datetime.date.fromisoformat(text.strip())
    except ValueError:
        return None


# ---------------------------------------------------------------------------
# Main Application (initialized lazily after GTK/WebKit2 import)
# ---------------------------------------------------------------------------

def run_app() -> int:
    # Handle IPC CLI commands before importing heavy GUI modules
    if "--shutdown" in sys.argv:
        _send_ipc("shutdown")
        return 0
    if "--toggle" in sys.argv:
        if _send_ipc("toggle"):
            return 0
    if "--show" in sys.argv:
        if _send_ipc("show"):
            return 0

    Gdk, Gio, GLib, Gtk, Pango, PangoCairo, WebKit2, cairo = _load_gtk_webkit()

    LOCAL_SCHEME = "to-do-list"
    LOCAL_PAGE_URL = "to-do-list://local/index.html"
    NOTE_WIDTH = 268
    WEEKDAYS = ("一", "二", "三", "四", "五", "六", "日")

    # -----------------------------------------------------------------------
    # 3rd-Level Side Flyout: Pixel Calendar Window (216 x 196)
    # -----------------------------------------------------------------------

    class PixelCalendarWindow(Gtk.Window):
        WIDTH = 216
        HEIGHT = 196

        def __init__(
            self,
            palette: ThemePalette,
            selected_iso: str,
            pending_dates: Set[str],
            on_select: Callable[[str], None],
            on_close: Callable[[], None],
        ) -> None:
            super().__init__(type=Gtk.WindowType.POPUP)
            self.palette = palette
            self.selected_date = parse_iso_date(selected_iso) or datetime.date.today()
            self.month_year = self.selected_date.year
            self.month_num = self.selected_date.month
            self.pending_dates = pending_dates
            self.on_select = on_select
            self.on_close = on_close
            self.hover_pt = (-1.0, -1.0)

            self.set_decorated(False)
            self.set_keep_above(True)
            self.set_skip_taskbar_hint(True)
            self.set_skip_pager_hint(True)
            self.set_size_request(self.WIDTH, self.HEIGHT)
            self.set_resizable(False)

            self.area = Gtk.DrawingArea()
            self.area.set_size_request(self.WIDTH, self.HEIGHT)
            self.area.add_events(
                Gdk.EventMask.POINTER_MOTION_MASK
                | Gdk.EventMask.LEAVE_NOTIFY_MASK
                | Gdk.EventMask.BUTTON_PRESS_MASK
                | Gdk.EventMask.BUTTON_RELEASE_MASK
            )
            self.area.connect("draw", self._on_draw)
            self.area.connect("motion-notify-event", self._on_motion)
            self.area.connect("leave-notify-event", self._on_leave)
            self.area.connect("button-release-event", self._on_click)
            self.add(self.area)

        def _prev_rect(self) -> Tuple[float, float, float, float]:
            return (4, 3, 23, 22)

        def _next_rect(self) -> Tuple[float, float, float, float]:
            return (self.WIDTH - 65, 3, 23, 22)

        def _today_rect(self) -> Tuple[float, float, float, float]:
            return (self.WIDTH - 39, 3, 35, 22)

        def _cell_rect(self, col: int, y: float, h: float) -> Tuple[float, float, float, float]:
            w = self.WIDTH - 8
            left = 4 + int(w * col / 7)
            right = 4 + int(w * (col + 1) / 7)
            return (left, y, right - left, h)

        def _day_rect(self, idx: int) -> Tuple[float, float, float, float]:
            return self._cell_rect(idx % 7, 46 + (idx // 7) * 24, 24)

        def _grid_start(self) -> datetime.date:
            first = datetime.date(self.month_year, self.month_num, 1)
            return first - datetime.timedelta(days=first.weekday())

        @staticmethod
        def _in_rect(pt: Tuple[float, float], rect: Tuple[float, float, float, float]) -> bool:
            x, y = pt
            rx, ry, rw, rh = rect
            return rx <= x < rx + rw and ry <= y < ry + rh

        def _draw_centered(
            self,
            cr,
            text: str,
            rect: Tuple[float, float, float, float],
            color: RGB,
        ) -> None:
            layout = PangoCairo.create_layout(cr)
            layout.set_font_description(Pango.FontDescription("Sans 9"))
            layout.set_text(text, -1)
            tw, th = layout.get_pixel_size()
            rx, ry, rw, rh = rect
            cr.set_source_rgb(*to_cairo(color))
            cr.move_to(rx + (rw - tw) // 2, ry + (rh - th) // 2)
            PangoCairo.show_layout(cr, layout)

        def _draw_box(
            self,
            cr,
            rect: Tuple[float, float, float, float],
            text: str,
            fg: RGB,
            active: bool,
            edge: Optional[RGB],
        ) -> None:
            rx, ry, rw, rh = rect
            if active or self._in_rect(self.hover_pt, rect):
                fill = self.palette.accent if active else self.palette.highlight
                cr.set_source_rgb(*to_cairo(fill))
                cr.rectangle(rx, ry, rw, rh)
                cr.fill()
            if edge is not None:
                cr.set_source_rgb(*to_cairo(edge))
                cr.set_line_width(1.0)
                cr.rectangle(rx + 0.5, ry + 0.5, rw - 1.0, rh - 1.0)
                cr.stroke()
            self._draw_centered(cr, text, rect, fg)

        def _on_draw(self, _widget, cr) -> bool:
            p = self.palette
            # Background & 1px pixel frame
            cr.set_source_rgb(*to_cairo(p.paper))
            cr.rectangle(0, 0, self.WIDTH, self.HEIGHT)
            cr.fill()

            cr.set_source_rgb(*to_cairo(p.border))
            cr.set_line_width(1.0)
            cr.rectangle(0.5, 0.5, self.WIDTH - 1.0, self.HEIGHT - 1.0)
            cr.stroke()

            prev_r = self._prev_rect()
            next_r = self._next_rect()
            today_r = self._today_rect()
            self._draw_box(cr, prev_r, "<", p.ink, False, None)
            self._draw_box(cr, next_r, ">", p.ink, False, None)
            self._draw_box(cr, today_r, "今天", p.ink, False, p.border)

            title = f"{self.month_year}年{self.month_num}月"
            heading = (prev_r[0] + prev_r[2], 0, next_r[0] - (prev_r[0] + prev_r[2]), 26)
            self._draw_centered(cr, title, heading, p.ink)

            for col in range(7):
                self._draw_centered(cr, WEEKDAYS[col], self._cell_rect(col, 26, 20), p.muted)

            first = self._grid_start()
            today = datetime.date.today()
            for idx in range(42):
                d = first + datetime.timedelta(days=idx)
                rx, ry, rw, rh = self._day_rect(idx)
                area = (rx + 1, ry + 1, rw - 2, rh - 2)
                is_selected = d == self.selected_date
                is_today = d == today
                in_month = d.month == self.month_num
                fg = p.paper if is_selected else (p.ink if in_month else p.muted)
                self._draw_box(cr, area, str(d.day), fg, is_selected, p.border if is_today else None)

                if d.isoformat() in self.pending_dates:
                    dot_color = p.paper if is_selected else p.accent
                    cr.set_source_rgb(*to_cairo(dot_color))
                    cr.rectangle(int(area[0] + area[2] / 2 - 1), area[1] + area[3] - 3, 2, 2)
                    cr.fill()
            return False

        def _on_motion(self, _widget, event) -> bool:
            self.hover_pt = (event.x, event.y)
            self.area.queue_draw()
            return False

        def _on_leave(self, _widget, _event) -> bool:
            self.hover_pt = (-1.0, -1.0)
            self.area.queue_draw()
            return False

        def _shift_month(self, delta: int) -> None:
            total = (self.month_year * 12 + (self.month_num - 1)) + delta
            self.month_year, zero_m = divmod(total, 12)
            self.month_num = zero_m + 1
            self.area.queue_draw()

        def _on_click(self, _widget, event) -> bool:
            if event.button != 1:
                return False
            pt = (event.x, event.y)
            if self._in_rect(pt, self._prev_rect()):
                self._shift_month(-1)
                return True
            if self._in_rect(pt, self._next_rect()):
                self._shift_month(1)
                return True
            if self._in_rect(pt, self._today_rect()):
                self.on_select(datetime.date.today().isoformat())
                return True
            first = self._grid_start()
            for idx in range(42):
                if self._in_rect(pt, self._day_rect(idx)):
                    d = first + datetime.timedelta(days=idx)
                    self.on_select(d.isoformat())
                    return True
            return False

    # -----------------------------------------------------------------------
    # 3rd-Level Side Flyout: Pixel Multiline Note Window (216 x 112)
    # -----------------------------------------------------------------------

    class PixelNoteWindow(Gtk.Window):
        WIDTH = 216
        HEIGHT = 112

        def __init__(
            self,
            palette: ThemePalette,
            note: str,
            on_save: Callable[[str], None],
            on_dismiss: Callable[[], None],
        ) -> None:
            super().__init__(type=Gtk.WindowType.TOPLEVEL)
            self.palette = palette
            self.on_save = on_save
            self.on_dismiss = on_dismiss
            self.last_saved = note.replace("\r\n", "\n")

            self.set_decorated(False)
            self.set_keep_above(True)
            self.set_skip_taskbar_hint(True)
            self.set_skip_pager_hint(True)
            self.set_type_hint(Gdk.WindowTypeHint.UTILITY)
            self.set_size_request(self.WIDTH, self.HEIGHT)
            self.set_resizable(False)
            self.get_style_context().add_class("pixel-note-flyout")

            box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
            box.set_margin_top(8)
            box.set_margin_bottom(8)
            box.set_margin_start(8)
            box.set_margin_end(8)

            scroll = Gtk.ScrolledWindow()
            scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
            scroll.get_style_context().add_class("pixel-note-scroll")

            self.text_view = Gtk.TextView()
            self.text_view.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
            self.text_view.set_left_margin(4)
            self.text_view.set_right_margin(4)
            self.text_view.set_top_margin(3)
            self.text_view.set_bottom_margin(3)
            self.text_view.get_style_context().add_class("pixel-note-input")

            self.buf = self.text_view.get_buffer()
            self.buf.set_text(self.last_saved)
            self.buf.connect("changed", self._on_text_changed)

            self.text_view.connect("key-press-event", self._on_key_press)
            self.connect("focus-out-event", self._on_focus_out)

            scroll.add(self.text_view)
            box.pack_start(scroll, True, True, 0)
            self.add(box)

        def focus_input(self) -> None:
            self.present()
            self.text_view.grab_focus()
            end_iter = self.buf.get_end_iter()
            self.buf.place_cursor(end_iter)

        def _current_text(self) -> str:
            start, end = self.buf.get_bounds()
            return self.buf.get_text(start, end, True).replace("\r\n", "\n")

        def _on_text_changed(self, _buf) -> None:
            text = self._current_text()
            if len(text) > 500:
                self.buf.set_text(text[:500])
                return
            self.flush()

        def flush(self) -> None:
            text = self._current_text()
            if text == self.last_saved:
                return
            self.last_saved = text
            self.on_save(text)

        def _on_key_press(self, _widget, event) -> bool:
            if event.keyval == Gdk.KEY_Escape:
                self.flush()
                self.on_dismiss()
                return True
            return False

        def _on_focus_out(self, _widget, _event) -> bool:
            self.flush()
            self.on_dismiss()
            return False

    # -----------------------------------------------------------------------
    # Main Desktop Sticky Note Controller
    # -----------------------------------------------------------------------

    class DesktopNoteApp:
        def __init__(self) -> None:
            self.app_dir = self._resolve_app_dir()
            self.data_dir = self._resolve_data_dir()
            self.is_wayland = (
                os.environ.get("XDG_SESSION_TYPE", "").lower() == "wayland"
                or os.environ.get("GDK_BACKEND", "").lower() == "wayland"
            )

            self.palette = ThemePalette()
            self.hot_corner = HotCornerTracker()
            self.motion = NoteMotion()

            self.preferred_height = 72
            self.target_opacity = 0.88
            self.ready = False
            self.keep_open = False
            self.pinned = False
            self.secondary_panel = False
            self.sheet_open = False
            self.menu_visible = False
            self.menu_grace_until = 0.0
            self.note_visible = False

            self.calendar_view_date = datetime.date.today().isoformat()
            self.calendar_task_date = ""
            self.calendar_dots: Set[str] = set()
            self.note_task_key = ""
            self.note_text = ""

            self.poll_timer_id: Optional[int] = None
            self.anim_timer_id: Optional[int] = None
            self.reload_timer_id: Optional[int] = None
            self.file_monitor = None

            self.active_menu: Optional[Gtk.Menu] = None
            self.active_flyout: Optional[Gtk.Window] = None
            self.active_note_flyout: Optional[PixelNoteWindow] = None

            self.css_provider = Gtk.CssProvider()
            screen = Gdk.Screen.get_default()
            if screen is not None:
                Gtk.StyleContext.add_provider_for_screen(
                    screen, self.css_provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
                )

            self.window = self._build_window()
            self.web_view = self._build_web_view()
            self.window.add(self.web_view)

            self._apply_theme_css()
            self._apply_panel_bounds()
            self._start_ipc_server()
            self._start_page_watcher()

            self.web_view.load_uri(LOCAL_PAGE_URL)

        def _resolve_app_dir(self) -> Path:
            env_dir = os.environ.get("TODO_LIST_APP_DIR")
            if env_dir and (Path(env_dir) / "index.html").is_file():
                return Path(env_dir)
            here = Path(__file__).resolve().parent
            for candidate in (here / "app", here.parent, Path.cwd() / "app", Path.cwd()):
                if (candidate / "index.html").is_file():
                    return candidate
            return here / "app"

        def _resolve_data_dir(self) -> Path:
            xdg_data = os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share"))
            d = Path(xdg_data) / "to-do-list" / "webkit-data"
            d.mkdir(parents=True, exist_ok=True)
            return d

        def _build_window(self) -> Gtk.Window:
            win = Gtk.Window(type=Gtk.WindowType.TOPLEVEL)
            win.set_title("to-do-list")
            win.set_decorated(False)
            win.set_keep_above(True)
            win.set_skip_taskbar_hint(True)
            win.set_skip_pager_hint(True)
            win.set_type_hint(Gdk.WindowTypeHint.UTILITY)
            win.set_accept_focus(True)
            win.set_resizable(False)
            win.set_app_paintable(True)
            win.stick()

            screen = win.get_screen()
            visual = screen.get_rgba_visual()
            if visual is not None and screen.is_composited():
                win.set_visual(visual)

            win.connect("focus-out-event", self._on_focus_out)
            win.connect("delete-event", lambda *_: (self.quit(), True)[1])
            return win

        def _build_web_view(self):
            data_mgr = WebKit2.WebsiteDataManager(
                base_data_directory=str(self.data_dir / "data"),
                base_cache_directory=str(self.data_dir / "cache"),
            )
            context = WebKit2.WebContext.new_with_website_data_manager(data_mgr)
            context.register_uri_scheme(LOCAL_SCHEME, self._on_scheme_request)

            ucm = WebKit2.UserContentManager()
            bridge_js = """
            (function () {
              const listeners = new Set();
              window.desktopNote = {
                postMessage(msg) {
                  window.webkit.messageHandlers.desktopNote.postMessage(String(msg));
                },
                addEventListener(type, fn) {
                  if (type === "message" && typeof fn === "function") listeners.add(fn);
                },
                removeEventListener(type, fn) {
                  if (type === "message") listeners.delete(fn);
                },
                _dispatch(data) {
                  for (const fn of listeners) {
                    try { fn({ data }); } catch (e) {}
                  }
                }
              };
            })();
            """
            ucm.add_script(
                WebKit2.UserScript(
                    bridge_js,
                    WebKit2.UserContentInjectedFrames.TOP_FRAME,
                    WebKit2.UserScriptInjectionTime.START,
                    None,
                    None,
                )
            )
            ucm.register_script_message_handler("desktopNote")
            ucm.connect("script-message-received::desktopNote", self._on_web_message)

            web = WebKit2.WebView(web_context=context, user_content_manager=ucm)

            settings = web.get_settings()
            settings.set_enable_javascript(True)
            settings.set_enable_html5_local_storage(True)
            settings.set_enable_developer_extras(False)
            settings.set_enable_smooth_scrolling(True)

            # Match initial cream paper background
            rgba = Gdk.RGBA()
            rgba.parse(to_hex(self.palette.paper))
            web.set_background_color(rgba)

            web.connect("context-menu", lambda *_: True)  # Disable default WebKit menu
            web.connect("decide-policy", self._on_decide_policy)
            web.connect("load-changed", self._on_load_changed)
            web.show()
            return web

        # -------------------------------------------------------------------
        # Custom Scheme Handler (to-do-list://local/...)
        # -------------------------------------------------------------------

        def _on_scheme_request(self, request) -> None:
            uri = request.get_uri() or ""
            path_part = uri.split("://", 1)[-1]
            # Strip host ("local")
            if "/" in path_part:
                rel = path_part.split("/", 1)[1]
            else:
                rel = ""
            rel = rel.split("?", 1)[0].split("#", 1)[0]
            file_name = Path(rel).name if rel else "index.html"
            target = self.app_dir / file_name

            if not target.is_file():
                request.finish_error(GLib.Error(f"Asset not found: {file_name}"))
                return

            data = target.read_bytes()
            stream = Gio.MemoryInputStream.new_from_bytes(GLib.Bytes.new(data))
            request.finish(stream, len(data), mime_type_for(file_name))

        def _on_decide_policy(self, _web, decision, decision_type) -> bool:
            if decision_type == WebKit2.PolicyDecisionType.NAVIGATION_ACTION:
                action = decision.get_navigation_action()
                uri = action.get_request().get_uri()
                if uri == LOCAL_PAGE_URL:
                    decision.use()
                else:
                    decision.ignore()
                return True
            if decision_type == WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION:
                decision.ignore()
                return True
            return False

        def _on_load_changed(self, _web, load_event) -> None:
            if load_event != WebKit2.LoadEvent.FINISHED:
                return
            self.ready = True
            if not self.is_wayland and self.poll_timer_id is None:
                self.poll_timer_id = GLib.timeout_add(60, self._track_cursor)

            # On Wayland (no global cursor peek) or when pinned/--show, reveal immediately
            should_open = (
                self.pinned
                or "--show" in sys.argv
                or (self.is_wayland and "--hidden" not in sys.argv)
            )
            if should_open and not self.note_visible:
                self.show_note()

        # -------------------------------------------------------------------
        # Bridge Messaging
        # -------------------------------------------------------------------

        def send_to_web(self, message: str) -> None:
            payload = json.dumps([message])
            js = f"window.desktopNote && window.desktopNote._dispatch(({payload})[0]);"
            if hasattr(self.web_view, "evaluate_javascript"):
                self.web_view.evaluate_javascript(js, -1, None, None, None, None)
            else:
                self.web_view.run_javascript(js, None, None)

        def _on_web_message(self, _ucm, js_result) -> None:
            try:
                val = js_result.get_js_value()
                msg = val.to_string()
            except Exception:
                return
            if not isinstance(msg, str):
                return

            if msg.startswith("paper:"):
                parts = msg.split(":")
                if len(parts) == 5:
                    paper = parse_rgb(parts[1])
                    border = parse_hex(parts[2])
                    ink = parse_hex(parts[3])
                    accent = parse_hex(parts[4])
                    if paper and border and ink and accent:
                        self.palette = ThemePalette(paper, border, ink, accent)
                        rgba = Gdk.RGBA()
                        rgba.parse(to_hex(paper))
                        self.web_view.set_background_color(rgba)
                        self._apply_theme_css()
                return

            if msg.startswith("opacity:"):
                val_str = msg[8:]
                if val_str in ("0.80", "0.88", "0.95"):
                    self.target_opacity = float(val_str)
                    if self.anim_timer_id is not None:
                        self.window.set_opacity(self.target_opacity * self.motion.progress)
                    elif self.note_visible:
                        self.window.set_opacity(self.target_opacity)
                return

            if msg in ("pin:1", "pin:0"):
                self.pinned = msg == "pin:1"
                self.hot_corner.reset()
                if self.pinned and self.ready and not self.note_visible:
                    self.show_note()
                return

            if msg in ("panel:1", "panel:0"):
                self.secondary_panel = msg == "panel:1"
                self.hot_corner.reset()
                return

            if msg in ("sheet:1", "sheet:0"):
                self.sheet_open = msg == "sheet:1"
                self.hot_corner.reset()
                return

            if msg.startswith("notemeta:"):
                payload = msg[9:]
                fields = payload.split("|", 1)
                self.note_task_key = ""
                self.note_text = ""
                if len(fields) == 2 and fields[0]:
                    try:
                        self.note_text = base64.b64decode(fields[1]).decode("utf-8")
                        self.note_task_key = fields[0]
                    except Exception:
                        pass
                return

            if msg.startswith("calmeta:"):
                fields = msg[8:].split("|")
                if len(fields) == 3:
                    self.calendar_view_date = fields[0] or datetime.date.today().isoformat()
                    self.calendar_task_date = fields[1]
                    self.calendar_dots = {d for d in fields[2].split(",") if d}
                return

            if msg.startswith("menu:"):
                self._show_note_menu(msg)
                return

            if msg == "exit":
                self.quit()
                return

            if msg == "focus":
                if self.note_visible:
                    self.keep_open = True
                    self.window.present()
                    self.web_view.grab_focus()
                    self.send_to_web("focus-ready")
                return

            if msg == "hold":
                self.keep_open = True
                return

            if msg == "release":
                self.keep_open = False
                return

            if msg.startswith("height:"):
                try:
                    h = int(msg[7:])
                except ValueError:
                    return
                self.preferred_height = max(72, min(620, h))
                if self.note_visible and self.anim_timer_id is None and not self.menu_visible:
                    self._apply_panel_bounds()

        # -------------------------------------------------------------------
        # Theme CSS for Native GTK Menu & Note Flyout
        # -------------------------------------------------------------------

        def _apply_theme_css(self) -> None:
            p = self.palette
            css = f"""
            menu.cream-menu {{
                background-color: {to_hex(p.paper)};
                color: {to_hex(p.ink)};
                border: 1px solid {to_hex(p.border)};
                border-radius: 0px;
                padding: 2px 0px;
            }}
            menu.cream-menu menuitem {{
                color: {to_hex(p.ink)};
                padding: 4px 10px;
                border-radius: 0px;
            }}
            menu.cream-menu menuitem:hover {{
                background-color: {to_hex(p.highlight)};
                color: {to_hex(p.ink)};
            }}
            menu.cream-menu separator {{
                background-color: {to_hex(p.border)};
                min-height: 1px;
                margin: 2px 6px;
            }}
            window.pixel-note-flyout {{
                background-color: {to_hex(p.paper)};
                border: 1px solid {to_hex(p.border)};
            }}
            .pixel-note-scroll {{
                border: 1px solid {to_hex(p.accent)};
                background-color: {to_hex(p.input_bg)};
            }}
            textview.pixel-note-input,
            textview.pixel-note-input text {{
                background-color: {to_hex(p.input_bg)};
                color: {to_hex(p.ink)};
                caret-color: {to_hex(p.accent)};
                font-size: 12px;
            }}
            """
            self.css_provider.load_from_data(css.encode("utf-8"))

        # -------------------------------------------------------------------
        # Monitor Geometry, Hot Corner & 60fps Motion
        # -------------------------------------------------------------------

        def _primary_workarea(self) -> Tuple[int, int, int, int, int, int]:
            display = Gdk.Display.get_default()
            monitor = None
            if display is not None:
                monitor = display.get_primary_monitor() or (
                    display.get_monitor(0) if display.get_n_monitors() > 0 else None
                )
            if monitor is None:
                return (0, 0, 1920, 1080, 0, 0)
            work = monitor.get_workarea()
            geom = monitor.get_geometry()
            return (work.x, work.y, work.width, work.height, geom.x, geom.y)

        def _target_geometry(self) -> Tuple[int, int, int, int]:
            wx, wy, ww, wh, _, _ = self._primary_workarea()
            w = min(NOTE_WIDTH, ww)
            h = min(self.preferred_height, wh)
            return (wx, wy, w, h)

        def _apply_panel_bounds(self) -> None:
            if self.anim_timer_id is not None or self.menu_visible:
                return
            x, y, w, h = self._target_geometry()
            self.window.set_size_request(w, h)
            self.window.resize(w, h)
            if not self.is_wayland:
                self.window.move(x, y)

        def _cursor_position(self) -> Optional[Tuple[int, int]]:
            display = Gdk.Display.get_default()
            if display is None:
                return None
            seat = display.get_default_seat()
            if seat is None:
                return None
            pointer = seat.get_pointer()
            if pointer is None:
                return None
            _, cx, cy = pointer.get_position()
            return (int(cx), int(cy))

        def _track_cursor(self) -> bool:
            if not self.ready or self.is_wayland:
                return True
            pos = self._cursor_position()
            if pos is None:
                return True
            cx, cy = pos
            wx, wy, _, _, gx, gy = self._primary_workarea()
            now = time.monotonic()
            edge = 14

            if not self.note_visible:
                if self.hot_corner.should_show(cx, cy, gx, gy, edge, now):
                    self.show_note()
                return True

            hold = (
                self.pinned
                or self.secondary_panel
                or self.sheet_open
                or self.menu_visible
                or self.active_flyout is not None
                or (self.keep_open and self.window.is_active())
                or now < self.menu_grace_until
            )

            px, py = self.window.get_position()
            pw, ph = self.window.get_size()
            panel_rect = (px, py, pw, ph)
            in_panel = (px <= cx < px + pw) and (py <= cy < py + ph)

            if self.motion.target < 0.5:
                if hold or in_panel:
                    self.show_note()
                return True

            if self.hot_corner.should_hide(cx, cy, panel_rect, gx, gy, edge, hold, now):
                self.hide_note()
            return True

        def show_note(self) -> None:
            if not self.note_visible:
                self.note_visible = True
                self.motion.snap(0.0)
                x, y, w, h = self._target_geometry()
                self.window.set_size_request(w, h)
                self.window.resize(w, h)
                if not self.is_wayland:
                    self.window.move(x, y - 8)
                self.window.set_opacity(0.0 if not self.is_wayland else self.target_opacity)
                self.window.show_all()
                self.send_to_web("shown")

            self.hot_corner.reset()
            if self.is_wayland:
                self.motion.snap(1.0)
                self.window.set_opacity(self.target_opacity)
                self.window.present()
            else:
                self._start_animation(1.0)

        def hide_note(self) -> None:
            self.hot_corner.reset()
            if self.is_wayland:
                self._finish_hide()
            else:
                self._start_animation(0.0)

        def toggle_note(self) -> None:
            if self.note_visible and self.motion.target > 0.5:
                self.hide_note()
            else:
                self.show_note()
                self.window.present()

        def _start_animation(self, target: float) -> None:
            self.motion.begin(target, time.monotonic())
            if self.anim_timer_id is not None:
                GLib.source_remove(self.anim_timer_id)
            self.anim_timer_id = GLib.timeout_add(16, self._step_animation)

        def _step_animation(self) -> bool:
            done = self.motion.advance(time.monotonic())
            x, y, _, _ = self._target_geometry()
            offset = int(round((1.0 - self.motion.progress) * 8.0))
            if not self.is_wayland:
                self.window.move(x, y - offset)
            self.window.set_opacity(max(0.0, min(1.0, self.target_opacity * self.motion.progress)))

            if done:
                self.anim_timer_id = None
                if self.motion.target > 0.5:
                    self._apply_panel_bounds()
                    self.window.set_opacity(self.target_opacity)
                else:
                    self._finish_hide()
                return False
            return True

        def _finish_hide(self) -> None:
            self._close_flyout()
            self.window.hide()
            self.note_visible = False
            self.keep_open = False
            self.send_to_web("hidden")
            self._apply_panel_bounds()
            self.hot_corner.reset()

        def _on_focus_out(self, _win, _event) -> bool:
            self.keep_open = False
            if self.ready and self.sheet_open and not self.menu_visible:
                self.send_to_web("close-sheet")
            return False

        # -------------------------------------------------------------------
        # Native Right-Click Menu (Level 2) + 3rd-Level Side Flyouts
        # -------------------------------------------------------------------

        def _close_flyout(self) -> None:
            if self.active_note_flyout is not None:
                self.active_note_flyout.flush()
                self.active_note_flyout = None
            if self.active_flyout is not None:
                self.active_flyout.destroy()
                self.active_flyout = None

        def _dismiss_all_menus(self) -> None:
            self._close_flyout()
            if self.active_menu is not None:
                self.active_menu.popdown()

        def _menu_anchor_rect(self) -> Tuple[int, int, int, int]:
            if self.active_menu is not None and self.active_menu.get_window() is not None:
                _, mx, my = self.active_menu.get_window().get_origin()
                alloc = self.active_menu.get_allocation()
                return (mx, my, alloc.width, alloc.height)
            px, py = self.window.get_position()
            pw, ph = self.window.get_size()
            return (px + pw + 2, py, 160, ph)

        def _place_flyout(self, flyout: Gtk.Window, item: Gtk.MenuItem, fw: int, fh: int) -> None:
            mx, my, mw, _ = self._menu_anchor_rect()
            wx, wy, ww, wh, _, _ = self._primary_workarea()
            item_alloc = item.get_allocation()
            fx = mx + mw + 2
            if fx + fw > wx + ww:
                fx = max(wx, mx - fw - 2)
            fy = my + item_alloc.y
            if fy + fh > wy + wh:
                fy = max(wy, wy + wh - fh)
            flyout.move(fx, fy)

        def _open_calendar_flyout(self, item: Gtk.MenuItem, command: str, selected_iso: str) -> None:
            self._close_flyout()

            def on_select(iso: str) -> None:
                self.menu_grace_until = time.monotonic() + 0.5
                self._dismiss_all_menus()
                self.send_to_web(f"command:{command}/{iso}")

            cal = PixelCalendarWindow(
                self.palette,
                selected_iso,
                self.calendar_dots,
                on_select=on_select,
                on_close=self._dismiss_all_menus,
            )
            self.active_flyout = cal
            self._place_flyout(cal, item, PixelCalendarWindow.WIDTH, PixelCalendarWindow.HEIGHT)
            cal.show_all()

        def _open_note_flyout(self, item: Gtk.MenuItem) -> None:
            task_key = self.note_task_key
            self._close_flyout()

            def on_save(text: str) -> None:
                if not task_key:
                    return
                encoded = base64.b64encode(text.encode("utf-8")).decode("ascii")
                self.send_to_web(f"notesave:{task_key}|{encoded}")

            def on_dismiss() -> None:
                self.menu_grace_until = time.monotonic() + 0.5
                self._close_flyout()

            # Compute position before closing Gtk.Menu so the note window can take focus cleanly
            mx, my, mw, _ = self._menu_anchor_rect()
            item_y = item.get_allocation().y
            if self.active_menu is not None:
                self.active_menu.popdown()

            note_win = PixelNoteWindow(
                self.palette,
                self.note_text,
                on_save=on_save,
                on_dismiss=on_dismiss,
            )
            self.active_flyout = note_win
            self.active_note_flyout = note_win
            wx, wy, ww, wh, _, _ = self._primary_workarea()
            fx = mx + mw + 2
            if fx + PixelNoteWindow.WIDTH > wx + ww:
                fx = max(wx, mx - PixelNoteWindow.WIDTH - 2)
            fy = min(my + item_y, wy + wh - PixelNoteWindow.HEIGHT)
            note_win.move(fx, max(wy, fy))
            note_win.show_all()
            GLib.idle_add(note_win.focus_input)

        def _make_action_item(self, label: str, command: str, checked: Optional[bool] = None) -> Gtk.MenuItem:
            if checked is not None:
                item = Gtk.CheckMenuItem(label=label)
                item.set_active(checked)
            else:
                item = Gtk.MenuItem(label=label)

            def on_activate(_w) -> None:
                if command not in ("pin", "exit"):
                    self.menu_grace_until = time.monotonic() + 0.4
                self.send_to_web(f"command:{command}")

            item.connect("activate", on_activate)
            item.connect("select", lambda *_: self._close_flyout())
            return item

        def _make_submenu_item(
            self,
            key: str,
            label: str,
            selected: Optional[str],
            values: Tuple[Tuple[str, str], ...],
        ) -> Gtk.MenuItem:
            root = Gtk.MenuItem(label=label)
            sub = Gtk.Menu()
            sub.get_style_context().add_class("cream-menu")
            for val, title in values:
                is_sel = (selected == val) if selected is not None else None
                sub.append(self._make_action_item(title, f"{key}/{val}", checked=is_sel))
            sub.show_all()
            root.set_submenu(sub)
            root.connect("select", lambda *_: self._close_flyout())
            return root

        def _show_note_menu(self, message: str) -> None:
            parts = message.split(":")
            if not self.note_visible or len(parts) != 8:
                return
            try:
                _x = int(parts[1])
                y = int(parts[2])
            except ValueError:
                return

            self._dismiss_all_menus()
            menu = Gtk.Menu()
            menu.get_style_context().add_class("cream-menu")

            if parts[3] == "1":
                note_item = Gtk.MenuItem(label="备注  ▸")
                note_item.connect("activate", lambda it: self._open_note_flyout(it))
                menu.append(note_item)

                task_sel = self.calendar_task_date or self.calendar_view_date
                task_cal_item = Gtk.MenuItem(label="切换时间  ▸")
                task_cal_item.connect(
                    "select",
                    lambda it, s=task_sel: self._open_calendar_flyout(it, "taskDay", s),
                )
                task_cal_item.connect(
                    "activate",
                    lambda it, s=task_sel: self._open_calendar_flyout(it, "taskDay", s),
                )
                menu.append(task_cal_item)

                menu.append(self._make_action_item("延期一天", "delay"))
                menu.append(self._make_action_item("删除任务", "delete"))
                menu.append(Gtk.SeparatorMenuItem())

            if parts[7] == "1":
                menu.append(self._make_action_item("返回待办", "tasks"))

            menu.append(self._make_action_item("新增任务", "new"))
            menu.append(self._make_action_item("清理已完成", "clearDone"))
            menu.append(
                self._make_submenu_item(
                    "theme",
                    "便签颜色",
                    parts[4],
                    (
                        ("rose", "奶油藕粉"),
                        ("butter", "黄油奶黄"),
                        ("sage", "抹茶鼠尾草"),
                        ("lilac", "芋泥淡紫"),
                        ("oat", "海盐燕麦"),
                        ("sky", "冰川奶蓝"),
                    ),
                )
            )
            menu.append(
                self._make_submenu_item(
                    "opacity",
                    "透明度",
                    parts[5],
                    (
                        ("0.80", "轻透 (80%)"),
                        ("0.88", "柔和 (88%)"),
                        ("0.95", "清晰 (95%)"),
                    ),
                )
            )
            menu.append(
                self._make_submenu_item(
                    "filter",
                    "筛选任务",
                    parts[6],
                    (
                        ("today", "当日"),
                        ("all", "全部"),
                        ("postponed", "延期"),
                    ),
                )
            )

            cal_item = Gtk.MenuItem(label="切换日期  ▸")
            cal_item.connect(
                "select",
                lambda it: self._open_calendar_flyout(it, "calendar", self.calendar_view_date),
            )
            cal_item.connect(
                "activate",
                lambda it: self._open_calendar_flyout(it, "calendar", self.calendar_view_date),
            )
            menu.append(cal_item)

            menu.append(self._make_action_item("固定在左上角", "pin", checked=self.pinned))
            menu.append(Gtk.SeparatorMenuItem())
            menu.append(self._make_action_item("新增选项（本周每天）", "add"))
            menu.append(self._make_action_item("剪贴板", "clipboard"))
            menu.append(
                self._make_submenu_item(
                    "tool",
                    "工具",
                    None,
                    (("add30", "划线价"), ("usd", "换美金")),
                )
            )
            menu.append(Gtk.SeparatorMenuItem())
            menu.append(self._make_action_item("退出桌面便签", "exit"))

            def on_deactivate(_m) -> None:
                # Keep note flyout alive if it just opened; close calendar flyout
                if self.active_flyout is not None and self.active_flyout is not self.active_note_flyout:
                    self._close_flyout()
                self.menu_visible = False
                self.active_menu = None
                self.menu_grace_until = time.monotonic() + 0.4
                self._apply_panel_bounds()

            menu.connect("deactivate", on_deactivate)
            menu.show_all()

            self.active_menu = menu
            self.menu_visible = True

            # Anchor menu to the right edge of the sticky note so Level-3 flyouts never cover it
            rect = Gdk.Rectangle()
            pw, ph = self.window.get_size()
            rect.x = pw + 2
            rect.y = max(0, min(ph - 1, y))
            rect.width = 1
            rect.height = 1

            gdk_win = self.window.get_window()
            if gdk_win is not None:
                menu.popup_at_rect(
                    gdk_win,
                    rect,
                    Gdk.Gravity.NORTH_WEST,
                    Gdk.Gravity.NORTH_WEST,
                    None,
                )
            else:
                menu.popup_at_pointer(None)

        # -------------------------------------------------------------------
        # Live HTML Hot-Reload (250ms debounce)
        # -------------------------------------------------------------------

        def _start_page_watcher(self) -> None:
            index_path = self.app_dir / "index.html"
            if not index_path.is_file():
                return
            gfile = Gio.File.new_for_path(str(index_path))
            try:
                monitor = gfile.monitor_file(Gio.FileMonitorFlags.NONE, None)
                monitor.connect("changed", self._on_file_changed)
                self.file_monitor = monitor
            except Exception:
                pass

        def _on_file_changed(self, *_args) -> None:
            if self.reload_timer_id is not None:
                GLib.source_remove(self.reload_timer_id)
            self.reload_timer_id = GLib.timeout_add(250, self._do_reload)

        def _do_reload(self) -> bool:
            self.reload_timer_id = None
            self.web_view.reload()
            return False

        # -------------------------------------------------------------------
        # Single-Instance Unix Domain Socket IPC Server
        # -------------------------------------------------------------------

        def _start_ipc_server(self) -> None:
            sock_path = _socket_path()
            # If another instance is alive, exit cleanly
            if _send_ipc("ping"):
                sys.exit(0)
            try:
                if os.path.exists(sock_path):
                    os.unlink(sock_path)
            except OSError:
                pass

            server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            try:
                server.bind(sock_path)
                server.listen(4)
            except OSError:
                return

            self._ipc_server = server

            def serve() -> None:
                while True:
                    try:
                        conn, _ = server.accept()
                    except OSError:
                        break
                    with conn:
                        try:
                            cmd = conn.recv(64).decode("utf-8").strip()
                        except OSError:
                            continue
                        if cmd == "shutdown":
                            GLib.idle_add(self.quit)
                        elif cmd == "toggle":
                            GLib.idle_add(self.toggle_note)
                        elif cmd == "show":
                            GLib.idle_add(self.show_note)

            t = threading.Thread(target=serve, daemon=True)
            t.start()

        def quit(self) -> None:
            self._close_flyout()
            if hasattr(self, "_ipc_server"):
                try:
                    self._ipc_server.close()
                except OSError:
                    pass
                try:
                    os.unlink(_socket_path())
                except OSError:
                    pass
            Gtk.main_quit()

    app = DesktopNoteApp()
    try:
        Gtk.main()
    except KeyboardInterrupt:
        app.quit()
    return 0


if __name__ == "__main__":
    sys.exit(run_app())
