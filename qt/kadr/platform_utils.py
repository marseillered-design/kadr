"""Things that differ between macOS, Linux and Windows: window levels, autostart, revealing files,
screen-recording permission, Wayland detection."""
import os
import shlex
import subprocess
import sys
from pathlib import Path

from PySide6.QtCore import QUrl
from PySide6.QtGui import QDesktopServices, QGuiApplication

MAC = sys.platform == "darwin"
WIN = sys.platform.startswith("win")
LINUX = sys.platform.startswith("linux")


def is_wayland() -> bool:
    return LINUX and (os.environ.get("XDG_SESSION_TYPE", "").lower() == "wayland" or bool(os.environ.get("WAYLAND_DISPLAY")))


def executable_command() -> str:
    """How to start this app again (used for autostart, the Wayland shortcut hint)."""
    if os.environ.get("APPIMAGE"):
        return shlex.quote(os.environ["APPIMAGE"])
    if getattr(sys, "frozen", False):
        return shlex.quote(sys.executable)
    return f"{shlex.quote(sys.executable)} -m kadr"


def open_url(url: str):
    QDesktopServices.openUrl(QUrl(url))


def open_file(path: str):
    QDesktopServices.openUrl(QUrl.fromLocalFile(path))


def reveal(path: str):
    """Show the file selected in Finder / the file manager / Explorer."""
    try:
        if MAC:
            subprocess.Popen(["open", "-R", path])
            return
        if WIN:
            subprocess.Popen(["explorer", "/select,", path])
            return
        # freedesktop file managers (Nautilus, Dolphin, Nemo…) understand ShowItems over D-Bus.
        r = subprocess.run(["dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                            "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                            "array:string:" + QUrl.fromLocalFile(path).toString(), "string:"],
                           capture_output=True, timeout=5)
        if r.returncode == 0:
            return
    except Exception:
        pass
    open_file(str(Path(path).parent))


# ---------------- macOS window level / activation ----------------

def _cocoa() -> bool:
    """True only when Qt really draws through macOS windows (not the offscreen test platform)."""
    return MAC and QGuiApplication.platformName() == "cocoa"


def raise_overlay(widget):
    """Put the capture window above the menu bar and Dock on macOS, on every Space."""
    if not _cocoa():
        return
    try:
        import objc
        from AppKit import NSScreenSaverWindowLevel, NSWindowCollectionBehaviorCanJoinAllSpaces, \
            NSWindowCollectionBehaviorFullScreenAuxiliary
        view = objc.objc_object(c_void_p=int(widget.winId()))
        win = view.window()
        win.setLevel_(NSScreenSaverWindowLevel)
        win.setCollectionBehavior_(NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorFullScreenAuxiliary)
    except Exception:
        pass


def activate_app():
    """A menu-bar app must bring itself forward explicitly on macOS."""
    if not _cocoa():
        return
    try:
        from AppKit import NSApplication
        NSApplication.sharedApplication().activateIgnoringOtherApps_(True)
    except Exception:
        pass


def screen_capture_allowed(ask: bool) -> bool:
    """macOS 10.15+: without Screen Recording permission captures show only the wallpaper."""
    if not MAC:
        return True
    try:
        import ctypes
        cg = ctypes.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
        cg.CGPreflightScreenCaptureAccess.restype = ctypes.c_bool
        if cg.CGPreflightScreenCaptureAccess():
            return True
        if ask:
            cg.CGRequestScreenCaptureAccess.restype = ctypes.c_bool
            cg.CGRequestScreenCaptureAccess()
        return False
    except Exception:
        return True


def open_screen_recording_settings():
    if MAC:
        subprocess.Popen(["open", "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"])


# ---------------- autostart ----------------

_MAC_PLIST = Path.home() / "Library" / "LaunchAgents" / "app.kadr.plist"
_LINUX_DESKTOP = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "autostart" / "kadr.desktop"


def autostart_enabled() -> bool:
    if MAC:
        return _MAC_PLIST.exists()
    if LINUX:
        return _LINUX_DESKTOP.exists()
    return False


def set_autostart(on: bool):
    if MAC:
        if not on:
            _MAC_PLIST.unlink(missing_ok=True)
            return
        bundle = Path(sys.executable).resolve().parents[2] if getattr(sys, "frozen", False) else None
        if bundle is not None and bundle.suffix == ".app":
            args = ["/usr/bin/open", "-a", str(bundle), "--args", "--tray"]
        elif getattr(sys, "frozen", False):
            args = [sys.executable, "--tray"]
        else:
            args = [sys.executable, "-m", "kadr", "--tray"]
        items = "".join(f"<string>{a}</string>" for a in args)
        _MAC_PLIST.parent.mkdir(parents=True, exist_ok=True)
        _MAC_PLIST.write_text(f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>app.kadr</string>
<key>ProgramArguments</key><array>{items}</array>
<key>RunAtLoad</key><true/>
</dict></plist>
""", encoding="utf-8")
    elif LINUX:
        if not on:
            _LINUX_DESKTOP.unlink(missing_ok=True)
            return
        _LINUX_DESKTOP.parent.mkdir(parents=True, exist_ok=True)
        _LINUX_DESKTOP.write_text(f"""[Desktop Entry]
Type=Application
Name=Kadr
Exec={executable_command()} --tray
Icon=kadr
X-GNOME-Autostart-enabled=true
""", encoding="utf-8")


def dark_mode() -> bool:
    try:
        from PySide6.QtCore import Qt
        return QGuiApplication.styleHints().colorScheme() == Qt.ColorScheme.Dark
    except Exception:
        return False
