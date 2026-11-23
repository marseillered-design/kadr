"""Taking the screenshot and listing windows, per system.
- macOS, X11, Windows: Qt grabs every monitor directly (macOS needs the Screen Recording permission).
- Wayland: apps may not read the screen; ask the desktop portal (org.freedesktop.portal.Screenshot),
  falling back to grim / spectacle / gnome-screenshot."""
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path
from urllib.parse import unquote, urlparse

from PySide6.QtCore import QRect, QRectF
from PySide6.QtGui import QGuiApplication, QImage

from . import platform_utils
from .overlay import Shot


class CaptureError(Exception):
    pass


def grab_all() -> list[Shot]:
    if platform_utils.is_wayland():
        return _grab_wayland()
    shots = []
    for s in QGuiApplication.screens():
        pm = s.grabWindow(0)
        img = pm.toImage().convertToFormat(QImage.Format.Format_RGB32)
        img.setDevicePixelRatio(pm.devicePixelRatio() or s.devicePixelRatio())
        shots.append(Shot(s, s.geometry(), img))
    return shots


# ---------------- Wayland ----------------

def _grab_wayland() -> list[Shot]:
    path = None
    errors = []
    try:
        path = _portal_screenshot()
    except Exception as e:  # portal missing or refused
        errors.append(str(e))
    if path is None:
        path = _cli_screenshot(errors)
    if path is None:
        raise CaptureError("; ".join(errors) or "no screenshot tool")
    full = QImage(path)
    _cleanup(path)
    if full.isNull():
        raise CaptureError("empty screenshot")
    return _split(full.convertToFormat(QImage.Format.Format_RGB32))


def _portal_screenshot():
    from jeepney import DBusAddress, MatchRule, new_method_call
    from jeepney.bus_messages import message_bus
    from jeepney.io.blocking import open_dbus_connection

    conn = open_dbus_connection(bus="SESSION")
    try:
        token = "kadr" + uuid.uuid4().hex[:8]
        sender = conn.unique_name.lstrip(":").replace(".", "_")
        req_path = f"/org/freedesktop/portal/desktop/request/{sender}/{token}"
        rule = MatchRule(type="signal", interface="org.freedesktop.portal.Request", member="Response", path=req_path)
        conn.send_and_get_reply(message_bus.AddMatch(rule))
        with conn.filter(rule) as queue:
            portal = DBusAddress("/org/freedesktop/portal/desktop", bus_name="org.freedesktop.portal.Desktop",
                                 interface="org.freedesktop.portal.Screenshot")
            msg = new_method_call(portal, "Screenshot", "sa{sv}",
                                  ("", {"handle_token": ("s", token), "interactive": ("b", False)}))
            conn.send_and_get_reply(msg, timeout=10)
            reply = conn.recv_until_filtered(queue, timeout=90)
        response, results = reply.body
        if response != 0:
            raise CaptureError("portal refused (%s)" % response)
        uri = results.get("uri", ("s", ""))[1]
        p = unquote(urlparse(uri).path)
        return p if p and os.path.exists(p) else None
    finally:
        conn.close()


def _cli_screenshot(errors):
    tmp = os.path.join(tempfile.gettempdir(), "kadr-%s.png" % uuid.uuid4().hex[:8])
    for cmd in (["grim", tmp], ["spectacle", "-b", "-n", "-f", "-o", tmp], ["gnome-screenshot", "-f", tmp]):
        if not shutil.which(cmd[0]):
            continue
        try:
            subprocess.run(cmd, timeout=20, capture_output=True)
            if os.path.exists(tmp) and os.path.getsize(tmp) > 0:
                return tmp
        except Exception as e:
            errors.append(f"{cmd[0]}: {e}")
    return None


def _cleanup(path):
    """Delete the temporary file only if it is in a temp folder (never the user's Pictures)."""
    try:
        p = Path(path).resolve()
        temp_roots = [Path(tempfile.gettempdir()).resolve()]
        if os.environ.get("XDG_RUNTIME_DIR"):
            temp_roots.append(Path(os.environ["XDG_RUNTIME_DIR"]).resolve())
        if any(str(p).startswith(str(r) + os.sep) for r in temp_roots):
            p.unlink()
    except Exception:
        pass


