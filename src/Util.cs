using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Kadr
{
    static class Imaging
    {
        public static Bitmap Fit(Image src, int maxW, int maxH)
        {
            float s = Math.Min(1f, Math.Min(maxW / (float)src.Width, maxH / (float)src.Height));
            int w = Math.Max(1, (int)Math.Round(src.Width * s)), h = Math.Max(1, (int)Math.Round(src.Height * s));
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return b;
        }

        public static byte[] Png(Image img)
        {
            using (var ms = new MemoryStream()) { img.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        public static void SaveFile(Image img, string path, int quality)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            File.WriteAllBytes(path, Encode(img, ext == ".jpg" || ext == ".jpeg", quality));
        }

        /// <summary>The screenshot in the format chosen in Settings.</summary>
        public static byte[] Encode(Settings s, Image img) { return Encode(img, s.Jpeg, s.JpegQuality); }

        public static byte[] Encode(Image img, bool jpeg, int quality)
        {
            if (!jpeg) return Png(img);
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            // JPEG has no transparency: put see-through pixels (clipboard pictures) on white.
            using (var flat = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb))
            using (var p = new EncoderParameters(1))
            using (var ms = new MemoryStream())
            {
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.White);
                    g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
                }
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(1, Math.Min(100, quality)));
                flat.Save(ms, codec, p);
                return ms.ToArray();
            }
        }

        public static string ContentType(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".tif": case ".tiff": return "image/tiff";
                case ".mp4": return "video/mp4";
                default: return "image/png";
            }
        }

        public static string ShotName(string ext)
        {
            return L.T("Снимок") + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ext;
        }
    }

    /// <summary>Keeps a secret readable only by this Windows user on this PC.</summary>
    static class Secret
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
    }

    static class Clip
    {
        public static void SetImage(Bitmap bmp)
        {
            try
            {
                var d = new DataObject();
                d.SetData(DataFormats.Bitmap, true, bmp);
                var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                d.SetData("PNG", false, ms);
                Clipboard.SetDataObject(d, true, 10, 100);
            }
            catch { }
        }

        public static void SetText(string text)
        {
            try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 10, 100); } catch { }
        }
    }

    static class Shell
    {
        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        public static void ShowInFolder(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Kadr") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("Kadr", "\"" + Application.ExecutablePath + "\" --tray");
                    else if (k.GetValue("Kadr") != null) k.DeleteValue("Kadr");
                }
            }
        }

        static string SendToDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.SendTo); } }
        static string SendToLink { get { return Path.Combine(SendToDir, L.T("Кадр (загрузить)") + ".lnk"); } }
        static IEnumerable<string> AllSendToLinks
        {
            get { for (int i = 0; i < L.Codes.Length; i++) yield return Path.Combine(SendToDir, L.In(i, "Кадр (загрузить)") + ".lnk"); }
        }

        public static bool SendTo
        {
            get { return AllSendToLinks.Any(File.Exists); }
            set
            {
                foreach (var old in AllSendToLinks) { try { if (File.Exists(old)) File.Delete(old); } catch { } }
                if (!value) return;
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { SendToLink });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
                lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath + ",0" });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { L.T("Загрузить картинку через Кадр") });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
            }
        }

        public static bool DarkMode
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return k != null && (k.GetValue("AppsUseLightTheme") as int?) == 0;
                }
                catch { return false; }
            }
        }
    }

    static class UI
    {
        public static readonly Font Font = new Font("Segoe UI", 9f);
        static float k = -1;
        public static float K
        {
            get
            {
                if (k < 0) using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
                return k;
            }
        }
        public static int S(int v) { return (int)Math.Round(v * K); }

        public static string IconFont
        {
            get
            {
                foreach (var f in FontFamily.Families)
                    if (f.Name == "Segoe Fluent Icons") return f.Name;
                return "Segoe MDL2 Assets";
            }
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>The app icon, drawn in code: a rounded square with a white viewfinder.</summary>
    static class AppIcon
    {
        public static Bitmap Draw(int size)
        {
            var b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float m = size <= 20 ? 0.5f : size * 0.04f;
                var r = new RectangleF(m, m, size - 2 * m, size - 2 * m);
                using (var path = UI.Round(r, size * 0.22f))
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(255, 18, 181, 203), Color.FromArgb(255, 61, 90, 254)))
                    g.FillPath(br, path);

                float w = Math.Max(1.6f, size * 0.085f);
                float i = size * 0.24f, L = size * 0.2f, e = size - i;
                using (var pen = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLines(pen, new[] { new PointF(i, i + L), new PointF(i, i), new PointF(i + L, i) });
                    g.DrawLines(pen, new[] { new PointF(e - L, i), new PointF(e, i), new PointF(e, i + L) });
                    g.DrawLines(pen, new[] { new PointF(e, e - L), new PointF(e, e), new PointF(e - L, e) });
                    g.DrawLines(pen, new[] { new PointF(i + L, e), new PointF(i, e), new PointF(i, e - L) });
                }
                float dot = Math.Max(2.2f, size * 0.17f);
                g.FillEllipse(Brushes.White, size / 2f - dot / 2, size / 2f - dot / 2, dot, dot);
            }
            return b;
        }

        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        public static byte[] Ico()
        {
            var images = Sizes.Select(s => Entry(s)).ToList();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int n = 0; n < images.Count; n++)
                {
                    int s = Sizes[n];
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[n].Length); w.Write(offset);
                    offset += images[n].Length;
                }
                foreach (var img in images) w.Write(img);
                return ms.ToArray();
            }
        }

        static byte[] Entry(int s)
        {
            using (var bmp = Draw(s))
            {
                if (s >= 256) return Imaging.Png(bmp);
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
                    w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                    for (int y = s - 1; y >= 0; y--)
                        for (int x = 0; x < s; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                        }
                    int stride = ((s + 31) / 32) * 4;
                    w.Write(new byte[stride * s]);
                    return ms.ToArray();
                }
            }
        }

        static byte[] cached;
        public static Icon Get(int size)
        {
            if (cached == null)
            {
                // The designed icon embedded by build.ps1; the drawn one is only a fallback.
                using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Kadr.icon.ico"))
                {
                    if (s != null)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        cached = ms.ToArray();
                    }
                }
                if (cached == null) cached = Ico();
            }
            return new Icon(new MemoryStream(cached), size, size);
        }
    }
}
