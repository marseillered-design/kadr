"""Build checks: draw the overlay, dialogs and notices into PNG files; upload with a given service config."""
import json
import math
import os
import sys
from pathlib import Path

from PySide6.QtCore import QPointF, QRect, QRectF, Qt
from PySide6.QtGui import QColor, QFont, QGuiApplication, QImage, QLinearGradient, QPainter
from PySide6.QtWidgets import QApplication


def _fake_screen(w, h, dpr=1.0) -> QImage:
    img = QImage(int(w * dpr), int(h * dpr), QImage.Format.Format_RGB32)
    img.setDevicePixelRatio(dpr)
    p = QPainter(img)
    g = QLinearGradient(0, 0, w, h)
    g.setColorAt(0, QColor(235, 240, 248))
    g.setColorAt(1, QColor(200, 214, 235))
    p.fillRect(QRectF(0, 0, w, h), g)
    f = QFont()
    f.setPixelSize(22)
    p.setFont(f)
    p.setPen(QColor(105, 105, 105))
    for y in range(40, h, 46):
        p.drawText(QPointF(40, y + 18), f"Пример текста на экране — строка {y // 46}, имя: Иван Петров, телефон +380 00 000 0000")
    p.fillRect(QRectF(900, 520, 300, 160), QColor(70, 130, 180))
    p.end()
    return img


def render_test(out_dir: str) -> int:
    app = QApplication.instance() or QApplication(sys.argv[:1])
    from . import i18n, settings as st
    from .overlay import CaptureSession, Shot
    i18n.set_language(os.environ.get("KADR_LANG", "ru"))
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    s = st.Settings()
    s.Uploaders.append(st.normalize_uploader({"Name": "Мой сервер", "RequestURL": "https://example.com/upload"}))
    screen = QGuiApplication.primaryScreen()
    W, H = 1600, 900

    for dpr in (1.0, 2.0):
        shot = Shot(screen, QRect(0, 0, W, H), _fake_screen(W, H, dpr))
        sess = CaptureSession(s, [shot])
        sess.test_scene()
        img = QImage(W, H, QImage.Format.Format_ARGB32_Premultiplied)
        p = QPainter(img)
        sess.paint(0, p, QRect(0, 0, W, H))
        p.end()
        img.save(str(out / f"qt-overlay-{int(dpr * 100)}.png"))
        sess.render().save(str(out / f"qt-output-{int(dpr * 100)}.png"))
        sess.close_all()

    shot = Shot(screen, QRect(0, 0, W, H), _fake_screen(W, H))
    sess = CaptureSession(s, [shot], [QRectF(500, 300, 520, 330), QRectF(120, 80, 900, 600)])
    sess.test_hover(QPointF(700, 420))
    img = QImage(W, H, QImage.Format.Format_ARGB32_Premultiplied)
    p = QPainter(img)
    sess.paint(0, p, QRect(0, 0, W, H))
    p.end()
    img.save(str(out / "qt-overlay-hover.png"))
    sess.close_all()

    from .dialogs import HistoryDialog, SettingsDialog, UploaderDialog
    for name, w in (("qt-settings", SettingsDialog(s, True)), ("qt-settings-wayland", SettingsDialog(s, False)),
                    ("qt-uploader", UploaderDialog(s, s.Uploaders[0])), ("qt-history", HistoryDialog(s))):
        w.show()
        app.processEvents()
        w.grab().save(str(out / f"{name}.png"))
        w.close()

    from . import toast
    t = toast.show(_fake_screen(800, 450), "Ссылка скопирована", "https://files.catbox.moe/gmn4ey.png")
    t.done(i18n.T("Ссылка скопирована"), "https://files.catbox.moe/gmn4ey.png", None, False, 7, "https://files.catbox.moe/gmn4ey.png")
    app.processEvents()
    t.grab().save(str(out / "qt-toast.png"))
    t.close()
    t = toast.info(_fake_screen(800, 450), i18n.T("Снимок сохранён"), "/home/me/Pictures/Kadr/Screenshot 2026-11-23 19-40-00.png")
    t.set_button("folder", lambda: None, False)
    app.processEvents()
    t.grab().save(str(out / "qt-toast-saved.png"))
    t.close()

    from .app import app_icon_pixmap
    app_icon_pixmap(256).save(str(out / "qt-icon.png"))
    print("ok", flush=True)
    os._exit(0)


def upload_test(config_path: str, image_path: str) -> int:
    from . import settings as st, uploader
    s = st.Settings()
    if os.environ.get("KADR_PROXY"):
        s.set(ProxyMode="custom", ProxyAddress=os.environ["KADR_PROXY"], ProxyUser="u", ProxyPasswordFallback="p")
        s.proxy_password = lambda: "p"
    u = st.normalize_uploader(json.loads(Path(config_path).read_text(encoding="utf-8")))
    try:
        r = uploader.custom(s, u, Path(image_path).read_bytes(), Path(image_path).name)
        print("URL=" + r.url)
        print("DEL=" + str(r.delete_url))
        return 0
    except Exception as e:
        print("ERROR=" + type(e).__name__ + ": " + str(e))
        return 1
