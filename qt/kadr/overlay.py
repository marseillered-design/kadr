"""The capture overlay: one full-screen window per monitor showing the frozen screenshot; select an area,
draw on it, then upload / copy / save / print. All editor state lives in CaptureSession; the windows only
forward events and paint their own monitor."""
import sys

from PySide6.QtCore import QObject, QPoint, QPointF, QRect, QRectF, QSize, Qt, QTimer, Signal
from PySide6.QtGui import (QColor, QCursor, QFont, QFontMetricsF, QGuiApplication, QImage, QKeySequence,
                           QPainter, QPainterPath, QPen, QPolygonF, QRadialGradient)
from PySide6.QtWidgets import QMenu, QWidget

from . import i18n, platform_utils
from .i18n import T, F
from .shapes import Blur, Line, Rect, Step, Stroke, Text, norm

COLORS = ["#e53935", "#fb8c00", "#fdd835", "#43a047", "#1e88e5", "#8e24aa", "#212121", "#ffffff"]
INK = QColor(45, 45, 48)
ACCENT = QColor(30, 136, 229)
MAC = sys.platform == "darwin"


class Shot:
    """One monitor: its screen, logical geometry and the frozen picture (device pixels, dpr set)."""

    def __init__(self, screen, geometry: QRect, image: QImage):
        self.screen = screen
        self.geo = QRect(geometry)
        self.dpr = image.devicePixelRatio() or 1.0
        # premultiplied ARGB is what the raster painter draws without converting; the dimming is painted on the fly
        self.image = image.convertToFormat(QImage.Format.Format_ARGB32_Premultiplied)
        self.image.setDevicePixelRatio(self.dpr)


class Btn:
    def __init__(self, cmd, tip, tool=None, vertical=False):
        self.cmd, self.tip, self.tool, self.vertical = cmd, tip, tool, vertical
        self.r = QRectF()


def _key_tip(key, letter):
    return T(key)


class OverlayWindow(QWidget):
    def __init__(self, session, index):
        super().__init__(None, Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool)
        self.session, self.index = session, index
        self.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        self.setAttribute(Qt.WidgetAttribute.WA_OpaquePaintEvent)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.CrossCursor)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.setWindowTitle(T("Кадр"))

    def show_on(self, shot):
        self.setScreen(shot.screen)
        self.setGeometry(shot.geo)
        self.showFullScreen()
        platform_utils.raise_overlay(self)

    def paintEvent(self, e):
        p = QPainter(self)
        self.session.paint(self.index, p, e.rect())
        p.end()

    def mousePressEvent(self, e):
        self.session.mouse_press(self, self.index, e)

    def mouseMoveEvent(self, e):
        self.session.mouse_move(self.index, e)

    def mouseReleaseEvent(self, e):
        self.session.mouse_release(self.index, e)

    def mouseDoubleClickEvent(self, e):
        self.session.mouse_double(self.index, e)

    def wheelEvent(self, e):
        self.session.wheel(e)

    def keyPressEvent(self, e):
        self.session.key(e)

    def closeEvent(self, e):
        self.session.window_closed(self)
        super().closeEvent(e)


