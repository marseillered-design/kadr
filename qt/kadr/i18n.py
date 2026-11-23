"""Interface languages. Keys are the Russian texts (shared with the Windows version's src/Lang.cs);
values are [ru, uk, en, de, fr, es, it]."""
import json
import sys
from pathlib import Path

CODES = ["ru", "uk", "en", "de", "fr", "es", "it"]
NAMES = ["Русский", "Українська", "English", "Deutsch", "Français", "Español", "Italiano"]

_map = json.loads(Path(__file__).with_name("lang.json").read_text(encoding="utf-8"))

# Texts that only the Linux/macOS version needs.
_EXTRA = {
    "Нужен доступ к записи экрана": [
        "Нужен доступ к записи экрана", "Потрібен доступ до запису екрана", "Screen Recording access needed",
        "Zugriff auf Bildschirmaufnahme nötig", "Accès à l’enregistrement de l’écran requis",
        "Se necesita acceso a la grabación de pantalla", "Serve l’accesso alla registrazione dello schermo"],
    "mac.permission": [
        "macOS не даёт снимать чужие окна без разрешения. Открой Системные настройки → Конфиденциальность и безопасность → Запись экрана, включи «Кадр» и перезапусти его.",
        "macOS не дає знімати чужі вікна без дозволу. Відкрий Системні параметри → Приватність і безпека → Запис екрана, увімкни «Кадр» і перезапусти його.",
        "macOS needs permission to capture other windows. Open System Settings → Privacy & Security → Screen Recording, turn on Kadr and restart it.",
        "macOS braucht eine Erlaubnis, um andere Fenster aufzunehmen. Öffne Systemeinstellungen → Datenschutz & Sicherheit → Bildschirmaufnahme, aktiviere Kadr und starte es neu.",
        "macOS demande une autorisation pour capturer les autres fenêtres. Ouvrez Réglages Système → Confidentialité et sécurité → Enregistrement de l’écran, activez Kadr et relancez-le.",
        "macOS necesita permiso para capturar otras ventanas. Abre Ajustes del Sistema → Privacidad y seguridad → Grabación de pantalla, activa Kadr y reinícialo.",
        "macOS chiede un permesso per catturare le altre finestre. Apri Impostazioni di Sistema → Privacy e sicurezza → Registrazione schermo, attiva Kadr e riavvialo."],
    "Открыть настройки": ["Открыть настройки", "Відкрити налаштування", "Open Settings", "Einstellungen öffnen",
                          "Ouvrir les réglages", "Abrir Ajustes", "Apri Impostazioni"],
    "wayland.hotkey": [
        "В Wayland программы не могут сами ловить сочетания клавиш. Задай его в настройках системы (Клавиатура → Свои сочетания) с командой:",
        "У Wayland програми не можуть самі ловити поєднання клавіш. Задай його в налаштуваннях системи (Клавіатура → Власні поєднання) з командою:",
        "On Wayland apps can’t catch hotkeys themselves. Add one in the system settings (Keyboard → Custom Shortcuts) with this command:",
        "Unter Wayland können Programme selbst keine Tastenkürzel abfangen. Lege eines in den Systemeinstellungen an (Tastatur → Eigene Kürzel) mit dem Befehl:",
        "Sous Wayland, les applications ne peuvent pas capter les raccourcis elles-mêmes. Ajoutez-en un dans les paramètres du système (Clavier → Raccourcis personnalisés) avec la commande :",
        "En Wayland las aplicaciones no pueden capturar atajos por sí mismas. Añade uno en los ajustes del sistema (Teclado → Atajos personalizados) con el comando:",
        "Su Wayland le app non possono intercettare da sole le scorciatoie. Aggiungine una nelle impostazioni di sistema (Tastiera → Scorciatoie personalizzate) con il comando:"],
    "Снимок не получен: система отказала или окно выбора закрыли.": [
        "Снимок не получен: система отказала или окно выбора закрыли.",
        "Знімок не отримано: система відмовила або вікно вибору закрили.",
        "No screenshot: the system refused or the picker was closed.",
        "Kein Screenshot: Das System hat abgelehnt oder die Auswahl wurde geschlossen.",
        "Pas de capture : le système a refusé ou la fenêtre de choix a été fermée.",
        "Sin captura: el sistema la rechazó o se cerró la ventana de selección.",
        "Nessuno screenshot: il sistema ha rifiutato o la finestra di scelta è stata chiusa."],
    "Сочетание {0} занято другой программой. Выбери другое в настройках.": [
        "Сочетание {0} занято другой программой. Выбери другое в настройках.",
        "Поєднання {0} зайняте іншою програмою. Обери інше в налаштуваннях.",
        "{0} is already used by another app. Pick a different shortcut in Settings.",
        "{0} wird schon von einem anderen Programm benutzt. Wähle in den Einstellungen ein anderes Kürzel.",
        "{0} est déjà utilisé par une autre application. Choisissez un autre raccourci dans les paramètres.",
        "{0} ya lo usa otra aplicación. Elige otro atajo en Ajustes.",
        "{0} è già usata da un’altra app. Scegli un’altra scorciatoia nelle Impostazioni."],
    "Как в системе": ["Как в системе", "Як у системі", "Same as the system", "Wie im System", "Comme le système",
                      "Como el sistema", "Come il sistema"],
    "Запускать Кадр вместе с системой": [
        "Запускать Кадр вместе с системой", "Запускати Кадр разом із системою", "Start Kadr when I log in",
        "Kadr bei der Anmeldung starten", "Lancer Kadr à l’ouverture de session", "Iniciar Kadr al iniciar sesión",
        "Avvia Kadr all’accesso"],
    "Пароль хранится в связке ключей системы.": [
        "Пароль хранится в связке ключей системы.", "Пароль зберігається у в’язці ключів системи.",
        "The password is kept in the system keychain.", "Das Passwort liegt im Schlüsselbund des Systems.",
        "Le mot de passe est conservé dans le trousseau du système.", "La contraseña se guarda en el llavero del sistema.",
        "La password è conservata nel portachiavi di sistema."],
}
_map.update(_EXTRA)

_idx = 2


def auto() -> str:
    try:
        from PySide6.QtCore import QLocale
        code = QLocale.system().name()[:2]
    except Exception:
        code = "en"
    return code if code in CODES else "en"


def set_language(code: str) -> None:
    global _idx
    _idx = CODES.index(code) if code in CODES else CODES.index(auto())


def current() -> str:
    return CODES[_idx]


def index() -> int:
    return _idx


def T(key: str) -> str:
    row = _map.get(key)
    text = row[_idx] if row else key
    if sys.platform == "darwin":
        text = text.replace("Ctrl+", "⌘")
    return text


def F(key: str, *args) -> str:
    return T(key).format(*args)


def T_in(lang_index: int, key: str) -> str:
    row = _map.get(key)
    return row[lang_index] if row else key
