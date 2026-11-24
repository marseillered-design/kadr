using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Kadr
{
    /// <summary>Small notice in the bottom-right corner: upload progress, the link, errors.</summary>
    class Toast : Form
    {
        static readonly List<Toast> open = new List<Toast>();
        public static event Action AllClosed;
        public static int OpenCount { get { return open.Count; } }

        Image thumb;
        string title, text;
        Action onClick;
        string btnGlyph;
        Action btnAction;
        bool btnTick;
        bool copyHot, copied;
        string btn2Glyph;
        Action btn2Action;
        bool btn2Hot;
        bool busy, error;
        readonly float k;
        readonly Timer anim = new Timer { Interval = 30 };
        readonly Timer hide = new Timer();
        int phase;
        bool hot;
        readonly bool dark = Shell.DarkMode;
        readonly Font titleFont, textFont, glyphFont, copyFont;

        Toast(Image thumbSource, string title, string text)
        {
            k = Native.ScaleAt(Screen.PrimaryScreen.WorkingArea.Location);
            if (thumbSource != null) thumb = Imaging.Fit(thumbSource, (int)(88 * k), (int)(64 * k));
            this.title = title;
            this.text = text;
            busy = true;
            titleFont = new Font("Segoe UI Semibold", 14f * k, GraphicsUnit.Pixel);
            textFont = new Font("Segoe UI", 12.5f * k, GraphicsUnit.Pixel);
            glyphFont = new Font(UI.IconFont, 10f * k, GraphicsUnit.Pixel);
            copyFont = new Font(UI.IconFont, 15f * k, GraphicsUnit.Pixel);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size((int)(400 * k), (int)(88 * k));
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            anim.Tick += (s, e) => { phase++; Invalidate(); };
            hide.Tick += (s, e) => { if (!hot) Close(); };
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

        public static Toast Show(Image thumbSource, string title, string text)
        {
            var t = new Toast(thumbSource, title, text);
            open.Add(t);
            t.Reposition();
            t.Show();
            Native.RoundCorners(t.Handle);
            t.anim.Start();
            return t;
        }

        /// <summary>A finished notice (no progress bar) that hides by itself.</summary>
        public static Toast Info(Image thumbSource, string title, string text, Action onClick, int seconds = 6)
        {
            var t = Show(thumbSource, title, text);
            t.Done(title, text, onClick, false, seconds);
            return t;
        }

        void Reposition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(12 * k);
            int y = wa.Bottom - gap;
            foreach (var t in open)
            {
                y -= t.Height;
                if (t == this) break;
                y -= gap;
            }
            Location = new Point(wa.Right - Width - gap, y);
        }

        public void Done(string title, string text, Action onClick, bool error, int seconds = 7, string copyText = null)
        {
            if (copyText != null) { string c = copyText; btnGlyph = "\uE8C8"; btnAction = () => Clip.SetText(c); btnTick = true; }
            if (IsDisposed) return;
            busy = false;
            this.error = error;
            this.title = title;
            this.text = text;
            this.onClick = onClick;
            anim.Stop();
            hide.Interval = seconds * 1000;
            hide.Start();
            Invalidate();
        }

        Rectangle CopyRect { get { int s = (int)(34 * k); return new Rectangle(Width - s - (int)(10 * k), Height - s - (int)(10 * k), s, s); } }

        Rectangle Btn2Rect { get { var r = CopyRect; r.X -= r.Width + (int)(4 * k); return r; } }

        Rectangle CloseRect { get { int s = (int)(22 * k); return new Rectangle(Width - s - (int)(6 * k), (int)(6 * k), s, s); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color bg = dark ? Color.FromArgb(43, 43, 46) : Color.White;
            Color fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 30);
            Color sub = error ? Color.FromArgb(229, 57, 53) : (dark ? Color.FromArgb(150, 190, 255) : Color.FromArgb(25, 103, 210));
            if (busy) sub = dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(110, 110, 110);
            g.Clear(bg);
            using (var p = new Pen(dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(215, 215, 220))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            int pad = (int)(12 * k);
            int x = pad;
            if (thumb != null)
            {
                int ty = (Height - thumb.Height) / 2;
                g.DrawImage(thumb, x, ty, thumb.Width, thumb.Height);
                using (var p = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawRectangle(p, x, ty, thumb.Width - 1, thumb.Height - 1);
                x += (int)(88 * k) + pad;
            }
            int right = CloseRect.Left - (int)(4 * k);
            var tr = new Rectangle(x, (int)(18 * k), right - x, (int)(22 * k));
            TextRenderer.DrawText(g, title, titleFont, tr, fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
            int textRight = btn2Glyph != null ? Btn2Rect.Left - (int)(6 * k) : btnGlyph != null ? CopyRect.Left - (int)(6 * k) : Width - pad;
            var sr = new Rectangle(x, (int)(44 * k), textRight - x, (int)(36 * k));
            TextRenderer.DrawText(g, text ?? "", textFont, sr, sub, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.Left);

            using (var b = new SolidBrush(dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(120, 120, 120)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("", glyphFont, b, CloseRect, sf);

            if (btnGlyph != null)
            {
                var cr = CopyRect;
                if (copyHot || copied)
                    using (var path = UI.Round(cr, 6 * k))
                    using (var hb = new SolidBrush(dark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(234, 236, 240))) g.FillPath(hb, path);
                Color gc = copied ? Color.FromArgb(67, 160, 71) : (dark ? Color.FromArgb(225, 225, 225) : Color.FromArgb(50, 50, 54));
                using (var b = new SolidBrush(gc))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(copied ? "\uE73E" : btnGlyph, copyFont, b, cr, sf);
            }

            if (btn2Glyph != null)
            {
                var r2 = Btn2Rect;
                if (btn2Hot)
                    using (var path = UI.Round(r2, 6 * k))
                    using (var hb = new SolidBrush(dark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(234, 236, 240))) g.FillPath(hb, path);
                using (var b = new SolidBrush(dark ? Color.FromArgb(225, 225, 225) : Color.FromArgb(50, 50, 54)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(btn2Glyph, copyFont, b, r2, sf);
            }

            if (busy)
            {
                int h = Math.Max(2, (int)(3 * k));
                int w = Width / 3;
                int pos = (phase * (int)(8 * k)) % (Width + w) - w;
                using (var b = new SolidBrush(Color.FromArgb(61, 90, 254))) g.FillRectangle(b, pos, Height - h, w, h);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; if (copyHot || btn2Hot) { copyHot = btn2Hot = false; Invalidate(); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = btnGlyph != null && CopyRect.Contains(e.Location);
            bool h2 = btn2Glyph != null && Btn2Rect.Contains(e.Location);
            if (h != copyHot || h2 != btn2Hot) { copyHot = h; btn2Hot = h2; Invalidate(); }
        }

        /// <summary>An icon button in the bottom-right corner (copy the link, open the folder…).</summary>
        public void SetButton(string glyph, Action action, bool tickFeedback)
        {
            btnGlyph = glyph;
            btnAction = action;
            btnTick = tickFeedback;
            Invalidate();
        }

        /// <summary>A second icon button, left of the first (e.g. upload the video). Closes the notice when pressed.</summary>
        public void AddButton(string glyph, Action action)
        {
            btn2Glyph = glyph;
            btn2Action = action;
            Invalidate();
        }

        /// <summary>Runs the button; "copy" stays open and shows a green tick for a moment.</summary>
        void PressButton()
        {
            btnAction();
            if (!btnTick) { Close(); return; }
            copied = true;
            Invalidate();
            hide.Stop();
            hide.Start();
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Dispose(); copied = false; if (!IsDisposed) Invalidate(); };
            t.Start();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (CloseRect.Contains(e.Location) || e.Button == MouseButtons.Right) { Close(); return; }
            if (btnGlyph != null && CopyRect.Contains(e.Location)) { PressButton(); return; }
            if (btn2Glyph != null && Btn2Rect.Contains(e.Location)) { var a = btn2Action; Close(); if (a != null) a(); return; }
            if (onClick != null) onClick();
            if (!busy) Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            anim.Dispose();
            hide.Dispose();
            if (thumb != null) thumb.Dispose();
            open.Remove(this);
            foreach (var t in open) t.Reposition();
            base.OnFormClosed(e);
            if (open.Count == 0 && AllClosed != null) AllClosed();
        }
    }
}
