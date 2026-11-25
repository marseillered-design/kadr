# Kadr

[Русская версия](README.ru.md)

A screenshot tool for Windows, macOS and Linux in the spirit of Lightshot — but you choose where your screenshots go: Catbox, Imgur or any upload service of your own.

**Open source** under the [MIT licence](LICENSE) — change anything you like: see [Make Kadr your own](CUSTOMIZING.md).

## Download

Get the installer for your system from **Releases**:

| System | File |
|---|---|
| Windows 10/11 | `Kadr-Setup-Windows.exe` (or `Kadr.exe`, no installation needed) |
| macOS on Apple Silicon (M1–M4) | `Kadr-macOS-AppleSilicon.dmg` |
| macOS on Intel | `Kadr-macOS-Intel.dmg` |
| Ubuntu, Debian, Mint | `Kadr-Linux-amd64.deb` |
| Fedora, openSUSE | `Kadr-Linux-x86_64.rpm` |
| Any Linux, no installation | `Kadr-Linux-x86_64.AppImage` |

**Windows** — run the installer; it needs no administrator password. Kadr is not signed with a paid certificate, so Windows may show “Windows protected your PC” the first time: click **More info → Run anyway**.

**macOS** — open the .dmg and drag **Kadr** onto **Applications**. If macOS won’t open it the first time: **System Settings → Privacy & Security → Open Anyway**. On the first screenshot macOS asks for **Screen Recording** access: turn Kadr on and restart it. Kadr lives in the menu bar.

**Linux** — double-click the .deb or .rpm and choose **Install**; Kadr appears in your app menu. On GNOME the tray icon needs the AppIndicator extension (Ubuntu has it already).

## How to use

- **Shift+PageUp** selects an area (**⌘⇧2** on a Mac). You can choose any key combination you like in Settings. The window under the cursor is highlighted: a click takes exactly that window, a click on the desktop takes the whole screen. A magnifier next to the cursor shows the pixels, coordinates and colour.
- Tools on the right: pen (P), line (L), arrow (A), rectangle (R), ellipse (E), highlighter (M), text (T), numbered steps 1-2-3 (N), pixelate (B) and blur (U) to hide private data, colour, undo (Ctrl+Z). The mouse wheel changes the thickness and text size. Hold Shift for straight lines and squares.
- Below the selection: **upload** (Ctrl+D) — the arrow next to it lets you pick where to upload; **copy** (Ctrl+C or Enter); **save** (Ctrl+S); **print** (Ctrl+P); **close** (Esc).
- **Screen recording:** **Shift+PageDown** (**⌘⇧7** on a Mac) — select an area or click a window, switch the computer sound and the microphone on or off, press **Record** (Enter). A small bar next to the area shows the time and has pause, stop and discard; the bar and the red outline never appear in the video. Press Shift+PageDown again to stop. The MP4 (H.264 + AAC, made by the encoder built into Windows) goes to `Videos/Kadr` (`Movies/Kadr` on a Mac); the notice plays it, shows it in its folder or uploads it. Frames per second, folder and key in Settings → Screen recording. On Windows the encoder built into Windows makes the file; on macOS and Linux Qt's own encoder does (MP4, or WebM where no H.264 encoder is available). macOS records the computer's sound from macOS 13 on and asks once for the microphone. On Linux the computer's sound comes from PulseAudio/PipeWire; on Wayland the system asks which screen to share at the start of each recording.
- Move the selection with the mouse or the arrow keys (Shift+arrows resize it); drag its corners and edges.
- After an upload the link is already on your clipboard, and a notice appears in the corner: click it to open the link, or use the copy button.
- Saving (Ctrl+S) shows a notice too: click it to open the picture, or use the folder button to show it in Explorer / Finder.
- The tray / menu-bar icon: take a screenshot, upload a picture from disk or from the clipboard, choose where to upload, upload history (with deletion from Imgur), settings.
- Languages: Русский, Українська, English, Deutsch, Français, Español, Italiano — Settings → General → Language. Kadr starts in your system’s language.
- Screenshot format for saving and uploading: PNG, or JPG with a quality setting.
- Upload proxy: as in the system, none, or your own address with login and password (the password is stored encrypted).
- Success notices can be turned off; errors are always shown.
- **Updates itself:** Kadr checks GitHub Releases a minute after start and every 6 hours; a new version is downloaded, checked (SHA-256) and installed when Kadr is idle, then Kadr restarts. Turn it off in Settings; “Check for updates” is in the tray menu. (Linux .deb/.rpm ask for your password, so they install when you click the notice.)
- Windows: Kadr can add “Kadr (upload)” to the **Send to** menu — right-click a picture → Send to → Kadr.

## Where uploads go

**Catbox** — works right away: no account, no keys, links like `files.catbox.moe/abc123.png` don’t expire. This is the default.

**Imgur** — appears once a Client ID is built into the release (or set as `ImgurClientId` in settings.json, see [CUSTOMIZING.md](CUSTOMIZING.md)).

**ImgBB** — pictures up to 32 MB, the link points straight at the picture (`i.ibb.co/…`). Works right away: the key is built into the release. ImgBB does not take videos.

**Your own service** — Settings → Your own upload services → Add…: the address, the file field, and where the link is in the reply (`{json:data.url}`, `{response}`, `{regex:...}`). Ready-made ShareX `.sxcu` files can be imported as they are.

## Building

Everything is built on GitHub by `.github/workflows/build.yml`; a `vX.Y.Z` tag publishes the installers on Releases.

- **Windows:** `powershell -ExecutionPolicy Bypass -File build.ps1` builds `Kadr.exe` with the C# compiler that ships with Windows (.NET Framework 4.8) — nothing to install. The installer is `installer/windows/kadr.iss` (Inno Setup).
- **macOS and Linux:** the Qt version in [qt](qt/README.md).
- **Icon:** `qt/tools/draw_icon.py` draws every size into `icons/`.

Settings and history: Windows `%APPDATA%\Kadr`, macOS `~/Library/Application Support/Kadr`, Linux `~/.config/kadr`.
