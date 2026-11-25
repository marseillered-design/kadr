"""Settings, custom upload service, upload history."""
import json
import threading
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QBuffer, QIODevice, QSize, Qt, Signal
from PySide6.QtGui import QColor, QIcon, QImage, QKeySequence, QPainter, QPixmap
from PySide6.QtWidgets import (QAbstractItemView, QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFileDialog,
                               QFormLayout, QGridLayout, QGroupBox, QHBoxLayout, QHeaderView, QKeySequenceEdit,
                               QLabel, QLineEdit, QListWidget, QMenu, QMessageBox, QPlainTextEdit, QPushButton,
                               QSpinBox, QTreeWidget, QTreeWidgetItem, QVBoxLayout, QWidget)

from . import i18n, platform_utils, settings as st, uploader
from .i18n import T, F


def hint(text: str) -> QLabel:
    l = QLabel(text)
    l.setWordWrap(True)
    l.setStyleSheet("color: palette(placeholder-text);")
    return l


def dict_to_lines(d, sep):
    return "\n".join(f"{k}{sep}{v}" for k, v in (d or {}).items())


def lines_to_dict(text, sep):
    out = {}
    for line in (text or "").splitlines():
        if sep in line:
            k, v = line.split(sep, 1)
            if k.strip():
                out[k.strip()] = v.strip()
    return out


