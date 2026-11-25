"""Small notices in the corner: upload progress, the link with a copy button, 'saved' with a folder button.
On Wayland apps cannot place windows, so notices go through the system notifications instead."""
from PySide6.QtCore import QPointF, QRectF, Qt, QTimer
from PySide6.QtGui import QColor, QFont, QFontMetrics, QGuiApplication, QImage, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QWidget

from . import platform_utils
from .overlay import draw_icon

_open = []
tray_icon = None          # set by the app, for the Wayland fallback


class Toast(QWidget):
    W, H = 400, 88

    def __init__(self, thumb: QImage | None, title: str, text: str):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint |
                         Qt.WindowType.Tool | Qt.WindowType.WindowDoesNotAcceptFocus)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        self.setFixedSize(self.W, self.H)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.thumb = None
        if thumb is not None and not thumb.isNull():
            self.thumb = thumb.scaled(88, 64, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
        self.title, self.text = title, text
        self.busy, self.error = True, False
        self.on_click = None
        self.btn_icon, self.btn_action, self.btn_tick = None, None, False
        self.btn_hot = self.copied = self.hot = False
        self.btn2_icon, self.btn2_action, self.btn2_hot = None, None, False
        self.phase = 0
        self.dark = platform_utils.dark_mode()
        self.anim = QTimer(self)
        self.anim.timeout.connect(self._step)
        self.anim.start(30)
        self.hide_timer = QTimer(self)
        self.hide_timer.setSingleShot(True)
        self.hide_timer.timeout.connect(self._auto_hide)

    # ---- placement ----
    @staticmethod
    def reposition_all():
        scr = QGuiApplication.primaryScreen().availableGeometry()
        gap = 12
        y = scr.bottom() - gap
        for t in _open:
            y -= t.height()
            t.move(scr.right() - t.width() - gap, y)
            y -= gap

    # ---- api ----
    def done(self, title, text, on_click=None, error=False, seconds=7, copy_text=None):
        self.busy, self.error = False, error
        self.title, self.text, self.on_click = title, text, on_click
        if copy_text:
            self.set_button("copy", lambda: QGuiApplication.clipboard().setText(copy_text), True)
        self.anim.stop()
        self.hide_timer.start(seconds * 1000)
        self.update()

    def set_button(self, icon, action, tick):
        self.btn_icon, self.btn_action, self.btn_tick = icon, action, tick
        self.update()

    def add_button(self, icon, action):
        """A second icon button, left of the first (e.g. upload the video); closes the notice when pressed."""
        self.btn2_icon, self.btn2_action = icon, action
        self.update()

    # ---- painting ----
    def btn_rect(self):
        return QRectF(self.W - 44, self.H - 44, 34, 34)

    def btn2_rect(self):
        return QRectF(self.W - 82, self.H - 44, 34, 34)

    def close_rect(self):
        return QRectF(self.W - 28, 6, 22, 22)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        bg = QColor(43, 43, 46) if self.dark else QColor(255, 255, 255)
        fg = QColor(240, 240, 240) if self.dark else QColor(28, 28, 30)
        if self.busy:
            sub = QColor(170, 170, 170) if self.dark else QColor(110, 110, 110)
        elif self.error:
            sub = QColor(229, 57, 53)
        else:
            sub = QColor(150, 190, 255) if self.dark else QColor(25, 103, 210)
        path = QPainterPath()
        path.addRoundedRect(QRectF(0.5, 0.5, self.W - 1, self.H - 1), 8, 8)
        p.fillPath(path, bg)
        p.setPen(QPen(QColor(70, 70, 74) if self.dark else QColor(215, 215, 220), 1))
        p.drawPath(path)
        x = 12
        if self.thumb is not None:
            ty = (self.H - self.thumb.height()) / 2
            p.drawImage(QPointF(x, ty), self.thumb)
            x += 88 + 12
        right = self.close_rect().left() - 4
        tf = QFont()
        tf.setPixelSize(14)
        tf.setWeight(QFont.Weight.DemiBold)
        p.setFont(tf)
        p.setPen(fg)
        p.drawText(QRectF(x, 16, right - x, 22), Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
                   QFontMetrics(tf).elidedText(self.title, Qt.TextElideMode.ElideRight, int(right - x)))
        f = QFont()
        f.setPixelSize(13)
        p.setFont(f)
        p.setPen(sub)
        text_right = self.btn2_rect().left() - 6 if self.btn2_icon else self.btn_rect().left() - 6 if self.btn_icon else self.W - 12
        p.drawText(QRectF(x, 42, text_right - x, 38), Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignTop | Qt.TextFlag.TextWordWrap,
                   self.text or "")
        cr = self.close_rect()
        ink = QColor(170, 170, 170) if self.dark else QColor(120, 120, 120)
        draw_icon(p, "close", cr, ink, ink)
        if self.btn_icon:
            br = self.btn_rect()
            if self.btn_hot or self.copied:
                hp = QPainterPath()
                hp.addRoundedRect(br, 6, 6)
                p.fillPath(hp, QColor(62, 62, 66) if self.dark else QColor(234, 236, 240))
            col = QColor(67, 160, 71) if self.copied else (QColor(225, 225, 225) if self.dark else QColor(50, 50, 54))
            if self.copied:
                p.setPen(QPen(col, 2.2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
                c = br.center()
                p.drawPolyline([QPointF(c.x() - 6, c.y()), QPointF(c.x() - 1.5, c.y() + 4.5), QPointF(c.x() + 6.5, c.y() - 5)])
            else:
                draw_icon(p, self.btn_icon, br, col, col)
        if self.btn2_icon:
            b2 = self.btn2_rect()
            if self.btn2_hot:
                hp = QPainterPath()
                hp.addRoundedRect(b2, 6, 6)
                p.fillPath(hp, QColor(62, 62, 66) if self.dark else QColor(234, 236, 240))
            col = QColor(225, 225, 225) if self.dark else QColor(50, 50, 54)
            draw_icon(p, self.btn2_icon, b2, col, col)
        if self.busy:
            w = self.W // 3
            pos = (self.phase * 8) % (self.W + w) - w
            p.fillRect(QRectF(pos, self.H - 3, w, 3), QColor(61, 90, 254))
        p.end()

    def _step(self):
        self.phase += 1
        self.update()

    def _auto_hide(self):
        if self.hot:
            self.hide_timer.start(1500)
        else:
            self.close()

    # ---- mouse ----
    def enterEvent(self, e):
        self.hot = True

    def leaveEvent(self, e):
        self.hot = False
        if self.btn_hot or self.btn2_hot:
            self.btn_hot = self.btn2_hot = False
            self.update()

    def mouseMoveEvent(self, e):
        h = bool(self.btn_icon) and self.btn_rect().contains(e.position())
        h2 = bool(self.btn2_icon) and self.btn2_rect().contains(e.position())
        if h != self.btn_hot or h2 != self.btn2_hot:
            self.btn_hot, self.btn2_hot = h, h2
            self.update()

    def mouseReleaseEvent(self, e):
        pos = e.position()
        if self.close_rect().contains(pos) or e.button() == Qt.MouseButton.RightButton:
            self.close()
            return
        if self.btn2_icon and self.btn2_rect().contains(pos):
            action = self.btn2_action
            self.close()
            if action:
                action()
            return
        if self.btn_icon and self.btn_rect().contains(pos):
            self.btn_action()
            if not self.btn_tick:
                self.close()
                return
            self.copied = True
            self.update()
            self.hide_timer.start(7000)
            QTimer.singleShot(1500, self._untick)
            return
        if self.on_click:
            self.on_click()
        if not self.busy:
            self.close()

    def _untick(self):
        self.copied = False
        self.update()

    def closeEvent(self, e):
        if self in _open:
            _open.remove(self)
        Toast.reposition_all()
        super().closeEvent(e)


class _SystemNotice:
    """Wayland: the same calls, shown as a system notification when finished."""

    def __init__(self, title, text):
        self.title, self.text = title, text

    def done(self, title, text, on_click=None, error=False, seconds=7, copy_text=None):
        if tray_icon is not None:
            from PySide6.QtWidgets import QSystemTrayIcon
            icon = QSystemTrayIcon.MessageIcon.Warning if error else QSystemTrayIcon.MessageIcon.Information
            tray_icon.showMessage(title, text or "", icon, seconds * 1000)

    def set_button(self, *a):
        pass

    def add_button(self, *a):
        pass

    def close(self):
        pass


def show(thumb, title, text):
    if platform_utils.is_wayland():
        return _SystemNotice(title, text)
    t = Toast(thumb, title, text)
    _open.append(t)
    Toast.reposition_all()
    t.show()
    return t


def info(thumb, title, text, on_click=None, seconds=6):
    t = show(thumb, title, text)
    t.done(title, text, on_click, False, seconds)
    if isinstance(t, _SystemNotice):
        return t
    return t


def error(title, text, seconds=12):
    t = show(None, title, text)
    t.done(title, text, None, True, seconds)
    return t
