# PyInstaller build for macOS (.app) and Linux (folder that becomes an AppImage).
# Run from the qt/ folder:  pyinstaller kadr.spec   (icons come from ../icons, drawn by tools/draw_icon.py)
import sys

mac = sys.platform == "darwin"

hidden = ["kadr.selftest"]
if mac:
    hidden += ["Quartz", "AppKit", "objc"]
else:
    hidden += ["jeepney", "jeepney.io.blocking", "Xlib", "Xlib.display", "Xlib.XK", "Xlib.X", "Xlib.error",
               "keyring.backends.SecretService"]

a = Analysis(
    ["run_kadr.py"],
    pathex=["."],
    datas=[("kadr/lang.json", "kadr")] + [(f"../icons/kadr-{n}.png", "kadr/icons") for n in (16, 20, 24, 32, 48, 64, 128, 256, 512)],
    hiddenimports=hidden,
    excludes=["tkinter", "PySide6.QtQml", "PySide6.QtQuick", "PySide6.QtWebEngineCore", "PySide6.QtMultimedia",
              "PySide6.Qt3DCore", "PySide6.QtCharts", "PySide6.QtPdf", "PySide6.QtSql", "PySide6.QtTest"],
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="Kadr" if mac else "kadr", console=False,
          icon="../icons/kadr-mac-1024.png" if mac else "../icons/kadr-256.png", argv_emulation=False)
coll = COLLECT(exe, a.binaries, a.datas, name="Kadr" if mac else "kadr")

if mac:
    app = BUNDLE(
        coll,
        name="Kadr.app",
        icon="../icons/kadr-mac-1024.png",
        bundle_identifier="app.kadr",
        info_plist={
            "CFBundleName": "Kadr",
            "CFBundleDisplayName": "Kadr",
            "CFBundleShortVersionString": "1.0.0",
            "LSUIElement": True,                      # lives in the menu bar, no Dock icon
            "NSHighResolutionCapable": True,
            "LSMinimumSystemVersion": "11.0",
            "CFBundleDocumentTypes": [{
                "CFBundleTypeName": "Image",
                "CFBundleTypeRole": "Viewer",
                "LSHandlerRank": "Alternate",         # appears in "Open With", never becomes the default
                "LSItemContentTypes": ["public.image"],
            }],
        },
    )
