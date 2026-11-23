"""Drawing tools. Coordinates are logical pixels of one screen; the base image is that screen's capture
(device pixels, with its devicePixelRatio set)."""
import math

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QBrush, QColor, QFont, QFontMetricsF, QImage, QPainter, QPainterPath, QPen, QPolygonF

TOOLS = ["pen", "line", "arrow", "rect", "ellipse", "marker", "text", "step", "pixelate", "blur"]


def norm(a: QPointF, b: QPointF) -> QRectF:
    return QRectF(QPointF(min(a.x(), b.x()), min(a.y(), b.y())), QPointF(max(a.x(), b.x()), max(a.y(), b.y())))


class Shape:
    def __init__(self, color: QColor, size: int):
        self.color = QColor(color)
        self.size = size

    def draw(self, p: QPainter, base: QImage):
        raise NotImplementedError

    def bounds(self) -> QRectF:
        raise NotImplementedError

    def trivial(self) -> bool:
        return False


class Stroke(Shape):
    def __init__(self, color, size, marker=False):
        super().__init__(color, size)
        self.points: list[QPointF] = []
        self.marker = marker

    def width(self):
        return max(12, self.size * 5) if self.marker else self.size

    def ink(self):
        c = QColor(self.color)
        if self.marker:
            c.setAlpha(105)
        return c

    def draw(self, p, base):
        if not self.points:
            return
        w = self.width()
        if len(self.points) == 1:
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(self.ink())
            c = self.points[0]
            if self.marker:
                p.drawRect(QRectF(c.x() - w / 2, c.y() - w / 2, w, w))
            else:
                p.drawEllipse(c, w / 2, w / 2)
            return
        pen = QPen(self.ink(), w, Qt.PenStyle.SolidLine,
                   Qt.PenCapStyle.SquareCap if self.marker else Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
        p.setPen(pen)
        p.setBrush(Qt.BrushStyle.NoBrush)
        path = QPainterPath(self.points[0])
        for pt in self.points[1:]:
            path.lineTo(pt)
        p.drawPath(path)

    def bounds(self):
        if not self.points:
            return QRectF()
        xs = [pt.x() for pt in self.points]
        ys = [pt.y() for pt in self.points]
        pad = self.width() + 2
        return QRectF(min(xs) - pad, min(ys) - pad, max(xs) - min(xs) + 2 * pad, max(ys) - min(ys) + 2 * pad)


class Line(Shape):
    def __init__(self, color, size, a, arrow=False):
        super().__init__(color, size)
        self.a = QPointF(a)
        self.b = QPointF(a)
        self.arrow = arrow

    def head(self):
        return self.size * 3 + 10

    def draw(self, p, base):
        dx, dy = self.b.x() - self.a.x(), self.b.y() - self.a.y()
        ln = math.hypot(dx, dy)
        if ln < 1:
            return
        pen = QPen(self.color, self.size, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
        p.setPen(pen)
        if not self.arrow:
            p.drawLine(self.a, self.b)
            return
        ux, uy = dx / ln, dy / ln
        head = min(self.head(), ln * 0.9)
        half = head * 0.55
        base_pt = QPointF(self.b.x() - ux * head, self.b.y() - uy * head)
        left = QPointF(base_pt.x() - uy * half, base_pt.y() + ux * half)
        right = QPointF(base_pt.x() + uy * half, base_pt.y() - ux * half)
        end = QPointF(self.b.x() - ux * head * 0.7, self.b.y() - uy * head * 0.7)
        pen.setCapStyle(Qt.PenCapStyle.FlatCap)
        p.setPen(pen)
        p.drawLine(self.a, end)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(self.color)
        p.drawPolygon(QPolygonF([QPointF(self.b), left, right]))

    def bounds(self):
        pad = self.head() + self.size + 2
        return norm(self.a, self.b).adjusted(-pad, -pad, pad, pad)

    def trivial(self):
        return abs(self.a.x() - self.b.x()) + abs(self.a.y() - self.b.y()) < 4


class Rect(Shape):
    def __init__(self, color, size, a, ellipse=False):
        super().__init__(color, size)
        self.a = QPointF(a)
        self.b = QPointF(a)
        self.ellipse = ellipse

    def draw(self, p, base):
        r = norm(self.a, self.b)
        if r.width() < 1 or r.height() < 1:
            return
        p.setPen(QPen(self.color, self.size, Qt.PenStyle.SolidLine, Qt.PenCapStyle.SquareCap, Qt.PenJoinStyle.MiterJoin))
        p.setBrush(Qt.BrushStyle.NoBrush)
        if self.ellipse:
            p.drawEllipse(r)
        else:
            p.drawRect(r)

    def bounds(self):
        pad = self.size + 2
        return norm(self.a, self.b).adjusted(-pad, -pad, pad, pad)

    def trivial(self):
        r = norm(self.a, self.b)
        return r.width() < 3 or r.height() < 3


class Blur(Shape):
    """Hides what is under the rectangle: big pixels or a soft blur."""

    def __init__(self, color, size, a, smooth=False):
        super().__init__(color, size)
        self.a = QPointF(a)
        self.b = QPointF(a)
        self.smooth = smooth
        self._cache = None
        self._key = None

    def draw(self, p, base: QImage):
        dpr = base.devicePixelRatio() or 1.0
        r = norm(self.a, self.b)
        logical_bounds = QRectF(0, 0, base.width() / dpr, base.height() / dpr)
        r = r.intersected(logical_bounds)
        if r.width() < 2 or r.height() < 2:
            return
        dev = QRectF(r.x() * dpr, r.y() * dpr, r.width() * dpr, r.height() * dpr).toAlignedRect()
        key = (dev.x(), dev.y(), dev.width(), dev.height())
        if self._cache is None or self._key != key:
            src = base.copy(dev)
            src.setDevicePixelRatio(1.0)
            if self.smooth:
                f = max(4, self.size * 2) * dpr
            else:
                f = max(8, self.size * 3) * dpr
            sw, sh = max(1, int(dev.width() / f)), max(1, int(dev.height() / f))
            small = src.scaled(sw, sh, Qt.AspectRatioMode.IgnoreAspectRatio, Qt.TransformationMode.SmoothTransformation)
            mode = Qt.TransformationMode.SmoothTransformation if self.smooth else Qt.TransformationMode.FastTransformation
            img = small.scaled(dev.width(), dev.height(), Qt.AspectRatioMode.IgnoreAspectRatio, mode)
            img.setDevicePixelRatio(dpr)
            self._cache, self._key = img, key
        p.drawImage(QRectF(dev.x() / dpr, dev.y() / dpr, dev.width() / dpr, dev.height() / dpr), self._cache)

    def bounds(self):
        return norm(self.a, self.b).adjusted(-2, -2, 2, 2)

    def trivial(self):
        r = norm(self.a, self.b)
        return r.width() < 4 or r.height() < 4


class Step(Shape):
    """A numbered circle for step-by-step instructions."""

    def __init__(self, color, size, center, number):
        super().__init__(color, size)
        self.center = QPointF(center)
        self.number = number

    def radius(self):
        return 11 + self.size * 2.5

    def draw(self, p, base):
        r = self.radius()
        c = self.color
        light = c.red() * 0.299 + c.green() * 0.587 + c.blue() * 0.114 > 170
        p.setBrush(c)
        p.setPen(QPen(QColor(70, 70, 70) if light else QColor(Qt.GlobalColor.white), max(1.5, r / 9)))
        p.drawEllipse(self.center, r, r)
        f = QFont()
        f.setBold(True)
        f.setPixelSize(max(6, int(r * (0.82 if self.number > 9 else 1.02))))
        p.setFont(f)
        p.setPen(QColor(25, 25, 25) if light else QColor(Qt.GlobalColor.white))
        p.drawText(QRectF(self.center.x() - r, self.center.y() - r, 2 * r, 2 * r), Qt.AlignmentFlag.AlignCenter, str(self.number))

    def bounds(self):
        r = self.radius() + 4
        return QRectF(self.center.x() - r, self.center.y() - r, 2 * r, 2 * r)


class Text(Shape):
    def __init__(self, color, size, pos):
        super().__init__(color, size)
        self.pos = QPointF(pos)
        self.text = ""
        self.editing = True
        self.caret = True

    def font(self) -> QFont:
        f = QFont()
        f.setPixelSize(int(12 + self.size * 3))
        f.setWeight(QFont.Weight.DemiBold)
        return f

    def _metrics(self):
        f = self.font()
        fm = QFontMetricsF(f)
        lines = self.text.split("\n")
        lh = fm.lineSpacing()
        w = max([fm.horizontalAdvance(l) for l in lines] + [0])
        return f, fm, lines, lh, w

    def draw(self, p, base):
        f, fm, lines, lh, w = self._metrics()
        p.setFont(f)
        p.setPen(self.color)
        for i, line in enumerate(lines):
            p.drawText(QPointF(self.pos.x(), self.pos.y() + fm.ascent() + i * lh), line)
        if self.editing:
            pad = 4
            pen = QPen(QColor(128, 128, 128, 160), 1, Qt.PenStyle.DashLine)
            p.setPen(pen)
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawRect(QRectF(self.pos.x() - pad, self.pos.y() - pad, max(w, f.pixelSize() * 0.6) + 2 * pad, len(lines) * lh + 2 * pad))
            if self.caret:
                x = self.pos.x() + fm.horizontalAdvance(lines[-1]) + 1
                y = self.pos.y() + (len(lines) - 1) * lh
                p.setPen(QPen(self.color, max(1.5, f.pixelSize() / 14)))
                p.drawLine(QPointF(x, y + lh * 0.1), QPointF(x, y + lh * 0.92))

    def bounds(self):
        f, fm, lines, lh, w = self._metrics()
        return QRectF(self.pos.x() - 8, self.pos.y() - 8, max(w, f.pixelSize()) + 20, len(lines) * lh + 20)

    def trivial(self):
        return not self.text.strip()