class SettingsDialog(QDialog):
    def __init__(self, s: st.Settings, hotkey_supported: bool):
        super().__init__()
        self.S = s
        self.language_changed = False
        self.ups = [dict(u) for u in s.Uploaders]
        self.setWindowTitle(T("Кадр — настройки"))
        cols = QHBoxLayout()
        left, right = QVBoxLayout(), QVBoxLayout()
        cols.addLayout(left, 1)
        cols.addLayout(right, 1)

        # Upload
        g = QGroupBox(T("Загрузка"))
        f = QFormLayout(g)
        self.dest = QComboBox()
        f.addRow(T("Куда загружать по умолчанию:"), self.dest)
        # keys live in the build (GitHub secrets) or, for forks, in settings.json (ImgurClientId, ImgbbKey): no fields here
        ready = [d for d in s.destinations() if d in (st.CATBOX, st.IMGUR, st.IMGBB)]
        f.addRow("", hint(F("Работают сразу, без регистрации: {0}.", ", ".join(ready))))
        self.copy_link = QCheckBox(T("Копировать ссылку после загрузки"))
        self.copy_link.setChecked(s.CopyLink)
        self.open_browser = QCheckBox(T("Открывать ссылку в браузере"))
        self.open_browser.setChecked(s.OpenInBrowser)
        f.addRow(self.copy_link)
        f.addRow(self.open_browser)
        left.addWidget(g)

        # Own services
        g = QGroupBox(T("Свои сервисы для загрузки"))
        grid = QGridLayout(g)
        self.list = QListWidget()
        self.list.itemDoubleClicked.connect(lambda _: self.edit_selected())
        grid.addWidget(self.list, 0, 0, 4, 1)
        for i, (text, fn) in enumerate([(T("Добавить…"), self.add_new), (T("Изменить…"), self.edit_selected),
                                        (T("Удалить"), self.delete_selected), (T("Импорт .sxcu…"), self.import_sxcu)]):
            b = QPushButton(text)
            b.clicked.connect(fn)
            grid.addWidget(b, i, 1)
        grid.addWidget(hint(T("Любой сайт, который принимает картинку по HTTP: свой сервер, Catbox, ImgBB, Zipline и т. п. Готовые настройки ShareX (.sxcu) можно импортировать.")), 4, 0, 1, 2)
        left.addWidget(g)

        # Proxy
        g = QGroupBox(T("Прокси для загрузки"))
        f = QFormLayout(g)
        self.proxy_mode = QComboBox()
        self.proxy_mode.addItems([T("Как в Windows") if platform_utils.WIN else T("Как в системе"),
                                  T("Без прокси"), T("Свой прокси")])
        self.proxy_mode.setCurrentIndex({"none": 1, "custom": 2}.get(s.ProxyMode, 0))
        self.proxy_addr = QLineEdit(s.ProxyAddress)
        self.proxy_user = QLineEdit(s.ProxyUser)
        self.proxy_pass = QLineEdit(s.proxy_password())
        self.proxy_pass.setEchoMode(QLineEdit.EchoMode.Password)
        f.addRow(T("Прокси:"), self.proxy_mode)
        f.addRow(T("Адрес:"), self.proxy_addr)
        lp = QHBoxLayout()
        lp.addWidget(self.proxy_user)
        lp.addWidget(QLabel(T("Пароль:")))
        lp.addWidget(self.proxy_pass)
        f.addRow(T("Логин:"), lp)
        f.addRow("", hint("192.168.1.10:8080 · " + T("Пароль хранится в связке ключей системы.")))
        self.proxy_mode.currentIndexChanged.connect(self._proxy_state)
        self._proxy_state()
        left.addWidget(g)
        left.addStretch(1)

        # Saving
        g = QGroupBox(T("Сохранение"))
        f = QFormLayout(g)
        self.folder = QLineEdit(s.SaveFolder)
        browse = QPushButton(T("Обзор…"))
        browse.clicked.connect(self.pick_folder)
        row = QHBoxLayout()
        row.addWidget(self.folder, 1)
        row.addWidget(browse)
        f.addRow(T("Папка:"), row)
        self.ask_name = QCheckBox(T("Спрашивать имя файла при сохранении (Ctrl+S)"))
        self.ask_name.setChecked(s.AskFileName)
        f.addRow(self.ask_name)
        self.fmt = QComboBox()
        self.fmt.addItems(["PNG", "JPG"])
        self.fmt.setCurrentIndex(1 if s.jpeg else 0)
        self.quality = QSpinBox()
        self.quality.setRange(50, 100)
        self.quality.setValue(s.JpegQuality)
        self.q_label = QLabel(T("Качество JPG:"))
        row = QHBoxLayout()
        row.addWidget(self.fmt)
        row.addSpacing(12)
        row.addWidget(self.q_label)
        row.addWidget(self.quality)
        row.addStretch(1)
        f.addRow(T("Формат:"), row)
        f.addRow("", hint(T("Для сохранения и загрузки снимков. PNG — без потерь, JPG — файл меньше.")))
        self.fmt.currentIndexChanged.connect(self._fmt_state)
        self._fmt_state()
        right.addWidget(g)

        # Video
        g = QGroupBox(T("Запись видео"))
        f = QFormLayout(g)
        self.vkey_on = QCheckBox(T("Начать и остановить:"))
        self.vkey_on.setChecked(bool(s.data.get("VideoHotkeyOn", True)))
        self.vkeys = QKeySequenceEdit(QKeySequence(s.data.get("VideoHotkey", "")))
        try:
            self.vkeys.setMaximumSequenceLength(1)
        except Exception:
            pass
        self.vkey_on.toggled.connect(self.vkeys.setEnabled)
        self.vkeys.setEnabled(self.vkey_on.isChecked() and hotkey_supported)
        if not hotkey_supported:
            self.vkey_on.setEnabled(False)
        f.addRow(self.vkey_on, self.vkeys)
        self.v_sys = QCheckBox(T("Звук компьютера"))
        self.v_sys.setChecked(bool(s.data.get("VideoSysAudio", True)))
        self.v_mic = QCheckBox(T("Микрофон"))
        self.v_mic.setChecked(bool(s.data.get("VideoMic", False)))
        row = QHBoxLayout()
        row.addWidget(self.v_sys)
        row.addWidget(self.v_mic)
        row.addStretch(1)
        f.addRow(T("Звук:"), row)
        self.v_fps = QComboBox()
        self.v_fps.addItems(["15", "30", "60"])
        self.v_fps.setCurrentText(str(s.data.get("VideoFps", 30)))
        f.addRow(T("Кадров в секунду:"), self.v_fps)
        self.v_folder = QLineEdit(s.data.get("VideoFolder", ""))
        vb = QPushButton(T("Обзор…"))
        vb.clicked.connect(self._pick_video_folder)
        row = QHBoxLayout()
        row.addWidget(self.v_folder, 1)
        row.addWidget(vb)
        f.addRow(T("Папка:"), row)
        f.addRow("", hint(T("Видео сохраняется в MP4. Звук и микрофон можно включить и перед записью, на панели выбора области.")))
        right.addWidget(g)

        # General
        g = QGroupBox(T("Общее"))
        v = QVBoxLayout(g)
        row = QHBoxLayout()
        row.addWidget(QLabel(T("Язык:")))
        self.lang = QComboBox()
        self.lang.addItems(i18n.NAMES)
        self.lang.setCurrentIndex(i18n.index())
        row.addWidget(self.lang)
        row.addStretch(1)
        v.addLayout(row)
        row = QHBoxLayout()
        self.hook = QCheckBox(T("Снимок по сочетанию клавиш:"))
        self.hook.setChecked(s.HookPrintScreen)
        self.keys = QKeySequenceEdit(QKeySequence(s.Hotkey))
        try:
            self.keys.setMaximumSequenceLength(1)
        except Exception:
            pass
        row.addWidget(self.hook)
        row.addWidget(self.keys, 1)
        v.addLayout(row)
        if hotkey_supported:
            v.addWidget(hint(T("Щёлкни в поле и нажми нужное сочетание, например Shift+PageUp. Пока открыто это окно, сочетание не срабатывает.")))
        else:
            self.hook.setEnabled(False)
            self.keys.setEnabled(False)
            cmd = QLineEdit(platform_utils.executable_command() + " --capture")
            cmd.setReadOnly(True)
            v.addWidget(hint(T("wayland.hotkey")))
            v.addWidget(cmd)
        self.autostart = QCheckBox(T("Запускать Кадр вместе с системой"))
        self.autostart.setChecked(platform_utils.autostart_enabled())
        self.autostart.setEnabled(not platform_utils.WIN)
        v.addWidget(self.autostart)
        self.notify = QCheckBox(T("Показывать уведомления (об ошибках — всегда)"))
        self.notify.setChecked(s.ShowNotifications)
        v.addWidget(self.notify)
        from . import updater
        self.auto_update = QCheckBox(T("Обновляться автоматически"))
        self.auto_update.setChecked(s.data.get("AutoUpdate", True))
        v.addWidget(self.auto_update)
        v.addWidget(hint(F("Версия {0}", updater.version() + (" (dev)" if updater.is_dev() else ""))))
        right.addWidget(g)
        right.addStretch(1)

        box = QDialogButtonBox()
        ok = box.addButton(T("Сохранить"), QDialogButtonBox.ButtonRole.AcceptRole)
        box.addButton(T("Отмена"), QDialogButtonBox.ButtonRole.RejectRole)
        box.accepted.connect(self.save_and_close)
        box.rejected.connect(self.reject)
        root = QVBoxLayout(self)
        root.addLayout(cols)
        root.addWidget(box)
        self.refresh_lists(s.default())

    def _proxy_state(self):
        on = self.proxy_mode.currentIndex() == 2
        for w in (self.proxy_addr, self.proxy_user, self.proxy_pass):
            w.setEnabled(on)

    def _fmt_state(self):
        on = self.fmt.currentIndex() == 1
        self.quality.setEnabled(on)
        self.q_label.setEnabled(on)

    def refresh_lists(self, selected):
        self.list.clear()
        for u in self.ups:
            host = uploader.urlparse(u.get("RequestURL") or "").hostname or T("адрес не указан")
            self.list.addItem(f"{u['Name']}   —   {host}")
        self.dest.clear()
        names = [d for d in self.S.destinations() if d in (st.CATBOX, st.IMGUR, st.IMGBB)]
        names += [u["Name"] for u in self.ups if u["Name"] not in (st.CATBOX, st.IMGUR, st.IMGBB)]
        self.dest.addItems(names)
        self.dest.setCurrentText(selected if selected in names else st.CATBOX)

    def unique_name(self, name, except_index=-1):
        n = (name or "").strip() or T("Мой сервис")
        if n in (st.IMGUR, st.CATBOX, st.IMGBB):
            n += T(" (свой)")
        cand, k = n, 2
        while any(u["Name"] == cand for i, u in enumerate(self.ups) if i != except_index):
            cand = f"{n} {k}"
            k += 1
        return cand

    def add_new(self):
        u = st.normalize_uploader({"Name": self.unique_name(T("Мой сервис"))})
        d = UploaderDialog(self.S, u, self)
        if d.exec():
            u["Name"] = self.unique_name(u["Name"])
            self.ups.append(u)
            self.refresh_lists(self.dest.currentText())

    def edit_selected(self):
        i = self.list.currentRow()
        if i < 0:
            return
        u = json.loads(json.dumps(self.ups[i]))
        was = u["Name"]
        if UploaderDialog(self.S, u, self).exec():
            if u["Name"] != was:
                u["Name"] = self.unique_name(u["Name"], i)
            self.ups[i] = u
            cur = self.dest.currentText()
            self.refresh_lists(u["Name"] if cur == was else cur)

    def delete_selected(self):
        i = self.list.currentRow()
        if i < 0:
            return
        if QMessageBox.question(self, T("Кадр"), F("Удалить сервис «{0}»?", self.ups[i]["Name"])) == QMessageBox.StandardButton.Yes:
            del self.ups[i]
            self.refresh_lists(self.dest.currentText())

    def import_sxcu(self):
        files, _ = QFileDialog.getOpenFileNames(self, "", "", T("Настройки ShareX") + " (*.sxcu);;JSON (*.json);;" + T("Все файлы") + " (*)")
        errors = []
        for fpath in files:
            try:
                raw = json.loads(Path(fpath).read_text(encoding="utf-8-sig"))
                if not raw.get("RequestURL"):
                    raise ValueError(T("в файле нет адреса загрузки (RequestURL)"))
                u = st.normalize_uploader(raw)
                if u["Name"] == T("Мой сервис"):
                    u["Name"] = Path(fpath).stem
                u["Name"] = self.unique_name(u["Name"])
                self.ups.append(u)
            except Exception as e:
                errors.append(f"{Path(fpath).name}: {e}")
        self.refresh_lists(self.dest.currentText())
        if errors:
            QMessageBox.warning(self, T("Кадр"), T("Не всё удалось импортировать:") + "\n\n" + "\n".join(errors))

    def _pick_video_folder(self):
        d = QFileDialog.getExistingDirectory(self, T("Куда сохранять видео"), self.v_folder.text())
        if d:
            self.v_folder.setText(d)

    def pick_folder(self):
        d = QFileDialog.getExistingDirectory(self, T("Куда сохранять снимки"), self.folder.text())
        if d:
            self.folder.setText(d)

    def save_and_close(self):
        if self.proxy_mode.currentIndex() == 2 and not uploader.proxy_url(self.proxy_addr.text()):
            QMessageBox.warning(self, T("Кадр"), T("Адрес прокси не похож на настоящий. Пример: 192.168.1.10:8080"))
            self.proxy_addr.setFocus()
            return
        s = self.S
        code = i18n.CODES[max(0, self.lang.currentIndex())]
        self.language_changed = code != i18n.current()
        keys = self.keys.keySequence().toString(QKeySequence.SequenceFormat.PortableText)
        vkeys = self.vkeys.keySequence().toString(QKeySequence.SequenceFormat.PortableText) or s.data.get("VideoHotkey", "")
        if self.vkey_on.isChecked() and vkeys == (keys or s.Hotkey) and self.hook.isChecked():
            QMessageBox.warning(self, T("Кадр"), T("Для снимка и для видео нужны разные сочетания клавиш."))
            self.vkeys.setFocus()
            return
        s.set(VideoHotkeyOn=self.vkey_on.isChecked(), VideoHotkey=vkeys, VideoSysAudio=self.v_sys.isChecked(),
              VideoMic=self.v_mic.isChecked(), VideoFps=int(self.v_fps.currentText()),
              VideoFolder=self.v_folder.text().strip() or s.data.get("VideoFolder", ""))
        s.set(CopyLink=self.copy_link.isChecked(),
              OpenInBrowser=self.open_browser.isChecked(), AskFileName=self.ask_name.isChecked(),
              SaveFolder=self.folder.text().strip() or s.SaveFolder, HookPrintScreen=self.hook.isChecked(),
              Hotkey=keys or s.Hotkey, Uploaders=self.ups, DefaultDestination=self.dest.currentText(),
              ImageFormat="jpg" if self.fmt.currentIndex() == 1 else "png", JpegQuality=self.quality.value(),
              ShowNotifications=self.notify.isChecked(), AutoUpdate=self.auto_update.isChecked(),
              ProxyMode={1: "none", 2: "custom"}.get(self.proxy_mode.currentIndex(), "system"),
              ProxyAddress=self.proxy_addr.text().strip(), ProxyUser=self.proxy_user.text().strip(), Language=code)
        s.set_proxy_password(self.proxy_pass.text())
        i18n.set_language(code)
        s.save()
        try:
            if self.autostart.isEnabled() and self.autostart.isChecked() != platform_utils.autostart_enabled():
                platform_utils.set_autostart(self.autostart.isChecked())
        except Exception as e:
            QMessageBox.warning(self, T("Кадр"), T("Не удалось настроить автозапуск:") + " " + str(e))
        self.accept()


