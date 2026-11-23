# Кадр для macOS и Linux

Та же программа, что и для Windows, только на Qt: снимок области или окна, карандаш, стрелки, шаги 1-2-3, пиксели и размытие, загрузка в Catbox / Imgur / свой сервис, копирование, сохранение, печать, история, 7 языков.

## Установка

**macOS** — `Kadr-macOS-AppleSilicon.dmg` (M1–M4) или `Kadr-macOS-Intel.dmg`.
1. Открой .dmg и перетащи **Kadr** в «Программы».
2. Программа без платной подписи Apple, поэтому первый запуск: правой кнопкой по Kadr → «Открыть» → «Открыть». Если macOS всё равно не пускает: Системные настройки → Конфиденциальность и безопасность → «Всё равно открыть».
3. При первом снимке macOS попросит доступ к **Записи экрана**: включи Kadr и перезапусти его.
4. Значок появится в строке меню вверху. Снимок — **⌘⇧2** (меняется в настройках).

**Linux** — `Kadr-Linux-x86_64.AppImage`.
1. Сделай файл исполняемым (`chmod +x Kadr-Linux-x86_64.AppImage`) и запусти.
2. Если не запускается из-за FUSE: `sudo apt install libfuse2` (Ubuntu 22.04+) или запуск с `--appimage-extract-and-run`.
3. Снимок — **Shift+PageUp** в X11.
4. **Wayland** (Ubuntu, Fedora по умолчанию): программы не могут сами ловить клавиши и снимать экран. Кадр снимает через системный портал (GNOME один раз спросит разрешение), а сочетание задаётся в настройках системы: Клавиатура → Свои сочетания → команда `путь/к/Kadr-Linux-x86_64.AppImage --capture`. Подсветка окон под курсором в Wayland недоступна — тяни рамку или щёлкни по экрану.
5. Значок в трее: в GNOME нужно расширение AppIndicator (в Ubuntu уже стоит).

## Сборка

Собирается автоматически на GitHub (`.github/workflows/build.yml`): AppImage на Ubuntu 22.04, .dmg на Mac с Apple Silicon и Intel, плюс Kadr.exe для Windows.

Локально (из папки `qt`):
```
python -m venv .venv
.venv/bin/pip install -r requirements.txt pyinstaller
.venv/bin/python -m kadr            # запустить без сборки
.venv/bin/python tools/make_icons.py && .venv/bin/pyinstaller kadr.spec
```

Тексты на 7 языках общие с Windows-версией: `kadr/lang.json` выгружен из `src/Lang.cs`; строки только для Mac/Linux — в `kadr/i18n.py`.

Настройки: macOS `~/Library/Application Support/Kadr`, Linux `~/.config/kadr`.
