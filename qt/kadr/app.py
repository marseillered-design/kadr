"""Kadr for Linux and macOS: menu-bar / tray app, capture, upload, save, print."""
import getpass
import json
import os
import sys
import threading
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QBuffer, QEvent, QIODevice, QObject, QPointF, QRectF, QSize, Qt, QTimer, Signal
from PySide6.QtGui import (QAction, QColor, QGuiApplication, QIcon, QImage, QLinearGradient, QPainter, QPainterPath,
                           QPen, QPixmap)
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import QApplication, QFileDialog, QMenu, QSystemTrayIcon

from . import capture, i18n, platform_utils, settings as st, toast, uploader
from .dialogs import HistoryDialog, SettingsDialog
from .hotkey import GlobalHotkey
from .i18n import T, F
from .overlay import CaptureSession

IMAGE_FILTER = "*.png *.jpg *.jpeg *.gif *.webp *.bmp"


# ---------------- icons ----------------

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024]


def icon_file(size: int):
    """The designed icon (icons/kadr-<size>.png, drawn by tools/draw_icon.py): next to the package when built,
    in the repository's icons/ folder when run from source."""
    best = next((s for s in ICON_SIZES if s >= size), ICON_SIZES[-1])
    for folder in (Path(__file__).with_name("icons"), Path(__file__).resolve().parents[2] / "icons"):
        f = folder / f"kadr-{best}.png"
        if f.exists():
            return f
    return None


def app_icon_pixmap(size: int) -> QPixmap:
    f = icon_file(size)
    if f is not None:
        pm = QPixmap(str(f))
        if not pm.isNull():
            return pm if pm.width() == size else pm.scaled(size, size, Qt.AspectRatioMode.KeepAspectRatio,
                                                         Qt.TransformationMode.SmoothTransformation)
    return _drawn_icon(size)


def _drawn_icon(size: int) -> QPixmap:
    """Fallback if the icon files are missing: the plain viewfinder."""
    pm = QPixmap(size, size)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    m = 0.5 if size <= 20 else size * 0.04
    g = QLinearGradient(0, 0, size, size)
    g.setColorAt(0, QColor(18, 181, 203))
    g.setColorAt(1, QColor(61, 90, 254))
    path = QPainterPath()
    path.addRoundedRect(QRectF(m, m, size - 2 * m, size - 2 * m), size * 0.22, size * 0.22)
    p.fillPath(path, g)
    _viewfinder(p, size, QColor(Qt.GlobalColor.white))
    p.end()
    return pm


