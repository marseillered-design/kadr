using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Kadr
{
    enum Cmd { Tool, Color, Undo, Upload, UploadMenu, Copy, Save, Print, Close, Record, Sound, Mic }

    class Btn
    {
        public Rectangle R;
        public Cmd Cmd;
        public Tool Tool;
        public string Tip;
        public bool Vertical;
    }

    /// <summary>The full-screen overlay: frozen screenshot, area selection, drawing tools and the action bar.</summary>
    class CaptureForm : Form
    {
        public Bitmap Output;
        public string Action;
        public string Destination;
        /// <summary>Video mode: the area to record, in screen pixels, and the sound switches.</summary>
        public Rectangle RecordArea;
        public bool RecSys, RecMic;
        readonly bool video;

        readonly Bitmap shot;
        static readonly SolidBrush Dim = new SolidBrush(Color.FromArgb(115, 0, 0, 0));
        readonly Rectangle vs;
        readonly Settings S;
        readonly List<string> dests;

        Rectangle sel;
        bool hasSel;
        enum Mode { Idle, Selecting, Moving, Resizing, Drawing }
        Mode mode;
        Point down;
        Rectangle selAtDown;
        int grip = -1;
        bool dragged;

        Tool tool = Tool.None;
        Color color;
        int size;
        readonly List<Shape> shapes = new List<Shape>();
        Shape cur;
        Rectangle curBounds;
        TextShape editing;

        readonly List<Btn> btns = new List<Btn>();
        Btn hover;
        bool palette;
        readonly List<KeyValuePair<Rectangle, Color>> swatches = new List<KeyValuePair<Rectangle, Color>>();
        Rectangle toolsBar, actionsBar, paletteRect, lastFrame;
        float k = 1f;
        Point mouse;
        string sizeTip;
        DateTime sizeTipUntil;
        readonly Timer timer = new Timer { Interval = 100 };
        int ticks;
        readonly Font uiFont, tipFont, iconFont, iconFontSmall;

        /// <summary>The colours from settings.json ("Palette"), or the built-in ones if none are valid.</summary>
        Color[] palette_ = Colors;

        static Color[] ParsePalette(List<string> hex)
        {
            var list = new List<Color>();
            foreach (var h in hex ?? new List<string>())
            {
                try { list.Add(ColorTranslator.FromHtml(h.Trim())); } catch { }
                if (list.Count == 12) break;
            }
            return list.Count > 0 ? list.ToArray() : Colors;
        }

        static readonly Color[] Colors =
        {
            Color.FromArgb(229, 57, 53), Color.FromArgb(251, 140, 0), Color.FromArgb(253, 216, 53), Color.FromArgb(67, 160, 71),
            Color.FromArgb(30, 136, 229), Color.FromArgb(142, 36, 170), Color.FromArgb(33, 33, 33), Color.White
        };

        readonly List<Rectangle> wins;
        Rectangle hoverWin, downWin;

        public CaptureForm(Settings s, Bitmap shot, Rectangle virtualScreen, Rectangle? initial, List<string> destinations, float? scale = null, List<Rectangle> windows = null, bool video = false)
        {
            S = s;
            this.video = video;
            RecSys = s.VideoSysAudio;
            RecMic = s.VideoMic;
            palette_ = ParsePalette(s.Palette);
            wins = windows ?? new List<Rectangle>();
            this.shot = shot;
            vs = virtualScreen;
            dests = destinations;
            color = Color.FromArgb(s.PenColor);
            size = s.PenSize;
            k = scale ?? Native.ScaleAt(Cursor.Position);

            // No darkened copy of the screen: the dimming is painted on the fly (saves a full-screen bitmap and time).

            uiFont = new Font("Segoe UI", 12.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            tipFont = new Font("Segoe UI", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            iconFont = new Font(UI.IconFont, 16f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            iconFontSmall = new Font(UI.IconFont, 10f * k, FontStyle.Regular, GraphicsUnit.Pixel);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            Text = L.T("Кадр");
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            if (initial.HasValue)
            {
                sel = Rectangle.Intersect(initial.Value, ClientBounds);
                hasSel = sel.Width > 1 && sel.Height > 1;
            }
            Layout2();

            timer.Tick += OnTick;
            timer.Start();
        }

        Rectangle ClientBounds { get { return new Rectangle(0, 0, vs.Width, vs.Height); } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Native.ForceForeground(Handle);
            Activate();
            Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            shot.Dispose();
            base.OnFormClosed(e);
        }

        // ---------------- layout ----------------

        int Sc(float v) { return (int)Math.Round(v * k); }

        Rectangle MonitorOf(Rectangle r)
        {
            var screenRect = new Rectangle(r.X + vs.X, r.Y + vs.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
            var b = Screen.FromRectangle(screenRect).Bounds;
            b.Offset(-vs.X, -vs.Y);
            return b;
        }

        bool BarsVisible { get { return hasSel && (mode == Mode.Idle || mode == Mode.Drawing); } }

        void Layout2()
        {
            btns.Clear();
            swatches.Clear();
            toolsBar = actionsBar = paletteRect = Rectangle.Empty;
            if (!BarsVisible) return;

            int bs = Sc(34), narrow = Sc(20), pad = Sc(4), gap = Sc(8);
            var mon = MonitorOf(sel);

            var tools = new[]
            {
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Pen, Tip = L.T("Карандаш (P)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Line, Tip = L.T("Линия (L)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Arrow, Tip = L.T("Стрелка (A)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Rect, Tip = L.T("Прямоугольник (R)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Ellipse, Tip = L.T("Овал (E)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Marker, Tip = L.T("Маркер (M)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Text, Tip = L.T("Текст (T)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Step, Tip = L.T("Шаги 1, 2, 3 (N)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.Blur, Tip = L.T("Скрыть пикселями (B)") },
                new Btn { Cmd = Cmd.Tool, Tool = Tool.SoftBlur, Tip = L.T("Размытие (U)") },
                new Btn { Cmd = Cmd.Color, Tip = L.T("Цвет") },
                new Btn { Cmd = Cmd.Undo, Tip = L.T("Отменить (Ctrl+Z)") },
            };
            var actions = new[]
            {
                new Btn { Cmd = Cmd.Upload, Tip = L.F("Загрузить в {0} (Ctrl+D)", S.Default()) },
                new Btn { Cmd = Cmd.UploadMenu, Tip = L.T("Выбрать, куда загрузить") },
                new Btn { Cmd = Cmd.Copy, Tip = L.T("Копировать (Ctrl+C)") },
                new Btn { Cmd = Cmd.Save, Tip = L.T("Сохранить (Ctrl+S)") },
                new Btn { Cmd = Cmd.Print, Tip = L.T("Печать (Ctrl+P)") },
                new Btn { Cmd = Cmd.Close, Tip = L.T("Закрыть (Esc)") },
            };

            if (video)
            {
                actions = new[]
                {
                    new Btn { Cmd = Cmd.Record, Tip = L.T("Начать запись (Enter)") },
                    new Btn { Cmd = Cmd.Sound, Tip = RecSys ? L.T("Звук компьютера: вкл.") : L.T("Звук компьютера: выкл.") },
                    new Btn { Cmd = Cmd.Mic, Tip = RecMic ? L.T("Микрофон: вкл.") : L.T("Микрофон: выкл.") },
                    new Btn { Cmd = Cmd.Close, Tip = L.T("Закрыть (Esc)") },
                };
                tools = new Btn[0];
            }
            int recW = RecordWidth();
            int aw = actions.Sum(a => a.Cmd == Cmd.UploadMenu ? narrow : a.Cmd == Cmd.Record ? recW : bs) + 2 * pad, ah = bs + 2 * pad;
            int ax = sel.Right - aw, ay = sel.Bottom + gap;
            if (ay + ah > mon.Bottom - gap)
            {
                ay = sel.Top - gap - ah;
                if (ay < mon.Top + gap) ay = sel.Bottom - ah - gap;
            }
            ax = Math.Max(mon.Left + gap, Math.Min(ax, mon.Right - aw - gap));
            actionsBar = new Rectangle(ax, ay, aw, ah);

            int tw = bs + 2 * pad, th = tools.Length * bs + 2 * pad;
            int tx = sel.Right + gap;
            if (tools.Length == 0) tw = th = 0;
            if (tx + tw > mon.Right - gap)
            {
                tx = sel.Left - gap - tw;
                if (tx < mon.Left + gap) tx = sel.Right - tw - gap;
            }
            int ty = Math.Max(mon.Top + gap, Math.Min(sel.Bottom - th, mon.Bottom - th - gap));
            toolsBar = tools.Length == 0 ? Rectangle.Empty : new Rectangle(tx, ty, tw, th);
            if (!toolsBar.IsEmpty && toolsBar.IntersectsWith(actionsBar))
                toolsBar.Y = Math.Max(mon.Top + gap, actionsBar.Top - gap - th);

            for (int i = 0; i < tools.Length; i++)
            {
                tools[i].R = new Rectangle(toolsBar.X + pad, toolsBar.Y + pad + i * bs, bs, bs);
                tools[i].Vertical = true;
                btns.Add(tools[i]);
            }
            int x = actionsBar.X + pad;
            foreach (var a in actions)
            {
                int w = a.Cmd == Cmd.UploadMenu ? narrow : a.Cmd == Cmd.Record ? recW : bs;
                a.R = new Rectangle(x, actionsBar.Y + pad, w, bs);
                x += w;
                btns.Add(a);
            }

            if (palette && !video)
            {
                var cb = btns.First(b => b.Cmd == Cmd.Color).R;
                int sw = Sc(26);
                int pw = palette_.Length * sw + 2 * pad, ph = sw + 2 * pad;
                int px = toolsBar.Left - gap - pw;
                if (px < mon.Left + gap) px = toolsBar.Right + gap;
                paletteRect = new Rectangle(px, cb.Top + (cb.Height - ph) / 2, pw, ph);
                for (int i = 0; i < palette_.Length; i++)
                    swatches.Add(new KeyValuePair<Rectangle, Color>(new Rectangle(paletteRect.X + pad + i * sw, paletteRect.Y + pad, sw, sw), palette_[i]));
            }
        }

        int RecordWidth()
        {
            return Sc(34) + TextRenderer.MeasureText(L.T("Записать"), uiFont).Width + Sc(10);
        }

        Rectangle TipRect(Graphics g, Btn b, out string text)
        {
            text = b.Tip;
            var sz = g.MeasureString(text, tipFont);
            int w = (int)sz.Width + Sc(14), h = (int)sz.Height + Sc(8), gap = Sc(6);
            var mon = MonitorOf(b.R);
            int x, y;
            if (b.Vertical)
            {
                x = toolsBar.Left - gap - w;
                if (x < mon.Left) x = toolsBar.Right + gap;
                if (palette && paletteRect.IntersectsWith(new Rectangle(x, b.R.Y, w, h))) x = toolsBar.Right + gap;
                y = b.R.Y + (b.R.Height - h) / 2;
            }
            else
            {
                x = b.R.X + (b.R.Width - w) / 2;
                y = actionsBar.Bottom + gap;
                if (y + h > mon.Bottom) y = actionsBar.Top - gap - h;
                x = Math.Max(mon.Left + gap, Math.Min(x, mon.Right - w - gap));
            }
            return new Rectangle(x, y, w, h);
        }

        Rectangle SizeLabelRect()
        {
            int w = Sc(110), h = Sc(24);
            int y = sel.Top - h - Sc(6);
            if (y < MonitorOf(sel).Top) y = sel.Top + Sc(6);
            return new Rectangle(sel.Left, y, w, h);
        }

        Rectangle HintRect()
        {
            var mon = MonitorOf(new Rectangle(mouse, new Size(1, 1)));
            int w = Sc(620), h = Sc(44);
            return new Rectangle(mon.X + (mon.Width - w) / 2, mon.Y + Sc(40), w, h);
        }

        Rectangle Frame()
        {
            var f = Rectangle.Empty;
            Action<Rectangle> add = r => { if (r.Width > 0 && r.Height > 0) f = f.IsEmpty ? r : Rectangle.Union(f, r); };
            if (hasSel)
            {
                var s = sel;
                s.Inflate(Sc(8), Sc(8));
                add(s);
                var l = SizeLabelRect(); l.Width += Sc(60); add(l);
            }
            else add(HintRect());
            var bars = toolsBar.IsEmpty ? actionsBar : Rectangle.Union(toolsBar, actionsBar);
            if (!bars.IsEmpty) { bars.Inflate(Sc(260), Sc(48)); add(bars); }
            if (!paletteRect.IsEmpty) { var p = paletteRect; p.Inflate(Sc(4), Sc(4)); add(p); }
            if (sizeTip != null) add(new Rectangle(mouse.X - Sc(20), mouse.Y - Sc(20), Sc(200), Sc(80)));
            return f;
        }

        void Refresh2()
        {
            Layout2();
            var f = Frame();
            var r = lastFrame.IsEmpty ? f : (f.IsEmpty ? lastFrame : Rectangle.Union(f, lastFrame));
            if (!r.IsEmpty) Invalidate(r);
            lastFrame = f;
        }

        // ---------------- painting ----------------

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintTo(e.Graphics, e.ClipRectangle);
        }

        public void PaintTo(Graphics g, Rectangle clip)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            using (var dark = new Region(clip))
            {
                if (hasSel) dark.Exclude(sel);
                g.FillRegion(Dim, dark);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (hasSel)
            {
                var r = Rectangle.Intersect(sel, clip);
                if (r.Width > 0 && r.Height > 0)
                {
                    g.SetClip(r);
                    foreach (var s in shapes) s.Draw(g, shot);
                    if (cur != null && !shapes.Contains(cur)) cur.Draw(g, shot);
                    g.ResetClip();
                }
                DrawSelectionFrame(g);
            }
            else
            {
                if (!hoverWin.IsEmpty && mode == Mode.Idle) DrawHoverWin(g, clip);
                DrawHint(g);
            }

            if (BarsVisible)
            {
                if (!toolsBar.IsEmpty) DrawBar(g, toolsBar);
                DrawBar(g, actionsBar);
                foreach (var b in btns) DrawButton(g, b);
                if (palette) DrawPalette(g);
                if (hover != null && mode == Mode.Idle) DrawTip(g, hover);
            }
            if (sizeTip != null) DrawSizeTip(g);
            if (LoupeVisible) DrawLoupe(g);
        }

        // ---------------- window under the cursor ----------------

        void DrawHoverWin(Graphics g, Rectangle clip)
        {
            var r = Rectangle.Intersect(hoverWin, clip);
            if (r.Width > 0 && r.Height > 0)
            {
                var cm = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(shot, r, r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel);
                g.CompositingMode = cm;
            }
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            int w = Math.Max(2, Sc(2));
            using (var p = new Pen(Color.FromArgb(30, 136, 229), w) { Alignment = PenAlignment.Inset }) g.DrawRectangle(p, hoverWin);
            g.SmoothingMode = sm;
            string text = hoverWin.Width + " × " + hoverWin.Height;
            var sz = g.MeasureString(text, uiFont);
            var lr = HoverLabelRect();
            lr.Width = (int)sz.Width + Sc(12);
            using (var path = UI.Round(lr, Sc(5)))
            using (var b = new SolidBrush(Color.FromArgb(225, 30, 136, 229))) g.FillPath(b, path);
            g.DrawString(text, uiFont, Brushes.White, lr.X + Sc(6), lr.Y + (lr.Height - sz.Height) / 2);
        }

        Rectangle HoverLabelRect()
        {
            int h = Sc(24);
            int y = hoverWin.Top - h - Sc(4);
            if (y < MonitorOf(hoverWin).Top) y = hoverWin.Top + Sc(6);
            return new Rectangle(hoverWin.Left + (y > hoverWin.Top ? Sc(6) : 0), y, Sc(140), h);
        }

        void InvalidateWin(Rectangle r)
        {
            if (r.IsEmpty) return;
            var a = r;
            a.Inflate(Sc(4), Sc(4));
            var saved = hoverWin;
            hoverWin = r;
            a = Rectangle.Union(a, HoverLabelRect());
            hoverWin = saved;
            Invalidate(a);
        }

        // ---------------- magnifier ----------------

        const int LoupeCells = 15;

        bool LoupeVisible
        {
            get
            {
                if (mode == Mode.Selecting || mode == Mode.Resizing) return true;
                return mode == Mode.Idle && !hasSel && ClientBounds.Contains(mouse);
            }
        }

        Rectangle LoupeRect(Point m)
        {
            int z = Math.Max(6, Sc(8));
            int size = LoupeCells * z, info = Sc(42), gap = Sc(22);
            var mon = MonitorOf(new Rectangle(m, new Size(1, 1)));
            int x = m.X + gap, y = m.Y + gap;
            if (x + size > mon.Right) x = m.X - gap - size;
            if (y + size + info > mon.Bottom) y = m.Y - gap - size - info;
            return new Rectangle(x, y, size, size + info);
        }

        void InvalidateLoupe(Point m)
        {
            var r = LoupeRect(m);
            r.Inflate(Sc(4), Sc(4));
            Invalidate(r);
        }

        void DrawLoupe(Graphics g)
        {
            var R = LoupeRect(mouse);
            int z = R.Width / LoupeCells;
            var zoom = new Rectangle(R.X, R.Y, R.Width, R.Width);
            using (var sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(sb, R.X + Sc(2), R.Y + Sc(2), R.Width, R.Height);
            using (var bg = new SolidBrush(Color.FromArgb(32, 32, 34))) g.FillRectangle(bg, R);

            var im = g.InterpolationMode; var pm = g.PixelOffsetMode; var sm = g.SmoothingMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(Brushes.Black, zoom);
            var src = new Rectangle(mouse.X - LoupeCells / 2, mouse.Y - LoupeCells / 2, LoupeCells, LoupeCells);
            var part = Rectangle.Intersect(src, ClientBounds);
            if (part.Width > 0 && part.Height > 0)
            {
                var dst = new Rectangle(zoom.X + (part.X - src.X) * z, zoom.Y + (part.Y - src.Y) * z, part.Width * z, part.Height * z);
                g.DrawImage(shot, dst, part, GraphicsUnit.Pixel);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (var gp = new Pen(Color.FromArgb(38, 255, 255, 255)))
                for (int i = 1; i < LoupeCells; i++)
                {
                    g.DrawLine(gp, zoom.X + i * z, zoom.Y, zoom.X + i * z, zoom.Bottom - 1);
                    g.DrawLine(gp, zoom.X, zoom.Y + i * z, zoom.Right - 1, zoom.Y + i * z);
                }
            var c = new Rectangle(zoom.X + (LoupeCells / 2) * z, zoom.Y + (LoupeCells / 2) * z, z, z);
            g.DrawRectangle(Pens.Black, c.X - 1, c.Y - 1, c.Width + 1, c.Height + 1);
            g.DrawRectangle(Pens.White, c.X, c.Y, c.Width - 1, c.Height - 1);
            using (var bp = new Pen(Color.FromArgb(150, 255, 255, 255))) g.DrawRectangle(bp, R.X, R.Y, R.Width - 1, R.Height - 1);
            g.InterpolationMode = im; g.PixelOffsetMode = pm; g.SmoothingMode = sm;

            Color px = ClientBounds.Contains(mouse) ? shot.GetPixel(mouse.X, mouse.Y) : Color.Black;
            int ty = zoom.Bottom + Sc(3);
            g.DrawString((mouse.X + vs.X) + ", " + (mouse.Y + vs.Y), tipFont, Brushes.White, R.X + Sc(6), ty);
            var sw = new Rectangle(R.X + Sc(7), ty + Sc(22), Sc(11), Sc(11));
            using (var b = new SolidBrush(Color.FromArgb(255, px))) g.FillRectangle(b, sw);
            g.DrawRectangle(Pens.Gray, sw);
            g.DrawString("#" + px.R.ToString("X2") + px.G.ToString("X2") + px.B.ToString("X2"), tipFont, Brushes.White, sw.Right + Sc(5), ty + Sc(19));
        }

        void DrawSelectionFrame(Graphics g)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            var r = new Rectangle(sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
            using (var p1 = new Pen(Color.FromArgb(170, 0, 0, 0), 1)) g.DrawRectangle(p1, r);
            using (var p2 = new Pen(Color.White, 1) { DashPattern = new float[] { 4, 4 } }) g.DrawRectangle(p2, r);
            if (mode != Mode.Drawing)
            {
                foreach (var h in Grips())
                {
                    g.FillRectangle(Brushes.White, h);
                    using (var p = new Pen(Color.FromArgb(200, 40, 40, 40), 1)) g.DrawRectangle(p, h.X, h.Y, h.Width - 1, h.Height - 1);
                }
            }
            g.SmoothingMode = sm;

            var lr = SizeLabelRect();
            string text = sel.Width + " × " + sel.Height;
            var sz = g.MeasureString(text, uiFont);
            lr.Width = (int)sz.Width + Sc(12);
            using (var path = UI.Round(lr, Sc(5)))
            using (var b = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillPath(b, path);
            g.DrawString(text, uiFont, Brushes.White, lr.X + Sc(6), lr.Y + (lr.Height - sz.Height) / 2);
        }

        void DrawHint(Graphics g)
        {
            var r = HintRect();
            string text = video ? L.T("Выдели область для видео или щёлкни по окну · Esc — отмена") : L.T("Выдели область мышью или щёлкни по окну · Esc — отмена");
            var sz = g.MeasureString(text, uiFont);
            r = new Rectangle(r.X + (r.Width - (int)sz.Width - Sc(28)) / 2, r.Y, (int)sz.Width + Sc(28), r.Height);
            using (var path = UI.Round(r, Sc(10)))
            using (var b = new SolidBrush(Color.FromArgb(215, 20, 20, 20))) g.FillPath(b, path);
            g.DrawString(text, uiFont, Brushes.White, r.X + Sc(14), r.Y + (r.Height - sz.Height) / 2);
        }

        void DrawBar(Graphics g, Rectangle r)
        {
            var shadow = r; shadow.Offset(0, Sc(2)); shadow.Inflate(Sc(1), Sc(1));
            using (var sp = UI.Round(shadow, Sc(9)))
            using (var sb = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillPath(sb, sp);
            using (var path = UI.Round(r, Sc(8)))
            {
                using (var b = new SolidBrush(Color.FromArgb(250, 251, 252))) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(205, 208, 214), 1)) g.DrawPath(p, path);
            }
        }

        static readonly Color Ink = Color.FromArgb(45, 45, 48);

        void DrawButton(Graphics g, Btn b)
        {
            bool active = (b.Cmd == Cmd.Tool && b.Tool == tool) || (b.Cmd == Cmd.Sound && RecSys) || (b.Cmd == Cmd.Mic && RecMic);
            bool hot = hover == b;
            if (active || hot || (b.Cmd == Cmd.Color && palette))
            {
                var r = b.R; r.Inflate(-Sc(2), -Sc(2));
                using (var path = UI.Round(r, Sc(6)))
                using (var br = new SolidBrush(active ? Color.FromArgb(209, 226, 255) : Color.FromArgb(232, 236, 242))) g.FillPath(br, path);
            }
            float cx = b.R.X + b.R.Width / 2f, cy = b.R.Y + b.R.Height / 2f;
            float u = 7.5f * k;
            using (var pen = new Pen(Ink, Math.Max(1.5f, 1.8f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(Ink))
            {
                switch (b.Cmd)
                {
                    case Cmd.Tool:
                        switch (b.Tool)
                        {
                            case Tool.Pen: Glyph(g, "", cx, cy); break;
                            case Tool.Marker: Glyph(g, "", cx, cy); break;
                            case Tool.Text: Glyph(g, "", cx, cy); break;
                            case Tool.Line: g.DrawLine(pen, cx - u, cy + u, cx + u, cy - u); break;
                            case Tool.Arrow:
                                g.DrawLine(pen, cx - u, cy + u, cx + u - 1, cy - u + 1);
                                g.FillPolygon(brush, new[] { new PointF(cx + u + 1, cy - u - 1), new PointF(cx + u + 1 - 7 * k, cy - u - 1), new PointF(cx + u + 1, cy - u - 1 + 7 * k) });
                                break;
                            case Tool.Rect: g.DrawRectangle(pen, cx - u, cy - u * 0.72f, 2 * u, 1.44f * u); break;
                            case Tool.Ellipse: g.DrawEllipse(pen, cx - u, cy - u * 0.75f, 2 * u, 1.5f * u); break;
                            case Tool.Step:
                                g.FillEllipse(brush, cx - u, cy - u, 2 * u, 2 * u);
                                using (var nf = new Font("Segoe UI", 10.5f * k, FontStyle.Bold, GraphicsUnit.Pixel))
                                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                    g.DrawString("1", nf, Brushes.White, new RectangleF(cx - u, cy - u + 0.5f * k, 2 * u, 2 * u), sf);
                                break;
                            case Tool.SoftBlur:
                                using (var path = new GraphicsPath())
                                {
                                    path.AddEllipse(cx - u * 1.1f, cy - u * 1.1f, 2.2f * u, 2.2f * u);
                                    using (var pg = new PathGradientBrush(path) { CenterColor = Ink, SurroundColors = new[] { Color.FromArgb(0, Ink) } })
                                        g.FillPath(pg, path);
                                }
                                break;
                            case Tool.Blur:
                                float c = u * 2 / 3f;
                                for (int i = 0; i < 3; i++)
                                    for (int j = 0; j < 3; j++)
                                    {
                                        int shade = ((i + j) % 2 == 0) ? 60 : 165;
                                        using (var sb = new SolidBrush(Color.FromArgb(shade, shade, shade)))
                                            g.FillRectangle(sb, cx - u + i * c, cy - u + j * c, c, c);
                                    }
                                break;
                        }
                        break;
                    case Cmd.Color:
                        float d = 16 * k;
                        using (var cb = new SolidBrush(color)) g.FillEllipse(cb, cx - d / 2, cy - d / 2, d, d);
                        using (var p = new Pen(Color.FromArgb(120, 0, 0, 0), 1)) g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
                        break;
                    case Cmd.Undo: Glyph(g, "", cx, cy, shapes.Count == 0 && editing == null ? Color.FromArgb(170, 170, 170) : Ink); break;
                    case Cmd.Upload: Glyph(g, "", cx, cy); break;
                    case Cmd.UploadMenu: Glyph(g, "", cx, cy, Ink, iconFontSmall); break;
                    case Cmd.Copy: Glyph(g, "", cx, cy); break;
                    case Cmd.Save: Glyph(g, "", cx, cy); break;
                    case Cmd.Print: Glyph(g, "\uE749", cx, cy); break;
                    case Cmd.Close: Glyph(g, "", cx, cy); break;
                    case Cmd.Record:
                        float d2 = 13 * k, rx = b.R.X + Sc(17);
                        using (var rb = new SolidBrush(Color.FromArgb(229, 57, 53))) g.FillEllipse(rb, rx - d2 / 2, cy - d2 / 2, d2, d2);
                        TextRenderer.DrawText(g, L.T("Записать"), uiFont, new Rectangle(b.R.X + Sc(30), b.R.Y, b.R.Width - Sc(30), b.R.Height), Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                        break;
                    case Cmd.Sound: Glyph(g, RecSys ? "" : "", cx, cy, RecSys ? Ink : Color.FromArgb(150, 150, 150)); break;
                    case Cmd.Mic:
                        Glyph(g, "", cx, cy, RecMic ? Ink : Color.FromArgb(150, 150, 150));
                        if (!RecMic) using (var sp = new Pen(Color.FromArgb(150, 150, 150), Math.Max(1.5f, 1.6f * k))) g.DrawLine(sp, cx - u, cy - u, cx + u, cy + u);
                        break;
                }
            }
        }

        void Glyph(Graphics g, string s, float cx, float cy) { Glyph(g, s, cx, cy, Ink, iconFont); }
        void Glyph(Graphics g, string s, float cx, float cy, Color c) { Glyph(g, s, cx, cy, c, iconFont); }

        void Glyph(Graphics g, string s, float cx, float cy, Color c, Font f)
        {
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(c))
                g.DrawString(s, f, b, new RectangleF(cx - 20 * k, cy - 20 * k, 40 * k, 40 * k), sf);
            g.TextRenderingHint = hint;
        }

        void DrawPalette(Graphics g)
        {
            DrawBar(g, paletteRect);
            foreach (var sw in swatches)
            {
                var r = sw.Key; r.Inflate(-Sc(4), -Sc(4));
                using (var b = new SolidBrush(sw.Value)) g.FillEllipse(b, r);
                using (var p = new Pen(Color.FromArgb(110, 0, 0, 0), 1)) g.DrawEllipse(p, r);
                if (sw.Value.ToArgb() == color.ToArgb())
                {
                    var o = sw.Key; o.Inflate(-Sc(1), -Sc(1));
                    using (var p = new Pen(Color.FromArgb(30, 136, 229), Math.Max(1.5f, 2 * k))) g.DrawEllipse(p, o);
                }
            }
        }

        void DrawTip(Graphics g, Btn b)
        {
            string text;
            var r = TipRect(g, b, out text);
            using (var path = UI.Round(r, Sc(5)))
            using (var br = new SolidBrush(Color.FromArgb(235, 32, 32, 34))) g.FillPath(br, path);
            var sz = g.MeasureString(text, tipFont);
            g.DrawString(text, tipFont, Brushes.White, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
        }

        void DrawSizeTip(Graphics g)
        {
            float d = Math.Max(4, tool == Tool.Marker ? Math.Max(12, size * 5) : tool == Tool.Text ? 12 + size * 3 : tool == Tool.Step ? 2 * (11 + size * 2.5f) : size);
            using (var p = new Pen(Color.White, 1)) g.DrawEllipse(p, mouse.X - d / 2, mouse.Y - d / 2, d, d);
            using (var p = new Pen(Color.FromArgb(160, 0, 0, 0), 1)) g.DrawEllipse(p, mouse.X - d / 2 - 1, mouse.Y - d / 2 - 1, d + 2, d + 2);
            var sz = g.MeasureString(sizeTip, tipFont);
            var r = new RectangleF(mouse.X + d / 2 + Sc(8), mouse.Y - sz.Height / 2 - Sc(3), sz.Width + Sc(12), sz.Height + Sc(6));
            using (var path = UI.Round(r, Sc(5)))
            using (var br = new SolidBrush(Color.FromArgb(225, 32, 32, 34))) g.FillPath(br, path);
            g.DrawString(sizeTip, tipFont, Brushes.White, r.X + Sc(6), r.Y + Sc(3));
        }

        // ---------------- hit testing ----------------

        Rectangle[] Grips()
        {
            int h = Sc(7);
            int l = sel.Left, t = sel.Top, r = sel.Right, b = sel.Bottom, mx = (l + r) / 2, my = (t + b) / 2;
            Func<int, int, Rectangle> at = (x, y) => new Rectangle(x - h / 2, y - h / 2, h, h);
            return new[] { at(l, t), at(mx, t), at(r, t), at(r, my), at(r, b), at(mx, b), at(l, b), at(l, my) };
        }

        int HitGrip(Point p)
        {
            if (!hasSel) return -1;
            var gs = Grips();
            for (int i = 0; i < gs.Length; i++)
            {
                var r = gs[i]; r.Inflate(Sc(4), Sc(4));
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        Btn HitButton(Point p)
        {
            if (!BarsVisible) return null;
            return btns.FirstOrDefault(b => b.R.Contains(p));
        }

        static readonly Cursor[] GripCursors =
        {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE
        };

        // ---------------- mouse ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            mouse = e.Location;
            if (e.Button == MouseButtons.Right)
            {
                if (mode == Mode.Drawing) { cur = null; mode = Mode.Idle; Refresh2(); return; }
                if (editing != null) { CommitText(); Refresh2(); return; }
                if (!hasSel) { Cancel(); return; }
                hasSel = false; shapes.Clear(); palette = false; tool = Tool.None; mode = Mode.Idle;
                Invalidate(); lastFrame = Rectangle.Empty;
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (palette)
            {
                foreach (var sw in swatches)
                    if (sw.Key.Contains(e.Location)) { SetColor(sw.Value); palette = false; Refresh2(); return; }
                palette = false;
                if (paletteRect.Contains(e.Location)) { Refresh2(); return; }
            }

            var btn = HitButton(e.Location);
            if (btn != null) { Press(btn); return; }
            if (BarsVisible && (toolsBar.Contains(e.Location) || actionsBar.Contains(e.Location))) return;

            down = e.Location;
            dragged = false;
            selAtDown = sel;

            if (!hasSel)
            {
                downWin = hoverWin;
                InvalidateWin(hoverWin);
                hoverWin = Rectangle.Empty;
                mode = Mode.Selecting;
                sel = new Rectangle(e.Location, Size.Empty);
                Refresh2();
                return;
            }

            grip = HitGrip(e.Location);
            if (grip >= 0 && tool == Tool.None) { CommitText(); mode = Mode.Resizing; Refresh2(); return; }

            if (tool != Tool.None && sel.Contains(e.Location))
            {
                StartShape(e.Location);
                return;
            }
            if (grip >= 0) { CommitText(); mode = Mode.Resizing; Refresh2(); return; }
            CommitText();
            if (tool == Tool.None && sel.Contains(e.Location)) { mode = Mode.Moving; Refresh2(); return; }
            if (tool == Tool.None) { downWin = Rectangle.Empty; mode = Mode.Selecting; sel = new Rectangle(e.Location, Size.Empty); hasSel = false; Refresh2(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var old = mouse;
            mouse = e.Location;
            if (LoupeVisible) { InvalidateLoupe(old); InvalidateLoupe(mouse); }
            if (Math.Abs(e.X - down.X) + Math.Abs(e.Y - down.Y) > 3) dragged = true;
            var b = ClientBounds;
            int dx = e.X - down.X, dy = e.Y - down.Y;

            switch (mode)
            {
                case Mode.Selecting:
                    var p = new Point(Math.Max(0, Math.Min(e.X, b.Right)), Math.Max(0, Math.Min(e.Y, b.Bottom)));
                    sel = Shape.Norm(down, p);
                    hasSel = sel.Width > 0 && sel.Height > 0;
                    Refresh2();
                    return;
                case Mode.Moving:
                    var m = selAtDown;
                    m.X = Math.Max(0, Math.Min(selAtDown.X + dx, b.Width - m.Width));
                    m.Y = Math.Max(0, Math.Min(selAtDown.Y + dy, b.Height - m.Height));
                    sel = m;
                    Refresh2();
                    return;
                case Mode.Resizing:
                    int l = selAtDown.Left, t = selAtDown.Top, r = selAtDown.Right, bt = selAtDown.Bottom;
                    if (grip == 0 || grip == 6 || grip == 7) l += dx;
                    if (grip == 2 || grip == 3 || grip == 4) r += dx;
                    if (grip == 0 || grip == 1 || grip == 2) t += dy;
                    if (grip == 4 || grip == 5 || grip == 6) bt += dy;
                    sel = Rectangle.Intersect(Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, bt), Math.Max(l, r), Math.Max(t, bt)), b);
                    hasSel = sel.Width > 0 && sel.Height > 0;
                    Refresh2();
                    return;
                case Mode.Drawing:
                    UpdateShape(e.Location, (ModifierKeys & Keys.Shift) != 0);
                    return;
            }

            if (!hasSel)
            {
                var hw = wins.FirstOrDefault(r => r.Contains(e.Location));
                if (hw != hoverWin) { InvalidateWin(hoverWin); hoverWin = hw; InvalidateWin(hoverWin); }
            }
            var hb = HitButton(e.Location);
            if (hb != hover) { hover = hb; Refresh2(); }
            if (!hasSel && HintRect().IntersectsWith(new Rectangle(old.X - 400, old.Y - 400, 800, 800))) Invalidate(HintRect());
            UpdateCursor(e.Location);
            if (sizeTip != null) Refresh2();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (LoupeVisible) InvalidateLoupe(mouse);
            switch (mode)
            {
                case Mode.Selecting:
                    if (!dragged || sel.Width < 3 || sel.Height < 3)
                    {
                        sel = !downWin.IsEmpty ? downWin : MonitorOf(new Rectangle(e.Location, new Size(1, 1)));
                        hasSel = true;
                    }
                    break;
                case Mode.Drawing:
                    if (cur != null && !(cur is TextShape))
                    {
                        if (!cur.Trivial || (cur is StrokeShape)) shapes.Add(cur);
                        Invalidate(Rectangle.Union(curBounds, cur.Bounds));
                    }
                    cur = null;
                    break;
            }
            mode = Mode.Idle;
            grip = -1;
            Refresh2();
            UpdateCursor(e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && hasSel && tool == Tool.None && sel.Contains(e.Location) && HitButton(e.Location) == null)
                Finish("copy", null);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int ns = Math.Max(1, Math.Min(20, size + (e.Delta > 0 ? 1 : -1)));
            if (ns == size && sizeTip != null) return;
            size = ns;
            if (editing != null) { var before = editing.Bounds; editing.Size = size; Invalidate(Rectangle.Union(before, editing.Bounds)); }
            sizeTip = L.F("Размер {0}", size);
            sizeTipUntil = DateTime.Now.AddMilliseconds(900);
            Refresh2();
        }

        void UpdateCursor(Point p)
        {
            if (HitButton(p) != null || (palette && paletteRect.Contains(p))) { Cursor = Cursors.Hand; return; }
            if (BarsVisible && (toolsBar.Contains(p) || actionsBar.Contains(p))) { Cursor = Cursors.Default; return; }
            int gi = HitGrip(p);
            if (gi >= 0 && tool == Tool.None) { Cursor = GripCursors[gi]; return; }
            if (hasSel && sel.Contains(p))
            {
                if (tool == Tool.Text) { Cursor = Cursors.IBeam; return; }
                if (tool != Tool.None) { Cursor = Cursors.Cross; return; }
                Cursor = Cursors.SizeAll; return;
            }
            if (gi >= 0) { Cursor = GripCursors[gi]; return; }
            Cursor = Cursors.Cross;
        }

        // ---------------- shapes ----------------

        void StartShape(Point p)
        {
            if (tool == Tool.Text)
            {
                CommitText();
                editing = new TextShape { Pos = new Point(p.X, p.Y - (int)((12 + size * 3) * 0.6f)), Color = color, Size = size, Editing = true };
                shapes.Add(editing);
                Invalidate(editing.Bounds);
                return;
            }
            CommitText();
            switch (tool)
            {
                case Tool.Pen: case Tool.Marker:
                    var st = new StrokeShape { Marker = tool == Tool.Marker };
                    st.Points.Add(p);
                    cur = st;
                    break;
                case Tool.Line: case Tool.Arrow: cur = new LineShape { A = p, B = p, Arrow = tool == Tool.Arrow }; break;
                case Tool.Rect: cur = new RectShape { A = p, B = p }; break;
                case Tool.Ellipse: cur = new RectShape { A = p, B = p, Ellipse = true }; break;
                case Tool.Step: cur = new StepShape { Center = p, Number = shapes.OfType<StepShape>().Select(x => x.Number).DefaultIfEmpty(0).Max() + 1 }; break;
                case Tool.SoftBlur: cur = new BlurShape { A = p, B = p, Smooth = true }; break;
                case Tool.Blur: cur = new BlurShape { A = p, B = p }; break;
            }
            cur.Color = color;
            cur.Size = size;
            curBounds = cur.Bounds;
            mode = Mode.Drawing;
            Invalidate(curBounds);
        }

        void UpdateShape(Point p, bool shift)
        {
            if (cur == null) return;
            var before = curBounds;
            var st = cur as StrokeShape;
            if (st != null)
            {
                var last = st.Points[st.Points.Count - 1];
                if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) < 2) return;
                st.Points.Add(p);
                int pad = (int)(st.Marker ? Math.Max(12, size * 5) : size) + 3;
                var seg = Shape.Norm(last, p); seg.Inflate(pad, pad);
                curBounds = Rectangle.Union(curBounds, seg);
                Invalidate(seg);
                return;
            }
            var ln = cur as LineShape;
            if (ln != null)
            {
                if (shift)
                {
                    double ang = Math.Atan2(p.Y - ln.A.Y, p.X - ln.A.X);
                    double snap = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
                    double len = Math.Sqrt(Math.Pow(p.X - ln.A.X, 2) + Math.Pow(p.Y - ln.A.Y, 2));
                    p = new Point(ln.A.X + (int)Math.Round(Math.Cos(snap) * len), ln.A.Y + (int)Math.Round(Math.Sin(snap) * len));
                }
                ln.B = p;
            }
            var rc = cur as RectShape;
            if (rc != null) rc.B = shift ? Square(rc.A, p) : p;
            var bl = cur as BlurShape;
            if (bl != null) bl.B = p;
            var sp = cur as StepShape;
            if (sp != null) sp.Center = p;
            curBounds = cur.Bounds;
            Invalidate(Rectangle.Union(before, curBounds));
        }

        static Point Square(Point a, Point p)
        {
            int d = Math.Max(Math.Abs(p.X - a.X), Math.Abs(p.Y - a.Y));
            return new Point(a.X + Math.Sign(p.X - a.X) * d, a.Y + Math.Sign(p.Y - a.Y) * d);
        }

        void CommitText()
        {
            if (editing == null) return;
            var t = editing;
            editing = null;
            t.Editing = false;
            if (t.Trivial) shapes.Remove(t);
            Invalidate(t.Bounds);
        }

        void SetColor(Color c)
        {
            color = c;
            if (editing != null) { editing.Color = c; Invalidate(editing.Bounds); }
        }

        void Undo()
        {
            if (editing != null) { var b = editing.Bounds; shapes.Remove(editing); editing = null; Invalidate(b); Refresh2(); return; }
            if (shapes.Count == 0) return;
            var last = shapes[shapes.Count - 1];
            shapes.RemoveAt(shapes.Count - 1);
            Invalidate(last.Bounds);
            Refresh2();
        }

        void OnTick(object sender, EventArgs e)
        {
            ticks++;
            if (editing != null && ticks % 5 == 0)
            {
                editing.CaretOn = !editing.CaretOn;
                Invalidate(editing.Bounds);
            }
            if (sizeTip != null && DateTime.Now > sizeTipUntil)
            {
                sizeTip = null;
                Refresh2();
            }
        }

        // ---------------- commands ----------------

        void Press(Btn b)
        {
            switch (b.Cmd)
            {
                case Cmd.Tool:
                    CommitText();
                    tool = tool == b.Tool ? Tool.None : b.Tool;
                    break;
                case Cmd.Color: palette = !palette; break;
                case Cmd.Undo: Undo(); break;
                case Cmd.Upload: Finish("upload", S.Default()); return;
                case Cmd.UploadMenu: ShowUploadMenu(b); return;
                case Cmd.Copy: Finish("copy", null); return;
                case Cmd.Save: Finish("save", null); return;
                case Cmd.Print: Finish("print", null); return;
                case Cmd.Close: Cancel(); return;
                case Cmd.Record: Record(); return;
                case Cmd.Sound: RecSys = !RecSys; S.VideoSysAudio = RecSys; break;
                case Cmd.Mic: RecMic = !RecMic; S.VideoMic = RecMic; break;
            }
            Refresh2();
        }

        void ShowUploadMenu(Btn b)
        {
            var m = new ContextMenuStrip { Font = new Font("Segoe UI", 10f), ShowImageMargin = false };
            foreach (var d in dests)
            {
                var item = new ToolStripMenuItem(L.F("Загрузить в {0}", d));
                if (d == S.Default()) item.Font = new Font(m.Font, FontStyle.Bold);
                string dest = d;
                item.Click += (s, e) => Finish("upload", dest);
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            var hint = new ToolStripMenuItem(L.T("Свои сервисы добавляются в настройках")) { Enabled = false };
            m.Items.Add(hint);
            m.Show(this, new Point(b.R.Left, b.R.Bottom + Sc(4)));
        }

        void Cancel()
        {
            Output = null;
            Close();
        }

        void Record()
        {
            if (!hasSel) return;
            RecordArea = new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height);
            Action = "record";
            Close();
        }

        void Finish(string action, string dest)
        {
            if (video) { Record(); return; }
            CommitText();
            if (!hasSel) return;
            Output = Render();
            Action = action;
            Destination = dest;
            S.PenColor = color.ToArgb();
            S.PenSize = size;
            Close();
        }

        public Bitmap Render()
        {
            var b = new Bitmap(sel.Width, sel.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
            {
                g.DrawImage(shot, new Rectangle(0, 0, sel.Width, sel.Height), sel, GraphicsUnit.Pixel);
                g.TranslateTransform(-sel.X, -sel.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var s in shapes) s.Draw(g, shot);
            }
            return b;
        }

        // ---------------- keyboard ----------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool HandleKey(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;

            if (editing != null)
            {
                var before = editing.Bounds;
                switch (code)
                {
                    case Keys.Escape: CommitText(); Refresh2(); return true;
                    case Keys.Enter:
                        if (shift) editing.Text += "\n"; else { CommitText(); Refresh2(); return true; }
                        break;
                    case Keys.Back:
                        if (editing.Text.Length > 0) editing.Text = editing.Text.Substring(0, editing.Text.Length - 1);
                        break;
                    case Keys.V:
                        if (!ctrl) return false;
                        try { editing.Text += Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'); } catch { }
                        break;
                    default:
                        if (!ctrl) return false;
                        goto common;
                }
                editing.CaretOn = true;
                Invalidate(Rectangle.Union(before, editing.Bounds));
                return true;
            }

            common:
            if (video && ctrl && code != Keys.A) return code == Keys.C || code == Keys.S || code == Keys.P || code == Keys.D || code == Keys.Z;
            if (ctrl)
            {
                switch (code)
                {
                    case Keys.C: if (hasSel) Finish("copy", null); return true;
                    case Keys.S: if (hasSel) Finish("save", null); return true;
                    case Keys.P: if (hasSel) Finish("print", null); return true;
                    case Keys.D: if (hasSel) Finish("upload", S.Default()); return true;
                    case Keys.Z: Undo(); return true;
                    case Keys.A: sel = MonitorOf(new Rectangle(mouse, new Size(1, 1))); hasSel = true; Invalidate(); Refresh2(); return true;
                }
            }
            switch (code)
            {
                case Keys.Escape:
                    if (palette) { palette = false; Refresh2(); return true; }
                    Cancel(); return true;
                case Keys.Enter: if (hasSel) Finish("copy", null); return true;
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                    if (!hasSel) return true;
                    int step = ctrl ? 10 : 1;
                    int dx = code == Keys.Left ? -step : code == Keys.Right ? step : 0;
                    int dy = code == Keys.Up ? -step : code == Keys.Down ? step : 0;
                    var r = sel;
                    if (shift) { r.Width = Math.Max(1, r.Width + dx); r.Height = Math.Max(1, r.Height + dy); }
                    else r.Offset(dx, dy);
                    sel = Rectangle.Intersect(r, ClientBounds);
                    Refresh2();
                    return true;
            }
            if (!ctrl && hasSel && !video)
            {
                Tool t = Tool.None; bool hit = true;
                switch (code)
                {
                    case Keys.P: t = Tool.Pen; break;
                    case Keys.L: t = Tool.Line; break;
                    case Keys.A: t = Tool.Arrow; break;
                    case Keys.R: t = Tool.Rect; break;
                    case Keys.M: t = Tool.Marker; break;
                    case Keys.T: t = Tool.Text; break;
                    case Keys.B: t = Tool.Blur; break;
                    case Keys.E: t = Tool.Ellipse; break;
                    case Keys.N: t = Tool.Step; break;
                    case Keys.U: t = Tool.SoftBlur; break;
                    default: hit = false; break;
                }
                if (hit) { tool = tool == t ? Tool.None : t; Refresh2(); UpdateCursor(mouse); return true; }
            }
            return false;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (editing == null || char.IsControl(e.KeyChar)) return;
            var before = editing.Bounds;
            editing.Text += e.KeyChar;
            editing.CaretOn = true;
            Invalidate(Rectangle.Union(before, editing.Bounds));
            e.Handled = true;
        }

        // ---------------- self-test support ----------------

        public void TestScene()
        {
            sel = new Rectangle(300, 160, 760, 420);
            hasSel = true;
            tool = Tool.Arrow;
            var pen = new StrokeShape { Color = Colors[0], Size = 4 };
            for (int i = 0; i < 40; i++) pen.Points.Add(new Point(340 + i * 6, 220 + (int)(Math.Sin(i / 4.0) * 20)));
            shapes.Add(pen);
            shapes.Add(new LineShape { A = new Point(380, 520), B = new Point(560, 380), Arrow = true, Color = Colors[4], Size = 5 });
            shapes.Add(new RectShape { A = new Point(620, 300), B = new Point(820, 420), Color = Colors[3], Size = 3 });
            var mk = new StrokeShape { Marker = true, Color = Colors[2], Size = 4 };
            mk.Points.Add(new Point(640, 470)); mk.Points.Add(new Point(900, 470));
            shapes.Add(mk);
            shapes.Add(new BlurShape { A = new Point(880, 200), B = new Point(1040, 280), Size = 4 });
            shapes.Add(new BlurShape { A = new Point(330, 282), B = new Point(600, 302), Size = 4, Smooth = true });
            shapes.Add(new RectShape { A = new Point(860, 330), B = new Point(1040, 420), Color = Colors[5], Size = 3, Ellipse = true });
            shapes.Add(new StepShape { Center = new Point(330, 200), Number = 1, Color = Colors[0], Size = 4 });
            shapes.Add(new StepShape { Center = new Point(600, 300), Number = 2, Color = Colors[0], Size = 4 });
            shapes.Add(new StepShape { Center = new Point(860, 470), Number = 3, Color = Colors[2], Size = 4 });
            shapes.Add(new TextShape { Pos = new Point(640, 500), Text = "Привет, Кадр!", Color = Colors[0], Size = 4, Editing = true });
            Layout2();
            hover = btns.First(b => b.Cmd == Cmd.Upload);
        }

        public void TestHover(Point m)
        {
            mouse = m;
            hoverWin = wins.FirstOrDefault(r => r.Contains(m));
        }

        public void TestSelect(Rectangle r)
        {
            sel = r;
            hasSel = true;
            Layout2();
            hover = btns.FirstOrDefault(b => b.Cmd == Cmd.Record);
        }

        public int WindowCount { get { return wins.Count; } }
    }
}
