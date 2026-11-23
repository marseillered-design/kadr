"""Draws the Kadr icon and writes every file the installers need into <repo>/icons:
  kadr-<size>.png (16…1024, full-bleed for Windows/Linux), kadr-mac-1024.png (macOS shape with margin and shadow),
  kadr.ico (Windows, 16–256). Run: python tools/draw_icon.py  (works without a display)."""
import os
import struct
import sys
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
from PySide6.QtCore import QBuffer, QIODevice, QPointF, QRectF, Qt  # noqa: E402
from PySide6.QtGui import (QColor, QGuiApplication, QImage, QLinearGradient, QPainter, QPainterPath, QPen,  # noqa: E402
                           QPolygonF, QRadialGradient)

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "icons"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024]

SKY = QColor("#12B5CB")
INDIGO = QColor("#3D5AFE")
DEEP = QColor("#2A2FB8")
AMBER = QColor("#FFB300")


def _body(p: QPainter, r: QRectF, radius: float):
    path = QPainterPath()
    path.addRoundedRect(r, radius, radius)
    g = QLinearGradient(r.topLeft(), r.bottomRight())
    g.setColorAt(0.0, SKY)
    g.setColorAt(0.55, INDIGO)
    g.setColorAt(1.0, DEEP)
    p.fillPath(path, g)
    # soft light from the top-left, keeps it from looking flat at large sizes
    hl = QRadialGradient(QPointF(r.left() + r.width() * 0.25, r.top() + r.height() * 0.15), r.width() * 0.75)
    hl.setColorAt(0, QColor(255, 255, 255, 60))
    hl.setColorAt(1, QColor(255, 255, 255, 0))
    p.fillPath(path, hl)
    return path


def _glyph(p: QPainter, r: QRectF, small: bool):
    """White viewfinder corners, the captured area inside, and (big sizes) an amber pointer selecting it."""
    S = r.width()
    x0, y0 = r.left(), r.top()
    P = lambda fx, fy: QPointF(x0 + fx * S, y0 + fy * S)
    w = S * (0.10 if small else 0.072)
    a, b = (0.20, 0.80) if small else (0.21, 0.79)
    L = 0.21 if small else 0.18
    pen = QPen(QColor(Qt.GlobalColor.white), w, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    p.setBrush(Qt.BrushStyle.NoBrush)
    corners = [[P(a, a + L), P(a, a), P(a + L, a)], [P(b - L, a), P(b, a), P(b, a + L)],
               [P(a + L, b), P(a, b), P(a, b - L)]]
    if small:   # at big sizes the pointer takes the place of the bottom-right corner
        corners.append([P(b, b - L), P(b, b), P(b - L, b)])
    for pts in corners:
        p.drawPolyline(pts)
    if small:
        d = S * 0.2
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(Qt.GlobalColor.white))
        p.drawEllipse(P(0.5, 0.5), d / 2, d / 2)
        return
    # the captured area
    area = QPainterPath()
    area.addRoundedRect(QRectF(P(0.33, 0.33), P(0.67, 0.67)), S * 0.035, S * 0.035)
    p.fillPath(area, QColor(255, 255, 255, 70))
    p.setPen(QPen(QColor(255, 255, 255, 200), S * 0.012, Qt.PenStyle.DashLine))
    p.drawPath(area)
    # pointer, tip on the area's bottom-right corner
    tip = P(0.63, 0.61)
    s = S * 0.31
    shape = [(0, 0), (0, 0.78), (0.20, 0.60), (0.33, 0.92), (0.46, 0.86), (0.33, 0.56), (0.60, 0.56)]
    poly = QPolygonF([QPointF(tip.x() + dx * s, tip.y() + dy * s) for dx, dy in shape])
    shadow = QPolygonF([pt + QPointF(S * 0.012, S * 0.02) for pt in poly])
    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(QColor(0, 0, 40, 70))
    p.drawPolygon(shadow)
    p.setBrush(AMBER)
    p.setPen(QPen(QColor(Qt.GlobalColor.white), S * 0.022, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
    p.drawPolygon(poly)


def draw(size: int, mac: bool = False) -> QImage:
    img = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    img.fill(Qt.GlobalColor.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    small = size <= 24
    if mac:
        # Apple's grid: 824/1024 body, centred a bit high, with a soft drop shadow.
        body = size * 824 / 1024
        r = QRectF((size - body) / 2, (size - body) / 2 - size * 0.01, body, body)
        for i in range(12, 0, -1):
            sp = QPainterPath()
            grow = i * size / 1024 * 1.6
            sp.addRoundedRect(r.adjusted(-grow, -grow + size * 0.012, grow, grow + size * 0.012), body * 0.225 + grow, body * 0.225 + grow)
            p.fillPath(sp, QColor(0, 0, 0, 5))
        _body(p, r, body * 0.225)
        _glyph(p, r, False)
    else:
        m = 0.5 if size <= 20 else size * 0.04
        r = QRectF(m, m, size - 2 * m, size - 2 * m)
        _body(p, r, size * 0.22)
        _glyph(p, r, small)
    p.end()
    return img


def _png(img: QImage) -> bytes:
    buf = QBuffer()
    buf.open(QIODevice.OpenModeFlag.WriteOnly)
    img.save(buf, "PNG")
    return bytes(buf.data())


def _dib(img: QImage) -> bytes:
    """Classic BMP entry (32-bit BGRA + empty AND mask) for the small sizes, which every Windows tool reads."""
    s = img.width()
    img = img.convertToFormat(QImage.Format.Format_ARGB32)
    out = bytearray(struct.pack("<IiiHHIIiiII", 40, s, s * 2, 1, 32, 0, 0, 0, 0, 0, 0))
    for y in range(s - 1, -1, -1):
        for x in range(s):
            c = img.pixelColor(x, y)
            out += bytes((c.blue(), c.green(), c.red(), c.alpha()))
    out += bytes(((s + 31) // 32) * 4 * s)
    return bytes(out)


def write_ico(path: Path, sizes=(16, 20, 24, 32, 40, 48, 64, 256)):
    entries = [(s, _png(draw(s)) if s >= 256 else _dib(draw(s))) for s in sizes]
    head = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    table = b""
    for s, data in entries:
        table += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path.write_bytes(head + table + b"".join(d for _, d in entries))


def main():
    app = QGuiApplication(sys.argv[:1])  # noqa: F841
    OUT.mkdir(exist_ok=True)
    for s in SIZES:
        draw(s).save(str(OUT / f"kadr-{s}.png"))
    draw(1024, mac=True).save(str(OUT / "kadr-mac-1024.png"))
    write_ico(OUT / "kadr.ico")
    print("icons in", OUT)


if __name__ == "__main__":
    main()
