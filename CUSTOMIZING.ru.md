# Как настроить Кадр под себя

[English](CUSTOMIZING.md)

Кадр — программа с открытым кодом под [лицензией MIT](LICENSE): её можно менять, собирать свою версию и делиться ею. Ниже — что можно поменять: сначала без программирования, потом в коде.

## Без программирования

**Окно настроек** (значок в трее / строке меню → Настройки): сочетание клавиш (любое), куда загружать, свои сервисы загрузки, PNG или JPG, папка сохранения, прокси, язык, уведомления, автозапуск.

**Файл настроек** — обычный текстовый файл, его можно править в Блокноте / TextEdit, пока Кадр закрыт:

| Система | Файл |
|---|---|
| Windows | `%APPDATA%\Kadr\settings.json` |
| macOS | `~/Library/Application Support/Kadr/settings.json` |
| Linux | `~/.config/kadr/settings.json` |

Полезные поля:

| Поле | Что делает |
|---|---|
| `Palette` | Цвета в окне снимка, например `["#E53935", "#00BCD4", "#FFFFFF"]` — до 12 штук |
| `PenColor`, `PenSize` | С какого цвета и толщины начинают инструменты |
| `DefaultDestination` | `Catbox`, `Imgur` или название вашего сервиса |
| `Uploaders` | Ваши сервисы загрузки (те же поля, что в файлах ShareX `.sxcu`) |
| `SaveFolder`, `AskFileName`, `ImageFormat`, `JpegQuality` | Сохранение |
| `Language` | `ru`, `uk`, `en`, `de`, `fr`, `es`, `it`; пусто — язык системы |
| `ShowNotifications`, `CopyLink`, `OpenInBrowser` | Что происходит после загрузки |

**Любой сервис загрузки**: Настройки → «Свои сервисы для загрузки» → «Добавить…» или импорт файла ShareX `.sxcu`. Многие хостинги выкладывают готовые `.sxcu`.

## В коде

В проекте две версии с одинаковыми возможностями:

- `src/` — Windows, C# (.NET Framework 4.8). Собирается `build.ps1`, ставить ничего не нужно.
- `qt/kadr/` — macOS и Linux, Python + Qt (PySide6). Запуск из исходников: `python -m kadr`.

| Что поменять | Windows (`src/`) | macOS / Linux (`qt/kadr/`) |
|---|---|---|
| Встроенные цвета | `CaptureForm.cs` → `Colors` | `overlay.py` → `COLORS` |
| Сочетание клавиш по умолчанию | `Settings.cs` → `Hotkey` | `settings.py` → `default_hotkey()` |
| Буквы инструментов (P, L, A…) | `CaptureForm.cs` → `HandleKey` | `overlay.py` → `key()` |
| Как рисует инструмент / новый инструмент | `Shapes.cs` | `shapes.py` |
| Кнопки и значки панелей | `CaptureForm.cs` → `Layout2`, `DrawButton` | `overlay.py` → `layout()`, `draw_icon()` |
| Встроенные сервисы загрузки | `Uploader.cs` | `uploader.py` |
| Уведомления в углу | `Toast.cs` | `toast.py` |
| Окно настроек | `Forms.cs` | `dialogs.py` |
| Тексты на 7 языках | `Lang.cs` (одна строка на текст) | `lang.json` — пересоздаётся командой `python qt/tools/export_lang.py`; тексты только для Mac/Linux — в `i18n.py` |
| Иконка программы | `qt/tools/draw_icon.py` рисует все размеры в `icons/` | то же |

Проверки после изменений:

- Windows: `build.ps1 -TestOut KadrTest.exe`, затем `KadrTest.exe --render-test out` рисует окно снимка и окна настроек в папку `out/`.
- macOS / Linux: `python -m kadr --render-test out` (картинки) и `python -m kadr --paint-check` (имитирует мышь и проверяет, что на экране не остаётся «хвостов»).

## Свои установщики

Сделайте fork репозитория на GitHub. Каждое изменение собирается на серверах GitHub (`.github/workflows/build.yml`); тег вроде `v1.2.0` выкладывает установщик Windows, `.dmg` для macOS и `.deb`, `.rpm`, AppImage для Linux на странице Releases вашего fork.

## Поделиться изменениями

Улучшения приветствуются: создайте issue или pull request. Пожалуйста, меняйте обе версии одинаково и добавляйте тексты на все 7 языков.
