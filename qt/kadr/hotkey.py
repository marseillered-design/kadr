"""System-wide capture hotkey.
- macOS: Carbon RegisterEventHotKey (no Accessibility permission needed).
- Linux X11: XGrabKey on the root window, read on a background thread.
- Wayland / Windows-Qt: not possible for apps; the user binds `kadr --capture` in the system settings."""
import select
import sys
import threading

from PySide6.QtCore import QObject, Qt, Signal
from PySide6.QtGui import QKeySequence

from . import platform_utils


class GlobalHotkey(QObject):
    """One system-wide key. kind="capture" (screenshot) or "video" (start/stop a recording): each kind has its own
    Carbon hotkey id on macOS and its own GNOME shortcut, so the two never mix up."""
    activated = Signal()

    def __init__(self, kind="capture"):
        super().__init__()
        self._impl = None
        self.kind = kind

    def supported(self) -> bool:
        if platform_utils.MAC:
            return True
        if platform_utils.LINUX:
            return not platform_utils.is_wayland() or _GnomeShortcut.available()
        return False

    def register(self, sequence: str) -> bool:
        self.unregister()
        if not sequence or not self.supported():
            return False
        try:
            combo = QKeySequence(sequence)[0]
        except Exception:
            return False
        key, mods = combo.key(), combo.keyboardModifiers()
        try:
            if platform_utils.MAC:
                self._impl = _MacHotkey(key, mods, self.activated.emit, 1 if self.kind == "capture" else 2)
            elif platform_utils.is_wayland():
                self._impl = _GnomeShortcut(key, mods, self.kind)
            else:
                self._impl = _X11Hotkey(key, mods, self.activated.emit)
            return self._impl.ok
        except Exception:
            self._impl = None
            return False

    def disable(self):
        """The user switched the hotkey off: also remove it from GNOME's own shortcuts."""
        self.unregister()
        if platform_utils.LINUX and platform_utils.is_wayland() and _GnomeShortcut.available():
            _GnomeShortcut.remove(self.kind)

    def unregister(self):
        if self._impl is not None:
            try:
                self._impl.close()
            except Exception:
                pass
            self._impl = None


# ---------------- macOS (Carbon) ----------------

_MAC_KEYS = {
    **{getattr(Qt.Key, "Key_" + ch): code for ch, code in zip(
        "ASDFHGZXCVBQWERYT123465", [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0C, 0x0D,
                                    0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17])},
    Qt.Key.Key_9: 0x19, Qt.Key.Key_7: 0x1A, Qt.Key.Key_8: 0x1C, Qt.Key.Key_0: 0x1D, Qt.Key.Key_O: 0x1F,
    Qt.Key.Key_U: 0x20, Qt.Key.Key_I: 0x22, Qt.Key.Key_P: 0x23, Qt.Key.Key_L: 0x25, Qt.Key.Key_J: 0x26,
    Qt.Key.Key_K: 0x28, Qt.Key.Key_N: 0x2D, Qt.Key.Key_M: 0x2E, Qt.Key.Key_Space: 0x31,
    Qt.Key.Key_F1: 0x7A, Qt.Key.Key_F2: 0x78, Qt.Key.Key_F3: 0x63, Qt.Key.Key_F4: 0x76, Qt.Key.Key_F5: 0x60,
    Qt.Key.Key_F6: 0x61, Qt.Key.Key_F7: 0x62, Qt.Key.Key_F8: 0x64, Qt.Key.Key_F9: 0x65, Qt.Key.Key_F10: 0x6D,
    Qt.Key.Key_F11: 0x67, Qt.Key.Key_F12: 0x6F, Qt.Key.Key_PageUp: 0x74, Qt.Key.Key_PageDown: 0x79,
    Qt.Key.Key_Home: 0x73, Qt.Key.Key_End: 0x77,
}


def _fourcc(s):
    return (ord(s[0]) << 24) | (ord(s[1]) << 16) | (ord(s[2]) << 8) | ord(s[3])


