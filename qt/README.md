# Kadr for macOS and Linux (Qt)

The same app as the Windows version, written with Qt (PySide6). How to install and use it: see the main [README](../README.md) ([по-русски](../README.ru.md)).

## Notes per system

- **macOS:** lives in the menu bar; default hotkey **⌘⇧2** — you can choose any key combination in Settings. Needs the Screen Recording permission. The proxy password is kept in the Keychain.
- **Linux, X11:** default hotkey **Shift+PageUp** — any key combination can be chosen in Settings.
- **Linux, Wayland:** apps can’t catch keys or read the screen themselves. On GNOME (Ubuntu, Fedora) Kadr writes its shortcut into the system settings automatically; on KDE add a shortcut for the command `kadr --capture`. Screenshots go through the desktop portal (GNOME asks once). Highlighting the window under the cursor isn’t available on Wayland — drag a frame or click to take the screen.

## Building

Built on GitHub by `.github/workflows/build.yml` (.deb/.rpm/AppImage on Ubuntu 22.04, .dmg on Apple Silicon and Intel Macs).

Locally, from this folder:
```
python -m venv .venv
.venv/bin/pip install -r requirements.txt pyinstaller
.venv/bin/python -m kadr            # run without building
.venv/bin/pyinstaller kadr.spec
```

Texts in 7 languages are shared with the Windows version: `kadr/lang.json` is exported from `src/Lang.cs`; strings only the Mac/Linux version needs are in `kadr/i18n.py`.
