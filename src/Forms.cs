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
        readonly ListBox list;

        public SettingsForm(Settings s)
        {
            S = s;
            ups = s.Uploaders.Select(u => u.Clone()).ToList();
            Lay.Dialog(this, "Кадр — настройки");

            var root = Lay.Table(1);

            // Upload
            var up = Lay.Table(2);
            dest = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(240), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            Lay.Row(up, Lay.Label("Куда загружать по умолчанию:"), dest);
            imgurId = Lay.Text(240, s.ImgurClientId);
            var howTo = new LinkLabel { Text = "Где его взять?", AutoSize = true, Margin = new Padding(0, UI.S(6), 0, 0) };
            howTo.LinkClicked += (o, e) => ImgurHelp();
            Lay.Row(up, Lay.Label("Imgur Client ID:"), Lay.Flow(imgurId, howTo));
            copyLink = Lay.Check("Копировать ссылку после загрузки", s.CopyLink);
            openBrowser = Lay.Check("Открывать ссылку в браузере", s.OpenInBrowser);
            Lay.Row(up, new Label(), Lay.Hint("Catbox работает без регистрации, ссылки вечные. Для Imgur нужен Client ID.", 300));
            Lay.Row(up, copyLink);
            Lay.Row(up, openBrowser);
            root.Controls.Add(Lay.Group("Загрузка", up));

            // Own services
            var own = Lay.Table(2);
            list = new ListBox { Width = UI.S(300), Height = UI.S(110), IntegralHeight = false, Margin = new Padding(0, UI.S(3), UI.S(8), UI.S(3)) };
            list.DoubleClick += (o, e) => EditSelected();
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            col.Controls.Add(Lay.Button("Добавить…", (o, e) => AddNew()));
            col.Controls.Add(Lay.Button("Изменить…", (o, e) => EditSelected()));
            col.Controls.Add(Lay.Button("Удалить", (o, e) => DeleteSelected()));
            col.Controls.Add(Lay.Button("Импорт .sxcu…", (o, e) => Import()));
            Lay.Row(own, list, col);
            Lay.Row(own, Lay.Hint("Любой сайт, который принимает картинку по HTTP: свой сервер, Catbox, ImgBB, Zipline и т. п. Готовые настройки ShareX (.sxcu) можно импортировать.", 440));
            root.Controls.Add(Lay.Group("Свои сервисы для загрузки", own));

            // Saving
            var sv = Lay.Table(2);
            folder = Lay.Text(300, s.SaveFolder);
            Lay.Row(sv, Lay.Label("Папка:"), Lay.Flow(folder, Lay.Button("Обзор…", (o, e) => PickFolder())));
            askName = Lay.Check("Спрашивать имя файла при сохранении (Ctrl+S)", s.AskFileName);
            Lay.Row(sv, askName);
            root.Controls.Add(Lay.Group("Сохранение", sv));

            // General
            var gen = Lay.Table(1);
            hookKey = Lay.Check("Снимок по сочетанию клавиш:", s.HookPrintScreen);
            hotkey = new HotkeyBox(s.Hotkey);
            hookKey.CheckedChanged += (o, e) => hotkey.Enabled = hookKey.Checked;
            hotkey.Enabled = hookKey.Checked;
            autostart = Lay.Check("Запускать Кадр вместе с Windows", Shell.Autostart);
            sendTo = Lay.Check("Пункт «Кадр (загрузить)» в меню «Отправить» для картинок", Shell.SendTo);
            Lay.Row(gen, Lay.Flow(hookKey, hotkey));
            Lay.Row(gen, Lay.Hint("Щёлкни в поле и нажми нужное сочетание, например Shift+PageUp. Пока открыто это окно, сочетание не срабатывает.", 440));
            Lay.Row(gen, autostart);
            Lay.Row(gen, sendTo);
            root.Controls.Add(Lay.Group("Общее", gen));

            var ok = Lay.Button("Сохранить", (o, e) => SaveAndClose());
            var cancel = Lay.Button("Отмена", (o, e) => Close());
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
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u.Host : "адрес не указан";
        }

        string CurrentDest { get { return dest.SelectedItem as string ?? Settings.Catbox; } }

        void AddNew()
        {
            var u = new CustomUploader { Name = UniqueName("Мой сервис") };
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
            if (MessageBox.Show(this, "Удалить сервис «" + ups[i].Name + "»?", "Кадр", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ups.RemoveAt(i);
            RefreshLists(CurrentDest);
        }

        void Import()
        {
            using (var d = new OpenFileDialog { Filter = "Настройки ShareX (*.sxcu)|*.sxcu|JSON|*.json|Все файлы|*.*", Multiselect = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var errors = new StringBuilder();
                foreach (var file in d.FileNames)
                {
                    try
                    {
                        var u = Json.Ser.Deserialize<CustomUploader>(File.ReadAllText(file));
                        if (u == null || string.IsNullOrWhiteSpace(u.RequestURL)) throw new Exception("в файле нет адреса загрузки (RequestURL)");
                        u.Normalize();
                        if (u.Name == "Мой сервис") u.Name = Path.GetFileNameWithoutExtension(file);
                        u.Name = UniqueName(u.Name);
                        ups.Add(u);
                    }
                    catch (Exception ex) { errors.AppendLine(Path.GetFileName(file) + ": " + ex.Message); }
                }
                RefreshLists(CurrentDest);
                if (errors.Length > 0) MessageBox.Show(this, "Не всё удалось импортировать:\n\n" + errors, "Кадр", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        string UniqueName(string name, int except = -1)
        {
            string n = string.IsNullOrWhiteSpace(name) ? "Мой сервис" : name.Trim();
            if (n == Settings.Imgur || n == Settings.Catbox) n += " (свой)";
            string candidate = n;
            int k = 2;
            while (ups.Where((u, i) => i != except).Any(u => u.Name == candidate)) candidate = n + " " + k++;
            return candidate;
        }

        void PickFolder()
        {
            using (var d = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Куда сохранять снимки" })
                if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
        }

        void ImgurHelp()
        {
            var text =
                "Imgur выдаёт бесплатный Client ID для программ:\n\n" +
                "1. Войди на imgur.com (или зарегистрируйся).\n" +
                "2. Открой api.imgur.com/oauth2/addclient (сейчас откроется).\n" +
                "3. Application name — любое, например «Кадр».\n" +
                "4. Authorization type — «Anonymous usage without user authorization».\n" +
                "5. Authorization callback URL — любой, например https://example.com\n" +
                "6. Укажи свою почту, пройди проверку и нажми Submit.\n" +
                "7. Скопируй Client ID (не Client secret!) в это поле.\n\n" +
                "Картинки загружаются анонимно: у ссылок нет владельца, удалить их можно из «Истории загрузок».";
            MessageBox.Show(this, text, "Как получить Imgur Client ID", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Shell.Open("https://api.imgur.com/oauth2/addclient");
        }

        void SaveAndClose()
        {
            if (hookKey.Checked && !Settings.HotkeyAllowed(hotkey.Value))
            {
                MessageBox.Show(this, "Сочетание «" + Settings.KeyText(hotkey.Value) + "» мешало бы печатать. Добавь Ctrl, Alt или Shift (например Shift+PageUp) или возьми F1–F12, PrtScn, Pause, Insert, PageUp.", "Кадр", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                hotkey.Focus();
                return;
            }
            S.Hotkey = hotkey.Value;
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
            catch (Exception ex) { MessageBox.Show(this, "Не удалось настроить автозапуск: " + ex.Message, "Кадр"); }
            try { if (Shell.SendTo != sendTo.Checked) Shell.SendTo = sendTo.Checked; }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось изменить меню «Отправить»: " + ex.Message, "Кадр"); }
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
            Lay.Dialog(this, "Свой сервис для загрузки");
            ShowInTaskbar = false;

            var t = Lay.Table(2);
            name = Lay.Text(380, u.Name);
            url = Lay.Text(380, u.RequestURL + (u.Parameters.Count > 0 ? (u.RequestURL != null && u.RequestURL.Contains("?") ? "&" : "?") + string.Join("&", u.Parameters.Select(p => p.Key + "=" + p.Value)) : ""));
            method = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(100), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            method.Items.AddRange(new object[] { "POST", "PUT", "PATCH" });
            method.SelectedItem = method.Items.Contains(u.RequestMethod.ToUpperInvariant()) ? u.RequestMethod.ToUpperInvariant() : "POST";
            body = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UI.S(270), Margin = new Padding(0, UI.S(3), 0, UI.S(3)) };
            body.Items.AddRange(new object[] { "Форма (MultipartFormData)", "Файл целиком (Binary)" });
            body.SelectedIndex = u.Body.Equals("Binary", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            field = Lay.Text(160, u.FileFormName);
            headers = Multi(Lay.DictToLines(u.Headers, ": "));
            args = Multi(Lay.DictToLines(u.Arguments, "="));
            link = Lay.Text(380, u.URL);
            del = Lay.Text(380, u.DeletionURL);

            Lay.Row(t, Lay.Label("Название:"), name);
            Lay.Row(t, Lay.Label("Адрес загрузки:"), url);
            Lay.Row(t, Lay.Label("Метод:"), method);
            Lay.Row(t, Lay.Label("Как отправлять:"), body);
            Lay.Row(t, Lay.Label("Имя поля с файлом:"), field);
            Lay.Row(t, Lay.Label("Заголовки:"), headers);
            Lay.Row(t, new Label(), Lay.Hint("По строке: Имя: значение. Например  Authorization: Bearer ключ", 380));
            Lay.Row(t, Lay.Label("Доп. поля формы:"), args);
            Lay.Row(t, new Label(), Lay.Hint("По строке: имя=значение. Например  reqtype=fileupload", 380));
            Lay.Row(t, Lay.Label("Ссылка в ответе:"), link);
            Lay.Row(t, new Label(), Lay.Hint("Пусто — сервер отвечает просто ссылкой. Если отвечает JSON: {json:data.url} или {json:files[0].url}. Ещё можно {regex:https?://\\S+}.", 380));
            Lay.Row(t, Lay.Label("Ссылка удаления:"), del);
            Lay.Row(t, new Label(), Lay.Hint("Необязательно. Так же, например {json:data.delete_url}.", 380));

            test = Lay.Button("Проверить", (o, e) => Test());
            var ok = Lay.Button("OK", (o, e) => { Apply(U); DialogResult = DialogResult.OK; });
            var cancel = Lay.Button("Отмена", null);
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
            u.Name = string.IsNullOrWhiteSpace(name.Text) ? "Мой сервис" : name.Text.Trim();
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
            if (string.IsNullOrWhiteSpace(u.RequestURL)) { MessageBox.Show(this, "Сначала укажи адрес загрузки.", "Кадр"); return; }
            if (MessageBox.Show(this, "Загрузить на " + u.RequestURL + " маленькую тестовую картинку?", "Проверка", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            test.Enabled = false;
            test.Text = "Проверяю…";
            try
            {
                byte[] png;
                using (var b = new Bitmap(240, 80))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.FromArgb(61, 90, 254));
                        using (var f = new Font("Segoe UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("Кадр — тест", f, Brushes.White, 40, 24);
                    }
                    png = Imaging.Png(b);
                }
                var r = await Uploader.Custom(u, png, "kadr-test.png", "image/png");
                var answer = MessageBox.Show(this, "Работает! Ссылка:\n" + r.Url + (r.DeleteUrl != null ? "\n\nСсылка удаления:\n" + r.DeleteUrl : "") + "\n\nОткрыть в браузере?", "Проверка", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) Shell.Open(r.Url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не получилось: " + (ex is UserError ? ex.Message : ex.GetBaseException().Message), "Проверка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                test.Enabled = true;
                test.Text = "Проверить";
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
            Text = "Кадр — история загрузок";
            Font = UI.Font;
            Icon = AppIcon.Get(32);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(UI.S(720), UI.S(460));
            MinimumSize = new Size(UI.S(420), UI.S(300));

            thumbs = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Math.Min(256, UI.S(64)), Math.Min(256, UI.S(40))) };
            view = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, SmallImageList = thumbs, HideSelection = false, BorderStyle = BorderStyle.None };
            view.Columns.Add("Когда", UI.S(190));
            view.Columns.Add("Куда", UI.S(90));
            view.Columns.Add("Ссылка", UI.S(400));
            view.DoubleClick += (o, e) => { var it = Selected(); if (it != null) Shell.Open(it.Url); };
            view.KeyDown += (o, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { var it = Selected(); if (it != null) Clip.SetText(it.Url); }
                if (e.KeyCode == Keys.Delete) RemoveSelected();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, (o, e) => { var it = Selected(); if (it != null) Shell.Open(it.Url); });
            menu.Items.Add("Копировать ссылку", null, (o, e) => { var it = Selected(); if (it != null) Clip.SetText(it.Url); });
            var delServer = new ToolStripMenuItem("Удалить с сервера…", null, (o, e) => DeleteFromServer());
            menu.Items.Add(delServer);
            menu.Items.Add("Убрать из истории", null, (o, e) => RemoveSelected());
            menu.Opening += (o, e) =>
            {
                var it = Selected();
                if (it == null) { e.Cancel = true; return; }
                delServer.Enabled = !string.IsNullOrEmpty(it.ImgurDeleteHash) || !string.IsNullOrEmpty(it.DeleteUrl);
            };
            view.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(UI.S(8)) };
            bottom.Controls.Add(Lay.Button("Закрыть", (o, e) => Close()));
            bottom.Controls.Add(Lay.Button("Очистить историю", (o, e) => ClearAll()));
            var hint = Lay.Hint("Двойной щелчок — открыть, правая кнопка — копировать или удалить.", 400);
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
                var lv = new ListViewItem(it.Time.ToLocalTime().ToString("d MMMM yyyy, HH:mm"), img) { Tag = it };
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
                if (MessageBox.Show(this, "Удалить картинку с Imgur? Ссылка перестанет открываться.\n\n" + it.Url, "Кадр", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    await Uploader.ImgurDelete(S.ImgurClientId, it.ImgurDeleteHash);
                    RemoveSelected();
                    MessageBox.Show(this, "Удалено с Imgur.", "Кадр");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не удалось удалить: " + (ex is UserError ? ex.Message : ex.GetBaseException().Message), "Кадр", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            if (!string.IsNullOrEmpty(it.DeleteUrl))
            {
                if (MessageBox.Show(this, "Открыть ссылку удаления в браузере?\n\n" + it.DeleteUrl, "Кадр", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    Shell.Open(it.DeleteUrl);
            }
        }

        void ClearAll()
        {
            if (MessageBox.Show(this, "Очистить историю? Сами картинки на серверах останутся.", "Кадр", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var it in History.Load()) History.DeleteThumb(it);
            History.Save(new List<HistoryItem>());
            Reload();
        }
    }
}
