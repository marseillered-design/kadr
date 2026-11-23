"""Background picture for the macOS .dmg window: 'drag Kadr to Applications' and how to open an app
without an Apple paid signature. Writes build/dmg-background.png (660×420) and @2x."""
import os
import sys
from pathlib import Path

if sys.platform.startswith("linux") and not os.environ.get("DISPLAY"):
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")   # elsewhere use the real platform: it has the fonts
from PySide6.QtCore import QPointF, QRectF, Qt  # noqa: E402
from PySide6.QtGui import QColor, QFont, QGuiApplication, QImage, QPainter, QPainterPath, QPen  # noqa: E402

W, H = 660, 420


def draw(scale: int) -> QImage:
    img = QImage(W * scale, H * scale, QImage.Format.Format_ARGB32)
    img.setDevicePixelRatio(scale)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.fillRect(QRectF(0, 0, W, H), QColor("#f5f6fa"))
    p.fillRect(QRectF(0, 300, W, 120), QColor("#eceef5"))
    # arrow from the app (x=165) to Applications (x=495), icons centred at y=170
    pen = QPen(QColor("#3D5AFE"), 6, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    p.drawLine(QPointF(262, 170), QPointF(392, 170))
    path = QPainterPath(QPointF(372, 152))
    path.lineTo(QPointF(396, 170))
    path.lineTo(QPointF(372, 188))
    p.drawPath(path)
    f = QFont("Helvetica Neue")
    f.setPixelSize(17)
    f.setWeight(QFont.Weight.DemiBold)
    p.setFont(f)
    p.setPen(QColor("#1f2430"))
    p.drawText(QRectF(0, 252, W, 24), Qt.AlignmentFlag.AlignCenter, "Перетащи Kadr в «Программы» · Drag Kadr to Applications")
    f.setPixelSize(13)
    f.setWeight(QFont.Weight.Normal)
    p.setFont(f)
    p.setPen(QColor("#4b5163"))
    lines = [
        "Если при первом запуске macOS не открывает Kadr:",
        "Системные настройки → Конфиденциальность и безопасность → «Всё равно открыть»",
        "If macOS won’t open Kadr the first time: System Settings → Privacy & Security → Open Anyway",
    ]
    for i, t in enumerate(lines):
        p.drawText(QRectF(20, 318 + i * 22, W - 40, 20), Qt.AlignmentFlag.AlignCenter, t)
    p.end()
    return img


if __name__ == "__main__":
    app = QGuiApplication(sys.argv[:1])
    out = Path(__file__).resolve().parents[1] / "build"
    out.mkdir(exist_ok=True)
    draw(1).save(str(out / "dmg-background.png"))
    draw(2).save(str(out / "dmg-background@2x.png"))
    print("ok")