class UploaderDialog(QDialog):
    tested = Signal(object, object)

    def __init__(self, s, u: dict, parent=None):
        super().__init__(parent)
        self.S, self.u = s, u
        self.setWindowTitle(T("Свой сервис для загрузки"))
        f = QFormLayout()
        url = u.get("RequestURL") or ""
        if u.get("Parameters"):
            url += ("&" if "?" in url else "?") + "&".join(f"{k}={v}" for k, v in u["Parameters"].items())
        self.name = QLineEdit(u["Name"])
        self.url = QLineEdit(url)
        self.method = QComboBox()
        self.method.addItems(["POST", "PUT", "PATCH"])
        self.method.setCurrentText((u.get("RequestMethod") or "POST").upper())
        self.body = QComboBox()
        self.body.addItems([T("Форма (MultipartFormData)"), T("Файл целиком (Binary)")])
        self.body.setCurrentIndex(1 if (u.get("Body") or "").lower() == "binary" else 0)
        self.field = QLineEdit(u.get("FileFormName") or "file")
        self.headers = QPlainTextEdit(dict_to_lines(u.get("Headers"), ": "))
        self.args = QPlainTextEdit(dict_to_lines(u.get("Arguments"), "="))
        for e in (self.headers, self.args):
            e.setFixedHeight(56)
        self.link = QLineEdit(u.get("URL") or "")
        self.dele = QLineEdit(u.get("DeletionURL") or "")
        f.addRow(T("Название:"), self.name)
        f.addRow(T("Адрес загрузки:"), self.url)
        f.addRow(T("Метод:"), self.method)
        f.addRow(T("Как отправлять:"), self.body)
        f.addRow(T("Имя поля с файлом:"), self.field)
        f.addRow(T("Заголовки:"), self.headers)
        f.addRow("", hint(T("По строке: Имя: значение. Например  Authorization: Bearer ключ")))
        f.addRow(T("Доп. поля формы:"), self.args)
        f.addRow("", hint(T("По строке: имя=значение. Например  reqtype=fileupload")))
        f.addRow(T("Ссылка в ответе:"), self.link)
        f.addRow("", hint(T("Пусто — сервер отвечает просто ссылкой. Если отвечает JSON: {json:data.url} или {json:files[0].url}. Ещё можно {regex:https?://\\S+}.")))
        f.addRow(T("Ссылка удаления:"), self.dele)
        f.addRow("", hint(T("Необязательно. Так же, например {json:data.delete_url}.")))
        self.test_btn = QPushButton(T("Проверить"))
        self.test_btn.clicked.connect(self.test)
        box = QDialogButtonBox()
        box.addButton("OK", QDialogButtonBox.ButtonRole.AcceptRole)
        box.addButton(T("Отмена"), QDialogButtonBox.ButtonRole.RejectRole)
        box.accepted.connect(self.ok)
        box.rejected.connect(self.reject)
        bottom = QHBoxLayout()
        bottom.addWidget(self.test_btn)
        bottom.addStretch(1)
        bottom.addWidget(box)
        v = QVBoxLayout(self)
        v.addLayout(f)
        v.addLayout(bottom)
        self.tested.connect(self._tested)

    def apply(self, u):
        u.update(Name=self.name.text().strip() or T("Мой сервис"), RequestURL=self.url.text().strip(), Parameters={},
                 RequestMethod=self.method.currentText(), Body="Binary" if self.body.currentIndex() == 1 else "MultipartFormData",
                 FileFormName=self.field.text().strip() or "file", Headers=lines_to_dict(self.headers.toPlainText(), ":"),
                 Arguments=lines_to_dict(self.args.toPlainText(), "="), URL=self.link.text().strip(), DeletionURL=self.dele.text().strip())

    def ok(self):
        self.apply(self.u)
        self.accept()

    def test(self):
        u = st.normalize_uploader({})
        self.apply(u)
        if not u["RequestURL"]:
            QMessageBox.information(self, T("Кадр"), T("Сначала укажи адрес загрузки."))
            return
        if QMessageBox.question(self, T("Проверка"), F("Загрузить на {0} маленькую тестовую картинку?", u["RequestURL"])) != QMessageBox.StandardButton.Yes:
            return
        img = QImage(240, 80, QImage.Format.Format_RGB32)
        img.fill(QColor(61, 90, 254))
        p = QPainter(img)
        p.setPen(Qt.GlobalColor.white)
        p.drawText(img.rect(), Qt.AlignmentFlag.AlignCenter, T("Кадр — тест"))
        p.end()
        buf = QBuffer()
        buf.open(QIODevice.OpenModeFlag.WriteOnly)
        img.save(buf, "PNG")
        data = bytes(buf.data())
        self.test_btn.setEnabled(False)
        self.test_btn.setText(T("Проверяю…"))

        def work():
            try:
                self.tested.emit(uploader.custom(self.S, u, data, "kadr-test.png"), None)
            except Exception as e:
                self.tested.emit(None, e)

        threading.Thread(target=work, daemon=True).start()

    def _tested(self, r, err):
        self.test_btn.setEnabled(True)
        self.test_btn.setText(T("Проверить"))
        if err is not None:
            QMessageBox.warning(self, T("Проверка"), T("Не получилось:") + " " + str(err))
            return
        msg = T("Работает! Ссылка:") + "\n" + r.url
        if r.delete_url:
            msg += "\n\n" + T("Ссылка удаления:") + "\n" + r.delete_url
        if QMessageBox.question(self, T("Проверка"), msg + "\n\n" + T("Открыть в браузере?")) == QMessageBox.StandardButton.Yes:
            platform_utils.open_url(r.url)