class _MacHotkey:
    def __init__(self, key, mods, callback, hid_number=1):
        import ctypes
        from ctypes import CFUNCTYPE, POINTER, Structure, c_int32, c_uint32, c_void_p

        self.ok = False
        code = _MAC_KEYS.get(key)
        if code is None:
            return
        # Qt on macOS: ControlModifier = ⌘, MetaModifier = the Control key.
        m = 0
        if mods & Qt.KeyboardModifier.ControlModifier: m |= 1 << 8     # cmdKey
        if mods & Qt.KeyboardModifier.ShiftModifier: m |= 1 << 9       # shiftKey
        if mods & Qt.KeyboardModifier.AltModifier: m |= 1 << 11        # optionKey
        if mods & Qt.KeyboardModifier.MetaModifier: m |= 1 << 12       # controlKey

        class EventTypeSpec(Structure):
            _fields_ = [("eventClass", c_uint32), ("eventKind", c_uint32)]

        class EventHotKeyID(Structure):
            _fields_ = [("signature", c_uint32), ("id", c_uint32)]

        carbon = ctypes.CDLL("/System/Library/Frameworks/Carbon.framework/Carbon")
        self.carbon = carbon
        HANDLER = CFUNCTYPE(c_int32, c_void_p, c_void_p, c_void_p)
        carbon.GetApplicationEventTarget.restype = c_void_p
        carbon.InstallEventHandler.argtypes = [c_void_p, HANDLER, c_uint32, POINTER(EventTypeSpec), c_void_p, POINTER(c_void_p)]
        carbon.InstallEventHandler.restype = c_int32
        carbon.RegisterEventHotKey.argtypes = [c_uint32, c_uint32, EventHotKeyID, c_void_p, c_uint32, POINTER(c_void_p)]
        carbon.RegisterEventHotKey.restype = c_int32
        carbon.UnregisterEventHotKey.argtypes = [c_void_p]
        carbon.RemoveEventHandler.argtypes = [c_void_p]

        carbon.GetEventParameter.argtypes = [c_void_p, c_uint32, c_uint32, c_void_p, c_uint32, c_void_p, c_void_p]
        carbon.GetEventParameter.restype = c_int32

        def handler(call_ref, event, user):
            # every Kadr hotkey reaches every handler: act only on our own id
            try:
                got = EventHotKeyID()
                if carbon.GetEventParameter(event, _fourcc("----"), _fourcc("hkid"), None, ctypes.sizeof(got), None, ctypes.byref(got)) == 0 \
                        and got.id != hid_number:
                    return -9874   # eventNotHandledErr: let the other handler have it
                callback()
            except Exception:
                pass
            return 0

        self._handler = HANDLER(handler)       # keep a reference, or the callback is freed
        target = carbon.GetApplicationEventTarget()
        spec = EventTypeSpec(_fourcc("keyb"), 5)  # kEventClassKeyboard, kEventHotKeyPressed
        self._handler_ref = c_void_p()
        if carbon.InstallEventHandler(target, self._handler, 1, ctypes.byref(spec), None, ctypes.byref(self._handler_ref)) != 0:
            return
        self._hotkey_ref = c_void_p()
        hid = EventHotKeyID(_fourcc("KADR"), hid_number)
        self.ok = carbon.RegisterEventHotKey(code, m, hid, target, 0, ctypes.byref(self._hotkey_ref)) == 0

    def close(self):
        if getattr(self, "_hotkey_ref", None):
            self.carbon.UnregisterEventHotKey(self._hotkey_ref)
        if getattr(self, "_handler_ref", None):
            self.carbon.RemoveEventHandler(self._handler_ref)


# ---------------- Linux Wayland on GNOME (Ubuntu, Fedora): a custom shortcut in the system settings ----------------

_GNOME_LIST = ("org.gnome.settings-daemon.plugins.media-keys", "custom-keybindings")
_GNOME_PATHS = {"capture": "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/kadr/",
                "video": "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/kadr-video/"}
_GNOME_ARGS = {"capture": "--capture", "video": "--record"}


def _gnome_item(kind):
    return "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:" + _GNOME_PATHS[kind]

_GNOME_KEYS = {Qt.Key.Key_PageUp: "Page_Up", Qt.Key.Key_PageDown: "Page_Down", Qt.Key.Key_Print: "Print",
               Qt.Key.Key_Home: "Home", Qt.Key.Key_End: "End", Qt.Key.Key_Insert: "Insert", Qt.Key.Key_Pause: "Pause",
               Qt.Key.Key_Space: "space", Qt.Key.Key_ScrollLock: "Scroll_Lock"}


def _gsettings(*args) -> str:
    import subprocess
    r = subprocess.run(["gsettings", *args], capture_output=True, text=True, timeout=5)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip())
    return r.stdout.strip()


