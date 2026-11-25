"""The windows around a recording (macOS and Linux): a red outline round the area, a small bar with time, pause,
stop and discard, and — on Wayland, where notices have no buttons — a small "video recorded" window."""
from PySide6.QtCore import QPointF, QRect, QRectF, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QFont, QPainter, QPainterPath, QPen, QRegion
from PySide6.QtWidgets import QDialog, QHBoxLayout, QLabel, QPushButton, QToolTip, QVBoxLayout, QWidget

from . import platform_utils
from .i18n import T, F

RED, AMBER = QColor(229, 57, 53), QColor(251, 140, 0)


def hide_from_capture(w: QWidget):
    """macOS: the window stays visible but is left out of screen recordings (NSWindowSharingNone).
    Linux has no such switch, so the bar is placed outside the recorded area instead."""
    if not platform_utils.MAC:
        return
    try:
        import objc
        view = objc.objc_object(c_void_p=int(w.winId()))
        view.window().setSharingType_(0)
    except Exception:
        pass


def duration(seconds: float) -> str:
    s = int(seconds)
    return f"{s // 3600}:{s % 3600 // 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60}:{s % 60:02d}"


def file_size(n: int) -> str:
    if n < 1024 * 1024:
        return F("{0} КБ", max(1, n // 1024))
    mb = n / 1048576
    from . import i18n
    text = f"{mb:.1f}" if mb < 100 else f"{mb:.0f}"
    return F("{0} МБ", text if i18n.current() == "en" else text.replace(".", ","))


class RecordFrame(QWidget):
    """A red outline just outside the area; clicks go through it (a window shaped like a ring)."""
    W = 3

    def __init__(self, area: QRect):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool |
                         Qt.WindowType.WindowTransparentForInput | Qt.WindowType.WindowDoesNotAcceptFocus |
                         Qt.WindowType.X11BypassWindowManagerHint)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self.paused = False
        r = area.adjusted(-self.W, -self.W, self.W, self.W)
        self.setGeometry(r)
        outer = QRegion(0, 0, r.width(), r.height())
        self.setMask(outer.subtracted(QRegion(self.W, self.W, r.width() - 2 * self.W, r.height() - 2 * self.W)))

    def showEvent(self, e):
        super().showEvent(e)
        hide_from_capture(self)

    def set_paused(self, p):
        self.paused = p
        self.update()

    def paintEvent(self, e):
        p = QPainter(self)
        p.fillRect(self.rect(), AMBER if self.paused else RED)
        p.end()


class RecordPanel(QWidget):
    """Time, pause/continue, stop and discard. Drag it by the time."""
    pause_clicked = Signal()
    stop_clicked = Signal()
    discard_clicked = Signal()

    def __init__(self, area: QRect, elapsed, stop_key=""):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool |
                         Qt.WindowType.WindowDoesNotAcceptFocus)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self.setMouseTracking(True)
        self.elapsed = elapsed
        self.stop_key = stop_key
        self.paused = False
        self.hot = -1
        self.phase = 0
        self.drag = None
        self.setFixedSize(4 + 92 + 3 * 32 + 4, 40)
        self.btns = [QRectF(96, 4, 32, 32), QRectF(128, 4, 32, 32), QRectF(160, 4, 32, 32)]
        self.move(self._place(area))
        self.timer = QTimer(self)
        self.timer.timeout.connect(self._tick)
        self.timer.start(250)

    def _place(self, area: QRect):
        """Below the area on the right, else above, else inside its bottom-right corner (macOS hides it from the video)."""
        from PySide6.QtGui import QGuiApplication
        scr = QGuiApplication.screenAt(area.center()) or QGuiApplication.primaryScreen()
        wa = scr.availableGeometry()
        gap = 8
        x = max(wa.left() + gap, min(area.right() - self.width(), wa.right() - self.width() - gap))
        if area.bottom() + gap + self.height() <= wa.bottom():
            return QRect(x, area.bottom() + gap, 0, 0).topLeft()
        if area.top() - gap - self.height() >= wa.top():
            return QRect(x, area.top() - gap - self.height(), 0, 0).topLeft()
        return QRect(min(area.right(), wa.right()) - self.width() - 3 * gap, min(area.bottom(), wa.bottom()) - self.height() - 3 * gap, 0, 0).topLeft()

    def showEvent(self, e):
        super().showEvent(e)
        hide_from_capture(self)

    def _tick(self):
        self.phase += 1
        self.update()

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        path = QPainterPath()
        path.addRoundedRect(QRectF(0.5, 0.5, self.width() - 1, self.height() - 1), 8, 8)
        p.fillPath(path, QColor(32, 32, 34))
        p.setPen(QPen(QColor(70, 70, 74), 1))
        p.drawPath(path)
        if self.paused or self.phase % 4 < 3:
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(AMBER if self.paused else RED)
            p.drawEllipse(QPointF(17, 20), 5, 5)
        f = QFont()
        f.setPixelSize(14)
        f.setWeight(QFont.Weight.DemiBold)
        p.setFont(f)
        p.setPen(QColor(251, 180, 80) if self.paused else QColor(255, 255, 255))
        p.drawText(QRectF(30, 0, 64, 40), Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft, duration(self.elapsed()))
        for i, r in enumerate(self.btns):
            if self.hot == i:
                hp = QPainterPath()
                hp.addRoundedRect(r.adjusted(2, 2, -2, -2), 6, 6)
                p.fillPath(hp, QColor(62, 62, 66))
            c = r.center()
            if i == 0:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(255, 255, 255))
                if self.paused:   # play triangle
                    from PySide6.QtGui import QPolygonF
                    p.drawPolygon(QPolygonF([QPointF(c.x() - 4, c.y() - 6), QPointF(c.x() + 6, c.y()), QPointF(c.x() - 4, c.y() + 6)]))
                else:
                    p.drawRect(QRectF(c.x() - 5, c.y() - 6, 3.5, 12))
                    p.drawRect(QRectF(c.x() + 1.5, c.y() - 6, 3.5, 12))
            elif i == 1:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(255, 110, 100))
                p.drawRoundedRect(QRectF(c.x() - 6, c.y() - 6, 12, 12), 2, 2)
            else:   # bin
                p.setPen(QPen(QColor(200, 200, 200), 1.6))
                p.setBrush(Qt.BrushStyle.NoBrush)
                p.drawLine(QPointF(c.x() - 6, c.y() - 5), QPointF(c.x() + 6, c.y() - 5))
                p.drawLine(QPointF(c.x() - 2, c.y() - 7), QPointF(c.x() + 2, c.y() - 7))
                p.drawRoundedRect(QRectF(c.x() - 4.5, c.y() - 4, 9, 11), 1.5, 1.5)
        p.end()

    def _hit(self, pos):
        for i, r in enumerate(self.btns):
            if r.contains(pos):
                return i
        return -1

    def mouseMoveEvent(self, e):
        if self.drag is not None:
            self.move(e.globalPosition().toPoint() - self.drag)
            return
        h = self._hit(e.position())
        if h != self.hot:
            self.hot = h
            self.setCursor(Qt.CursorShape.PointingHandCursor if h >= 0 else Qt.CursorShape.SizeAllCursor)
            tip = [(T("Продолжить") if self.paused else T("Пауза")),
                   (F("Остановить и сохранить ({0})", self.stop_key) if self.stop_key else T("Остановить и сохранить")),
                   T("Остановить и удалить")][h] if h >= 0 else ""
            if tip:
                QToolTip.showText(e.globalPosition().toPoint(), tip, self)
            else:
                QToolTip.hideText()
            self.update()

    def leaveEvent(self, e):
        self.hot = -1
        self.update()

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton and self._hit(e.position()) < 0:
            self.drag = e.globalPosition().toPoint() - self.pos()

    def mouseReleaseEvent(self, e):
        if self.drag is not None:
            self.drag = None
            return
        h = self._hit(e.position())
        if h == 0:
            self.pause_clicked.emit()
        elif h == 1:
            self.stop_clicked.emit()
        elif h == 2:
            self.discard_clicked.emit()

    def set_paused(self, p):
        self.paused = p
        self.update()


class VideoDoneDialog(QDialog):
    """Wayland: system notices have no buttons, so the finished video gets a small window."""

    def __init__(self, title, text, on_open, on_folder, on_upload, upload_label):
        super().__init__(None)
        self.setWindowTitle(T("Кадр"))
        v = QVBoxLayout(self)
        t = QLabel(f"<b>{title}</b><br>{text}")
        t.setWordWrap(True)
        v.addWidget(t)
        row = QHBoxLayout()
        for label, fn in ((T("Открыть"), on_open), (T("Показать в папке"), on_folder), (upload_label, on_upload)):
            b = QPushButton(label)
            b.clicked.connect(lambda checked=False, f=fn: (self.accept(), f()))
            row.addWidget(b)
        v.addLayout(row)