class HistoryDialog(QDialog):
    deleted = Signal(object)

    def __init__(self, s):
        super().__init__()
        self.S = s
        self.setWindowTitle(T("Кадр — история загрузок"))
        self.resize(760, 480)
        self.tree = QTreeWidget()
        self.tree.setHeaderLabels([T("Когда"), T("Куда"), T("Ссылка")])
        self.tree.setRootIsDecorated(False)
        self.tree.setIconSize(QSize(64, 40))
        self.tree.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.tree.header().setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        self.tree.setColumnWidth(0, 210)
        self.tree.setColumnWidth(1, 90)
        self.tree.itemDoubleClicked.connect(lambda it, c: platform_utils.open_url(it.data(0, Qt.ItemDataRole.UserRole)["Url"]))
        self.tree.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.tree.customContextMenuRequested.connect(self.menu)
        bottom = QHBoxLayout()
        bottom.addWidget(hint(T("Двойной щелчок — открыть, правая кнопка — копировать или удалить.")), 1)
        clear = QPushButton(T("Очистить историю"))
        clear.clicked.connect(self.clear_all)
        close = QPushButton(T("Закрыть"))
        close.clicked.connect(self.close)
        bottom.addWidget(clear)
        bottom.addWidget(close)
        v = QVBoxLayout(self)
        v.addWidget(self.tree)
        v.addLayout(bottom)
        self.deleted.connect(self._deleted)
        self.reload()

    def reload(self):
        self.tree.clear()
        for it in st.load_history():
            try:
                when = datetime.fromisoformat(str(it.get("Time"))).strftime("%d.%m.%Y %H:%M")
            except Exception:
                when = str(it.get("Time", ""))
            row = QTreeWidgetItem([when, it.get("Destination", ""), it.get("Url", "")])
            row.setData(0, Qt.ItemDataRole.UserRole, it)
            if it.get("Thumb"):
                pm = QPixmap(str(st.thumb_dir() / it["Thumb"]))
                if not pm.isNull():
                    row.setIcon(0, QIcon(pm))
            self.tree.addTopLevelItem(row)

    def selected(self):
        it = self.tree.currentItem()
        return it.data(0, Qt.ItemDataRole.UserRole) if it else None

    def menu(self, pos):
        it = self.selected()
        if not it:
            return
        m = QMenu(self)
        m.addAction(T("Открыть"), lambda: platform_utils.open_url(it["Url"]))
        from PySide6.QtGui import QGuiApplication
        m.addAction(T("Копировать ссылку"), lambda: QGuiApplication.clipboard().setText(it["Url"]))
        a = m.addAction(T("Удалить с сервера…"), lambda: self.delete_server(it))
        a.setEnabled(bool(it.get("ImgurDeleteHash") or it.get("DeleteUrl")))
        m.addAction(T("Убрать из истории"), lambda: self.remove(it))
        m.exec(self.tree.viewport().mapToGlobal(pos))

    def remove(self, it):
        items = [x for x in st.load_history() if not (x.get("Url") == it.get("Url") and x.get("Time") == it.get("Time"))]
        st.delete_thumb(it)
        st.save_history(items)
        self.reload()

    def delete_server(self, it):
        if it.get("ImgurDeleteHash"):
            if QMessageBox.question(self, T("Кадр"), T("Удалить картинку с Imgur? Ссылка перестанет открываться.") + "\n\n" + it["Url"]) != QMessageBox.StandardButton.Yes:
                return

            def work():
                try:
                    uploader.imgur_delete(self.S, it["ImgurDeleteHash"])
                    self.deleted.emit((it, None))
                except Exception as e:
                    self.deleted.emit((it, e))

            threading.Thread(target=work, daemon=True).start()
        elif it.get("DeleteUrl"):
            if QMessageBox.question(self, T("Кадр"), T("Открыть ссылку удаления в браузере?") + "\n\n" + it["DeleteUrl"]) == QMessageBox.StandardButton.Yes:
                platform_utils.open_url(it["DeleteUrl"])

    def _deleted(self, pair):
        it, err = pair
        if err is not None:
            QMessageBox.warning(self, T("Кадр"), T("Не удалось удалить:") + " " + str(err))
            return
        self.remove(it)
        QMessageBox.information(self, T("Кадр"), T("Удалено с Imgur."))

    def clear_all(self):
        if QMessageBox.question(self, T("Кадр"), T("Очистить историю? Сами картинки на серверах останутся.")) != QMessageBox.StandardButton.Yes:
            return
        for it in st.load_history():
            st.delete_thumb(it)
        st.save_history([])
        self.reload()
