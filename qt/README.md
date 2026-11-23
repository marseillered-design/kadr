# Кадр для macOS и Linux

Та же программа, что и для Windows, только на Qt: снимок области или окна, карандаш, стрелки, шаги 1-2-3, пиксели и размытие, загрузка в Catbox / Imgur / свой сервис, копирование, сохранение, печать, история, 7 языков.

## Установка (скачать — на странице Releases)

**macOS** — `Kadr-macOS-AppleSilicon.dmg` (M1–M4) или `Kadr-macOS-Intel.dmg`.
1. Открой .dmg и перетащи **Kadr** на папку «Программы».
2. Запусти Kadr из «Программ». У программы нет платной подписи Apple, поэтому в первый раз macOS может не открыть её: Системные настройки → Конфиденциальность и безопасность → «Всё равно открыть». Это один раз.
3. При первом снимке macOS попросит доступ к **Записи экрана**: включи Kadr и перезапусти его.
4. Значок появится в строке меню вверху. Снимок — **⌘⇧2** (меняется в настройках).

**Linux** — `Kadr-Linux-amd64.deb` (Ubuntu, Mint, Debian) или `Kadr-Linux-x86_64.rpm` (Fedora, openSUSE).
1. Двойной щелчок по файлу → «Установить». Кадр появится в меню приложений.
2. Снимок — **Shift+PageUp**. В Wayland на GNOME (Ubuntu, Fedora) Кадр сам прописывает это сочетание в настройки системы; в KDE Wayland его нужно задать вручную на команду `kadr --capture`.
3. В Wayland снимок идёт через системный портал (GNOME один раз спросит разрешение), подсветки окон под курсором там нет — тяни рамку или щёлкни по экрану.
4. Значок в трее: в GNOME нужно расширение AppIndicator (в Ubuntu уже стоит).

`Kadr-Linux-x86_64.AppImage` — вариант без установки (нужно разрешить запуск файла в его свойствах).

## Сборка

Собирается автоматически на GitHub (`.github/workflows/build.yml`): .deb/.rpm/AppImage на Ubuntu 22.04, .dmg на Mac с Apple Silicon и Intel, установщик Windows (Inno Setup, `installer/windows/kadr.iss`). Тег `vX.Y.Z` публикует всё на странице Releases.

Иконка: `qt/tools/draw_icon.py` рисует все размеры в `icons/`.

Локально (из папки `qt`):
```
python -m venv .venv
.venv/bin/pip install -r requirements.txt pyinstaller
.venv/bin/python -m kadr            # запустить без сборки
.venv/bin/pyinstaller kadr.spec
```

Тексты на 7 языках общие с Windows-версией: `kadr/lang.json` выгружен из `src/Lang.cs`; строки только для Mac/Linux — в `kadr/i18n.py`.

Настройки: macOS `~/Library/Application Support/Kadr`, Linux `~/.config/kadr`.
