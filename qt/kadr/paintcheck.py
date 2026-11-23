"""Build check for the partial repaints: drives the overlay with simulated mouse and keys on the offscreen
platform and compares what is on screen (the backing store) with a full repaint after every step.
Any difference means some area was not updated (a 'ghost'). Run: QT_QPA_PLATFORM=offscreen python -m kadr --paint-check"""
import os
import sys

from PySide6.QtCore import QEvent, QPoint, QPointF, QRect, QRectF, Qt
from PySide6.QtGui import QGuiApplication, QImage, QKeyEvent, QMouseEvent, QPainter, QWheelEvent
from PySide6.QtWidgets import QApplication


def run() -> int:
    app = QApplication.instance() or QApplication(sys.argv[:1])
    from . import i18n, settings as st
    from .overlay import CaptureSession, Shot
    from .selftest import _fake_screen
    i18n.set_language("en")
    screen = QGuiApplication.primaryScreen()
    g = screen.geometry()
    W, H = g.width(), g.height()
    s = st.Settings()
    shot = Shot(screen, QRect(0, 0, W, H), _fake_screen(W, H))
    sess = CaptureSession(s, [shot], [QRectF(200, 150, 500, 300)])
    sess.show()
    win = sess.wins[0]
    if os.environ.get("KADR_PAINTCHECK_BREAK"):     # proves the check catches missing repaints
        sess.update_rect = lambda i, r: None
    app.processEvents()

    def mouse(kind, x, y, button=Qt.MouseButton.LeftButton, mods=Qt.KeyboardModifier.NoModifier):
        t = {"press": QEvent.Type.MouseButtonPress, "move": QEvent.Type.MouseMove, "release": QEvent.Type.MouseButtonRelease}[kind]
        buttons = button if kind != "release" else Qt.MouseButton.NoButton
        if kind == "move" and not pressed[0]:
            buttons = Qt.MouseButton.NoButton
        ev = QMouseEvent(t, QPointF(x, y), QPointF(win.mapToGlobal(QPoint(int(x), int(y)))), button if kind != "move" else Qt.MouseButton.NoButton, buttons, mods)
        QApplication.sendEvent(win, ev)
        if kind == "press":
            pressed[0] = True
        if kind == "release":
            pressed[0] = False
        app.processEvents()

    pressed = [False]

    def key(k, text=""):
        QApplication.sendEvent(win, QKeyEvent(QEvent.Type.KeyPress, k, Qt.KeyboardModifier.NoModifier, text))
        app.processEvents()

    def wheel(dy):
        QApplication.sendEvent(win, QWheelEvent(QPointF(600, 400), QPointF(600, 400), QPoint(0, 0), QPoint(0, dy),
                                                Qt.MouseButton.NoButton, Qt.KeyboardModifier.NoModifier, Qt.ScrollPhase.NoScrollPhase, False))
        app.processEvents()

    problems = []

    def check(step):
        app.processEvents()
        shown = screen.grabWindow(win.winId()).toImage().convertToFormat(QImage.Format.Format_RGB32)
        full = QImage(W, H, QImage.Format.Format_ARGB32_Premultiplied)
        p = QPainter(full)
        sess.paint(0, p, QRect(0, 0, W, H))
        p.end()
        full = full.convertToFormat(QImage.Format.Format_RGB32)
        if shown.size() != full.size():
            problems.append(f"{step}: size {shown.size()} vs {full.size()}")
            return
        diff = 0
        bad = None
        for y in range(0, H, 2):
            for x in range(0, W, 2):
                if shown.pixel(x, y) != full.pixel(x, y):
                    diff += 1
                    if bad is None:
                        bad = (x, y)
        if diff > 8:
            problems.append(f"{step}: {diff} stale pixels, first at {bad}")
            shown.save(os.path.join(out, f"stale-{len(problems)}-shown.png"))
            full.save(os.path.join(out, f"stale-{len(problems)}-full.png"))

    out = os.environ.get("KADR_PAINTCHECK_OUT", ".")
    # idle: magnifier follows the mouse, a window gets highlighted
    for x, y in ((100, 100), (300, 250), (650, 420), (900, 600)):
        mouse("move", x, y)
        check(f"idle move {x},{y}")
    # select an area by dragging
    mouse("press", 300, 200)
    for x, y in ((400, 300), (700, 500), (650, 450)):
        mouse("move", x, y)
        check(f"selecting {x},{y}")
    mouse("release", 650, 450)
    check("selection done")
    # move the selection, resize from the corner
    mouse("move", 450, 300); mouse("press", 450, 300); mouse("move", 520, 340); check("moving"); mouse("release", 520, 340); check("moved")
    sel = sess.sel
    mouse("move", sel.right(), sel.bottom()); mouse("press", sel.right(), sel.bottom()); mouse("move", sel.right() + 80, sel.bottom() + 60)
    check("resizing"); mouse("release", sel.right() + 80, sel.bottom() + 60); check("resized")
    # hover a toolbar button (tooltip), choose the pen, draw
    b = next(x for x in sess.btns if x.cmd == "tool" and x.tool == "pen")
    mouse("move", b.r.center().x(), b.r.center().y()); check("tooltip")
    mouse("press", b.r.center().x(), b.r.center().y()); mouse("release", b.r.center().x(), b.r.center().y()); check("pen chosen")
    c = sess.sel.center()
    mouse("move", c.x(), c.y()); mouse("press", c.x(), c.y())
    for k in range(1, 12):
        mouse("move", c.x() + k * 9, c.y() + (k % 3) * 7)
    check("drawing"); mouse("release", c.x() + 99, c.y()); check("drawn")
    # arrow, step, blur, text with typing, size wheel, palette, undo
    for tool, dx in (("arrow", 60), ("step", 0), ("blur", 50)):
        bt = next(x for x in sess.btns if x.cmd == "tool" and x.tool == tool)
        mouse("press", bt.r.center().x(), bt.r.center().y()); mouse("release", bt.r.center().x(), bt.r.center().y())
        mouse("move", c.x() - 60, c.y() - 40); mouse("press", c.x() - 60, c.y() - 40)
        mouse("move", c.x() - 60 + dx, c.y() - 40 + dx / 2); check(f"{tool} drawing"); mouse("release", c.x() - 60 + dx, c.y() - 40 + dx / 2)
        check(f"{tool} done")
    bt = next(x for x in sess.btns if x.cmd == "tool" and x.tool == "text")
    mouse("press", bt.r.center().x(), bt.r.center().y()); mouse("release", bt.r.center().x(), bt.r.center().y())
    mouse("move", c.x() - 100, c.y() + 40); mouse("press", c.x() - 100, c.y() + 40); mouse("release", c.x() - 100, c.y() + 40)
    for ch in "Hello":
        key(Qt.Key.Key_A + (ord(ch.upper()) - ord("A")), ch)
    check("typing")
    wheel(120); check("wheel size tip")
    for _ in range(12):
        sess._tick()
    check("size tip gone")
    key(Qt.Key.Key_Escape); check("text committed")
    cb = next(x for x in sess.btns if x.cmd == "color")
    mouse("press", cb.r.center().x(), cb.r.center().y()); mouse("release", cb.r.center().x(), cb.r.center().y()); check("palette open")
    key(Qt.Key.Key_Escape); check("palette closed")
    ub = next(x for x in sess.btns if x.cmd == "undo")
    mouse("press", ub.r.center().x(), ub.r.center().y()); mouse("release", ub.r.center().x(), ub.r.center().y()); check("undo")
    # arrows nudge the selection; right click drops it
    key(Qt.Key.Key_Left); check("nudge")
    mouse("press", 50, 50, Qt.MouseButton.RightButton); mouse("release", 50, 50, Qt.MouseButton.RightButton); check("selection dropped")
    mouse("move", 300, 300); check("idle again")

    print("STEPS OK" if not problems else "\n".join(problems), flush=True)
    os._exit(0 if not problems else 1)
