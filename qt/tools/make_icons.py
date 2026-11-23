"""Draws the app icon into build/icon.png (and build/kadr.png for Linux) without a display."""
import os
import sys
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from PySide6.QtGui import QGuiApplication  # noqa: E402

app = QGuiApplication(sys.argv[:1])
from kadr.app import app_icon_pixmap  # noqa: E402

out = Path(__file__).resolve().parents[1] / "build"
out.mkdir(exist_ok=True)
app_icon_pixmap(1024).save(str(out / "icon.png"))
app_icon_pixmap(256).save(str(out / "kadr.png"))
print("icons in", out)
