using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Kadr
{
    /// <summary>A red outline around the area being recorded. Clicks pass through it and it never shows up in the video.</summary>
    class RecordFrame : Form
    {
        const int W = 3;
        bool paused;

        public RecordFrame(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            var r = area;
            r.Inflate(W, W);
            Bounds = r;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | 0x20 | 0x80000;   // + transparent to clicks, layered
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RecordUI.HideFromCapture(Handle);
        }

        public bool Paused { set { paused = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Magenta);
            using (var p = new Pen(paused ? Color.FromArgb(251, 140, 0) : Color.FromArgb(229, 57, 53), W - 1) { Alignment = PenAlignment.Inset })
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>The small bar next to the recording: time, pause, stop and discard.</summary>
    class RecordPanel : Form
    {
        public event Action PauseClicked, StopClicked, DiscardClicked;
        public Func<TimeSpan> Elapsed;
        public bool Paused;
        public string StopKey = "";

        readonly float k;
        readonly Font timeFont, iconFont;
        readonly Timer tick = new Timer { Interval = 250 };
        readonly ToolTip tips = new ToolTip();
        Rectangle pauseR, stopR, discardR, dotR;
        int hot = -1, phase;
        Point dragFrom;
        bool dragging;

        public RecordPanel(Rectangle area)
        {
            k = Native.ScaleAt(area.Location);
            timeFont = new Font("Segoe UI Semibold", 14f * k, GraphicsUnit.Pixel);
            iconFont = new Font(UI.IconFont, 14f * k, GraphicsUnit.Pixel);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Cursor = Cursors.Default;
            BackColor = Color.FromArgb(32, 32, 34);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            int h = S(40), b = S(32), pad = S(4);
            int timeW = S(92);
            Size = new Size(pad + timeW + 3 * b + pad, h);
            dotR = new Rectangle(S(12), (h - S(10)) / 2, S(10), S(10));
            pauseR = new Rectangle(pad + timeW, (h - b) / 2, b, b);
            stopR = new Rectangle(pauseR.Right, pauseR.Y, b, b);
            discardR = new Rectangle(stopR.Right, pauseR.Y, b, b);
            Location = Place(area, Size);
            tick.Tick += (s, e) => { phase++; Invalidate(); };
        }

        int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>Below the area on the right, else above it, else inside its bottom-right corner.</summary>
        static Point Place(Rectangle area, Size size)
        {
            var wa = Screen.FromRectangle(area).WorkingArea;
            int gap = 8;
            int x = Math.Max(wa.Left + gap, Math.Min(area.Right - size.Width, wa.Right - size.Width - gap));
            if (area.Bottom + gap + size.Height <= wa.Bottom) return new Point(x, area.Bottom + gap);
            if (area.Top - gap - size.Height >= wa.Top) return new Point(x, area.Top - gap - size.Height);
            return new Point(Math.Min(area.Right, wa.Right) - size.Width - 3 * gap, Math.Min(area.Bottom, wa.Bottom) - size.Height - 3 * gap);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RecordUI.HideFromCapture(Handle);
            Native.RoundCorners(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            tick.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Dispose();
            tips.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(BackColor);
            using (var p = new Pen(Color.FromArgb(70, 70, 74))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            bool dotOn = Paused || phase % 4 < 3;
            var dotColor = Paused ? Color.FromArgb(251, 140, 0) : Color.FromArgb(229, 57, 53);
            if (dotOn) using (var b = new SolidBrush(dotColor)) g.FillEllipse(b, dotR);
            var t = Elapsed != null ? Elapsed() : TimeSpan.Zero;
            string time = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            TextRenderer.DrawText(g, time, timeFont, new Rectangle(dotR.Right + S(8), 0, pauseR.Left - dotR.Right - S(8), Height),
                Paused ? Color.FromArgb(251, 180, 80) : Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            Button(g, pauseR, Paused ? "" : "", 0, Color.White);
            Button(g, stopR, "", 1, Color.FromArgb(255, 110, 100));
            Button(g, discardR, "", 2, Color.FromArgb(200, 200, 200));
        }

        void Button(Graphics g, Rectangle r, string glyph, int index, Color c)
        {
            if (hot == index)
            {
                var h = r; h.Inflate(-S(2), -S(2));
                using (var path = UI.Round(h, S(6)))
                using (var b = new SolidBrush(Color.FromArgb(62, 62, 66))) g.FillPath(b, path);
            }
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(glyph, iconFont, b, r, sf);
        }

        int Hit(Point p)
        {
            if (pauseR.Contains(p)) return 0;
            if (stopR.Contains(p)) return 1;
            if (discardR.Contains(p)) return 2;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y); return; }
            int h = Hit(e.Location);
            if (h == hot) return;
            hot = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.SizeAll;
            string tip = h == 0 ? (Paused ? L.T("Продолжить") : L.T("Пауза")) : h == 1 ? (StopKey.Length > 0 ? L.F("Остановить и сохранить ({0})", StopKey) : L.T("Остановить и сохранить")) : h == 2 ? L.T("Остановить и удалить") : null;
            if (tip == null) tips.Hide(this); else tips.Show(tip, this, e.X, Height + S(4), 2500);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = -1;
            tips.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Hit(e.Location) < 0) { dragging = true; dragFrom = e.Location; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; return; }
            if (e.Button != MouseButtons.Left) return;
            Action a = null;
            switch (Hit(e.Location))
            {
                case 0: a = PauseClicked; break;
                case 1: a = StopClicked; break;
                case 2: a = DiscardClicked; break;
            }
            if (a != null) a();
            Invalidate();
        }
    }

    static class RecordUI
    {
        /// <summary>Windows 10 2004+: the window stays visible on screen but is left out of every capture, our own included.</summary>
        public static void HideFromCapture(IntPtr hwnd)
        {
            try { Gdi.SetWindowDisplayAffinity(hwnd, 0x11); } catch { }
        }

        public static string Duration(TimeSpan t)
        {
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        public static string FileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return L.F("{0} КБ", Math.Max(1, bytes / 1024));
            return L.F("{0} МБ", (bytes / 1048576.0).ToString(bytes < 100 * 1048576L ? "0.0" : "0", L.Culture));
        }
    }
}