class _GnomeShortcut:
    """Wayland lets no app catch keys, but GNOME runs `kadr --capture` for a shortcut stored in its settings.
    The running Kadr gets that call and opens the capture."""

    @staticmethod
    def available() -> bool:
        import os
        import shutil
        return "GNOME" in os.environ.get("XDG_CURRENT_DESKTOP", "").upper() and shutil.which("gsettings") is not None

    def __init__(self, key, mods, kind="capture"):
        self.ok = False
        path, item = _GNOME_PATHS[kind], _gnome_item(kind)
        if key in _GNOME_KEYS:
            name = _GNOME_KEYS[key]
        elif Qt.Key.Key_F1 <= key <= Qt.Key.Key_F24:
            name = "F%d" % (key - Qt.Key.Key_F1 + 1)
        elif Qt.Key.Key_A <= key <= Qt.Key.Key_Z or Qt.Key.Key_0 <= key <= Qt.Key.Key_9:
            name = chr(key).lower()
        else:
            return
        accel = ""
        if mods & Qt.KeyboardModifier.ControlModifier: accel += "<Control>"
        if mods & Qt.KeyboardModifier.ShiftModifier: accel += "<Shift>"
        if mods & Qt.KeyboardModifier.AltModifier: accel += "<Alt>"
        if mods & Qt.KeyboardModifier.MetaModifier: accel += "<Super>"
        current = _gsettings("get", *_GNOME_LIST)
        paths = [] if current.startswith("@as") else [p.strip().strip("'") for p in current.strip("[]").split(",") if p.strip()]
        if path not in paths:
            paths.append(path)
            _gsettings("set", *_GNOME_LIST, "[" + ", ".join(f"'{p}'" for p in paths) + "]")
        _gsettings("set", item, "name", "Kadr" if kind == "capture" else "Kadr video")
        _gsettings("set", item, "command", platform_utils.executable_command() + " " + _GNOME_ARGS[kind])
        _gsettings("set", item, "binding", accel + name)
        self.ok = True

    @staticmethod
    def remove(kind="capture"):
        path, item = _GNOME_PATHS[kind], _gnome_item(kind)
        try:
            current = _gsettings("get", *_GNOME_LIST)
            paths = [p.strip().strip("'") for p in current.strip("[]").split(",") if p.strip() and not current.startswith("@as")]
            if path in paths:
                paths.remove(path)
                _gsettings("set", *_GNOME_LIST, "[" + ", ".join(f"'{p}'" for p in paths) + "]" if paths else "@as []")
            _gsettings("reset", item, "binding")
        except Exception:
            pass

    def close(self):
        pass   # the shortcut stays in GNOME while Kadr is closed; starting it again is what the shortcut does


# ---------------- Linux X11 ----------------

_X_NAMES = {
    Qt.Key.Key_PageUp: "Prior", Qt.Key.Key_PageDown: "Next", Qt.Key.Key_Print: "Print", Qt.Key.Key_Home: "Home",
    Qt.Key.Key_End: "End", Qt.Key.Key_Insert: "Insert", Qt.Key.Key_Pause: "Pause", Qt.Key.Key_Space: "space",
    Qt.Key.Key_ScrollLock: "Scroll_Lock",
}


class _X11Hotkey:
    def __init__(self, key, mods, callback):
        from Xlib import X, XK, display, error

        self.ok = False
        self._stop = False
        if key in _X_NAMES:
            name = _X_NAMES[key]
        elif Qt.Key.Key_F1 <= key <= Qt.Key.Key_F24:
            name = "F%d" % (key - Qt.Key.Key_F1 + 1)
        elif Qt.Key.Key_A <= key <= Qt.Key.Key_Z or Qt.Key.Key_0 <= key <= Qt.Key.Key_9:
            name = chr(key).lower()
        else:
            return
        self.d = display.Display()
        self.root = self.d.screen().root
        self.code = self.d.keysym_to_keycode(XK.string_to_keysym(name))
        if not self.code:
            self.d.close()
            return
        m = 0
        if mods & Qt.KeyboardModifier.ShiftModifier: m |= X.ShiftMask
        if mods & Qt.KeyboardModifier.ControlModifier: m |= X.ControlMask
        if mods & Qt.KeyboardModifier.AltModifier: m |= X.Mod1Mask
        if mods & Qt.KeyboardModifier.MetaModifier: m |= X.Mod4Mask
        self.masks = [m | extra for extra in (0, X.LockMask, X.Mod2Mask, X.LockMask | X.Mod2Mask)]
        catch = error.CatchError(error.BadAccess)
        for mask in self.masks:
            self.root.grab_key(self.code, mask, True, X.GrabModeAsync, X.GrabModeAsync, onerror=catch)
        self.d.sync()
        if catch.get_error():
            for mask in self.masks:
                self.root.ungrab_key(self.code, mask)
            self.d.close()
            return
        self.ok = True
        self.callback = callback
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()

    def _loop(self):
        from Xlib import X
        fd = self.d.fileno()
        while not self._stop:
            r, _, _ = select.select([fd], [], [], 0.5)
            if self._stop:
                break
            if not r and not self.d.pending_events():
                continue
            while self.d.pending_events():
                ev = self.d.next_event()
                if ev.type == X.KeyPress and ev.detail == self.code:
                    self.callback()
        try:
            for mask in self.masks:
                self.root.ungrab_key(self.code, mask)
            self.d.sync()
            self.d.close()
        except Exception:
            pass

    def close(self):
        self._stop = True
