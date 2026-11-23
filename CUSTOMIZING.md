# Make Kadr your own

[По-русски](CUSTOMIZING.ru.md)

Kadr is open source under the [MIT licence](LICENSE): you may change it, build your own version and share it. Here is what you can change — first without any programming, then in the code.

## Without programming

**Settings window** (tray / menu-bar icon → Settings): hotkey (any key combination), where to upload, your own upload services, PNG or JPG, save folder, proxy, language, notifications, autostart.

**The settings file** — a plain text file you can edit in Notepad / TextEdit while Kadr is closed:

| System | File |
|---|---|
| Windows | `%APPDATA%\Kadr\settings.json` |
| macOS | `~/Library/Application Support/Kadr/settings.json` |
| Linux | `~/.config/kadr/settings.json` |

Fields worth knowing:

| Field | What it does |
|---|---|
| `Palette` | The colours offered in the capture window, e.g. `["#E53935", "#00BCD4", "#FFFFFF"]` — up to 12 |
| `PenColor`, `PenSize` | The colour and thickness the tools start with |
| `DefaultDestination` | `Catbox`, `Imgur` or the name of your own service |
| `Uploaders` | Your own upload services (same fields as ShareX `.sxcu` files) |
| `SaveFolder`, `AskFileName`, `ImageFormat`, `JpegQuality` | Saving |
| `Language` | `ru`, `uk`, `en`, `de`, `fr`, `es`, `it`; empty = the system’s language |
| `ShowNotifications`, `CopyLink`, `OpenInBrowser` | What happens after an upload |

**Any upload service**: Settings → Your own upload services → Add… or import a ShareX `.sxcu` file. Many hosts publish ready-made `.sxcu` files.

## In the code

The project has two versions with the same features:

- `src/` — Windows, C# (.NET Framework 4.8). Build with `build.ps1`; nothing to install.
- `qt/kadr/` — macOS and Linux, Python + Qt (PySide6). Run from source with `python -m kadr`.

| You want to change | Windows (`src/`) | macOS / Linux (`qt/kadr/`) |
|---|---|---|
| Built-in colours | `CaptureForm.cs` → `Colors` | `overlay.py` → `COLORS` |
| Default hotkey | `Settings.cs` → `Hotkey` | `settings.py` → `default_hotkey()` |
| Tool letters (P, L, A…) | `CaptureForm.cs` → `HandleKey` | `overlay.py` → `key()` |
| How a tool draws / a new tool | `Shapes.cs` | `shapes.py` |
| Toolbar buttons and icons | `CaptureForm.cs` → `Layout2`, `DrawButton` | `overlay.py` → `layout()`, `draw_icon()` |
| Built-in upload services | `Uploader.cs` | `uploader.py` |
| Notices in the corner | `Toast.cs` | `toast.py` |
| Settings window | `Forms.cs` | `dialogs.py` |
| Texts in 7 languages | `Lang.cs` (one row per text) | `lang.json` — regenerate with `python qt/tools/export_lang.py`; Mac/Linux-only texts in `i18n.py` |
| App icon | `qt/tools/draw_icon.py` draws every size into `icons/` | same |

Checks you can run after a change:

- Windows: `build.ps1 -TestOut KadrTest.exe`, then `KadrTest.exe --render-test out` draws the capture window and dialogs into `out/`.
- macOS / Linux: `python -m kadr --render-test out` (pictures) and `python -m kadr --paint-check` (simulates the mouse and makes sure nothing is left behind on screen).

## Your own installers

Fork the repository on GitHub. Every push builds everything on GitHub’s servers (`.github/workflows/build.yml`); pushing a tag such as `v1.2.0` publishes the Windows installer, the macOS `.dmg` files and the Linux `.deb`, `.rpm` and AppImage on your fork’s Releases page.

## Sharing changes back

Improvements are welcome: open an issue or a pull request. Please keep both versions in step and add the texts for all 7 languages.
