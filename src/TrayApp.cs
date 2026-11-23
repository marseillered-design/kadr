using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Kadr
{
    /// <summary>Watches the capture hotkey on its own thread so a busy UI never delays the keyboard.</summary>
    class KeyboardHook
    {
        /// <summary>Fires with true for "whole screen" (only Shift+PrtScn when the hotkey is plain PrtScn).</summary>
        public event Action<bool> Pressed;
        public volatile int Hotkey = (int)(Keys.Shift | Keys.PageUp);
        Thread thread;
        uint threadId;
        IntPtr hook;
        Native.LowLevelKeyboardProc proc;
        bool downSeen;

        public void Start()
        {
            if (thread != null) return;
            var ready = new ManualResetEvent(false);
            thread = new Thread(() =>
            {
                threadId = Native.GetCurrentThreadId();
                proc = Callback;
                hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
                ready.Set();
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }) { IsBackground = true, Name = "Kadr keyboard" };
            thread.Start();
            ready.WaitOne(2000);
        }

        public void Stop()
        {
            if (thread == null) return;
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1000);
            thread = null;
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                int hk = Hotkey;
                int key = hk & 0xFFFF;
                if (vk == key)
                {
                    int m = wParam.ToInt32();
                    bool isDown = m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN;
                    if (!isDown && downSeen) { downSeen = false; return (IntPtr)1; }

                    int flags = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 8);
                    bool alt = (flags & 0x20) != 0;
                    bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
                    bool shift = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
                    bool win = (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;
                    bool wantCtrl = (hk & (int)Keys.Control) != 0;
                    bool wantAlt = (hk & (int)Keys.Alt) != 0;
                    bool wantShift = (hk & (int)Keys.Shift) != 0;

                    bool match = !win && alt == wantAlt && ctrl == wantCtrl && shift == wantShift;
                    bool full = false;
                    bool plainPrtScn = key == 0x2C && !wantCtrl && !wantAlt && !wantShift;
                    if (!match && plainPrtScn && shift && !alt && !ctrl && !win) { match = true; full = true; }

                    if (match)
                    {
                        if (isDown)
                        {
                            if (!downSeen) { downSeen = true; Fire(full); }
                            return (IntPtr)1;
                        }
                        // Some keyboards send PrtScn as a key-up only.
                        if (key == 0x2C) { Fire(full); return (IntPtr)1; }
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void Fire(bool shift)
        {
            var h = Pressed;
            if (h != null) h(shift);
        }
    }

    /// <summary>Upload and save steps shared by the tray app and the "Send to" mode.</summary>
    static class Actions
    {
        public static async Task<UploadResult> Upload(Settings s, byte[] data, string fileName, string dest, Image thumbSource, bool isScreenshot)
        {
            var toast = s.ShowNotifications ? Toast.Show(thumbSource, L.F("Загружаю в {0}…", dest), fileName) : null;
            try
            {
                var r = await Uploader.Upload(s, dest, data, fileName, Imaging.ContentType(fileName));
                if (s.CopyLink) Clip.SetText(r.Url);
                History.Add(new HistoryItem { Time = DateTime.Now, Destination = dest, Url = r.Url, DeleteUrl = r.DeleteUrl, ImgurDeleteHash = r.ImgurDeleteHash }, thumbSource);
                string url = r.Url;
                if (toast != null) toast.Done(s.CopyLink ? L.T("Ссылка скопирована") : L.F("Загружено в {0}", dest), url, () => Shell.Open(url), false, 7, url);
                if (s.OpenInBrowser) Shell.Open(url);
                return r;
            }
            catch (Exception ex)
            {
                string msg = ex is UserError ? ex.Message : Describe(ex);
                string saved = null;
                if (isScreenshot)
                {
                    try
                    {
                        Directory.CreateDirectory(s.SaveFolder);
                        saved = Path.Combine(s.SaveFolder, Path.GetFileName(fileName));
                        File.WriteAllBytes(saved, data);
                        msg += " " + L.T("Снимок сохранён в папку.");
                    }
                    catch { saved = null; }
                }
                string savedPath = saved;
                if (toast == null) toast = Toast.Show(thumbSource, "", fileName);
                toast.Done(L.T("Не получилось загрузить"), msg, savedPath == null ? (Action)null : () => Shell.ShowInFolder(savedPath), true, 15);
                return null;
            }
        }

        static string Describe(Exception ex)
        {
            if (ex is TaskCanceledException) return L.T("Сервер не ответил вовремя.");
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            if (e is System.Net.WebException || e is System.Net.Sockets.SocketException || ex is System.Net.Http.HttpRequestException)
                return L.F("Нет связи с сервером ({0})", e.Message);
            return e.Message;
        }

        public static void Save(Settings s, Bitmap bmp)
        {
            string name = Imaging.ShotName(s.Ext);
            string path;
            if (s.AskFileName)
            {
                using (var d = new SaveFileDialog())
                {
                    d.Filter = "PNG|*.png|JPEG|*.jpg";
                    d.FilterIndex = s.Jpeg ? 2 : 1;
                    d.FileName = name;
                    try { Directory.CreateDirectory(s.SaveFolder); d.InitialDirectory = s.SaveFolder; } catch { }
                    if (d.ShowDialog() != DialogResult.OK) return;
                    path = d.FileName;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) { s.SaveFolder = dir; s.Save(); }
                }
                Imaging.SaveFile(bmp, path, s.JpegQuality);
                Saved(s, bmp, path);
            }
            else
            {
                Directory.CreateDirectory(s.SaveFolder);
                path = Path.Combine(s.SaveFolder, name);
                Imaging.SaveFile(bmp, path, s.JpegQuality);
                Saved(s, bmp, path);
            }
        }

        /// <summary>"Saved" notice: a click opens the picture, the folder button shows it in Explorer.</summary>
        static void Saved(Settings s, Bitmap bmp, string path)
        {
            if (!s.ShowNotifications) return;
            var t = Toast.Info(bmp, L.T("Снимок сохранён"), path, () => Shell.Open(path), 8);
            t.SetButton("\uE838", () => Shell.ShowInFolder(path), false);
        }

        public static async Task UploadFiles(Settings s, IEnumerable<string> files, string dest)
        {
            var links = new List<string>();
            foreach (var f in files)
            {
                byte[] data;
                Image thumb = null;
                try
                {
                    data = File.ReadAllBytes(f);
                    try { using (var ms = new MemoryStream(data)) using (var img = Image.FromStream(ms)) thumb = Imaging.Fit(img, 400, 300); } catch { }
                }
                catch (Exception ex)
                {
                    Toast.Info(null, L.T("Не открылся файл"), Path.GetFileName(f) + ": " + ex.Message, null, 10);
                    continue;
                }
                var r = await Upload(s, data, Path.GetFileName(f), dest, thumb, false);
                if (thumb != null) thumb.Dispose();
                if (r != null) links.Add(r.Url);
            }
            if (links.Count > 1 && s.CopyLink)
            {
                Clip.SetText(string.Join(Environment.NewLine, links));
                if (s.ShowNotifications) Toast.Info(null, L.T("Все ссылки скопированы"), L.F("{0} шт., каждая с новой строки", links.Count), null);
            }
        }

        public static string[] ImageFilter = { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp" };
    }

    class TrayApp : ApplicationContext
    {
        readonly Settings S;
        readonly NotifyIcon tray;
        readonly KeyboardHook hook = new KeyboardHook();
        readonly Control ui;
        CaptureForm capture;
        SettingsForm settingsForm;
        HistoryForm historyForm;
        ToolStripMenuItem destMenu, shotItem, fullItem;

        public TrayApp(EventWaitHandle captureSignal, bool quiet)
        {
            S = Settings.Load();
            ui = new Control();
            var h = ui.Handle;

            tray = new NotifyIcon
            {
                Icon = AppIcon.Get(SystemInformation.SmallIconSize.Width),
                Text = L.T("Кадр"),
                ContextMenuStrip = BuildMenu(),
                Visible = true
            };
            tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) StartCapture(false); };

            hook.Pressed += full => ui.BeginInvoke((Action)(() => StartCapture(full)));
            ApplyHook();

            var listener = new Thread(() =>
            {
                while (true)
                {
                    captureSignal.WaitOne();
                    try { ui.BeginInvoke((Action)(() => StartCapture(false))); } catch { return; }
                }
            }) { IsBackground = true };
            listener.Start();

            if (Settings.FirstRun)
            {
                S.Save();
                Toast.Info(null, L.T("Кадр запущен"), L.F("Нажми {0}, чтобы сделать снимок. Настройки — в значке у часов.", Settings.KeyText(S.Hotkey)), null, 10);
            }
            else if (!quiet && S.ShowNotifications)
                Toast.Info(null, L.T("Кадр работает"), S.HookPrintScreen ? L.F("{0} — снимок экрана.", Settings.KeyText(S.Hotkey)) : L.T("Снимок — щелчок по значку у часов."), null, 5);
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9.5f) };
            shotItem = new ToolStripMenuItem(L.T("Сделать снимок"), null, (s, e) => StartCapture(false));
            shotItem.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(shotItem);
            fullItem = new ToolStripMenuItem(L.T("Снимок экрана целиком"), null, (s, e) => StartCapture(true));
            menu.Items.Add(fullItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("Загрузить картинку с компьютера…"), null, (s, e) => UploadFromDisk()));
            menu.Items.Add(new ToolStripMenuItem(L.T("Загрузить картинку из буфера"), null, (s, e) => UploadFromClipboard()));
            destMenu = new ToolStripMenuItem(L.T("Куда загружать"));
            menu.Items.Add(destMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("История загрузок"), null, (s, e) => ShowHistory()));
            menu.Items.Add(new ToolStripMenuItem(L.T("Настройки"), null, (s, e) => ShowSettings()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("Выход"), null, (s, e) => Quit()));
            menu.Opening += (s, e) => FillDestinations();
            return menu;

        }

        void ApplyHook()
        {
            hook.Hotkey = S.Hotkey;
            if (S.HookPrintScreen) hook.Start(); else hook.Stop();
            string key = Settings.KeyText(S.Hotkey);
            bool plainPrtScn = S.Hotkey == (int)Keys.PrintScreen;
            shotItem.ShortcutKeyDisplayString = S.HookPrintScreen ? key : "";
            fullItem.ShortcutKeyDisplayString = S.HookPrintScreen && plainPrtScn ? "Shift+PrtScn" : "";
            tray.Text = S.HookPrintScreen ? L.F("Кадр — снимок по {0}", key) : L.T("Кадр — щёлкни для снимка");
        }

        void FillDestinations()
        {
            destMenu.DropDownItems.Clear();
            foreach (var d in S.Destinations())
            {
                string dest = d;
                var item = new ToolStripMenuItem(d) { Checked = d == S.Default() };
                item.Click += (s, e) => { S.DefaultDestination = dest; S.Save(); };
                destMenu.DropDownItems.Add(item);
            }
            destMenu.DropDownItems.Add(new ToolStripSeparator());
            destMenu.DropDownItems.Add(new ToolStripMenuItem(L.T("Добавить свой сервис…"), null, (s, e) => ShowSettings()));
        }

        public void StartCapture(bool fullScreen)
        {
            if (capture != null) { capture.Activate(); return; }
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap bmp;
            try
            {
                bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch (Exception ex)
            {
                Toast.Info(null, L.T("Не удалось снять экран"), ex.Message, null, 8);
                return;
            }
            Rectangle? init = null;
            if (fullScreen)
            {
                var b = Screen.FromPoint(Cursor.Position).Bounds;
                b.Offset(-vs.X, -vs.Y);
                init = b;
            }
            capture = new CaptureForm(S, bmp, vs, init, S.Destinations());
            capture.FormClosed += OnCaptureClosed;
            capture.Show();
            SelfTest(capture, vs);
        }

        /// <summary>Build-time check only: with KADR_SELFTEST set, saves what the overlay shows and closes it.</summary>
        static void SelfTest(CaptureForm f, Rectangle vs)
        {
            string dir = Environment.GetEnvironmentVariable("KADR_SELFTEST");
            if (string.IsNullOrEmpty(dir)) return;
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) =>
            {
                t.Dispose();
                try
                {
                    using (var b = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                        using (var small = Imaging.Fit(b, 1600, 1600)) small.Save(Path.Combine(dir, "overlay-live.png"), ImageFormat.Png);
                    }
                    var info = "virtual=" + vs + " form=" + f.Bounds + " active=" + (Form.ActiveForm == f) +
                               " screens=" + string.Join(";", Screen.AllScreens.Select(sc => sc.Bounds + "@" + Native.ScaleAt(sc.Bounds.Location)));
                    File.WriteAllText(Path.Combine(dir, "overlay-live.txt"), info);
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "overlay-live.txt"), ex.ToString()); }
                f.Close();
            };
            t.Start();
        }

        void OnCaptureClosed(object sender, FormClosedEventArgs e)
        {
            var f = capture;
            capture = null;
            S.Save();
            var bmp = f.Output;
            if (bmp == null) return;
            switch (f.Action)
            {
                case "copy":
                    Clip.SetImage(bmp);
                    bmp.Dispose();
                    break;
                case "save":
                    try { Actions.Save(S, bmp); }
                    catch (Exception ex) { Toast.Info(null, L.T("Не удалось сохранить"), ex.Message, null, 10); }
                    bmp.Dispose();
                    break;
                case "upload":
                    UploadShot(bmp, f.Destination ?? S.Default());
                    break;
            }
        }

        async void UploadShot(Bitmap bmp, string dest)
        {
            byte[] data = Imaging.Encode(S, bmp);
            await Actions.Upload(S, data, Imaging.ShotName(S.Ext), dest, bmp, true);
            bmp.Dispose();
        }

        async void UploadFromDisk()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = L.F("Какие картинки загрузить в {0}?", S.Default());
                d.Filter = L.T("Картинки") + "|" + string.Join(";", Actions.ImageFilter) + "|" + L.T("Все файлы") + "|*.*";
                d.Multiselect = true;
                if (d.ShowDialog() != DialogResult.OK) return;
                await Actions.UploadFiles(S, d.FileNames, S.Default());
            }
        }

        async void UploadFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList();
                    if (files.Count > 0) { await Actions.UploadFiles(S, files, S.Default()); return; }
                }
                if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    using (var bmp = new Bitmap(img))
                        await Actions.Upload(S, Imaging.Encode(S, bmp), Imaging.ShotName(S.Ext), S.Default(), bmp, true);
                    return;
                }
                Toast.Info(null, L.T("В буфере нет картинки"), L.T("Скопируй картинку или файл и попробуй ещё раз."), null);
            }
            catch (Exception ex)
            {
                Toast.Info(null, L.T("Не удалось прочитать буфер"), ex.Message, null, 8);
            }
        }

        void ShowSettings()
        {
            if (settingsForm != null) { settingsForm.Activate(); return; }
            hook.Stop();
            settingsForm = new SettingsForm(S);
            settingsForm.FormClosed += (s, e) =>
            {
                var f = (SettingsForm)s;
                settingsForm = null;
                if (f.LanguageChanged)
                {
                    var old = tray.ContextMenuStrip;
                    tray.ContextMenuStrip = BuildMenu();
                    old.Dispose();
                }
                ApplyHook();
            };
            settingsForm.Show();
            Native.ForceForeground(settingsForm.Handle);
        }

        void ShowHistory()
        {
            if (historyForm != null) { historyForm.Activate(); return; }
            historyForm = new HistoryForm(S);
            historyForm.FormClosed += (s, e) => historyForm = null;
            historyForm.Show();
            Native.ForceForeground(historyForm.Handle);
        }

        void Quit()
        {
            hook.Stop();
            tray.Visible = false;
            tray.Dispose();
            S.Save();
            ExitThread();
        }
    }

    /// <summary>Runs when files are passed on the command line ("Send to" menu): uploads them, then exits.</summary>
    class FileUploadContext : ApplicationContext
    {
        public FileUploadContext(string[] files)
        {
            var s = Settings.Load();
            var ui = new Control();
            var h = ui.Handle;
            bool finished = false;
            Toast.AllClosed += () => { if (finished) ExitThread(); };
            ui.BeginInvoke((Action)(async () =>
            {
                await Actions.UploadFiles(s, files, s.Default());
                finished = true;
                if (Toast.OpenCount == 0) ExitThread();
            }));
        }
    }
}