class CaptureSession(QObject):
    finished = Signal(str, object, str)   # action ("" = cancelled), QImage, destination

    def __init__(self, settings, shots, windows=None, full_screen=False, video=False):
        super().__init__()
        self.S = settings
        # video mode: choose the area to record; the bar has Record, computer sound, microphone and Close
        self.video = video
        self.rec_sys = bool(settings.data.get("VideoSysAudio", True))
        self.rec_mic = bool(settings.data.get("VideoMic", False))
        self.shots = shots
        self.windows_global = windows or []
        self.color = QColor(settings.PenColor)
        # colours offered in the palette: settings.json "Palette" (up to 12), else the built-in ones
        self.palette_colors = [c for c in (settings.data.get("Palette") or []) if QColor.isValidColorName(str(c))][:12] or COLORS
        self.size = settings.PenSize
        self.tool = None
        self.shapes = []
        self.cur = None
        self.editing = None
        self.active = 0          # monitor that holds the selection
        self.sel = QRectF()
        self.has_sel = False
        self.mode = "idle"       # idle, selecting, moving, resizing, drawing
        self.down = QPointF()
        self.sel_down = QRectF()
        self.grip = -1
        self.dragged = False
        self.mouse = QPointF()
        self.mouse_screen = 0
        self.hover_btn = None
        self.palette = False
        self.btns = []
        self.swatches = []
        self.tools_bar = self.actions_bar = self.palette_rect = QRectF()
        self.size_tip = None
        self.size_tip_until = 0
        self.hover_win = QRectF()
        self.down_win = QRectF()
        self.done = False
        self.ticks = 0
        self.ui_font = QFont()
        self.ui_font.setPixelSize(13)
        self.tip_font = QFont()
        self.tip_font.setPixelSize(12)

        # where is the cursor now
        cp = QCursor.pos()
        for i, s in enumerate(shots):
            if s.geo.contains(cp):
                self.mouse_screen = i
                self.mouse = QPointF(cp - s.geo.topLeft())
        if full_screen:
            self.active = self.mouse_screen
            self.sel = QRectF(0, 0, shots[self.active].geo.width(), shots[self.active].geo.height())
            self.has_sel = True

        self.wins = []
        for i, s in enumerate(shots):
            w = OverlayWindow(self, i)
            self.wins.append(w)
        self.timer = QTimer(self)
        self.timer.timeout.connect(self._tick)
        self.timer.start(100)
        self.layout()

    def show(self):
        for i, w in enumerate(self.wins):
            w.show_on(self.shots[i])
        w = self.wins[self.mouse_screen]
        w.activateWindow()
        w.raise_()
        w.setFocus()
        platform_utils.activate_app()

    # ---------------- geometry helpers ----------------

    def screen_rect(self, i) -> QRectF:
        g = self.shots[i].geo
        return QRectF(0, 0, g.width(), g.height())

    def local_windows(self, i):
        g = self.shots[i].geo
        out = []
        for r in self.windows_global:
            lr = QRectF(r).translated(-g.x(), -g.y()).intersected(self.screen_rect(i))
            if lr.width() >= 8 and lr.height() >= 8:
                out.append(lr)
        return out

    def bars_visible(self):
        return self.has_sel and self.mode in ("idle", "drawing")

    def update_all(self):
        for w in self.wins:
            w.update()

    def update_active(self):
        """The selection and what is around it, on the monitor that holds it."""
        if self.wins and self.has_sel:
            self.update_rect(self.active, self.sel.adjusted(-12, -12, 12, 12))
        elif self.wins:
            self.wins[self.active].update()

    def update_rect(self, i, r: QRectF):
        if 0 <= i < len(self.wins) and not r.isEmpty():
            self.wins[i].update(r.toAlignedRect().adjusted(-2, -2, 2, 2))

    def hint_rect(self, i) -> QRectF:
        mon = self.screen_rect(i)
        return QRectF(mon.x() + mon.width() / 2 - 450, mon.y() + 36, 900, 52)

    def frame_rect(self) -> QRectF:
        """Everything that moves with the selection: frame, size label, toolbars and their tips, palette,
        size tip, magnifier, hint."""
        r = QRectF()

        def add(x):
            nonlocal r
            if not x.isEmpty():
                r = x if r.isEmpty() else r.united(x)

        if self.has_sel:
            add(self.sel.adjusted(-12, -36, 12, 12))
            add(QRectF(self.sel.left(), self.sel.top() - 36, 220, 70))
        bars = self.actions_bar if self.tools_bar.isEmpty() else self.tools_bar.united(self.actions_bar)
        if not bars.isEmpty():
            add(bars.adjusted(-300, -50, 300, 50))
        if not self.palette_rect.isEmpty():
            add(self.palette_rect.adjusted(-6, -6, 6, 6))
        if self.size_tip:
            add(QRectF(self.mouse.x() - 80, self.mouse.y() - 80, 320, 160))
        if self.loupe_visible():
            add(self.loupe_rect(self.mouse, self.mouse_screen).adjusted(-6, -6, 8, 8))
        return r

    # ---------------- toolbar layout ----------------

    def layout(self):
        self.btns = []
        self.swatches = []
        self.tools_bar = self.actions_bar = self.palette_rect = QRectF()
        if not self.bars_visible():
            return
        bs, narrow, pad, gap = 34.0, 20.0, 4.0, 8.0
        mon = self.screen_rect(self.active)
        sel = self.sel
        tools = [
            Btn("tool", T("Карандаш (P)"), "pen"), Btn("tool", T("Линия (L)"), "line"),
            Btn("tool", T("Стрелка (A)"), "arrow"), Btn("tool", T("Прямоугольник (R)"), "rect"),
            Btn("tool", T("Овал (E)"), "ellipse"), Btn("tool", T("Маркер (M)"), "marker"),
            Btn("tool", T("Текст (T)"), "text"), Btn("tool", T("Шаги 1, 2, 3 (N)"), "step"),
            Btn("tool", T("Скрыть пикселями (B)"), "pixelate"), Btn("tool", T("Размытие (U)"), "blur"),
            Btn("color", T("Цвет")), Btn("undo", T("Отменить (Ctrl+Z)")),
        ]
        actions = [
            Btn("upload", F("Загрузить в {0} (Ctrl+D)", self.S.default())), Btn("upmenu", T("Выбрать, куда загрузить")),
            Btn("copy", T("Копировать (Ctrl+C)")), Btn("save", T("Сохранить (Ctrl+S)")),
            Btn("print", T("Печать (Ctrl+P)")), Btn("close", T("Закрыть (Esc)")),
        ]
        if self.video:
            tools = []
            actions = [
                Btn("record", T("Начать запись (Enter)")),
                Btn("sound", T("Звук компьютера: вкл.") if self.rec_sys else T("Звук компьютера: выкл.")),
                Btn("mic", T("Микрофон: вкл.") if self.rec_mic else T("Микрофон: выкл.")),
                Btn("close", T("Закрыть (Esc)")),
            ]
        rec_w = bs + QFontMetricsF(self.ui_font).horizontalAdvance(T("Записать")) + 12

        def width(b):
            return narrow if b.cmd == "upmenu" else rec_w if b.cmd == "record" else bs

        aw, ah = sum(width(b) for b in actions) + 2 * pad, bs + 2 * pad
        ax, ay = sel.right() - aw, sel.bottom() + gap
        if ay + ah > mon.bottom() - gap:
            ay = sel.top() - gap - ah
            if ay < mon.top() + gap:
                ay = sel.bottom() - ah - gap
        ax = max(mon.left() + gap, min(ax, mon.right() - aw - gap))
        self.actions_bar = QRectF(ax, ay, aw, ah)

        tw, th = bs + 2 * pad, len(tools) * bs + 2 * pad
        tx = sel.right() + gap
        if tx + tw > mon.right() - gap:
            tx = sel.left() - gap - tw
            if tx < mon.left() + gap:
                tx = sel.right() - tw - gap
        ty = max(mon.top() + gap, min(sel.bottom() - th, mon.bottom() - th - gap))
        self.tools_bar = QRectF(tx, ty, tw, th) if tools else QRectF()
        if tools and self.tools_bar.intersects(self.actions_bar):
            self.tools_bar.moveTop(max(mon.top() + gap, self.actions_bar.top() - gap - th))

        for i, b in enumerate(tools):
            b.r = QRectF(self.tools_bar.x() + pad, self.tools_bar.y() + pad + i * bs, bs, bs)
            b.vertical = True
            self.btns.append(b)
        x = self.actions_bar.x() + pad
        for b in actions:
            w = width(b)
            b.r = QRectF(x, self.actions_bar.y() + pad, w, bs)
            x += w
            self.btns.append(b)

        if self.palette and not self.video:
            cb = next(b for b in self.btns if b.cmd == "color").r
            sw = 26.0
            pw, ph = len(self.palette_colors) * sw + 2 * pad, sw + 2 * pad
            px = self.tools_bar.left() - gap - pw
            if px < mon.left() + gap:
                px = self.tools_bar.right() + gap
            self.palette_rect = QRectF(px, cb.top() + (cb.height() - ph) / 2, pw, ph)
            for i, c in enumerate(self.palette_colors):
                self.swatches.append((QRectF(self.palette_rect.x() + pad + i * sw, self.palette_rect.y() + pad, sw, sw), QColor(c)))

    def refresh(self):
        self.layout()
        if getattr(self, "_last_active", None) != self.active or getattr(self, "_last_has_sel", None) != self.has_sel:
            self.update_all()                       # the selection moved to another monitor, or appeared/vanished
        else:
            f = self.frame_rect()
            last = getattr(self, "_last_frame", QRectF())
            r = f if last.isEmpty() else (last if f.isEmpty() else f.united(last))
            self.update_rect(self.active, r)
            if self.mouse_screen != self.active:
                self.update_rect(self.mouse_screen, r)
        self._last_frame = self.frame_rect()
        self._last_active = self.active
        self._last_has_sel = self.has_sel

    # ---------------- painting ----------------

    def paint(self, i, p: QPainter, clip: QRect):
        shot = self.shots[i]
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform, False)
        p.drawImage(QPointF(0, 0), shot.image)
        on_active = i == self.active
        shade = QPainterPath()
        shade.addRect(self.screen_rect(i))
        if self.has_sel and on_active:
            hole = QPainterPath()
            hole.addRect(self.sel)
            shade = shade.subtracted(hole)
        p.fillPath(shade, QColor(0, 0, 0, 115))
        p.setRenderHint(QPainter.RenderHint.Antialiasing, True)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing, True)

        if self.has_sel and on_active:
            p.save()
            p.setClipRect(self.sel)
            for s in self.shapes:
                s.draw(p, shot.image)
            if self.cur is not None and self.cur not in self.shapes:
                self.cur.draw(p, shot.image)
            p.restore()
            self.draw_frame(p)
        elif not self.has_sel:
            if not self.hover_win.isEmpty() and self.mode == "idle" and i == self.mouse_screen:
                self.draw_hover(p, shot)
            if i == self.mouse_screen:
                self.draw_hint(p, i)

        if self.bars_visible() and on_active:
            if not self.tools_bar.isEmpty():
                self.draw_bar(p, self.tools_bar)
            self.draw_bar(p, self.actions_bar)
            for b in self.btns:
                self.draw_button(p, b)
            if self.palette:
                self.draw_palette(p)
            if self.hover_btn is not None and self.mode == "idle":
                self.draw_tip(p, self.hover_btn)
        if self.size_tip and i == self.mouse_screen:
            self.draw_size_tip(p)
        if self.loupe_visible() and i == self.mouse_screen:
            self.draw_loupe(p, shot)

    def rounded(self, p, r: QRectF, radius, fill: QColor, border: QColor | None = None):
        path = QPainterPath()
        path.addRoundedRect(r, radius, radius)
        p.fillPath(path, fill)
        if border is not None:
            p.setPen(QPen(border, 1))
            p.drawPath(path)

    def label(self, p, r: QRectF, text, fill):
        fm = QFontMetricsF(self.ui_font)
        r = QRectF(r.x(), r.y(), fm.horizontalAdvance(text) + 12, r.height())
        self.rounded(p, r, 5, fill)
        p.setFont(self.ui_font)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(r, Qt.AlignmentFlag.AlignCenter, text)

    def draw_frame(self, p):
        p.save()
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        r = self.sel.adjusted(-1, -1, 0, 0)
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(QColor(0, 0, 0, 170), 1))
        p.drawRect(r)
        pen = QPen(QColor(Qt.GlobalColor.white), 1)
        pen.setDashPattern([4, 4])
        p.setPen(pen)
        p.drawRect(r)
        if self.mode != "drawing":
            for g in self.grips():
                p.fillRect(g, Qt.GlobalColor.white)
                p.setPen(QPen(QColor(40, 40, 40, 200), 1))
                p.drawRect(g.adjusted(0, 0, -1, -1))
        p.restore()
        shot = self.shots[self.active]
        w, h = round(self.sel.width() * shot.dpr), round(self.sel.height() * shot.dpr)
        y = self.sel.top() - 30
        if y < 0:
            y = self.sel.top() + 6
        self.label(p, QRectF(self.sel.left(), y, 0, 24), f"{w} × {h}", QColor(20, 20, 20, 200))

    def draw_hint(self, p, i):
        text = T("Выдели область для видео или щёлкни по окну · Esc — отмена") if self.video else T("Выдели область мышью или щёлкни по окну · Esc — отмена")
        fm = QFontMetricsF(self.ui_font)
        w = fm.horizontalAdvance(text) + 28
        mon = self.screen_rect(i)
        r = QRectF(mon.x() + (mon.width() - w) / 2, mon.y() + 40, w, 44)
        self.rounded(p, r, 10, QColor(20, 20, 20, 215))
        p.setFont(self.ui_font)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(r, Qt.AlignmentFlag.AlignCenter, text)

    def draw_hover(self, p, shot):
        r = self.hover_win
        dpr = shot.dpr
        p.drawImage(r, shot.image, QRectF(r.x() * dpr, r.y() * dpr, r.width() * dpr, r.height() * dpr))
        p.save()
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(ACCENT, 2))
        p.drawRect(r.adjusted(1, 1, -1, -1))
        p.restore()
        y = r.top() - 28
        x = r.left()
        if y < 0:
            y, x = r.top() + 6, r.left() + 6
        self.label(p, QRectF(x, y, 0, 24), f"{round(r.width() * dpr)} × {round(r.height() * dpr)}", QColor(30, 136, 229, 225))

    def draw_bar(self, p, r):
        self.rounded(p, r.adjusted(-1, 1, 1, 3), 9, QColor(0, 0, 0, 60))
        self.rounded(p, r, 8, QColor(250, 251, 252), QColor(205, 208, 214))

    def draw_button(self, p, b: Btn):
        active = (b.cmd == "tool" and b.tool == self.tool) or (b.cmd == "sound" and self.rec_sys) or (b.cmd == "mic" and self.rec_mic)
        hot = self.hover_btn is b
        if active or hot or (b.cmd == "color" and self.palette):
            self.rounded(p, b.r.adjusted(2, 2, -2, -2), 6, QColor(209, 226, 255) if active else QColor(232, 236, 242))
        if b.cmd == "record":
            p.save()
            p.setRenderHint(QPainter.RenderHint.Antialiasing, True)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(229, 57, 53))
            p.drawEllipse(QPointF(b.r.x() + 17, b.r.center().y()), 6.5, 6.5)
            p.setFont(self.ui_font)
            p.setPen(INK)
            p.drawText(b.r.adjusted(30, 0, 0, 0), Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft, T("Записать"))
            p.restore()
            return
        name = b.tool if b.cmd == "tool" else b.cmd
        dim = (b.cmd == "undo" and not self.shapes and self.editing is None) or (b.cmd == "sound" and not self.rec_sys) or (b.cmd == "mic" and not self.rec_mic)
        draw_icon(p, name, b.r, QColor(170, 170, 170) if dim else INK, self.color)
        if b.cmd in ("sound", "mic") and dim:   # crossed out while off
            p.save()
            p.setRenderHint(QPainter.RenderHint.Antialiasing, True)
            p.setPen(QPen(QColor(170, 170, 170), 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            c = b.r.center()
            p.drawLine(QPointF(c.x() - 8, c.y() - 8), QPointF(c.x() + 8, c.y() + 8))
            p.restore()

    def draw_palette(self, p):
        self.draw_bar(p, self.palette_rect)
        for r, c in self.swatches:
            rr = r.adjusted(4, 4, -4, -4)
            p.setBrush(c)
            p.setPen(QPen(QColor(0, 0, 0, 110), 1))
            p.drawEllipse(rr)
            if c.rgb() == self.color.rgb():
                p.setBrush(Qt.BrushStyle.NoBrush)
                p.setPen(QPen(ACCENT, 2))
                p.drawEllipse(r.adjusted(1, 1, -1, -1))

    def draw_tip(self, p, b: Btn):
        fm = QFontMetricsF(self.tip_font)
        w, h = fm.horizontalAdvance(b.tip) + 14, fm.height() + 8
        mon = self.screen_rect(self.active)
        if b.vertical:
            x = self.tools_bar.left() - 6 - w
            if x < mon.left() or (self.palette and self.palette_rect.intersects(QRectF(x, b.r.y(), w, h))):
                x = self.tools_bar.right() + 6
            y = b.r.y() + (b.r.height() - h) / 2
        else:
            x = b.r.x() + (b.r.width() - w) / 2
            y = self.actions_bar.bottom() + 6
            if y + h > mon.bottom():
                y = self.actions_bar.top() - 6 - h
            x = max(mon.left() + 8, min(x, mon.right() - w - 8))
        r = QRectF(x, y, w, h)
        self.rounded(p, r, 5, QColor(32, 32, 34, 235))
        p.setFont(self.tip_font)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(r, Qt.AlignmentFlag.AlignCenter, b.tip)

    def draw_size_tip(self, p):
        t = self.tool
        d = max(4, max(12, self.size * 5) if t == "marker" else (12 + self.size * 3) if t == "text"
                else 2 * (11 + self.size * 2.5) if t == "step" else self.size)
        m = self.mouse
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(QColor(Qt.GlobalColor.white), 1))
        p.drawEllipse(m, d / 2, d / 2)
        p.setPen(QPen(QColor(0, 0, 0, 160), 1))
        p.drawEllipse(m, d / 2 + 1, d / 2 + 1)
        fm = QFontMetricsF(self.tip_font)
        r = QRectF(m.x() + d / 2 + 8, m.y() - fm.height() / 2 - 3, fm.horizontalAdvance(self.size_tip) + 12, fm.height() + 6)
        self.rounded(p, r, 5, QColor(32, 32, 34, 225))
        p.setFont(self.tip_font)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(r, Qt.AlignmentFlag.AlignCenter, self.size_tip)

    # ---------------- magnifier ----------------

    CELLS = 15
    ZOOM = 8

    def loupe_visible(self):
        if self.mode in ("selecting", "resizing"):
            return True
        return self.mode == "idle" and not self.has_sel

    def loupe_rect(self, m: QPointF, i) -> QRectF:
        size = self.CELLS * self.ZOOM
        info, gap = 42, 22
        mon = self.screen_rect(i)
        x, y = m.x() + gap, m.y() + gap
        if x + size > mon.right():
            x = m.x() - gap - size
        if y + size + info > mon.bottom():
            y = m.y() - gap - size - info
        return QRectF(x, y, size, size + info)

    def draw_loupe(self, p, shot):
        R = self.loupe_rect(self.mouse, self.mouse_screen)
        z, n = self.ZOOM, self.CELLS
        zoom = QRectF(R.x(), R.y(), R.width(), R.width())
        p.fillRect(R.translated(2, 2), QColor(0, 0, 0, 70))
        p.fillRect(R, QColor(32, 32, 34))
        p.fillRect(zoom, Qt.GlobalColor.black)
        dpr = shot.dpr
        cx, cy = int(self.mouse.x() * dpr), int(self.mouse.y() * dpr)
        src = QRect(cx - n // 2, cy - n // 2, n, n)
        part = src.intersected(QRect(0, 0, shot.image.width(), shot.image.height()))
        if not part.isEmpty():
            img = shot.image.copy(part)
            img.setDevicePixelRatio(1.0)
            dst = QRectF(zoom.x() + (part.x() - src.x()) * z, zoom.y() + (part.y() - src.y()) * z, part.width() * z, part.height() * z)
            p.save()
            p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform, False)
            p.drawImage(dst, img)
            p.restore()
        p.save()
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        p.setPen(QPen(QColor(255, 255, 255, 38), 1))
        for k in range(1, n):
            p.drawLine(QPointF(zoom.x() + k * z, zoom.y()), QPointF(zoom.x() + k * z, zoom.bottom()))
            p.drawLine(QPointF(zoom.x(), zoom.y() + k * z), QPointF(zoom.right(), zoom.y() + k * z))
        c = QRectF(zoom.x() + (n // 2) * z, zoom.y() + (n // 2) * z, z, z)
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.setPen(QPen(Qt.GlobalColor.black, 1))
        p.drawRect(c.adjusted(-1, -1, 0, 0))
        p.setPen(QPen(Qt.GlobalColor.white, 1))
        p.drawRect(c.adjusted(0, 0, -1, -1))
        p.setPen(QPen(QColor(255, 255, 255, 150), 1))
        p.drawRect(R.adjusted(0, 0, -1, -1))
        p.restore()
        px = QColor(shot.image.pixel(cx, cy)) if shot.image.rect().contains(cx, cy) else QColor(Qt.GlobalColor.black)
        g = shot.geo
        p.setFont(self.tip_font)
        p.setPen(Qt.GlobalColor.white)
        ty = zoom.bottom() + 3
        p.drawText(QPointF(R.x() + 6, ty + 14), f"{int(self.mouse.x() + g.x())}, {int(self.mouse.y() + g.y())}")
        sw = QRectF(R.x() + 7, ty + 22, 11, 11)
        p.fillRect(sw, px)
        p.setPen(QPen(QColor(Qt.GlobalColor.gray), 1))
        p.drawRect(sw)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(QPointF(sw.right() + 5, ty + 32), "#%02X%02X%02X" % (px.red(), px.green(), px.blue()))

    # ---------------- hit testing ----------------

    def grips(self):
        h = 7.0
        s = self.sel
        l, t, r, b = s.left(), s.top(), s.right(), s.bottom()
        mx, my = (l + r) / 2, (t + b) / 2
        at = lambda x, y: QRectF(x - h / 2, y - h / 2, h, h)
        return [at(l, t), at(mx, t), at(r, t), at(r, my), at(r, b), at(mx, b), at(l, b), at(l, my)]

    def hit_grip(self, pt):
        if not self.has_sel:
            return -1
        for i, g in enumerate(self.grips()):
            if g.adjusted(-4, -4, 4, 4).contains(pt):
                return i
        return -1

    def hit_button(self, pt):
        if not self.bars_visible():
            return None
        for b in self.btns:
            if b.r.contains(pt):
                return b
        return None

    GRIP_CURSORS = [Qt.CursorShape.SizeFDiagCursor, Qt.CursorShape.SizeVerCursor, Qt.CursorShape.SizeBDiagCursor,
                    Qt.CursorShape.SizeHorCursor, Qt.CursorShape.SizeFDiagCursor, Qt.CursorShape.SizeVerCursor,
                    Qt.CursorShape.SizeBDiagCursor, Qt.CursorShape.SizeHorCursor]

    def update_cursor(self, i, pt):
        w = self.wins[i]
        if i == self.active and (self.hit_button(pt) is not None or (self.palette and self.palette_rect.contains(pt))):
            w.setCursor(Qt.CursorShape.PointingHandCursor)
            return
        if i == self.active and self.bars_visible() and (self.tools_bar.contains(pt) or self.actions_bar.contains(pt)):
            w.setCursor(Qt.CursorShape.ArrowCursor)
            return
        if i == self.active:
            g = self.hit_grip(pt)
            if g >= 0 and self.tool is None:
                w.setCursor(self.GRIP_CURSORS[g])
                return
            if self.has_sel and self.sel.contains(pt):
                if self.tool == "text":
                    w.setCursor(Qt.CursorShape.IBeamCursor)
                elif self.tool is not None:
                    w.setCursor(Qt.CursorShape.CrossCursor)
                else:
                    w.setCursor(Qt.CursorShape.SizeAllCursor)
                return
        w.setCursor(Qt.CursorShape.CrossCursor)

    # ---------------- mouse ----------------

    def mouse_press(self, win, i, e):
        pt = e.position()
        self.mouse, self.mouse_screen = pt, i
        if e.button() == Qt.MouseButton.RightButton:
            if self.mode == "drawing":
                self.cur, self.mode = None, "idle"
                self.refresh()
                return
            if self.editing is not None:
                self.commit_text()
                self.refresh()
                return
            if not self.has_sel:
                self.cancel()
                return
            self.has_sel = False
            self.shapes.clear()
            self.palette = False
            self.tool = None
            self.mode = "idle"
            self.refresh()
            return
        if e.button() != Qt.MouseButton.LeftButton:
            return

        if self.palette and i == self.active:
            for r, c in self.swatches:
                if r.contains(pt):
                    self.set_color(c)
                    self.palette = False
                    self.refresh()
                    return
            self.palette = False
            if self.palette_rect.contains(pt):
                self.refresh()
                return

        if i == self.active:
            b = self.hit_button(pt)
            if b is not None:
                self.press(win, b)
                return
            if self.bars_visible() and (self.tools_bar.contains(pt) or self.actions_bar.contains(pt)):
                return

        self.down = QPointF(pt)
        self.dragged = False
        self.sel_down = QRectF(self.sel)

        if not self.has_sel or i != self.active:
            if self.has_sel and i != self.active and self.tool is not None:
                return
            self.down_win = QRectF(self.hover_win)
            self.hover_win = QRectF()
            self.active = i
            self.has_sel = False
            self.mode = "selecting"
            self.sel = QRectF(pt, QSize(0, 0))
            self.refresh()
            return

        self.grip = self.hit_grip(pt)
        if self.grip >= 0 and self.tool is None:
            self.commit_text()
            self.mode = "resizing"
            self.refresh()
            return
        if self.tool is not None and self.sel.contains(pt):
            self.start_shape(pt)
            return
        if self.grip >= 0:
            self.commit_text()
            self.mode = "resizing"
            self.refresh()
            return
        self.commit_text()
        if self.tool is None and self.sel.contains(pt):
            self.mode = "moving"
            self.refresh()
            return
        if self.tool is None:
            self.down_win = QRectF()
            self.mode = "selecting"
            self.sel = QRectF(pt, QSize(0, 0))
            self.has_sel = False
            self.refresh()

    def mouse_move(self, i, e):
        pt = e.position()
        old_screen, old_mouse = self.mouse_screen, QPointF(self.mouse)
        self.mouse, self.mouse_screen = pt, i
        if old_screen != i:
            self.wins[old_screen].update()
        if abs(pt.x() - self.down.x()) + abs(pt.y() - self.down.y()) > 3:
            self.dragged = True
        bounds = self.screen_rect(self.active)
        dx, dy = pt.x() - self.down.x(), pt.y() - self.down.y()

        if self.mode == "selecting" and i == self.active:
            q = QPointF(max(0, min(pt.x(), bounds.right())), max(0, min(pt.y(), bounds.bottom())))
            self.sel = norm(self.down, q)
            self.has_sel = self.sel.width() > 0 and self.sel.height() > 0
            self.refresh()
            return
        if self.mode == "moving":
            m = QRectF(self.sel_down)
            m.moveLeft(max(0, min(self.sel_down.x() + dx, bounds.width() - m.width())))
            m.moveTop(max(0, min(self.sel_down.y() + dy, bounds.height() - m.height())))
            self.sel = m
            self.refresh()
            return
        if self.mode == "resizing":
            l, t, r, b = self.sel_down.left(), self.sel_down.top(), self.sel_down.right(), self.sel_down.bottom()
            g = self.grip
            if g in (0, 6, 7): l += dx
            if g in (2, 3, 4): r += dx
            if g in (0, 1, 2): t += dy
            if g in (4, 5, 6): b += dy
            self.sel = QRectF(QPointF(min(l, r), min(t, b)), QPointF(max(l, r), max(t, b))).intersected(bounds)
            self.has_sel = self.sel.width() > 0 and self.sel.height() > 0
            self.refresh()
            return
        if self.mode == "drawing":
            self.update_shape(pt, bool(e.modifiers() & Qt.KeyboardModifier.ShiftModifier))
            return

        if self.loupe_visible():
            self.update_rect(old_screen, self.loupe_rect(old_mouse, old_screen).adjusted(-6, -6, 8, 8))
            self.update_rect(i, self.loupe_rect(pt, i).adjusted(-6, -6, 8, 8))
        if not self.has_sel:
            hw = QRectF()
            for r in self.local_windows(i):
                if r.contains(pt):
                    hw = r
                    break
            if hw != self.hover_win or old_screen != i:
                for scr, w in ((old_screen, self.hover_win), (i, hw)):
                    if not w.isEmpty():
                        self.update_rect(scr, w.adjusted(-4, -34, 4, 4))
                self.hover_win = hw
        hb = self.hit_button(pt) if i == self.active else None
        if hb is not self.hover_btn:
            self.hover_btn = hb
            self.refresh()
        if self.size_tip:
            self.refresh()
        self.update_cursor(i, pt)

    def mouse_release(self, i, e):
        if e.button() != Qt.MouseButton.LeftButton:
            return
        if self.mode == "selecting":
            if not self.dragged or self.sel.width() < 3 or self.sel.height() < 3:
                self.sel = QRectF(self.down_win) if not self.down_win.isEmpty() else self.screen_rect(self.active)
                self.has_sel = True
        elif self.mode == "drawing":
            if self.cur is not None and not isinstance(self.cur, Text):
                if not self.cur.trivial() or isinstance(self.cur, Stroke):
                    self.shapes.append(self.cur)
            self.cur = None
        self.mode = "idle"
        self.grip = -1
        self.refresh()
        self.update_cursor(i, e.position())

    def mouse_double(self, i, e):
        if (e.button() == Qt.MouseButton.LeftButton and self.has_sel and i == self.active and self.tool is None
                and self.sel.contains(e.position()) and self.hit_button(e.position()) is None):
            self.finish("copy")

    def wheel(self, e):
        step = 1 if e.angleDelta().y() > 0 else -1
        ns = max(1, min(20, self.size + step))
        self.size = ns
        if self.editing is not None:
            self.editing.size = ns
        self.size_tip = F("Размер {0}", ns)
        self.size_tip_until = self.ticks + 9
        self.refresh()
        if self.editing is not None:
            self.update_rect(self.active, self.editing.bounds().adjusted(-40, -40, 200, 120))

    # ---------------- shapes ----------------

    def start_shape(self, pt):
        if self.tool == "text":
            self.commit_text()
            t = Text(self.color, self.size, QPointF(pt.x(), pt.y() - (12 + self.size * 3) * 0.6))
            self.editing = t
            self.shapes.append(t)
            self.update_active()
            return
        self.commit_text()
        t, c, s = self.tool, self.color, self.size
        if t in ("pen", "marker"):
            self.cur = Stroke(c, s, marker=t == "marker")
            self.cur.points.append(QPointF(pt))
        elif t in ("line", "arrow"):
            self.cur = Line(c, s, pt, arrow=t == "arrow")
        elif t in ("rect", "ellipse"):
            self.cur = Rect(c, s, pt, ellipse=t == "ellipse")
        elif t == "step":
            n = max([x.number for x in self.shapes if isinstance(x, Step)] + [0]) + 1
            self.cur = Step(c, s, pt, n)
        elif t in ("pixelate", "blur"):
            self.cur = Blur(c, s, pt, smooth=t == "blur")
        self.mode = "drawing"
        self.update_active()

    def update_shape(self, pt, shift):
        cur = self.cur
        if cur is None:
            return
        before = cur.bounds() if not isinstance(cur, Stroke) else QRectF()
        if isinstance(cur, Stroke) and cur.points:
            w = cur.width() + 4
            last = cur.points[-1]
            before = QRectF(last.x() - w, last.y() - w, 2 * w, 2 * w)
        if isinstance(cur, Stroke):
            last = cur.points[-1]
            if abs(last.x() - pt.x()) + abs(last.y() - pt.y()) < 2:
                return
            cur.points.append(QPointF(pt))
        elif isinstance(cur, Line):
            if shift:
                import math
                ang = math.atan2(pt.y() - cur.a.y(), pt.x() - cur.a.x())
                snap = round(ang / (math.pi / 4)) * (math.pi / 4)
                ln = math.hypot(pt.x() - cur.a.x(), pt.y() - cur.a.y())
                pt = QPointF(cur.a.x() + math.cos(snap) * ln, cur.a.y() + math.sin(snap) * ln)
            cur.b = QPointF(pt)
        elif isinstance(cur, Rect):
            if shift:
                d = max(abs(pt.x() - cur.a.x()), abs(pt.y() - cur.a.y()))
                pt = QPointF(cur.a.x() + (d if pt.x() >= cur.a.x() else -d), cur.a.y() + (d if pt.y() >= cur.a.y() else -d))
            cur.b = QPointF(pt)
        elif isinstance(cur, Blur):
            cur.b = QPointF(pt)
        elif isinstance(cur, Step):
            cur.center = QPointF(pt)
        r = cur.bounds()
        self.update_rect(self.active, (r if before.isEmpty() else r.united(before)).adjusted(-4, -4, 4, 4))

    def commit_text(self):
        t = self.editing
        if t is None:
            return
        self.editing = None
        t.editing = False
        if t.trivial() and t in self.shapes:
            self.shapes.remove(t)
        self.update_active()

    def set_color(self, c):
        self.color = QColor(c)
        if self.editing is not None:
            self.editing.color = QColor(c)

    def undo(self):
        if self.editing is not None:
            if self.editing in self.shapes:
                self.shapes.remove(self.editing)
            self.editing = None
        elif self.shapes:
            self.shapes.pop()
        self.refresh()

    def _tick(self):
        self.ticks += 1
        if self.editing is not None and self.ticks % 5 == 0:
            self.editing.caret = not self.editing.caret
            self.update_rect(self.active, self.editing.bounds())
        if self.size_tip and self.ticks > self.size_tip_until:
            self.size_tip_area = QRectF(self.mouse.x() - 80, self.mouse.y() - 80, 320, 160)
            self.size_tip = None
            self.update_rect(self.mouse_screen, self.size_tip_area)

    # ---------------- commands ----------------

    def press(self, win, b):
        if b.cmd == "tool":
            self.commit_text()
            self.tool = None if self.tool == b.tool else b.tool
        elif b.cmd == "color":
            self.palette = not self.palette
        elif b.cmd == "undo":
            self.undo()
            return
        elif b.cmd == "upload":
            self.finish("upload", self.S.default())
            return
        elif b.cmd == "upmenu":
            self.upload_menu(win, b)
            return
        elif b.cmd in ("copy", "save", "print"):
            self.finish(b.cmd)
            return
        elif b.cmd == "close":
            self.cancel()
            return
        elif b.cmd == "record":
            self.record()
            return
        elif b.cmd == "sound":
            self.rec_sys = not self.rec_sys
            self.S.set(VideoSysAudio=self.rec_sys)
        elif b.cmd == "mic":
            self.rec_mic = not self.rec_mic
            self.S.set(VideoMic=self.rec_mic)
        self.refresh()

    def upload_menu(self, win, b):
        m = QMenu(win)
        for d in self.S.destinations():
            a = m.addAction(F("Загрузить в {0}", d))
            if d == self.S.default():
                f = a.font()
                f.setBold(True)
                a.setFont(f)
            a.triggered.connect(lambda checked=False, dest=d: self.finish("upload", dest))
        m.addSeparator()
        hint = m.addAction(T("Свои сервисы добавляются в настройках"))
        hint.setEnabled(False)
        m.popup(win.mapToGlobal(QPoint(int(b.r.left()), int(b.r.bottom()) + 4)))

    def render(self) -> QImage:
        shot = self.shots[self.active]
        dpr = shot.dpr
        s = self.sel
        out = QImage(max(1, round(s.width() * dpr)), max(1, round(s.height() * dpr)), QImage.Format.Format_RGB32)
        out.setDevicePixelRatio(dpr)
        p = QPainter(out)
        p.setRenderHint(QPainter.RenderHint.Antialiasing, True)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing, True)
        p.drawImage(QRectF(0, 0, s.width(), s.height()), shot.image, QRectF(s.x() * dpr, s.y() * dpr, s.width() * dpr, s.height() * dpr))
        p.translate(-s.x(), -s.y())
        for sh in self.shapes:
            sh.draw(p, shot.image)
        p.end()
        out.setDevicePixelRatio(1.0)
        return out

    def record(self):
        """Video mode: hand over the screen and the area (logical pixels, global) and close."""
        if not self.has_sel or self.done:
            return
        shot = self.shots[self.active]
        area = self.sel.translated(shot.geo.x(), shot.geo.y()).toAlignedRect()
        self.done = True
        self.close_all()
        self.finished.emit("record", {"screen": shot.screen, "area": area, "sys": self.rec_sys, "mic": self.rec_mic}, "")

    def finish(self, action, dest=""):
        if self.video:
            self.record()
            return
        self.commit_text()
        if not self.has_sel or self.done:
            return
        img = self.render()
        self.S.set(PenColor=self.color.name(), PenSize=self.size)
        self.done = True
        self.close_all()
        self.finished.emit(action, img, dest or "")

    def cancel(self):
        if self.done:
            return
        self.done = True
        self.close_all()
        self.finished.emit("", None, "")

    def close_all(self):
        self.timer.stop()
        for w in list(self.wins):
            w.hide()
            w.deleteLater()
        self.wins = []

    def window_closed(self, w):
        if not self.done:
            self.cancel()

    # ---------------- keyboard ----------------

    def key(self, e):
        k = e.key()
        mods = e.modifiers()
        ctrl = bool(mods & Qt.KeyboardModifier.ControlModifier)
        shift = bool(mods & Qt.KeyboardModifier.ShiftModifier)

        if self.editing is not None:
            t = self.editing
            handled = True
            if k == Qt.Key.Key_Escape:
                self.commit_text()
                self.refresh()
                return
            if k in (Qt.Key.Key_Return, Qt.Key.Key_Enter):
                if not shift:
                    self.commit_text()
                    self.refresh()
                    return
                t.text += "\n"
            elif k == Qt.Key.Key_Backspace:
                t.text = t.text[:-1]
            elif e.matches(QKeySequence.StandardKey.Paste):
                t.text += QGuiApplication.clipboard().text().replace("\r\n", "\n").replace("\r", "\n")
            elif not ctrl and e.text() and e.text().isprintable():
                t.text += e.text()
            elif ctrl:
                handled = False          # Ctrl/⌘ shortcuts below still work while typing
            else:
                return
            if handled:
                t.caret = True
                self.update_active()
                return

        if self.video and ctrl and k != Qt.Key.Key_A:
            return   # no copy/save/print/upload while choosing a video area
        if e.matches(QKeySequence.StandardKey.Copy):
            self.finish("copy")
            return
        if e.matches(QKeySequence.StandardKey.Save):
            self.finish("save")
            return
        if e.matches(QKeySequence.StandardKey.Print):
            self.finish("print")
            return
        if e.matches(QKeySequence.StandardKey.Undo):
            self.undo()
            return
        if ctrl and k == Qt.Key.Key_D:
            self.finish("upload", self.S.default())
            return
        if ctrl and k == Qt.Key.Key_A:
            self.active = self.mouse_screen
            self.sel = self.screen_rect(self.active)
            self.has_sel = True
            self.refresh()
            return
        if k == Qt.Key.Key_Escape:
            if self.palette:
                self.palette = False
                self.refresh()
            else:
                self.cancel()
            return
        if k in (Qt.Key.Key_Return, Qt.Key.Key_Enter):
            self.finish("copy")
            return
        if k in (Qt.Key.Key_Left, Qt.Key.Key_Right, Qt.Key.Key_Up, Qt.Key.Key_Down):
            if not self.has_sel:
                return
            step = 10 if ctrl else 1
            dx = -step if k == Qt.Key.Key_Left else step if k == Qt.Key.Key_Right else 0
            dy = -step if k == Qt.Key.Key_Up else step if k == Qt.Key.Key_Down else 0
            r = QRectF(self.sel)
            if shift:
                r.setWidth(max(1, r.width() + dx))
                r.setHeight(max(1, r.height() + dy))
            else:
                r.translate(dx, dy)
            self.sel = r.intersected(self.screen_rect(self.active))
            self.refresh()
            return
        if not ctrl and self.has_sel and not self.video:
            keys = {Qt.Key.Key_P: "pen", Qt.Key.Key_L: "line", Qt.Key.Key_A: "arrow", Qt.Key.Key_R: "rect",
                    Qt.Key.Key_E: "ellipse", Qt.Key.Key_M: "marker", Qt.Key.Key_T: "text", Qt.Key.Key_N: "step",
                    Qt.Key.Key_B: "pixelate", Qt.Key.Key_U: "blur"}
            if k in keys:
                self.tool = None if self.tool == keys[k] else keys[k]
                self.refresh()

    # ---------------- self-test ----------------

    def test_scene(self):
        self.active = 0
        self.sel = QRectF(300, 160, 760, 420)
        self.has_sel = True
        self.tool = "arrow"
        c = [QColor(x) for x in COLORS]
        pen = Stroke(c[0], 4)
        import math
        for k in range(40):
            pen.points.append(QPointF(340 + k * 6, 220 + math.sin(k / 4.0) * 20))
        self.shapes += [pen]
        a = Line(c[4], 5, QPointF(380, 520), arrow=True); a.b = QPointF(560, 380)
        r = Rect(c[3], 3, QPointF(620, 300)); r.b = QPointF(820, 420)
        el = Rect(c[5], 3, QPointF(860, 330), ellipse=True); el.b = QPointF(1040, 420)
        mk = Stroke(c[2], 4, marker=True); mk.points += [QPointF(640, 470), QPointF(900, 470)]
        bl = Blur(c[0], 4, QPointF(880, 200)); bl.b = QPointF(1040, 280)
        sb = Blur(c[0], 4, QPointF(330, 282), smooth=True); sb.b = QPointF(600, 302)
        tx = Text(c[0], 4, QPointF(640, 500)); tx.text = "Привет, Кадр!"
        self.shapes += [a, r, el, mk, bl, sb, Step(c[0], 4, QPointF(330, 200), 1), Step(c[0], 4, QPointF(600, 300), 2),
                        Step(c[2], 4, QPointF(860, 470), 3), tx]
        self.editing = tx
        self.layout()
        self.hover_btn = next(b for b in self.btns if b.cmd == "upload")

    def test_video(self, rect: QRectF):
        self.active = 0
        self.sel = QRectF(rect)
        self.has_sel = True
        self.layout()
        self.hover_btn = next(b for b in self.btns if b.cmd == "record")

    def test_hover(self, pt):
        self.mouse = QPointF(pt)
        self.mouse_screen = 0
        for r in self.local_windows(0):
            if r.contains(self.mouse):
                self.hover_win = r
                break


# ---------------- icons (drawn, so they look the same on every system) ----------------

def draw_icon(p: QPainter, name, r: QRectF, ink: QColor, color: QColor):
    p.save()
    c = r.center()
    u = 7.5
    pen = QPen(ink, 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    p.setBrush(Qt.BrushStyle.NoBrush)
    x, y = c.x(), c.y()
    P = QPointF
    if name == "pen":
        path = QPainterPath(P(x - u, y + u))
        path.lineTo(P(x - u + 1, y + u - 4))
        path.lineTo(P(x + u - 3, y - u))
        path.lineTo(P(x + u, y - u + 3))
        path.lineTo(P(x - u + 4, y + u - 1))
        path.closeSubpath()
        p.drawPath(path)
    elif name == "line":
        p.drawLine(P(x - u, y + u), P(x + u, y - u))
    elif name == "arrow":
        p.drawLine(P(x - u, y + u), P(x + u - 1, y - u + 1))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(ink)
        p.drawPolygon(QPolygonF([P(x + u + 1, y - u - 1), P(x + u + 1 - 7, y - u - 1), P(x + u + 1, y - u - 1 + 7)]))
    elif name == "rect":
        p.drawRect(QRectF(x - u, y - u * 0.72, 2 * u, 1.44 * u))
    elif name == "ellipse":
        p.drawEllipse(QRectF(x - u, y - u * 0.75, 2 * u, 1.5 * u))
    elif name == "marker":
        path = QPainterPath(P(x - 4, y + 2))
        path.lineTo(P(x + 2, y - u))
        path.lineTo(P(x + u, y - 3))
        path.lineTo(P(x + 1, y + 5))
        path.closeSubpath()
        p.drawPath(path)
        p.setPen(QPen(ink, 3.2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.FlatCap))
        p.drawLine(P(x - u, y + u - 0.5), P(x + 1, y + u - 0.5))
    elif name == "text":
        f = QFont()
        f.setPixelSize(17)
        f.setBold(True)
        p.setFont(f)
        p.drawText(r, Qt.AlignmentFlag.AlignCenter, "A")
    elif name == "step":
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(ink)
        p.drawEllipse(c, u, u)
        f = QFont()
        f.setPixelSize(11)
        f.setBold(True)
        p.setFont(f)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(r, Qt.AlignmentFlag.AlignCenter, "1")
    elif name == "pixelate":
        cell = 2 * u / 3
        for i in range(3):
            for j in range(3):
                shade = 60 if (i + j) % 2 == 0 else 165
                p.fillRect(QRectF(x - u + i * cell, y - u + j * cell, cell, cell), QColor(shade, shade, shade))
    elif name == "blur":
        g = QRadialGradient(c, u * 1.1)
        g.setColorAt(0, ink)
        t = QColor(ink)
        t.setAlpha(0)
        g.setColorAt(1, t)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(g)
        p.drawEllipse(c, u * 1.1, u * 1.1)
    elif name == "color":
        p.setBrush(color)
        p.setPen(QPen(QColor(0, 0, 0, 120), 1))
        p.drawEllipse(c, 8, 8)
    elif name == "undo":
        path = QPainterPath(P(x + u, y + u * 0.6))
        path.cubicTo(P(x + u, y - u * 0.6), P(x - u * 0.2, y - u * 0.9), P(x - u * 0.9, y - u * 0.1))
        p.drawPath(path)
        p.drawLine(P(x - u * 0.9, y - u * 0.1), P(x - u * 0.9, y - u * 0.8))
        p.drawLine(P(x - u * 0.9, y - u * 0.1), P(x - u * 0.2, y - u * 0.1))
    elif name == "upload":
        p.drawLine(P(x - u, y - u), P(x + u, y - u))
        p.drawLine(P(x, y + u), P(x, y - u + 4))
        p.drawLine(P(x - 4.5, y - u + 8.5), P(x, y - u + 4))
        p.drawLine(P(x + 4.5, y - u + 8.5), P(x, y - u + 4))
    elif name == "upmenu":
        p.drawLine(P(x - 3.5, y - 1.5), P(x, y + 2))
        p.drawLine(P(x + 3.5, y - 1.5), P(x, y + 2))
    elif name == "copy":
        p.drawRoundedRect(QRectF(x - u + 3, y - u + 3, 2 * u - 3, 2 * u - 3), 2, 2)
        p.drawPolyline(QPolygonF([P(x - u, y + u - 5), P(x - u, y - u), P(x + u - 5, y - u)]))
    elif name == "save":
        p.drawRoundedRect(QRectF(x - u, y - u, 2 * u, 2 * u), 2, 2)
        p.drawRect(QRectF(x - u + 3.5, y - u, 2 * u - 7, 5))
        p.drawRect(QRectF(x - u + 3, y + 1.5, 2 * u - 6, u - 1.5))
    elif name == "print":
        p.drawRect(QRectF(x - u + 3, y - u, 2 * u - 6, 5))
        p.drawRoundedRect(QRectF(x - u, y - u + 5, 2 * u, 2 * u - 10), 2, 2)
        p.drawRect(QRectF(x - u + 3, y + 1, 2 * u - 6, u - 1))
    elif name == "folder":
        path = QPainterPath(P(x - u, y + u * 0.8))
        path.lineTo(P(x - u, y - u * 0.7))
        path.lineTo(P(x - u * 0.25, y - u * 0.7))
        path.lineTo(P(x + u * 0.05, y - u * 0.35))
        path.lineTo(P(x + u, y - u * 0.35))
        path.lineTo(P(x + u, y + u * 0.8))
        path.closeSubpath()
        p.drawPath(path)
        p.drawLine(P(x - u, y - u * 0.05), P(x + u, y - u * 0.05))
    elif name == "sound":
        path = QPainterPath(P(x - u, y - 2.5))
        path.lineTo(P(x - u + 3.5, y - 2.5))
        path.lineTo(P(x - 0.5, y - u + 0.5))
        path.lineTo(P(x - 0.5, y + u - 0.5))
        path.lineTo(P(x - u + 3.5, y + 2.5))
        path.lineTo(P(x - u, y + 2.5))
        path.closeSubpath()
        p.drawPath(path)
        p.drawArc(QRectF(x - 2, y - 4, 6, 8), -60 * 16, 120 * 16)
        p.drawArc(QRectF(x - 3, y - 7.5, 11, 15), -60 * 16, 120 * 16)
    elif name == "mic":
        p.drawRoundedRect(QRectF(x - 3, y - u, 6, 10), 3, 3)
        p.drawArc(QRectF(x - 5.5, y - 4, 11, 9), 180 * 16, 180 * 16)
        p.drawLine(P(x, y + 5), P(x, y + u))
        p.drawLine(P(x - 3.5, y + u), P(x + 3.5, y + u))
    elif name == "close":
        p.drawLine(P(x - u * 0.8, y - u * 0.8), P(x + u * 0.8, y + u * 0.8))
        p.drawLine(P(x + u * 0.8, y - u * 0.8), P(x - u * 0.8, y + u * 0.8))
    p.restore()
