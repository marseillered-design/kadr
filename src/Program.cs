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
            if (args.Length >= 3 && args[0] == "--record-test") { return RecordTest(args); }

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
                if (!created && args.Contains("--updated"))
                {
                    // started by the updater: wait for the old version to quit
                    try { created = mutex.WaitOne(20000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    if (!args.Contains("--tray")) signal.Set();
                    return 0;
                }
                Application.Run(new TrayApp(signal, args.Contains("--tray"), args.Contains("--updated")));
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
            using (var form = new CaptureForm(s, FakeScreen(vs.Width, vs.Height), vs, null, s.Destinations(), 1.25f, null, true))
            {
                form.TestSelect(new Rectangle(240, 140, 960, 540));
                using (var b = new Bitmap(vs.Width, vs.Height))
                {
                    using (var g = Graphics.FromImage(b)) form.PaintTo(g, vs);
                    b.Save(Path.Combine(outDir, "overlay-video.png"), ImageFormat.Png);
                }
            }
            using (var ic = AppIcon.Draw(256)) ic.Save(Path.Combine(outDir, "icon-256.png"), ImageFormat.Png);
            using (var ic = AppIcon.Draw(16)) ic.Save(Path.Combine(outDir, "icon-16.png"), ImageFormat.Png);
        }

        /// <summary>--record-test out.mp4 seconds [x y w h] [sys] [mic] [fps=N]: records without any window and prints what came out.</summary>
        static int RecordTest(string[] args)
        {
            double secs = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            var area = Screen.PrimaryScreen.Bounds;
            int fps = 30;
            var nums = args.Skip(3).Select(a => { int v; return int.TryParse(a, out v) ? (int?)v : null; }).Where(v => v.HasValue).Select(v => v.Value).ToArray();
            if (nums.Length >= 4) area = new Rectangle(nums[0], nums[1], nums[2], nums[3]);
            var f = args.FirstOrDefault(a => a.StartsWith("fps="));
            if (f != null) fps = int.Parse(f.Substring(4));
            var r = new Recorder(area, Path.GetFullPath(args[1]), fps, args.Contains("sys"), args.Contains("mic"));
            var cpu = System.Diagnostics.Process.GetCurrentProcess();
            var t0 = cpu.TotalProcessorTime;
            System.Media.SoundPlayer player = null;
            if (args.Contains("tone"))
            {
                // a quiet 440 Hz tone, so the computer-sound track has something to capture
                var ms = new MemoryStream();
                var w = new BinaryWriter(ms);
                int rate = 48000, n = rate * (int)Math.Ceiling(secs + 1);
                w.Write(0x46464952); w.Write(36 + n * 2); w.Write(0x45564157); w.Write(0x20746d66); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(0x61746164); w.Write(n * 2);
                for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 3000));
                ms.Position = 0;
                player = new System.Media.SoundPlayer(ms);
                player.Load();
            }
            try { r.Start(); }
            catch (Exception ex) { Console.WriteLine("START ERROR " + ex); return 1; }
            if (player != null) player.Play();
            if (args.Contains("pause"))
            {
                // a third of the time paused: the video should come out a third shorter
                Thread.Sleep((int)(secs * 333)); r.SetPaused(true);
                Thread.Sleep((int)(secs * 333)); r.SetPaused(false);
                Thread.Sleep((int)(secs * 333));
            }
            else Thread.Sleep((int)(secs * 1000));
            bool ok = r.Stop();
            if (player != null) player.Stop();
            if (r.Thumb != null) r.Thumb.Save(Path.ChangeExtension(Path.GetFullPath(args[1]), ".png"), ImageFormat.Png);
            cpu.Refresh();
            Console.WriteLine("ok={0} frames={1} fps={2:0.0} size={3} out={4} sys={5} mic={6} bytes={7} cpu={8:0}%", ok, r.Frames, r.Frames / secs, r.Area.Size, r.OutSize, r.SysAudioOn, r.MicOn, r.Bytes,
                (cpu.TotalProcessorTime - t0).TotalMilliseconds / (secs * 1000) * 100 / Environment.ProcessorCount * Environment.ProcessorCount);
            if (!ok) Console.WriteLine("ERROR " + r.Error);
            return ok ? 0 : 1;
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

            var vt = Toast.Info(FakeScreen(800, 450), L.T("Видео записано"), RecordUI.Duration(TimeSpan.FromSeconds(83)) + " · " + RecordUI.FileSize(12345678) + " · " + L.T("Видео") + " 2026-11-24 15-30-00.mp4", null, 8);
            vt.SetButton("", null, false);
            vt.AddButton("", null);
            Application.DoEvents();
            using (var b = new Bitmap(vt.Width, vt.Height))
            {
                vt.DrawToBitmap(b, new Rectangle(0, 0, vt.Width, vt.Height));
                b.Save(Path.Combine(outDir, "toast-video.png"), ImageFormat.Png);
            }
            vt.Close();
            var rp = new RecordPanel(new Rectangle(200, 200, 800, 450)) { Elapsed = () => TimeSpan.FromSeconds(75), StopKey = "Shift+PageDown" };
            rp.StartPosition = FormStartPosition.Manual;
            rp.Show();
            rp.Location = new Point(-5000, -5000);
            Application.DoEvents();
            using (var b = new Bitmap(rp.Width, rp.Height))
            {
                rp.DrawToBitmap(b, new Rectangle(0, 0, rp.Width, rp.Height));
                b.Save(Path.Combine(outDir, "record-panel.png"), ImageFormat.Png);
            }
            rp.Close();

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
