"""Draws the pictures of the Windows installer (Inno Setup) into installer/windows/:
  wizard-<w>.bmp — the tall picture on the welcome and finish pages (164×314 at 100 %, plus 150 % and 200 %);
  small-<w>.bmp  — the square in the corner of the other pages (55×55 and its larger versions).
24-bit BMP, which every Inno Setup 6 reads. Run: python tools/draw_icon.py && python tools/installer_art.py"""
import os
import sys
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, str(Path(__file__).resolve().parent))
from PySide6.QtCore import QPointF, QRectF, Qt  # noqa: E402
from PySide6.QtGui import QColor, QFont, QGuiApplication, QImage, QLinearGradient, QPainter, QRadialGradient  # noqa: E402

import draw_icon  # noqa: E402

OUT = Path(__file__).resolve().parents[2] / "installer" / "windows"


def wizard(w: int, h: int) -> QImage:
    k = w / 164
    img = QImage(w, h, QImage.Format.Format_RGB32)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setRenderHint(QPainter.RenderHint.TextAntialiasing)
    g = QLinearGradient(0, 0, w * 0.6, h)
    g.setColorAt(0.0, draw_icon.CORAL)
    g.setColorAt(0.55, draw_icon.ROSE)
    g.setColorAt(1.0, draw_icon.BERRY)
    p.fillRect(img.rect(), g)
    # soft light behind the mark
    glow = QRadialGradient(QPointF(w / 2, h * 0.34), w * 0.75)
    glow.setColorAt(0, QColor(255, 255, 255, 70))
    glow.setColorAt(1, QColor(255, 255, 255, 0))
    p.fillRect(img.rect(), glow)
    # the aperture, white, as on the app icon
    draw_icon.aperture(p, QPointF(w / 2, h * 0.34), w * 0.27, w, QColor(Qt.GlobalColor.white), QColor(draw_icon.ROSE))
    f = QFont("Segoe UI")
    f.setPixelSize(round(30 * k))
    f.setWeight(QFont.Weight.Bold)
    p.setFont(f)
    p.setPen(Qt.GlobalColor.white)
    p.drawText(QRectF(0, h * 0.56, w, 40 * k), Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter, "Kadr")
    f.setPixelSize(round(11 * k))
    f.setWeight(QFont.Weight.Normal)
    p.setFont(f)
    p.setPen(QColor(255, 255, 255, 225))
    p.drawText(QRectF(8 * k, h * 0.56 + 40 * k, w - 16 * k, 44 * k), Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignTop | Qt.TextFlag.TextWordWrap,
               "Screenshots · Video · Upload")
    f.setPixelSize(round(9 * k))
    p.setFont(f)
    p.setPen(QColor(255, 255, 255, 170))
    p.drawText(QRectF(0, h - 26 * k, w, 18 * k), Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter, "open source · MIT")
    p.end()
    return img


def small(w: int, h: int) -> QImage:
    img = QImage(w, h, QImage.Format.Format_RGB32)
    img.fill(QColor(255, 255, 255))
    p = QPainter(img)
    side = min(w, h)
    icon = draw_icon.draw(side)
    p.drawImage(QPointF((w - side) / 2, (h - side) / 2), icon)
    p.end()
    return img


def main():
    app = QGuiApplication.instance() or QGuiApplication(sys.argv[:1])
    OUT.mkdir(parents=True, exist_ok=True)
    for w, h in ((164, 314), (246, 459), (328, 628)):
        wizard(w, h).save(str(OUT / f"wizard-{w}.bmp"), "BMP")
    for w, h in ((55, 55), (83, 80), (110, 106)):
        small(w, h).save(str(OUT / f"small-{w}.bmp"), "BMP")
    print("installer pictures written to", OUT)


if __name__ == "__main__":
    main()