def _viewfinder(p, size, color):
    w = max(1.6, size * 0.085)
    i, L = size * 0.24, size * 0.2
    e = size - i
    p.setPen(QPen(color, w, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
    P = QPointF
    for pts in ([P(i, i + L), P(i, i), P(i + L, i)], [P(e - L, i), P(e, i), P(e, i + L)],
                [P(e, e - L), P(e, e), P(e - L, e)], [P(i + L, e), P(i, e), P(i, e - L)]):
        p.drawPolyline(pts)
    d = max(2.2, size * 0.17)
    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(color)
    p.drawEllipse(QPointF(size / 2, size / 2), d / 2, d / 2)


def _aperture_mask(p, size):
    """The app icon's shutter blades in black on transparent: macOS recolours it for the menu bar."""
    import math
    from PySide6.QtGui import QPainterPathStroker, QPolygonF
    c = QPointF(size / 2, size / 2)
    R = size * 0.42
    r_in = R * 0.42
    hexa = [QPointF(c.x() + r_in * math.cos(math.radians(i * 60 - 90)), c.y() + r_in * math.sin(math.radians(i * 60 - 90)))
            for i in range(6)]
    disc = QPainterPath()
    disc.addEllipse(c, R, R)
    hole = QPainterPath()
    hole.addPolygon(QPolygonF(hexa))
    hole.closeSubpath()
    shape = disc.subtracted(hole)
    st = QPainterPathStroker()
    st.setWidth(max(1.0, size * 0.07))
    for i, v in enumerate(hexa):
        a = math.radians(i * 60 + 30)
        line = QPainterPath(v)
        line.lineTo(QPointF(v.x() + math.cos(a) * R * 1.3, v.y() + math.sin(a) * R * 1.3))
        shape = shape.subtracted(st.createStroke(line))
    p.fillPath(shape, QColor(Qt.GlobalColor.black))


def tray_icon() -> QIcon:
    if platform_utils.MAC:
        # Menu-bar icons are black "template" images that macOS recolours for light/dark menus.
        icon = QIcon()
        for s in (18, 36):
            pm = QPixmap(s, s)
            pm.fill(Qt.GlobalColor.transparent)
            p = QPainter(pm)
            p.setRenderHint(QPainter.RenderHint.Antialiasing)
            _aperture_mask(p, s)
            p.end()
            icon.addPixmap(pm)
        icon.setIsMask(True)
        return icon
    icon = QIcon()
    for s in (16, 20, 24, 32, 48, 64):
        icon.addPixmap(app_icon_pixmap(s))
    return icon


# ---------------- images ----------------

def shot_name(ext: str) -> str:
    return T("Снимок") + " " + datetime.now().strftime("%Y-%m-%d %H-%M-%S") + ext


def encode(img: QImage, jpeg: bool, quality: int) -> bytes:
    if jpeg and img.hasAlphaChannel():
        flat = QImage(img.size(), QImage.Format.Format_RGB32)
        flat.fill(Qt.GlobalColor.white)
        p = QPainter(flat)
        p.drawImage(0, 0, img)
        p.end()
        img = flat
    buf = QBuffer()
    buf.open(QIODevice.OpenModeFlag.WriteOnly)
    img.save(buf, "JPG" if jpeg else "PNG", quality if jpeg else -1)
    return bytes(buf.data())


# ---------------- the app ----------------

class KadrApp(QObject):
    uploaded = Signal(object)

    def __init__(self, qapp: QApplication, quiet: bool):
        super().__init__()
        self.qapp = qapp
        self.S = st.Settings.load()
        self.session = None
        self.settings_dlg = None
        self.history_dlg = None
        self.uploaded.connect(self._uploaded)
        self.hotkey = GlobalHotkey()
        self.hotkey.activated.connect(lambda: self.capture(False))

        self.tray = QSystemTrayIcon(tray_icon())
        toast.tray_icon = self.tray
        self.tray.setContextMenu(self.build_menu())
        self.tray.activated.connect(self._tray_activated)
        self.tray.show()
        self.apply_hotkey(first=True)

        if self.S.first_run:
            self.S.save()
            if self.hotkey.supported():
                toast.info(None, T("Кадр запущен"), F("Нажми {0}, чтобы сделать снимок. Настройки — в значке у часов.", self.key_text()), None, 10)
            else:
                toast.info(None, T("Кадр запущен"), T("wayland.hotkey") + " " + platform_utils.executable_command() + " --capture", None, 15)
        elif not quiet and self.S.ShowNotifications:
            toast.info(None, T("Кадр работает"), F("{0} — снимок экрана.", self.key_text()) if self.hotkey.supported()
                       else T("Снимок — щелчок по значку у часов."), None, 5)

        if platform_utils.MAC:
            platform_utils.screen_capture_allowed(ask=True)   # asks once, on the first start

    def key_text(self):
        from PySide6.QtGui import QKeySequence
        return QKeySequence(self.S.Hotkey).toString(QKeySequence.SequenceFormat.NativeText)

    # ---- tray ----
    def build_menu(self) -> QMenu:
        m = QMenu()
        shot = m.addAction(T("Сделать снимок"), lambda: self.capture(False))
        f = shot.font()
        f.setBold(True)
        shot.setFont(f)
        if self.S.HookPrintScreen and self.hotkey_supported_cached():
            shot.setShortcut(self.S.Hotkey)
            shot.setShortcutVisibleInContextMenu(True)
        m.addAction(T("Снимок экрана целиком"), lambda: self.capture(True))
        m.addSeparator()
        m.addAction(T("Загрузить картинку с компьютера…"), self.upload_from_disk)
        m.addAction(T("Загрузить картинку из буфера"), self.upload_from_clipboard)
        self.dest_menu = m.addMenu(T("Куда загружать"))
        self.dest_menu.aboutToShow.connect(self._fill_destinations)
        m.addSeparator()
        m.addAction(T("История загрузок"), self.show_history)
        m.addAction(T("Настройки"), self.show_settings)
        m.addSeparator()
        m.addAction(T("Выход"), self.quit)
        self._menu = m
        return m

    def hotkey_supported_cached(self):
        return getattr(self, "hotkey", None) is not None and self.hotkey.supported()

    def _fill_destinations(self):
        self.dest_menu.clear()
        for d in self.S.destinations():
            a = self.dest_menu.addAction(d)
            a.setCheckable(True)
            a.setChecked(d == self.S.default())
            a.triggered.connect(lambda checked=False, dest=d: (self.S.set(DefaultDestination=dest), self.S.save()))
        self.dest_menu.addSeparator()
        self.dest_menu.addAction(T("Добавить свой сервис…"), self.show_settings)

    def _tray_activated(self, reason):
        if reason == QSystemTrayIcon.ActivationReason.Trigger and not platform_utils.MAC:
            self.capture(False)

    def apply_hotkey(self, first=False):
        if self.S.HookPrintScreen and self.hotkey.supported():
            if not self.hotkey.register(self.S.Hotkey):
                msg = F("Сочетание {0} занято другой программой. Выбери другое в настройках.", self.key_text())
                QTimer.singleShot(1500 if first else 0, lambda: toast.error(T("Кадр"), msg))
        elif not self.S.HookPrintScreen:
            self.hotkey.disable()
        else:
            self.hotkey.unregister()
        self.tray.setToolTip(F("Кадр — снимок по {0}", self.key_text()) if self.hotkey.supported() and self.S.HookPrintScreen
                             else T("Кадр — щёлкни для снимка"))

    # ---- messages from a second start ("kadr --capture", "kadr file.png") ----
    def handle_args(self, args: list[str]):
        files = [a for a in args if not a.startswith("--") and Path(a).is_file()]
        if files:
            self.upload_files(files)
        elif "--full" in args:
            self.capture(True)
        elif "--capture" in args or not [a for a in args if a == "--tray"]:
            self.capture(False)

    # ---- capture ----
    def capture(self, full: bool):
        if self.session is not None:
            return
        if platform_utils.MAC and not platform_utils.screen_capture_allowed(ask=True):
            t = toast.error(T("Нужен доступ к записи экрана"), T("mac.permission"), 20)
            if hasattr(t, "on_click"):
                t.on_click = platform_utils.open_screen_recording_settings
            platform_utils.open_screen_recording_settings()
            return
        try:
            shots = capture.grab_all()
        except Exception as e:
            toast.error(T("Не удалось снять экран"), T("Снимок не получен: система отказала или окно выбора закрыли.") + f" ({e})")
            return
        if not shots:
            return
        self.session = CaptureSession(self.S, shots, capture.window_list(), full)
        self.session.finished.connect(self._captured)
        self.session.show()
        if os.environ.get("KADR_SELFTEST"):
            QTimer.singleShot(1500, self._selftest_snapshot)

    def _selftest_snapshot(self):
        """Build check only: save what each monitor's overlay shows, then close it."""
        s = self.session
        if s is None:
            return
        out = Path(os.environ["KADR_SELFTEST"])
        lines = []
        for i, w in enumerate(s.wins):
            w.grab().scaled(1600, 1600, Qt.AspectRatioMode.KeepAspectRatio).save(str(out / f"qt-live-{i}.png"))
            lines.append(f"screen{i} geo={s.shots[i].geo.getRect()} img={s.shots[i].image.width()}x{s.shots[i].image.height()} "
                         f"dpr={s.shots[i].dpr} visible={w.isVisible()} win={w.geometry().getRect()}")
        lines.append(f"windows={len(s.windows_global)} active_window_ok={QApplication.activeWindow() is not None}")
        (out / "qt-live.txt").write_text("\n".join(lines), encoding="utf-8")
        s.cancel()

    def _captured(self, action, img, dest):
        self.session = None
        self.S.save()
        QTimer.singleShot(1500, trim_memory)
        if not action or img is None:
            return
        if action == "copy":
            QGuiApplication.clipboard().setImage(img)
        elif action == "save":
            self.save(img)
        elif action == "print":
            self.print_image(img)
        elif action == "upload":
            self.upload(encode(img, self.S.jpeg, self.S.JpegQuality), shot_name(self.S.ext), dest or self.S.default(), img, True)

    # ---- save / print ----
    def save(self, img: QImage):
        s = self.S
        folder = Path(s.SaveFolder)
        name = shot_name(s.ext)
        if s.AskFileName:
            folder.mkdir(parents=True, exist_ok=True)
            filters = "PNG (*.png);;JPEG (*.jpg *.jpeg)"
            path, _ = QFileDialog.getSaveFileName(None, T("Сохранить (Ctrl+S)").split(" (")[0], str(folder / name), filters,
                                                  "JPEG (*.jpg *.jpeg)" if s.jpeg else "PNG (*.png)")
            if not path:
                return
            if not Path(path).suffix:
                path += s.ext
            s.set(SaveFolder=str(Path(path).parent))
            s.save()
        else:
            folder.mkdir(parents=True, exist_ok=True)
            path = str(folder / name)
        jpeg = Path(path).suffix.lower() in (".jpg", ".jpeg")
        try:
            Path(path).write_bytes(encode(img, jpeg, s.JpegQuality))
        except Exception as e:
            toast.error(T("Не удалось сохранить"), str(e))
            return
        if s.ShowNotifications:
            t = toast.info(img, T("Снимок сохранён"), path, lambda: platform_utils.open_file(path), 8)
            t.set_button("folder", lambda: platform_utils.reveal(path), False)

    def print_image(self, img: QImage):
        from PySide6.QtPrintSupport import QPrintDialog, QPrinter
        from PySide6.QtGui import QPageLayout
        printer = QPrinter(QPrinter.PrinterMode.HighResolution)
        printer.setDocName(shot_name(""))
        printer.setPageOrientation(QPageLayout.Orientation.Landscape if img.width() > img.height() else QPageLayout.Orientation.Portrait)
        dlg = QPrintDialog(printer)
        platform_utils.activate_app()
        if dlg.exec() != QPrintDialog.DialogCode.Accepted:
            return
        try:
            p = QPainter(printer)
            page = printer.pageRect(QPrinter.Unit.DevicePixel)
            res = printer.resolution()
            w, h = img.width() * res / 96.0, img.height() * res / 96.0
            k = min(1.0, page.width() / w, page.height() / h)
            w, h = w * k, h * k
            p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
            p.drawImage(QRectF((page.width() - w) / 2, 0, w, h), img)
            p.end()
        except Exception as e:
            toast.error(T("Не удалось напечатать"), str(e))

    # ---- upload ----
    def upload(self, data: bytes, name: str, dest: str, thumb: QImage | None, is_shot: bool):
        s = self.S
        notice = toast.show(thumb, F("Загружаю в {0}…", dest), name) if s.ShowNotifications else None

        def work():
            try:
                r = uploader.upload(s, dest, data, name)
                self.uploaded.emit((notice, dest, name, data, thumb, is_shot, r, None))
            except Exception as e:
                self.uploaded.emit((notice, dest, name, data, thumb, is_shot, None, e))

        threading.Thread(target=work, daemon=True).start()

    def _uploaded(self, payload):
        notice, dest, name, data, thumb, is_shot, r, err = payload
        s = self.S
        if err is None:
            if s.CopyLink:
                QGuiApplication.clipboard().setText(r.url)
            st.add_history({"Destination": dest, "Url": r.url, "DeleteUrl": r.delete_url, "ImgurDeleteHash": r.imgur_hash}, thumb)
            if notice is not None:
                url = r.url
                notice.done(T("Ссылка скопирована") if s.CopyLink else F("Загружено в {0}", dest), url,
                            lambda: platform_utils.open_url(url), False, 7, url)
            if s.OpenInBrowser:
                platform_utils.open_url(r.url)
            return
        msg = str(err) if isinstance(err, uploader.UserError) else _describe(err)
        saved = None
        if is_shot:
            try:
                folder = Path(s.SaveFolder)
                folder.mkdir(parents=True, exist_ok=True)
                saved = str(folder / name)
                Path(saved).write_bytes(data)
                msg += " " + T("Снимок сохранён в папку.")
            except Exception:
                saved = None
        if notice is None:
            notice = toast.show(thumb, "", name)
        notice.done(T("Не получилось загрузить"), msg, (lambda: platform_utils.reveal(saved)) if saved else None, True, 15)

    def upload_files(self, paths):
        for pth in paths:
            try:
                data = Path(pth).read_bytes()
            except Exception as e:
                toast.error(T("Не открылся файл"), f"{Path(pth).name}: {e}")
                continue
            thumb = QImage(pth)
            self.upload(data, Path(pth).name, self.S.default(), None if thumb.isNull() else thumb, False)

    def upload_from_disk(self):
        platform_utils.activate_app()
        files, _ = QFileDialog.getOpenFileNames(None, F("Какие картинки загрузить в {0}?", self.S.default()), "",
                                                f"{T('Картинки')} ({IMAGE_FILTER});;{T('Все файлы')} (*)")
        if files:
            self.upload_files(files)

    def upload_from_clipboard(self):
        cb = QGuiApplication.clipboard()
        md = cb.mimeData()
        if md is not None and md.hasUrls():
            files = [u.toLocalFile() for u in md.urls() if u.isLocalFile() and Path(u.toLocalFile()).is_file()]
            if files:
                self.upload_files(files)
                return
        img = cb.image()
        if not img.isNull():
            self.upload(encode(img, self.S.jpeg, self.S.JpegQuality), shot_name(self.S.ext), self.S.default(), img, True)
            return
        toast.info(None, T("В буфере нет картинки"), T("Скопируй картинку или файл и попробуй ещё раз."))

    # ---- windows ----
    def show_settings(self):
        if self.settings_dlg is not None:
            self.settings_dlg.raise_()
            self.settings_dlg.activateWindow()
            return
        self.hotkey.unregister()          # so the shortcut can be typed into the box
        d = SettingsDialog(self.S, self.hotkey.supported())
        self.settings_dlg = d

        def closed(_):
            self.settings_dlg = None
            if d.language_changed:
                old = self._menu
                self.tray.setContextMenu(self.build_menu())
                old.deleteLater()
            self.apply_hotkey()

        d.finished.connect(closed)
        d.show()
        platform_utils.activate_app()
        d.raise_()
        d.activateWindow()

    def show_history(self):
        if self.history_dlg is not None:
            self.history_dlg.raise_()
            self.history_dlg.activateWindow()
            return
        d = HistoryDialog(self.S)
        self.history_dlg = d
        d.finished.connect(lambda _: setattr(self, "history_dlg", None))
        d.show()
        platform_utils.activate_app()
        d.raise_()
        d.activateWindow()

    def quit(self):
        self.hotkey.unregister()
        self.tray.hide()
        self.S.save()
        self.qapp.quit()


def _describe(e: Exception) -> str:
    import requests
    if isinstance(e, requests.Timeout):
        return T("Сервер не ответил вовремя.")
    if isinstance(e, requests.ConnectionError):
        return F("Нет связи с сервером ({0})", e.__class__.__name__)
    return str(e)


def trim_memory():
    """The monitor pictures of a capture are big: collect them now and hand the memory back to the system."""
    import gc
    gc.collect()
    if platform_utils.LINUX:
        try:
            import ctypes
            ctypes.CDLL("libc.so.6").malloc_trim(0)
        except Exception:
            pass


def install_qt_translation(qapp):
    """Standard Qt texts (file and print dialogs on Linux) in the interface language."""
    from PySide6.QtCore import QLibraryInfo, QTranslator
    if i18n.current() == "en":
        return None
    tr = QTranslator(qapp)
    if tr.load("qtbase_" + i18n.current(), QLibraryInfo.path(QLibraryInfo.LibraryPath.TranslationsPath)):
        qapp.installTranslator(tr)
        return tr
    return None


class _FileOpenFilter(QObject):
    """macOS 'Open With → Kadr' and dropping files on the app icon."""

    def __init__(self, app):
        super().__init__()
        self.app = app
        self.pending = []

    def eventFilter(self, obj, ev):
        if ev.type() == QEvent.Type.FileOpen:
            path = ev.file()
            if self.app is not None:
                self.app.upload_files([path])
            else:
                self.pending.append(path)
            return True
        return False


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv[:1] == ["--render-test"]:
        from .selftest import render_test
        return render_test(argv[1])
    if argv[:1] == ["--paint-check"]:
        from .paintcheck import run
        return run()
    if argv[:1] == ["--upload-test"]:
        from .selftest import upload_test
        return upload_test(argv[1], argv[2])

    QApplication.setApplicationName("Kadr")
    QApplication.setOrganizationName("Kadr")
    qapp = QApplication(sys.argv[:1])
    qapp.setQuitOnLastWindowClosed(False)
    qapp.setWindowIcon(QIcon(app_icon_pixmap(256)))

    # one copy only: later starts pass their arguments to the running one
    server_name = "kadr-" + getpass.getuser()
    sock = QLocalSocket()
    sock.connectToServer(server_name)
    if sock.waitForConnected(300):
        sock.write(json.dumps(argv).encode("utf-8"))
        sock.flush()
        sock.waitForBytesWritten(1000)
        sock.disconnectFromServer()
        return 0

    file_filter = _FileOpenFilter(None)
    qapp.installEventFilter(file_filter)

    kadr = KadrApp(qapp, quiet="--tray" in argv)
    kadr._qt_tr = install_qt_translation(qapp)
    QTimer.singleShot(4000, trim_memory)
    file_filter.app = kadr
    for pth in file_filter.pending:
        kadr.upload_files([pth])

    QLocalServer.removeServer(server_name)
    server = QLocalServer()
    server.listen(server_name)

    def incoming():
        c = server.nextPendingConnection()

        def read():
            try:
                kadr.handle_args(json.loads(bytes(c.readAll()).decode("utf-8") or "[]"))
            except Exception:
                pass

        c.readyRead.connect(read)
        c.disconnected.connect(c.deleteLater)

    server.newConnection.connect(incoming)

    files = [a for a in argv if not a.startswith("--") and Path(a).is_file()]
    if files:
        QTimer.singleShot(300, lambda: kadr.upload_files(files))
    elif "--capture" in argv or "--full" in argv:
        QTimer.singleShot(300, lambda: kadr.capture("--full" in argv))

    code = qapp.exec()
    # Leave without Python tearing down Qt objects one by one (PySide can crash there); settings are saved already.
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(code)