def _split(full: QImage) -> list[Shot]:
    """The portal returns the whole desktop; cut it into monitors."""
    screens = QGuiApplication.screens()
    union = QRect()
    for s in screens:
        union = union.united(s.geometry())
    scale = full.width() / max(1, union.width())
    shots = []
    for s in screens:
        g = s.geometry()
        r = QRect(round((g.x() - union.x()) * scale), round((g.y() - union.y()) * scale),
                  round(g.width() * scale), round(g.height() * scale))
        img = full.copy(r)
        img.setDevicePixelRatio(scale)
        shots.append(Shot(s, g, img))
    return shots


# ---------------- windows under the cursor ----------------

def window_list() -> list[QRectF]:
    """Visible windows in global logical coordinates, topmost first. Empty where the system hides them (Wayland)."""
    try:
        if platform_utils.MAC:
            return _mac_windows()
        if platform_utils.WIN:
            return _win_windows()
        if platform_utils.LINUX and not platform_utils.is_wayland():
            return _x11_windows()
    except Exception:
        pass
    return []


def _mac_windows():
    import Quartz
    opts = Quartz.kCGWindowListOptionOnScreenOnly | Quartz.kCGWindowListExcludeDesktopElements
    me = os.getpid()
    out = []
    for w in Quartz.CGWindowListCopyWindowInfo(opts, Quartz.kCGNullWindowID) or []:
        if w.get("kCGWindowLayer", 1) != 0 or w.get("kCGWindowOwnerPID") == me:
            continue
        if float(w.get("kCGWindowAlpha", 1)) <= 0:
            continue
        b = w.get("kCGWindowBounds") or {}
        r = QRectF(float(b.get("X", 0)), float(b.get("Y", 0)), float(b.get("Width", 0)), float(b.get("Height", 0)))
        if r.width() >= 8 and r.height() >= 8:
            out.append(r)
    return out


def _x11_windows():
    from Xlib import X, display
    d = display.Display()
    try:
        root = d.screen().root
        a = lambda n: d.intern_atom(n)
        stack = root.get_full_property(a("_NET_CLIENT_LIST_STACKING"), X.AnyPropertyType)
        if not stack:
            return []
        hidden = a("_NET_WM_STATE_HIDDEN")
        dpr = QGuiApplication.primaryScreen().devicePixelRatio() or 1.0
        out = []
        for wid in reversed(list(stack.value)):
            w = d.create_resource_object("window", wid)
            try:
                st = w.get_full_property(a("_NET_WM_STATE"), X.AnyPropertyType)
                if st and hidden in st.value:
                    continue
                g = w.get_geometry()
                pos = w.translate_coords(root, 0, 0)
                x, y = -pos.x, -pos.y
                fe = w.get_full_property(a("_NET_FRAME_EXTENTS"), X.AnyPropertyType)
                l = r = t = b = 0
                if fe and len(fe.value) == 4:
                    l, r, t, b = fe.value
                rect = QRectF((x - l) / dpr, (y - t) / dpr, (g.width + l + r) / dpr, (g.height + t + b) / dpr)
                if rect.width() >= 8 and rect.height() >= 8:
                    out.append(rect)
            except Exception:
                continue
        return out
    finally:
        d.close()


def _win_windows():
    """Only for trying the Qt version on Windows."""
    import ctypes
    from ctypes import wintypes
    user32, dwm = ctypes.windll.user32, ctypes.windll.dwmapi
    me = os.getpid()
    out = []
    dpr = QGuiApplication.primaryScreen().devicePixelRatio() or 1.0

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(h, l):
        if not user32.IsWindowVisible(h) or user32.IsIconic(h):
            return True
        if user32.GetWindowLongW(h, -20) & 0x20:
            return True
        cloaked = ctypes.c_int(0)
        if dwm.DwmGetWindowAttribute(h, 14, ctypes.byref(cloaked), 4) == 0 and cloaked.value:
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value == me:
            return True
        cls = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(h, cls, 64)
        if cls.value in ("Progman", "WorkerW"):
            return True
        r = wintypes.RECT()
        if dwm.DwmGetWindowAttribute(h, 9, ctypes.byref(r), 16) != 0:
            user32.GetWindowRect(h, ctypes.byref(r))
        rect = QRectF(r.left / dpr, r.top / dpr, (r.right - r.left) / dpr, (r.bottom - r.top) / dpr)
        if rect.width() >= 8 and rect.height() >= 8:
            out.append(rect)
        return True

    user32.EnumWindows(cb, 0)
    return out
