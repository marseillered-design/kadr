using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Kadr
{
    /// <summary>
    /// Records an area of the screen to MP4: the picture (with the mouse pointer) on one thread,
    /// the sound (computer + microphone, mixed) on another, both timed by one clock that stops while paused.
    /// </summary>
    sealed class Recorder
    {
        public readonly Rectangle Area;       // screen pixels
        public readonly string FilePath;
        public readonly Size OutSize;
        public readonly bool SysAudio, Mic;   // what was asked for
        public bool SysAudioOn { get; private set; }
        public bool MicOn { get; private set; }
        public Exception Error { get; private set; }
        public Bitmap Thumb { get; private set; }
        public int Frames { get; private set; }
        /// <summary>Frames that were the same as the one before and were not encoded again.</summary>
        public int Unchanged { get; private set; }
        /// <summary>Measurements: milliseconds spent grabbing, comparing and encoding.</summary>
        public double MsGrab, MsCompare, MsWrite;
        /// <summary>The screen came through DXGI Desktop Duplication (fast), not GDI BitBlt.</summary>
        public bool FastCapture { get; private set; }
        /// <summary>The video card encodes (much less CPU); false = Windows' software encoder.</summary>
        public bool Hardware { get; private set; }

        /// <summary>Raised on a worker thread when recording broke (disk full, encoder error).</summary>
        public event Action Failed;

        readonly int fps;
        static readonly long keepAlive = (long)(double.TryParse(Environment.GetEnvironmentVariable("KADR_KEEPALIVE"), out keepAliveSec) ? keepAliveSec : 1.0) * 10000000L;
        static double keepAliveSec;
        static readonly bool skipEncode = Environment.GetEnvironmentVariable("KADR_PROFILE") == "grab";   // build checks: capture only
        readonly Stopwatch clock = new Stopwatch();
        volatile bool stopping, paused;
        Thread videoThread, audioThread;
        Mp4Writer writer;
        AudioSource sys, mic;
        readonly ManualResetEvent ready = new ManualResetEvent(false);

        public Recorder(Rectangle area, string path, int fps, bool sysAudio, bool micAudio)
        {
            // H.264 wants even sizes and at most 4096 pixels across: bigger areas are scaled down to fit 3840 × 2160
            Area = new Rectangle(area.X, area.Y, Math.Max(2, area.Width & ~1), Math.Max(2, area.Height & ~1));
            double k = Math.Min(1.0, Math.Min(3840.0 / Area.Width, 2160.0 / Area.Height));
            OutSize = new Size(Math.Max(2, (int)(Area.Width * k) & ~1), Math.Max(2, (int)(Area.Height * k) & ~1));
            FilePath = path;
            this.fps = Math.Max(5, Math.Min(60, fps));
            SysAudio = sysAudio;
            Mic = micAudio;
        }

        public TimeSpan Elapsed { get { return clock.Elapsed; } }
        public bool Paused { get { return paused; } }
        public long Bytes { get { try { return new FileInfo(FilePath).Length; } catch { return 0; } } }

        /// <summary>Opens the devices and the file; throws if the file cannot be started.</summary>
        public void Start()
        {
            Exception startError = null;
            videoThread = new Thread(() =>
            {
                try
                {
                    if (SysAudio) sys = AudioSource.Open(true);
                    if (Mic) mic = AudioSource.Open(false);
                    SysAudioOn = sys != null;
                    MicOn = mic != null;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    writer = CreateWriter();
                }
                catch (Exception ex) { startError = ex; }
                if (startError == null && writer.HasAudio)
                {
                    audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "Kadr audio", Priority = ThreadPriority.Highest };
                    audioThread.Start();
                }
                ready.Set();
                if (startError == null)
                {
                    VideoLoop();
                    if (audioThread != null) audioThread.Join();
                    clock.Stop();
                    // the encoder lives on this thread, so the file is finished here too
                    try { if (Error == null) writer.Finish(); }
                    catch (Exception ex) { Error = ex; }
                }
                Cleanup();
            }) { IsBackground = true, Name = "Kadr video", Priority = ThreadPriority.AboveNormal };
            videoThread.Start();
            ready.WaitOne();
            if (startError != null)
            {
                videoThread.Join();
                throw startError;
            }
        }

        public void SetPaused(bool p)
        {
            if (p == paused) return;
            paused = p;
            if (p) clock.Stop(); else clock.Start();
        }

        /// <summary>Stops and finishes the file (blocks for a moment). Returns false if it failed.</summary>
        public bool Stop()
        {
            stopping = true;
            if (videoThread != null) videoThread.Join();
            return Error == null;
        }

        void Cleanup()
        {
            if (writer != null) { writer.Dispose(); writer = null; }
            if (sys != null) { sys.Dispose(); sys = null; }
            if (mic != null) { mic.Dispose(); mic = null; }
        }

        void Fail(Exception ex)
        {
            if (Error != null) return;
            Error = ex;
            stopping = true;
            var f = Failed;
            if (f != null) f();
        }

        /// <summary>
        /// Windows' software H.264 encoder. KADR_HW=1 tries the video card's encoder (measurements only): fed from
        /// system memory it used more CPU, not less, and made 3–4× bigger files.
        /// </summary>
        Mp4Writer CreateWriter()
        {
            bool audio = sys != null || mic != null;
            if (Environment.GetEnvironmentVariable("KADR_HW") == "1")   // measured: on this kind of input the card saves nothing
            {
                try
                {
                    var w = new Mp4Writer(FilePath, OutSize.Width, OutSize.Height, fps, audio, true);
                    Hardware = true;
                    return w;
                }
                catch { try { File.Delete(FilePath); } catch { } }
            }
            return new Mp4Writer(FilePath, OutSize.Width, OutSize.Height, fps, audio, false);
        }

        // ---------------- picture ----------------

        void VideoLoop()
        {
            IntPtr screen = Gdi.GetDC(IntPtr.Zero);
            var grab = new Dib(screen, Area.Width, Area.Height);
            Dib scaled = OutSize != Area.Size ? new Dib(screen, OutSize.Width, OutSize.Height) : null;
            long interval = 10000000L / fps;
            var cursors = new CursorCache();
            // a still screen gives the same picture again and again: comparing (≈2 ms) is far cheaper than encoding
            // it (≈16 ms at 1440p), so unchanged frames are skipped; one per second still goes in, and the last one at the stop
            int bytes = OutSize.Width * OutSize.Height * 4;
            IntPtr prev = Marshal.AllocHGlobal(bytes);
            bool havePrev = false;
            long lastWritten = 0;
            // fast capture through the video card when the area is on one monitor; "clean" keeps the desktop without
            // the pointer, which is drawn on a fresh copy every frame (the card only sends pictures that changed)
            DesktopDup dup = Environment.GetEnvironmentVariable("KADR_GDI") == "1" ? null : DesktopDup.TryCreate(Area);
            Dib clean = dup != null ? new Dib(screen, Area.Width, Area.Height) : null;
            if (dup != null) dup.CopyFirst(clean.Bits, Area.Width * 4);
            int areaBytes = Area.Width * Area.Height * 4;
            FastCapture = dup != null;
            try
            {
                clock.Start();
                long next = 0;
                while (!stopping)
                {
                    if (paused) { Thread.Sleep(15); continue; }
                    long t = clock.Elapsed.Ticks;
                    if (t < next) { Thread.Sleep((int)Math.Max(1, (next - t) / 10000)); continue; }
                    var sw = Stopwatch.StartNew();
                    if (dup != null)
                    {
                        bool lost;
                        dup.Grab(clean.Bits, Area.Width * 4, out lost);
                        if (lost)
                        {
                            // the desktop changed mode or went to a secure screen: start again, or fall back to BitBlt
                            dup.Dispose();
                            dup = DesktopDup.TryCreate(Area);
                            if (dup == null) { clean.Dispose(); clean = null; FastCapture = false; }
                            else dup.CopyFirst(clean.Bits, Area.Width * 4);
                        }
                    }
                    if (dup != null) MF.CopyMemory(grab.Bits, clean.Bits, (UIntPtr)(uint)areaBytes);
                    else Gdi.BitBlt(grab.Dc, 0, 0, Area.Width, Area.Height, screen, Area.X, Area.Y, Gdi.SRCCOPY);
                    cursors.Draw(grab.Dc, Area.Location);
                    var frame = grab;
                    if (scaled != null)
                    {
                        Gdi.SetStretchBltMode(scaled.Dc, 4);   // HALFTONE: smooth
                        Gdi.StretchBlt(scaled.Dc, 0, 0, OutSize.Width, OutSize.Height, grab.Dc, 0, 0, Area.Width, Area.Height, Gdi.SRCCOPY);
                        frame = scaled;
                    }
                    Gdi.GdiFlush();
                    MsGrab += sw.Elapsed.TotalMilliseconds; sw.Restart();
                    bool same = havePrev && Gdi.memcmp(frame.Bits, prev, (UIntPtr)(uint)bytes) == 0;
                    MsCompare += sw.Elapsed.TotalMilliseconds; sw.Restart();
                    if (!same || t - lastWritten >= keepAlive)
                    {
                        if (!skipEncode) writer.WriteFrame(frame.Bits, t, interval);
                        if (!same) MF.CopyMemory(prev, frame.Bits, (UIntPtr)(uint)bytes);
                        havePrev = true;
                        lastWritten = t;
                    }
                    else Unchanged++;
                    MsWrite += sw.Elapsed.TotalMilliseconds;
                    if (Frames == 0) Thumb = frame.ToBitmap(400, 300);
                    Frames++;
                    next = Math.Max(next + interval, t + interval / 2);   // fell behind: skip ahead instead of rushing
                }
                // the picture lasts until the stop even if the screen stood still at the end
                long end = clock.Elapsed.Ticks;
                if (havePrev && !skipEncode && end - lastWritten > interval && Error == null) writer.WriteFrame(prev, end - interval, interval);
            }
            catch (Exception ex) { Fail(ex); }
            finally
            {
                Marshal.FreeHGlobal(prev);
                if (dup != null) dup.Dispose();
                if (clean != null) clean.Dispose();
                grab.Dispose();
                if (scaled != null) scaled.Dispose();
                Gdi.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ---------------- sound ----------------

        void AudioLoop()
        {
            const int rate = Mp4Writer.AudioRate;
            const int delay = rate / 10;   // stay 0.1 s behind the clock so both devices have delivered
            long written = 0;
            float[] mix = new float[rate * 2];
            short[] pcm = new short[rate * 2];
            try
            {
                while (true)
                {
                    bool last = stopping;
                    bool drop = paused;
                    if (sys != null) sys.Poll(drop);
                    if (mic != null) mic.Poll(drop);
                    if (!drop)
                    {
                        long target = (long)(clock.Elapsed.TotalSeconds * rate) - (last ? 0 : delay);
                        while (written < target)
                        {
                            int n = (int)Math.Min(target - written, rate);
                            Array.Clear(mix, 0, n * 2);
                            if (sys != null) sys.MixInto(mix, n);
                            if (mic != null) mic.MixInto(mix, n);
                            for (int i = 0; i < n * 2; i++)
                            {
                                float v = mix[i] * 32767f;
                                pcm[i] = (short)(v > 32767f ? 32767 : v < -32768f ? -32768 : v);
                            }
                            writer.WriteAudio(pcm, n, written * 10000000L / rate);
                            written += n;
                        }
                    }
                    if (last) break;
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        // ---------------- screen grabbing helpers ----------------

        /// <summary>A top-down 32-bit bitmap that GDI draws into and the encoder reads directly.</summary>
        sealed class Dib : IDisposable
        {
            public readonly IntPtr Dc, Bits;
            readonly IntPtr bmp, old;
            readonly int w, h;

            public Dib(IntPtr screen, int w, int h)
            {
                this.w = w; this.h = h;
                Dc = Gdi.CreateCompatibleDC(screen);
                var bi = new Gdi.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr bits;
                bmp = Gdi.CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection " + w + "x" + h);
                Bits = bits;
                old = Gdi.SelectObject(Dc, bmp);
            }

            public Bitmap ToBitmap(int maxW, int maxH)
            {
                using (var full = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, Bits))
                    return Imaging.Fit(full, maxW, maxH);
            }

            public void Dispose()
            {
                Gdi.SelectObject(Dc, old);
                Gdi.DeleteObject(bmp);
                Gdi.DeleteDC(Dc);
            }
        }

        /// <summary>Draws the mouse pointer; remembers each pointer's hot spot so it is looked up once.</summary>
        sealed class CursorCache
        {
            IntPtr lastCursor;
            int hx, hy;

            public void Draw(IntPtr dc, Point origin)
            {
                var ci = new Gdi.CURSORINFO { cbSize = Marshal.SizeOf(typeof(Gdi.CURSORINFO)) };
                if (!Gdi.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
                if (ci.hCursor != lastCursor)
                {
                    Gdi.ICONINFO ii;
                    if (!Gdi.GetIconInfo(ci.hCursor, out ii)) return;
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) Gdi.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Gdi.DeleteObject(ii.hbmColor);
                    lastCursor = ci.hCursor;
                }
                Gdi.DrawIconEx(dc, ci.x - hx - origin.X, ci.y - hy - origin.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
            }
        }
    }

    static class Gdi
    {
        public const int SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int memcmp(IntPtr a, IntPtr b, UIntPtr count);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, int affinity);
    }
}
