"""Settings and upload history, stored as JSON in the user's config folder.
Field names follow the Windows version so the files look the same."""
import json
import os
import sys
import uuid
from datetime import datetime
from pathlib import Path

from . import i18n

IMGUR = "Imgur"
CATBOX = "Catbox"
APP_NAME = "Kadr"


def config_dir() -> Path:
    if sys.platform == "darwin":
        d = Path.home() / "Library" / "Application Support" / APP_NAME
    elif sys.platform.startswith("win"):
        # Separate from the Windows version (%APPDATA%\Kadr) so both can run side by side.
        d = Path(os.environ.get("APPDATA", Path.home())) / "KadrQt"
    else:
        d = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "kadr"
    d.mkdir(parents=True, exist_ok=True)
    return d


def pictures_dir() -> Path:
    try:
        from PySide6.QtCore import QStandardPaths
        p = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.PicturesLocation)
        if p:
            return Path(p)
    except Exception:
        pass
    return Path.home() / "Pictures"


def default_hotkey() -> str:
    # Macs often have no PageUp key; ⌘⇧2 is free (⌘⇧3/4/5 belong to macOS).
    return "Ctrl+Shift+2" if sys.platform == "darwin" else "Shift+PgUp"


DEFAULTS = {
    "DefaultDestination": CATBOX,
    "ImgurClientId": "",
    "CopyLink": True,
    "OpenInBrowser": False,
    "AskFileName": True,
    "SaveFolder": "",
    "HookPrintScreen": True,          # hotkey on
    "Hotkey": "",                     # QKeySequence text, e.g. "Shift+PgUp"
    "PenColor": "#e53935",
    "PenSize": 4,
    "Uploaders": [],
    "Palette": ["#e53935", "#fb8c00", "#fdd835", "#43a047", "#1e88e5", "#8e24aa", "#212121", "#ffffff"],
    "Language": "",
    "ImageFormat": "png",
    "JpegQuality": 90,
    "ShowNotifications": True,
    "ProxyMode": "system",
    "ProxyAddress": "",
    "ProxyUser": "",
    "ProxyPasswordFallback": "",      # only when no system keychain is available
}

UPLOADER_DEFAULTS = {
    "Name": "", "RequestMethod": "POST", "RequestURL": "", "Parameters": {}, "Headers": {},
    "Body": "MultipartFormData", "Arguments": {}, "FileFormName": "file", "URL": "", "DeletionURL": "",
}


def normalize_uploader(u: dict) -> dict:
    out = dict(UPLOADER_DEFAULTS)
    out.update({k: v for k, v in (u or {}).items() if v is not None})
    for k in ("Parameters", "Headers", "Arguments"):
        if not isinstance(out.get(k), dict):
            out[k] = {}
    if not out["Name"]:
        out["Name"] = i18n.T("Мой сервис")
    if not out["FileFormName"]:
        out["FileFormName"] = "file"
    return out


class Settings:
    def __init__(self, data: dict | None = None):
        self.data = dict(DEFAULTS)
        if data:
            self.data.update(data)
        d = self.data
        if not d["SaveFolder"]:
            d["SaveFolder"] = str(pictures_dir() / "Kadr")
        if not d["Hotkey"]:
            d["Hotkey"] = default_hotkey()
        if d["ImageFormat"] not in ("png", "jpg"):
            d["ImageFormat"] = "png"
        d["JpegQuality"] = max(50, min(100, int(d.get("JpegQuality") or 90)))
        d["PenSize"] = max(1, min(20, int(d.get("PenSize") or 4)))
        if d["ProxyMode"] not in ("system", "none", "custom"):
            d["ProxyMode"] = "system"
        d["Uploaders"] = [normalize_uploader(u) for u in d.get("Uploaders") or []]

    def __getattr__(self, name):
        data = self.__dict__.get("data")
        if data is not None and name in data:
            return data[name]
        raise AttributeError(name)

    def set(self, **kw):
        self.data.update(kw)

    # ---- files ----
    first_run = False

    @staticmethod
    def path() -> Path:
        return config_dir() / "settings.json"

    @classmethod
    def load(cls) -> "Settings":
        p = cls.path()
        if p.exists():
            try:
                s = cls(json.loads(p.read_text(encoding="utf-8")))
                i18n.set_language(s.Language)
                return s
            except Exception:
                pass
        s = cls()
        s.first_run = True
        i18n.set_language(s.Language)
        return s

    def save(self):
        try:
            self.path().write_text(json.dumps(self.data, ensure_ascii=False, indent=1), encoding="utf-8")
        except Exception:
            pass

    # ---- helpers ----
    def destinations(self) -> list[str]:
        names = [CATBOX, IMGUR]
        for u in self.Uploaders:
            if u["Name"] not in names:
                names.append(u["Name"])
        return names

    def default(self) -> str:
        return self.DefaultDestination if self.DefaultDestination in self.destinations() else CATBOX

    @property
    def jpeg(self) -> bool:
        return self.ImageFormat == "jpg"

    @property
    def ext(self) -> str:
        return ".jpg" if self.jpeg else ".png"

    # ---- proxy password: system keychain (macOS Keychain, GNOME Keyring/KWallet) ----
    def proxy_password(self) -> str:
        try:
            import keyring
            v = keyring.get_password(APP_NAME, "proxy:" + (self.ProxyUser or ""))
            if v is not None:
                return v
        except Exception:
            pass
        return self.ProxyPasswordFallback or ""

    def set_proxy_password(self, value: str):
        try:
            import keyring
            if value:
                keyring.set_password(APP_NAME, "proxy:" + (self.ProxyUser or ""), value)
            else:
                try:
                    keyring.delete_password(APP_NAME, "proxy:" + (self.ProxyUser or ""))
                except Exception:
                    pass
            self.data["ProxyPasswordFallback"] = ""
        except Exception:
            # No keychain on this system: keep it in the settings file, readable only by this user.
            self.data["ProxyPasswordFallback"] = value


# ---------------- history ----------------

def _history_path() -> Path:
    return config_dir() / "history.json"


def thumb_dir() -> Path:
    d = config_dir() / "thumbs"
    d.mkdir(parents=True, exist_ok=True)
    return d


def load_history() -> list[dict]:
    try:
        return json.loads(_history_path().read_text(encoding="utf-8"))
    except Exception:
        return []


def save_history(items: list[dict]):
    try:
        _history_path().write_text(json.dumps(items, ensure_ascii=False, indent=1), encoding="utf-8")
    except Exception:
        pass


def add_history(item: dict, thumb_image=None):
    """thumb_image: a QImage."""
    items = load_history()
    if thumb_image is not None and not thumb_image.isNull():
        try:
            from PySide6.QtCore import Qt
            name = uuid.uuid4().hex + ".png"
            t = thumb_image.scaled(160, 100, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
            t.save(str(thumb_dir() / name), "PNG")
            item["Thumb"] = name
        except Exception:
            pass
    item.setdefault("Time", datetime.now().isoformat(timespec="seconds"))
    items.insert(0, item)
    while len(items) > 500:
        delete_thumb(items.pop())
    save_history(items)


def delete_thumb(item: dict):
    t = item.get("Thumb")
    if t:
        try:
            (thumb_dir() / t).unlink()
        except Exception:
            pass
