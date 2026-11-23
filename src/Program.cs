using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Кадр")]
[assembly: AssemblyProduct("Кадр")]
[assembly: AssemblyDescription("Снимки экрана с загрузкой в Imgur и свои сервисы")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Kadr
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            ServicePointManager.Expect100Continue = false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 2 && args[0] == "--make-icon") { File.WriteAllBytes(args[1], AppIcon.Ico()); return 0; }
            if (args.Length >= 2 && args[0] == "--render-test") { RenderTest(args[1]); return 0; }
            if (args.Length >= 2 && args[0] == "--parse-test") { Console.WriteLine(Uploader.Parse(args[1], File.ReadAllText(args[2]))); return 0; }
            if (args.Length >= 3 && args[0] == "--upload-test") { return UploadTest(args[1], args[2]); }
            if (args.Length >= 2 && args[0] == "--form-test") { FormTest(args[1]); return 0; }
            if (args.Length >= 3 && args[0] == "--print-test") { using (var img = Image.FromFile(args[1])) Actions.Print(img, args[2]); return 0; }

            var files = args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToArray();
            if (files.Length > 0)
            {
                Application.Run(new FileUploadContext(files));
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, "Kadr.SingleInstance.v1", out created))
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Kadr.Capture.v1"))
            {
                if (!created)
                {
                    if (!args.Contains("--tray")) signal.Set();
                    return 0;
                }
                Application.Run(new TrayApp(signal, args.Contains("--tray")));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        // ---------- self tests (used while building; not visible to users) ----------

        static Bitmap FakeScreen(int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(235, 240, 248), Color.FromArgb(200, 214, 235), 30f))
                    g.FillRectangle(br, 0, 0, w, h);
                using (var f = new Font("Segoe UI", 22f, GraphicsUnit.Pixel))
                    for (int y = 40; y < h; y += 46)
                        g.DrawString("Пример текста на экране — строка " + (y / 46) + ", имя: Иван Петров, телефон +380 00 000 0000", f, Brushes.DimGray, 40, y);
                g.FillRectangle(Brushes.SteelBlue, 900, 520, 300, 160);
            }
            return b;
        }

        static void RenderTest(string outDir)
        {
            string lc = Environment.GetEnvironmentVariable("KADR_LANG");
            if (!string.IsNullOrEmpty(lc)) L.Current = lc;
            Directory.CreateDirectory(outDir);
            var s = new Settings();
            s.Uploaders.Add(new CustomUploader { Name = "Мой сервер" });
            var vs = new Rectangle(0, 0, 1600, 900);
            foreach (float scale in new[] { 1f, 1.5f })
            {
                using (var form = new CaptureForm(s, FakeScreen(vs.Width, vs.Height), vs, null, s.Destinations(), scale))
                {
                    form.TestScene();
                    using (var b = new Bitmap(vs.Width, vs.Height))
                    {
                        using (var g = Graphics.FromImage(b)) form.PaintTo(g, vs);
                        b.Save(Path.Combine(outDir, "overlay-" + (int)(scale * 100) + ".png"), ImageFormat.Png);
                    }
                    if (scale == 1f) using (var o = form.Render()) o.Save(Path.Combine(outDir, "output.png"), ImageFormat.Png);
                }
            }
            var fakeWins = new System.Collections.Generic.List<Rectangle> { new Rectangle(500, 300, 520, 330), new Rectangle(120, 80, 900, 600) };
            using (var form = new CaptureForm(s, FakeScreen(vs.Width, vs.Height), vs, null, s.Destinations(), 1f, fakeWins))
            {
                form.TestHover(new Point(700, 420));
                using (var b = new Bitmap(vs.Width, vs.Height))
                {
                    using (var g = Graphics.FromImage(b)) form.PaintTo(g, vs);
                    b.Save(Path.Combine(outDir, "overlay-hover.png"), ImageFormat.Png);
                }
            }
            using (var ic = AppIcon.Draw(256)) ic.Save(Path.Combine(outDir, "icon-256.png"), ImageFormat.Png);
            using (var ic = AppIcon.Draw(16)) ic.Save(Path.Combine(outDir, "icon-16.png"), ImageFormat.Png);
        }

        static int UploadTest(string configPath, string imagePath)
        {
            var u = Json.Ser.Deserialize<CustomUploader>(File.ReadAllText(configPath));
            string proxy = Environment.GetEnvironmentVariable("KADR_PROXY");
            if (!string.IsNullOrEmpty(proxy))
                Uploader.Configure(new Settings { ProxyMode = "custom", ProxyAddress = proxy, ProxyUser = "u", ProxyPassword = Secret.Protect("p") });
            byte[] data = File.ReadAllBytes(imagePath);
            string name = Path.GetFileName(imagePath);
            if (Environment.GetEnvironmentVariable("KADR_JPG") == "1")
            {
                using (var img = Image.FromFile(imagePath)) data = Imaging.Encode(img, true, 80);
                name = Path.ChangeExtension(name, ".jpg");
            }
            try
            {
                var r = Uploader.Custom(u, data, name, Imaging.ContentType(name)).Result;
                Console.WriteLine("URL=" + r.Url);
                Console.WriteLine("DEL=" + r.DeleteUrl);
                return 0;
            }
            catch (Exception ex)
            {
                var e = ex is AggregateException ? ex.InnerException : ex;
                Console.WriteLine("ERROR=" + e.GetType().Name + ": " + e.Message);
                return 1;
            }
        }

        static void FormTest(string outDir)
        {
            string lc = Environment.GetEnvironmentVariable("KADR_LANG");
            if (!string.IsNullOrEmpty(lc)) L.Current = lc;
            Directory.CreateDirectory(outDir);
            var s = new Settings();
            s.Uploaders.Add(new CustomUploader { Name = "Мой сервер", RequestURL = "https://example.com/upload", URL = "{json:data.url}" });
            Action<Form, string> snap = (f, name) =>
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.Show();
                Application.DoEvents();
                using (var b = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                    b.Save(Path.Combine(outDir, name), ImageFormat.Png);
                }
                f.Close();
            };
            snap(new SettingsForm(s), "settings.png");
            snap(new UploaderForm(s.Uploaders[0]), "uploader.png");
            snap(new HistoryForm(s), "history.png");
            var t = Toast.Show(FakeScreen(800, 450), "Ссылка скопирована", "https://i.imgur.com/AbCdEf1.png");
            t.Done("Ссылка скопирована", "https://files.catbox.moe/gmn4ey.png", null, false, 7, "https://files.catbox.moe/gmn4ey.png");
            Application.DoEvents();
            using (var b = new Bitmap(t.Width, t.Height))
            {
                t.DrawToBitmap(b, new Rectangle(0, 0, t.Width, t.Height));
                b.Save(Path.Combine(outDir, "toast.png"), ImageFormat.Png);
            }
            t.Close();

            var sv = Toast.Info(FakeScreen(800, 450), L.T("Снимок сохранён"), @"C:\Users\me\Pictures\Kadr\Screenshot 2026-11-22 19-40-00.png", null, 8);
            sv.SetButton("\uE838", null, false);
            Application.DoEvents();
            using (var b = new Bitmap(sv.Width, sv.Height))
            {
                sv.DrawToBitmap(b, new Rectangle(0, 0, sv.Width, sv.Height));
                b.Save(Path.Combine(outDir, "toast-saved.png"), ImageFormat.Png);
            }
            sv.Close();
        }
    }
}
