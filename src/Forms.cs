using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Kadr
{
    /// <summary>Small helpers for building auto-sizing dialogs that look right at any screen scale.</summary>
    static class Lay
    {
        public static void Dialog(Form f, string title)
        {
            f.Text = title;
            f.Font = UI.Font;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding(UI.S(10));
            f.Icon = AppIcon.Get(32);
        }

        public static TableLayoutPanel Table(int cols)
        {
            var t = new TableLayoutPanel { ColumnCount = cols, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0) };
            for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        public static GroupBox Group(string title, Control content)
        {
            var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(UI.S(10), UI.S(6), UI.S(10), UI.S(8)), Margin = new Padding(0, 0, 0, UI.S(8)) };
            g.Controls.Add(content);
            return g;
        }

        public static Label Label(string text, int maxWidth = 0)
        {
            var l = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, UI.S(6), UI.S(8), UI.S(4)) };
            if (maxWidth > 0) l.MaximumSize = new Size(UI.S(maxWidth), 0);
            return l;
        }

        public static Label Hint(string text, int maxWidth)
        {
            var l = Label(text, maxWidth);
            l.ForeColor = SystemColors.GrayText;
            return l;
        }

        public static TextBox Text(int width, string value)
        {
            return new TextBox { Width = UI.S(width), Text = value ?? "", Anchor = AnchorStyles.Left, Margin = new Padding(0, UI.S(3), UI.S(6), UI.S(3)) };
        }

        public static Button Button(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(UI.S(88), UI.S(28)), Padding = new Padding(UI.S(6), 0, UI.S(6), 0), Margin = new Padding(0, UI.S(3), UI.S(6), UI.S(3)) };
            if (click != null) b.Click += click;
            return b;
        }

        public static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, UI.S(4), 0, UI.S(4)) };
        }

        public static FlowLayoutPanel Flow(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            f.Controls.AddRange(items);
            return f;
        }

        public static void Row(TableLayoutPanel t, params Control[] cells)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < cells.Length; i++)
            {
                t.Controls.Add(cells[i], i, row);
                if (i == cells.Length - 1 && cells.Length < t.ColumnCount) t.SetColumnSpan(cells[i], t.ColumnCount - i);
            }
        }

        public static FlowLayoutPanel Buttons(params Button[] buttons)
        {
            var f = Flow(buttons);
            f.Anchor = AnchorStyles.Right;
            f.Margin = new Padding(0, UI.S(4), 0, 0);
            return f;
        }

        public static string DictToLines(Dictionary<string, string> d, string sep)
        {
            return string.Join(Environment.NewLine, (d ?? new Dictionary<string, string>()).Select(p => p.Key + sep + p.Value));
        }

        public static Dictionary<string, string> LinesToDict(string text, char sep)
        {
            var d = new Dictionary<string, string>();
            foreach (var raw in (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = raw.IndexOf(sep);
                if (i <= 0) continue;
                d[raw.Substring(0, i).Trim()] = raw.Substring(i + 1).Trim();
            }
            return d;
        }
    }

    class SettingsForm : Form
    {
        readonly Settings S;
        readonly List<CustomUploader> ups;
        readonly ComboBox dest;
        readonly TextBox imgurId, folder;
        readonly CheckBox copyLink, openBrowser, askName, hookKey, autostart, sendTo;
        readonly HotkeyBox hotkey;
        readonly ComboBox lang, format, proxyMode;
        readonly NumericUpDown quality;
        readonly TextBox proxyAddr, proxyUser, proxyPass;
        readonly CheckBox notify, autoUpdate;
        readonly HotkeyBox videoKey;
        readonly CheckBox videoKeyOn;
        readonly CheckBox vSys, vMic;
        readonly ComboBox vFps;
        readonly TextBox vFolder;
        public bool LanguageChanged;
        readonly ListBox list;

        public SettingsForm(Settings s)
        {
            S = s;
            ups = s.Uploaders.Select(u => u.Clone()).ToList();
            Lay.Dialog(this, L.T("Кадр — настройки"));

            var root = Lay.Table(1);
            var cols = Lay.Table(2);
            var left = Lay.Table(1);
            var right = Lay.Table(1);
            left.Margin = new Padding(0, 0, UI.S(10), 0);
            cols.Controls.Add(left, 0, 0);
            cols.Controls.Add(right, 1, 0);
            root.Controls.Add(cols);

            // Upload
            var up = Lay.Table(2);
            dest = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(240), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            Lay.Row(up, Lay.Label(L.T("Куда загружать по умолчанию:")), dest);
            imgurId = Lay.Text(240, s.ImgurClientId);
            var howTo = new LinkLabel { Text = L.T("Где его взять?"), AutoSize = true, Margin = new Padding(0, UI.S(6), 0, 0) };
            howTo.LinkClicked += (o, e) => ImgurHelp();
            Lay.Row(up, Lay.Label("Imgur Client ID:"), Lay.Flow(imgurId, howTo));
            copyLink = Lay.Check(L.T("Копировать ссылку после загрузки"), s.CopyLink);
            openBrowser = Lay.Check(L.T("Открывать ссылку в браузере"), s.OpenInBrowser);
            Lay.Row(up, new Label(), Lay.Hint(Uploader.BuiltInImgurId.Length > 0 ? L.T("Catbox и Imgur работают сразу. Свой Client ID для Imgur — по желанию.") : L.T("Catbox работает без регистрации, ссылки вечные. Для Imgur нужен Client ID."), 300));
            Lay.Row(up, copyLink);
            Lay.Row(up, openBrowser);
            left.Controls.Add(Lay.Group(L.T("Загрузка"), up));

            // Own services
            var own = Lay.Table(2);
            list = new ListBox { Width = UI.S(300), Height = UI.S(110), IntegralHeight = false, Margin = new Padding(0, UI.S(3), UI.S(8), UI.S(3)) };
            list.DoubleClick += (o, e) => EditSelected();
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            col.Controls.Add(Lay.Button(L.T("Добавить…"), (o, e) => AddNew()));
            col.Controls.Add(Lay.Button(L.T("Изменить…"), (o, e) => EditSelected()));
            col.Controls.Add(Lay.Button(L.T("Удалить"), (o, e) => DeleteSelected()));
            col.Controls.Add(Lay.Button(L.T("Импорт .sxcu…"), (o, e) => Import()));
            Lay.Row(own, list, col);
            Lay.Row(own, Lay.Hint(L.T("Любой сайт, который принимает картинку по HTTP: свой сервер, Catbox, ImgBB, Zipline и т. п. Готовые настройки ShareX (.sxcu) можно импортировать."), 420));
            left.Controls.Add(Lay.Group(L.T("Свои сервисы для загрузки"), own));

            // Proxy
            var px = Lay.Table(2);
            proxyMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(200), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            proxyMode.Items.AddRange(new object[] { L.T("Как в Windows"), L.T("Без прокси"), L.T("Свой прокси") });
            proxyMode.SelectedIndex = s.ProxyMode == "none" ? 1 : s.ProxyMode == "custom" ? 2 : 0;
            proxyAddr = Lay.Text(260, s.ProxyAddress);
            proxyUser = Lay.Text(120, s.ProxyUser);
            proxyPass = Lay.Text(120, Secret.Unprotect(s.ProxyPassword));
            proxyPass.UseSystemPasswordChar = true;
            Lay.Row(px, Lay.Label(L.T("Прокси:")), proxyMode);
            Lay.Row(px, Lay.Label(L.T("Адрес:")), proxyAddr);
            Lay.Row(px, Lay.Label(L.T("Логин:")), Lay.Flow(proxyUser, Lay.Label(L.T("Пароль:")), proxyPass));
            Lay.Row(px, new Label(), Lay.Hint(L.T("Например 192.168.1.10:8080. Логин и пароль — если прокси их просит; пароль хранится зашифрованным."), 300));
            EventHandler pxState = (o, e) => { bool on = proxyMode.SelectedIndex == 2; proxyAddr.Enabled = proxyUser.Enabled = proxyPass.Enabled = on; };
            proxyMode.SelectedIndexChanged += pxState;
            pxState(null, null);
            left.Controls.Add(Lay.Group(L.T("Прокси для загрузки"), px));

            // Saving
            var sv = Lay.Table(2);
            folder = Lay.Text(300, s.SaveFolder);
            Lay.Row(sv, Lay.Label(L.T("Папка:")), Lay.Flow(folder, Lay.Button(L.T("Обзор…"), (o, e) => PickFolder())));
            askName = Lay.Check(L.T("Спрашивать имя файла при сохранении (Ctrl+S)"), s.AskFileName);
            Lay.Row(sv, askName);
            format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(80), Margin = new Padding(0, UI.S(3), UI.S(12), UI.S(3)) };
            format.Items.AddRange(new object[] { "PNG", "JPG" });
            format.SelectedIndex = s.Jpeg ? 1 : 0;
            quality = new NumericUpDown { Minimum = 50, Maximum = 100, Value = Math.Max(50, Math.Min(100, s.JpegQuality)), Width = UI.S(60), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            var qLabel = Lay.Label(L.T("Качество JPG:"));
            EventHandler fmtState = (o, e) => { quality.Enabled = qLabel.Enabled = format.SelectedIndex == 1; };
            format.SelectedIndexChanged += fmtState;
            fmtState(null, null);
            Lay.Row(sv, Lay.Label(L.T("Формат:")), Lay.Flow(format, qLabel, quality));
            Lay.Row(sv, new Label(), Lay.Hint(L.T("Для сохранения и загрузки снимков. PNG — без потерь, JPG — файл меньше."), 300));
            right.Controls.Add(Lay.Group(L.T("Сохранение"), sv));

            // Video
            var vg = Lay.Table(2);
            videoKey = new HotkeyBox(s.VideoHotkey);
            videoKey.Margin = new Padding(0, UI.S(3), 0, UI.S(3));
            videoKeyOn = Lay.Check(L.T("Начать и остановить:"), s.VideoHotkeyOn);
            videoKeyOn.CheckedChanged += (o, e) => videoKey.Enabled = videoKeyOn.Checked;
            videoKey.Enabled = videoKeyOn.Checked;
            Lay.Row(vg, videoKeyOn, videoKey);
            vSys = Lay.Check(L.T("Звук компьютера"), s.VideoSysAudio);
            vMic = Lay.Check(L.T("Микрофон"), s.VideoMic);
            Lay.Row(vg, Lay.Label(L.T("Звук:")), Lay.Flow(vSys, vMic));
            vFps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(80), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            vFps.Items.AddRange(new object[] { "15", "30", "60" });
            vFps.SelectedItem = s.VideoFps.ToString();
            if (vFps.SelectedIndex < 0) vFps.SelectedIndex = 1;
            Lay.Row(vg, Lay.Label(L.T("Кадров в секунду:")), vFps);
            vFolder = Lay.Text(300, s.VideoFolder);
            Lay.Row(vg, Lay.Label(L.T("Папка:")), Lay.Flow(vFolder, Lay.Button(L.T("Обзор…"), (o, e) => PickVideoFolder())));
            Lay.Row(vg, new Label(), Lay.Hint(L.T("Видео сохраняется в MP4. Звук и микрофон можно включить и перед записью, на панели выбора области."), 300));
            right.Controls.Add(Lay.Group(L.T("Запись видео"), vg));

            // General
            var gen = Lay.Table(1);
            lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(170), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            lang.Items.AddRange(L.Names);
            lang.SelectedIndex = L.Index;
            Lay.Row(gen, Lay.Flow(Lay.Label(L.T("Язык:")), lang));
            hookKey = Lay.Check(L.T("Снимок по сочетанию клавиш:"), s.HookPrintScreen);
            hotkey = new HotkeyBox(s.Hotkey);
            hookKey.CheckedChanged += (o, e) => hotkey.Enabled = hookKey.Checked;
            hotkey.Enabled = hookKey.Checked;
            autostart = Lay.Check(L.T("Запускать Кадр вместе с Windows"), Shell.Autostart);
            sendTo = Lay.Check(L.T("Пункт «Кадр (загрузить)» в меню «Отправить» для картинок"), Shell.SendTo);
            Lay.Row(gen, Lay.Flow(hookKey, hotkey));
            Lay.Row(gen, Lay.Hint(L.T("Щёлкни в поле и нажми нужное сочетание, например Shift+PageUp. Пока открыто это окно, сочетание не срабатывает."), 440));
            Lay.Row(gen, autostart);
            Lay.Row(gen, sendTo);
            notify = Lay.Check(L.T("Показывать уведомления (об ошибках — всегда)"), s.ShowNotifications);
            Lay.Row(gen, notify);
            autoUpdate = Lay.Check(L.T("Обновляться автоматически"), s.AutoUpdate);
            Lay.Row(gen, autoUpdate);
            Lay.Row(gen, Lay.Hint(L.F("Версия {0}", Updater.IsDev ? Updater.Version + " (dev)" : Updater.Version), 440));
            right.Controls.Add(Lay.Group(L.T("Общее"), gen));

            var ok = Lay.Button(L.T("Сохранить"), (o, e) => SaveAndClose());
            var cancel = Lay.Button(L.T("Отмена"), (o, e) => Close());
            root.Controls.Add(Lay.Buttons(ok, cancel));
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(root);
            RefreshLists(s.Default());
        }

        void RefreshLists(string selectedDest)
        {
            list.Items.Clear();
            foreach (var u in ups) list.Items.Add(u.Name + "   —   " + Host(u.RequestURL));
            dest.Items.Clear();
            dest.Items.Add(Settings.Catbox);
            dest.Items.Add(Settings.Imgur);
            foreach (var u in ups) if (!dest.Items.Contains(u.Name)) dest.Items.Add(u.Name);
            dest.SelectedItem = dest.Items.Contains(selectedDest) ? selectedDest : Settings.Catbox;
        }

        static string Host(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u.Host : L.T("адрес не указан");
        }

        string CurrentDest { get { return dest.SelectedItem as string ?? Settings.Catbox; } }

        void AddNew()
        {
            var u = new CustomUploader { Name = UniqueName(L.T("Мой сервис")) };
            using (var f = new UploaderForm(u))
                if (f.ShowDialog(this) == DialogResult.OK) { u.Name = UniqueName(u.Name); ups.Add(u); RefreshLists(CurrentDest); list.SelectedIndex = ups.Count - 1; }
        }

        void EditSelected()
        {
            int i = list.SelectedIndex;
            if (i < 0) return;
            var copy = ups[i].Clone();
            string was = copy.Name;
            using (var f = new UploaderForm(copy))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    if (copy.Name != was) copy.Name = UniqueName(copy.Name, i);
                    ups[i] = copy;
                    RefreshLists(CurrentDest == was ? copy.Name : CurrentDest);
                    list.SelectedIndex = i;
                }
        }

        void DeleteSelected()
        {
            int i = list.SelectedIndex;
            if (i < 0) return;
            if (MessageBox.Show(this, L.F("Удалить сервис «{0}»?", ups[i].Name), L.T("Кадр"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ups.RemoveAt(i);
            RefreshLists(CurrentDest);
        }

        void Import()
        {
            using (var d = new OpenFileDialog { Filter = L.T("Настройки ShareX") + " (*.sxcu)|*.sxcu|JSON|*.json|" + L.T("Все файлы") + "|*.*", Multiselect = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var errors = new StringBuilder();
                foreach (var file in d.FileNames)
                {
                    try
                    {
                        var u = Json.Ser.Deserialize<CustomUploader>(File.ReadAllText(file));
                        if (u == null || string.IsNullOrWhiteSpace(u.RequestURL)) throw new Exception(L.T("в файле нет адреса загрузки (RequestURL)"));
                        u.Normalize();
                        if (u.Name == L.T("Мой сервис")) u.Name = Path.GetFileNameWithoutExtension(file);
                        u.Name = UniqueName(u.Name);
                        ups.Add(u);
                    }
                    catch (Exception ex) { errors.AppendLine(Path.GetFileName(file) + ": " + ex.Message); }
                }
                RefreshLists(CurrentDest);
                if (errors.Length > 0) MessageBox.Show(this, L.T("Не всё удалось импортировать:") + "\n\n" + errors, L.T("Кадр"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        string UniqueName(string name, int except = -1)
        {
            string n = string.IsNullOrWhiteSpace(name) ? L.T("Мой сервис") : name.Trim();
            if (n == Settings.Imgur || n == Settings.Catbox) n += L.T(" (свой)");
            string candidate = n;
            int k = 2;
            while (ups.Where((u, i) => i != except).Any(u => u.Name == candidate)) candidate = n + " " + k++;
            return candidate;
        }

        void PickFolder()
        {
            using (var d = new FolderBrowserDialog { SelectedPath = folder.Text, Description = L.T("Куда сохранять снимки") })
                if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
        }

        void PickVideoFolder()
        {
            using (var d = new FolderBrowserDialog { SelectedPath = vFolder.Text, Description = L.T("Куда сохранять видео") })
                if (d.ShowDialog(this) == DialogResult.OK) vFolder.Text = d.SelectedPath;
        }

        void ImgurHelp()
        {
            var text = L.T("imgur.help");
            MessageBox.Show(this, text, L.T("Как получить Imgur Client ID"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            Shell.Open("https://api.imgur.com/oauth2/addclient");
        }

        void SaveAndClose()
        {
            if (proxyMode.SelectedIndex == 2 && Uploader.ProxyUri(proxyAddr.Text) == null)
            {
                MessageBox.Show(this, L.T("Адрес прокси не похож на настоящий. Пример: 192.168.1.10:8080"), L.T("Кадр"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                proxyAddr.Focus();
                return;
            }
            S.ImageFormat = format.SelectedIndex == 1 ? "jpg" : "png";
            S.JpegQuality = (int)quality.Value;
            S.ShowNotifications = notify.Checked;
            S.AutoUpdate = autoUpdate.Checked;
            S.ProxyMode = proxyMode.SelectedIndex == 1 ? "none" : proxyMode.SelectedIndex == 2 ? "custom" : "system";
            S.ProxyAddress = proxyAddr.Text.Trim();
            S.ProxyUser = proxyUser.Text.Trim();
            S.ProxyPassword = Secret.Protect(proxyPass.Text);
            Uploader.Configure(S);
            if (hookKey.Checked && !Settings.HotkeyAllowed(hotkey.Value))
            {
                MessageBox.Show(this, L.F("Сочетание «{0}» мешало бы печатать. Добавь Ctrl, Alt или Shift (например Shift+PageUp) или возьми F1–F12, PrtScn, Pause, Insert, PageUp.", Settings.KeyText(hotkey.Value)), L.T("Кадр"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                hotkey.Focus();
                return;
            }
            bool same = hookKey.Checked && videoKey.Value == hotkey.Value;
            if (videoKeyOn.Checked && (!Settings.HotkeyAllowed(videoKey.Value) || same))
            {
                MessageBox.Show(this, same
                    ? L.T("Для снимка и для видео нужны разные сочетания клавиш.")
                    : L.F("Сочетание «{0}» мешало бы печатать. Добавь Ctrl, Alt или Shift (например Shift+PageUp) или возьми F1–F12, PrtScn, Pause, Insert, PageUp.", Settings.KeyText(videoKey.Value)),
                    L.T("Кадр"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                videoKey.Focus();
                return;
            }
            S.Hotkey = hotkey.Value;
            S.VideoHotkey = videoKey.Value;
            S.VideoHotkeyOn = videoKeyOn.Checked;
            S.VideoSysAudio = vSys.Checked;
            S.VideoMic = vMic.Checked;
            S.VideoFps = int.Parse((string)vFps.SelectedItem ?? "30");
            S.VideoFolder = string.IsNullOrWhiteSpace(vFolder.Text) ? new Settings().VideoFolder : vFolder.Text.Trim();
            string code = L.Codes[Math.Max(0, lang.SelectedIndex)];
            LanguageChanged = code != L.Current;
            S.Language = code;
            L.Current = code;
            S.ImgurClientId = imgurId.Text.Trim();
            S.CopyLink = copyLink.Checked;
            S.OpenInBrowser = openBrowser.Checked;
            S.AskFileName = askName.Checked;
            S.SaveFolder = string.IsNullOrWhiteSpace(folder.Text) ? new Settings().SaveFolder : folder.Text.Trim();
            S.HookPrintScreen = hookKey.Checked;
            S.Uploaders = ups;
            S.DefaultDestination = CurrentDest;
            S.Save();
            try { if (Shell.Autostart != autostart.Checked) Shell.Autostart = autostart.Checked; }
            catch (Exception ex) { MessageBox.Show(this, L.T("Не удалось настроить автозапуск:") + " " + ex.Message, L.T("Кадр")); }
            try { if (Shell.SendTo != sendTo.Checked) Shell.SendTo = sendTo.Checked; }
            catch (Exception ex) { MessageBox.Show(this, L.T("Не удалось изменить меню «Отправить»:") + " " + ex.Message, L.T("Кадр")); }
            Close();
        }
    }

    /// <summary>A box that records the key combination pressed in it.</summary>
    class HotkeyBox : TextBox
    {
        public int Value;

        public HotkeyBox(int value)
        {
            Value = value;
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Width = UI.S(170);
            Margin = new Padding(UI.S(4), UI.S(2), 0, UI.S(2));
            Text = Settings.KeyText(value);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin) return true;
            Set(keyData);
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.PrintScreen) Set(e.KeyData);
        }

        void Set(Keys keyData)
        {
            Value = (int)(keyData & (Keys.KeyCode | Keys.Shift | Keys.Control | Keys.Alt));
            Text = Settings.KeyText(Value);
            SelectionStart = Text.Length;
        }
    }

    class UploaderForm : Form
    {
        readonly CustomUploader U;
        readonly TextBox name, url, field, headers, args, link, del;
        readonly ComboBox method, body;
        readonly Button test;

        public UploaderForm(CustomUploader u)
        {
            U = u;
            U.Normalize();
            Lay.Dialog(this, L.T("Свой сервис для загрузки"));
            ShowInTaskbar = false;

            var t = Lay.Table(2);
            name = Lay.Text(380, u.Name);
            url = Lay.Text(380, u.RequestURL + (u.Parameters.Count > 0 ? (u.RequestURL != null && u.RequestURL.Contains("?") ? "&" : "?") + string.Join("&", u.Parameters.Select(p => p.Key + "=" + p.Value)) : ""));
            method = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(100), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            method.Items.AddRange(new object[] { "POST", "PUT", "PATCH" });
            method.SelectedItem = method.Items.Contains(u.RequestMethod.ToUpperInvariant()) ? u.RequestMethod.ToUpperInvariant() : "POST";
            body = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(270), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            body.Items.AddRange(new object[] { L.T("Форма (MultipartFormData)"), L.T("Файл целиком (Binary)") });
            body.SelectedIndex = u.Body.Equals("Binary", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            field = Lay.Text(160, u.FileFormName);
            headers = Multi(Lay.DictToLines(u.Headers, ": "));
            args = Multi(Lay.DictToLines(u.Arguments, "="));
            link = Lay.Text(380, u.URL);
            del = Lay.Text(380, u.DeletionURL);

            Lay.Row(t, Lay.Label(L.T("Название:")), name);
            Lay.Row(t, Lay.Label(L.T("Адрес загрузки:")), url);
            Lay.Row(t, Lay.Label(L.T("Метод:")), method);
            Lay.Row(t, Lay.Label(L.T("Как отправлять:")), body);
            Lay.Row(t, Lay.Label(L.T("Имя поля с файлом:")), field);
            Lay.Row(t, Lay.Label(L.T("Заголовки:")), headers);
            Lay.Row(t, new Label(), Lay.Hint(L.T("По строке: Имя: значение. Например  Authorization: Bearer ключ"), 380));
            Lay.Row(t, Lay.Label(L.T("Доп. поля формы:")), args);
            Lay.Row(t, new Label(), Lay.Hint(L.T("По строке: имя=значение. Например  reqtype=fileupload"), 380));
            Lay.Row(t, Lay.Label(L.T("Ссылка в ответе:")), link);
            Lay.Row(t, new Label(), Lay.Hint(L.T("Пусто — сервер отвечает просто ссылкой. Если отвечает JSON: {json:data.url} или {json:files[0].url}. Ещё можно {regex:https?://\\S+}."), 380));
            Lay.Row(t, Lay.Label(L.T("Ссылка удаления:")), del);
            Lay.Row(t, new Label(), Lay.Hint(L.T("Необязательно. Так же, например {json:data.delete_url}."), 380));

            test = Lay.Button(L.T("Проверить"), (o, e) => Test());
            var ok = Lay.Button("OK", (o, e) => { Apply(U); DialogResult = DialogResult.OK; });
            var cancel = Lay.Button(L.T("Отмена"), null);
            cancel.DialogResult = DialogResult.Cancel;
            var bottom = Lay.Table(2);
            bottom.Dock = DockStyle.Fill;
            bottom.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
            var left = Lay.Flow(test);
            Lay.Row(bottom, left, Lay.Buttons(ok, cancel));

            var root = Lay.Table(1);
            root.Controls.Add(t);
            root.Controls.Add(bottom);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static TextBox Multi(string text)
        {
            var tb = Lay.Text(380, text);
            tb.Multiline = true;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.Height = UI.S(48);
            tb.AcceptsReturn = true;
            return tb;
        }

        void Apply(CustomUploader u)
        {
            u.Name = string.IsNullOrWhiteSpace(name.Text) ? L.T("Мой сервис") : name.Text.Trim();
            u.RequestURL = url.Text.Trim();
            u.Parameters = new Dictionary<string, string>();
            u.RequestMethod = (string)method.SelectedItem;
            u.Body = body.SelectedIndex == 1 ? "Binary" : "MultipartFormData";
            u.FileFormName = string.IsNullOrWhiteSpace(field.Text) ? "file" : field.Text.Trim();
            u.Headers = Lay.LinesToDict(headers.Text, ':');
            u.Arguments = Lay.LinesToDict(args.Text, '=');
            u.URL = link.Text.Trim();
            u.DeletionURL = del.Text.Trim();
        }

        async void Test()
        {
            var u = new CustomUploader();
            Apply(u);
            if (string.IsNullOrWhiteSpace(u.RequestURL)) { MessageBox.Show(this, L.T("Сначала укажи адрес загрузки."), L.T("Кадр")); return; }
            if (MessageBox.Show(this, L.F("Загрузить на {0} маленькую тестовую картинку?", u.RequestURL), L.T("Проверка"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            test.Enabled = false;
            test.Text = L.T("Проверяю…");
            try
            {
                byte[] png;
                using (var b = new Bitmap(240, 80))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.FromArgb(61, 90, 254));
                        using (var f = new Font("Segoe UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString(L.T("Кадр — тест"), f, Brushes.White, 40, 24);
                    }
                    png = Imaging.Png(b);
                }
                var r = await Uploader.Custom(u, png, "kadr-test.png", "image/png");
                var answer = MessageBox.Show(this, L.T("Работает! Ссылка:") + "\n" + r.Url + (r.DeleteUrl != null ? "\n\n" + L.T("Ссылка удаления:") + "\n" + r.DeleteUrl : "") + "\n\n" + L.T("Открыть в браузере?"), L.T("Проверка"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) Shell.Open(r.Url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Не получилось:") + " " + (ex is UserError ? ex.Message : ex.GetBaseException().Message), L.T("Проверка"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                test.Enabled = true;
                test.Text = L.T("Проверить");
            }
        }
    }

    class HistoryForm : Form
    {
        readonly Settings S;
        readonly ListView view;
        readonly ImageList thumbs;
        List<HistoryItem> items;

        public HistoryForm(Settings s)
        {
            S = s;
            Text = L.T("Кадр — история загрузок");
            Font = UI.Font;
            Icon = AppIcon.Get(32);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(UI.S(720), UI.S(460));
            MinimumSize = new Size(UI.S(420), UI.S(300));

            thumbs = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Math.Min(256, UI.S(64)), Math.Min(256, UI.S(40))) };
            view = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, SmallImageList = thumbs, HideSelection = false, BorderStyle = BorderStyle.None };
            view.Columns.Add(L.T("Когда"), UI.S(190));
            view.Columns.Add(L.T("Куда"), UI.S(90));
            view.Columns.Add(L.T("Ссылка"), UI.S(400));
            view.DoubleClick += (o, e) => { var it = Selected(); if (it != null) Shell.Open(it.Url); };
            view.KeyDown += (o, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { var it = Selected(); if (it != null) Clip.SetText(it.Url); }
                if (e.KeyCode == Keys.Delete) RemoveSelected();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(L.T("Открыть"), null, (o, e) => { var it = Selected(); if (it != null) Shell.Open(it.Url); });
            menu.Items.Add(L.T("Копировать ссылку"), null, (o, e) => { var it = Selected(); if (it != null) Clip.SetText(it.Url); });
            var delServer = new ToolStripMenuItem(L.T("Удалить с сервера…"), null, (o, e) => DeleteFromServer());
            menu.Items.Add(delServer);
            menu.Items.Add(L.T("Убрать из истории"), null, (o, e) => RemoveSelected());
            menu.Opening += (o, e) =>
            {
                var it = Selected();
                if (it == null) { e.Cancel = true; return; }
                delServer.Enabled = !string.IsNullOrEmpty(it.ImgurDeleteHash) || !string.IsNullOrEmpty(it.DeleteUrl);
            };
            view.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(UI.S(8)) };
            bottom.Controls.Add(Lay.Button(L.T("Закрыть"), (o, e) => Close()));
            bottom.Controls.Add(Lay.Button(L.T("Очистить историю"), (o, e) => ClearAll()));
            var hint = Lay.Hint(L.T("Двойной щелчок — открыть, правая кнопка — копировать или удалить."), 400);
            bottom.Controls.Add(hint);

            Controls.Add(view);
            Controls.Add(bottom);
            Reload();
        }

        void Reload()
        {
            items = History.Load();
            view.BeginUpdate();
            view.Items.Clear();
            thumbs.Images.Clear();
            foreach (var it in items)
            {
                int img = -1;
                if (!string.IsNullOrEmpty(it.Thumb))
                {
                    try
                    {
                        string p = Path.Combine(History.ThumbDir, it.Thumb);
                        if (File.Exists(p))
                            using (var fs = new MemoryStream(File.ReadAllBytes(p)))
                            using (var src = Image.FromStream(fs))
                            {
                                var canvas = new Bitmap(thumbs.ImageSize.Width, thumbs.ImageSize.Height);
                                using (var fit = Imaging.Fit(src, canvas.Width, canvas.Height))
                                using (var g = Graphics.FromImage(canvas))
                                    g.DrawImage(fit, (canvas.Width - fit.Width) / 2, (canvas.Height - fit.Height) / 2, fit.Width, fit.Height);
                                thumbs.Images.Add(canvas);
                                img = thumbs.Images.Count - 1;
                            }
                    }
                    catch { }
                }
                var lv = new ListViewItem(it.Time.ToLocalTime().ToString("d MMMM yyyy, HH:mm", L.Culture), img) { Tag = it };
                lv.SubItems.Add(it.Destination);
                lv.SubItems.Add(it.Url);
                view.Items.Add(lv);
            }
            view.EndUpdate();
        }

        HistoryItem Selected()
        {
            return view.SelectedItems.Count > 0 ? (HistoryItem)view.SelectedItems[0].Tag : null;
        }

        void RemoveSelected()
        {
            var it = Selected();
            if (it == null) return;
            var all = History.Load();
            var match = all.FirstOrDefault(x => x.Url == it.Url && x.Time == it.Time);
            if (match != null) { all.Remove(match); History.DeleteThumb(match); History.Save(all); }
            Reload();
        }

        async void DeleteFromServer()
        {
            var it = Selected();
            if (it == null) return;
            if (!string.IsNullOrEmpty(it.ImgurDeleteHash))
            {
                if (MessageBox.Show(this, L.T("Удалить картинку с Imgur? Ссылка перестанет открываться.") + "\n\n" + it.Url, L.T("Кадр"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    await Uploader.ImgurDelete(Uploader.ImgurId(S), it.ImgurDeleteHash);
                    RemoveSelected();
                    MessageBox.Show(this, L.T("Удалено с Imgur."), L.T("Кадр"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, L.T("Не удалось удалить:") + " " + (ex is UserError ? ex.Message : ex.GetBaseException().Message), L.T("Кадр"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            if (!string.IsNullOrEmpty(it.DeleteUrl))
            {
                if (MessageBox.Show(this, L.T("Открыть ссылку удаления в браузере?") + "\n\n" + it.DeleteUrl, L.T("Кадр"), MessageBoxButtons.YesNo) == DialogResult.Yes)
                    Shell.Open(it.DeleteUrl);
            }
        }

        void ClearAll()
        {
            if (MessageBox.Show(this, L.T("Очистить историю? Сами картинки на серверах останутся."), L.T("Кадр"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var it in History.Load()) History.DeleteThumb(it);
            History.Save(new List<HistoryItem>());
            Reload();
        }
    }
}
