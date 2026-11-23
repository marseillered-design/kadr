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

CORAL = QColor("#FF6A5B")
ROSE = QColor("#F0425F")
BERRY = QColor("#D6246E")
HOLE = QColor("#E0335F")


def _body(p: QPainter, r: QRectF, radius: float):
    path = QPainterPath()
    path.addRoundedRect(r, radius, radius)
    g = QLinearGradient(r.topLeft(), r.bottomRight())
    g.setColorAt(0.0, CORAL)
    g.setColorAt(0.55, ROSE)
    g.setColorAt(1.0, BERRY)
    p.fillPath(path, g)
    # soft light from the top-left, keeps it from looking flat at large sizes
    hl = QRadialGradient(QPointF(r.left() + r.width() * 0.28, r.top() + r.height() * 0.18), r.width() * 0.8)
    hl.setColorAt(0, QColor(255, 255, 255, 55))
    hl.setColorAt(1, QColor(255, 255, 255, 0))
    p.fillPath(path, hl)
    return path


def aperture(p: QPainter, center: QPointF, R: float, size_px: int, ink: QColor, hole: QColor | None):
    """Camera shutter blades: a disc with a hexagonal opening and six blade edges.
    hole=None punches the opening and the edges out (for one-colour menu-bar icons)."""
    import math
    small = size_px <= 24
    r_in = R * (0.36 if small else 0.42)
    hexa = [QPointF(center.x() + r_in * math.cos(math.radians(i * 60 - 90)),
                    center.y() + r_in * math.sin(math.radians(i * 60 - 90))) for i in range(6)]
    disc = QPainterPath()
    disc.addEllipse(center, R, R)
    opening = QPainterPath()
    opening.addPolygon(QPolygonF(hexa))
    opening.closeSubpath()
    shape = disc.subtracted(opening)
    if True:
        # small sizes: wider gaps (at least ~1.3 px) so the blades stay visible in the tray
        width = max(1.3, size_px * 0.085) if small else size_px * 0.045
        for i, v in enumerate(hexa):
            a = math.radians(i * 60 - 90 + 120)       # each blade edge runs out tangentially to the rim
            end = QPointF(v.x() + math.cos(a) * R * 1.3, v.y() + math.sin(a) * R * 1.3)
            line = QPainterPath(v)
            line.lineTo(end)
            stroker = __import__("PySide6.QtGui", fromlist=["QPainterPathStroker"]).QPainterPathStroker()
            stroker.setWidth(width)
            stroker.setCapStyle(Qt.PenCapStyle.FlatCap)
            shape = shape.subtracted(stroker.createStroke(line))
    p.setPen(Qt.PenStyle.NoPen)
    p.fillPath(shape, ink)
    if hole is not None:
        # fill the cut-outs with the background colour so they read as blades, not holes
        rest = disc.subtracted(shape)
        p.fillPath(rest, hole)


def _glyph(p: QPainter, r: QRectF, small: bool):
    S = r.width()
    # small icons: a bigger disc so the blades have room
    aperture(p, r.center(), S * (0.37 if small else 0.30), int(S), QColor(Qt.GlobalColor.white), HOLE)


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
