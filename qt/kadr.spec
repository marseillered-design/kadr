# PyInstaller build for macOS (.app) and Linux (folder that becomes an AppImage).
# Run from the qt/ folder:  pyinstaller kadr.spec   (icons come from ../icons, drawn by tools/draw_icon.py)
import sys

mac = sys.platform == "darwin"

hidden = ["kadr.selftest", "kadr.recorder", "kadr.recordui", "PySide6.QtMultimedia"]
if mac:
    hidden += ["Quartz", "AppKit", "objc", "ScreenCaptureKit", "CoreMedia", "kadr.macaudio"]
else:
    hidden += ["jeepney", "jeepney.io.blocking", "Xlib", "Xlib.display", "Xlib.XK", "Xlib.X", "Xlib.error",
               "keyring.backends.SecretService"]

a = Analysis(
    ["run_kadr.py"],
    pathex=["."],
    datas=[("kadr/lang.json", "kadr")] + [(f"../icons/kadr-{n}.png", "kadr/icons") for n in (16, 20, 24, 32, 48, 64, 128, 256, 512)],
    hiddenimports=hidden,
    excludes=["tkinter", "PySide6.QtQml", "PySide6.QtQuick", "PySide6.QtWebEngineCore", "PySide6.QtMultimediaWidgets",
              "PySide6.Qt3DCore", "PySide6.QtCharts", "PySide6.QtPdf", "PySide6.QtSql", "PySide6.QtTest"],
)
# Smaller installers: drop the parts of Qt that Kadr never uses. Kept on purpose: QtNetwork (single-instance
# messages), QtDBus (Linux tray), QtSvg + iconengines (icons in file dialogs), platforminputcontexts (typing in
# other alphabets), wayland/xcb platform plugins, offscreen (build self-tests).
import re

_DROP = [
    r"Qt6?(Quick|Qml|QmlModels|QmlMeta|QmlWorkerScript|VirtualKeyboard|Pdf|Designer|Help|UiTools|Sql|Test|Concurrent|StateMachine)",
    r"qml/", r"/(tls|networkinformation|generic|sqldrivers|position|virtualkeyboard)/",
    r"plugins/imageformats/.*(tiff|webp|icns|pdf|wbmp|tga|heif|jp2|macjp2)",
    r"plugins/platforms/.*(vnc|linuxfb|eglfs|minimalegl|minimal\b)",
    r"plugins/platforminputcontexts/.*virtualkeyboard",
    r"translations/(?!qtbase_(ru|uk|de|fr|es|it)\.qm)",
]


def _keep(entry):
    name = entry[0].replace("\\", "/")
    return not any(re.search(pat, name, re.I) for pat in _DROP)


a.binaries = [b for b in a.binaries if _keep(b)]
a.datas = [d for d in a.datas if _keep(d)]

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
            # asked the first time a video records the microphone (without it macOS ends the app)
            "NSMicrophoneUsageDescription": "Kadr records your microphone into screen videos when you switch it on.",
            "CFBundleDocumentTypes": [{
                "CFBundleTypeName": "Image",
                "CFBundleTypeRole": "Viewer",
                "LSHandlerRank": "Alternate",         # appears in "Open With", never becomes the default
                "LSItemContentTypes": ["public.image"],
            }],
        },
    )
