using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;

namespace Kadr
{
    enum Tool { None, Pen, Line, Arrow, Rect, Marker, Text, Blur }

    abstract class Shape
    {
        public Color Color;
        public int Size;
        public abstract void Draw(Graphics g, Bitmap baseImage);
        public abstract Rectangle Bounds { get; }
        public virtual bool Trivial { get { return false; } }

        public static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
    }

    class StrokeShape : Shape
    {
        public readonly List<Point> Points = new List<Point>();
        public bool Marker;

        float Width { get { return Marker ? Math.Max(12, Size * 5) : Size; } }
        Color Ink { get { return Marker ? Color.FromArgb(105, Color) : Color; } }

        public override void Draw(Graphics g, Bitmap baseImage)
        {
            if (Points.Count == 0) return;
            if (Points.Count == 1)
            {
                float w = Width;
                using (var b = new SolidBrush(Ink))
                {
                    if (Marker) g.FillRectangle(b, Points[0].X - w / 2, Points[0].Y - w / 2, w, w);
                    else g.FillEllipse(b, Points[0].X - w / 2, Points[0].Y - w / 2, w, w);
                }
                return;
            }
            using (var pen = new Pen(Ink, Width))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = pen.EndCap = Marker ? LineCap.Square : LineCap.Round;
                g.DrawLines(pen, Points.ToArray());
            }
        }

        public override Rectangle Bounds
        {
            get
            {
                if (Points.Count == 0) return Rectangle.Empty;
                int pad = (int)Width + 2;
                return Rectangle.FromLTRB(Points.Min(p => p.X) - pad, Points.Min(p => p.Y) - pad, Points.Max(p => p.X) + pad, Points.Max(p => p.Y) + pad);
            }
        }
    }

    class LineShape : Shape
    {
        public Point A, B;
        public bool Arrow;

        float Head { get { return Size * 3f + 10f; } }

        public override void Draw(Graphics g, Bitmap baseImage)
        {
            float dx = B.X - A.X, dy = B.Y - A.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            using (var pen = new Pen(Color, Size) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (!Arrow) { g.DrawLine(pen, A, B); return; }
                float ux = dx / len, uy = dy / len;
                float head = Math.Min(Head, len * 0.9f);
                float half = head * 0.55f;
                var tip = new PointF(B.X, B.Y);
                var baseP = new PointF(B.X - ux * head, B.Y - uy * head);
                var left = new PointF(baseP.X - uy * half, baseP.Y + ux * half);
                var right = new PointF(baseP.X + uy * half, baseP.Y - ux * half);
                var lineEnd = new PointF(B.X - ux * head * 0.7f, B.Y - uy * head * 0.7f);
                pen.EndCap = LineCap.Flat;
                g.DrawLine(pen, new PointF(A.X, A.Y), lineEnd);
                using (var b = new SolidBrush(Color)) g.FillPolygon(b, new[] { tip, left, right });
            }
        }

        public override Rectangle Bounds
        {
            get { var r = Norm(A, B); r.Inflate((int)Head + Size + 2, (int)Head + Size + 2); return r; }
        }

        public override bool Trivial
        {
            get { return Math.Abs(A.X - B.X) + Math.Abs(A.Y - B.Y) < 4; }
        }
    }

    class RectShape : Shape
    {
        public Point A, B;

        public override void Draw(Graphics g, Bitmap baseImage)
        {
            var r = Norm(A, B);
            if (r.Width < 1 || r.Height < 1) return;
            using (var pen = new Pen(Color, Size) { LineJoin = LineJoin.Miter, Alignment = PenAlignment.Center })
                g.DrawRectangle(pen, r);
        }

        public override Rectangle Bounds
        {
            get { var r = Norm(A, B); r.Inflate(Size + 2, Size + 2); return r; }
        }

        public override bool Trivial
        {
            get { var r = Norm(A, B); return r.Width < 3 || r.Height < 3; }
        }
    }

    /// <summary>Pixelates the screenshot under the rectangle (for hiding names, numbers, faces).</summary>
    class BlurShape : Shape
    {
        public Point A, B;
        Bitmap cache;
        Rectangle cacheRect;

        public override void Draw(Graphics g, Bitmap baseImage)
        {
            var r = Rectangle.Intersect(Norm(A, B), new Rectangle(0, 0, baseImage.Width, baseImage.Height));
            if (r.Width < 2 || r.Height < 2) return;
            if (cache == null || cacheRect != r)
            {
                if (cache != null) cache.Dispose();
                int block = Math.Max(8, Size * 3);
                int sw = Math.Max(1, (r.Width + block - 1) / block), sh = Math.Max(1, (r.Height + block - 1) / block);
                using (var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                {
                    using (var g2 = Graphics.FromImage(small))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.DrawImage(baseImage, new Rectangle(0, 0, sw, sh), r, GraphicsUnit.Pixel);
                    }
                    cache = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
                    using (var g3 = Graphics.FromImage(cache))
                    {
                        g3.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g3.PixelOffsetMode = PixelOffsetMode.Half;
                        g3.DrawImage(small, new Rectangle(0, 0, sw * block, sh * block), new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
                    }
                }
                cacheRect = r;
            }
            var im = g.InterpolationMode; var pm = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(cache, r, new Rectangle(0, 0, r.Width, r.Height), GraphicsUnit.Pixel);
            g.InterpolationMode = im; g.PixelOffsetMode = pm;
        }

        public override Rectangle Bounds
        {
            get { var r = Norm(A, B); r.Inflate(2, 2); return r; }
        }

        public override bool Trivial
        {
            get { var r = Norm(A, B); return r.Width < 4 || r.Height < 4; }
        }
    }

    class TextShape : Shape
    {
        public Point Pos;
        public string Text = "";
        public bool Editing;
        public bool CaretOn = true;

        static readonly StringFormat Fmt = MakeFormat();
        static StringFormat MakeFormat()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        public float FontPx { get { return 12 + Size * 3; } }

        Font MakeFont() { return new Font("Segoe UI Semibold", FontPx, FontStyle.Regular, GraphicsUnit.Pixel); }

        string[] Lines { get { return Text.Split('\n'); } }

        float LineHeight(Font f) { return f.Size * 1.33f; }

        SizeF Measure(Graphics g, Font f)
        {
            float w = 0;
            foreach (var l in Lines) w = Math.Max(w, g.MeasureString(l, f, PointF.Empty, Fmt).Width);
            return new SizeF(w, Lines.Length * LineHeight(f));
        }

        public override void Draw(Graphics g, Bitmap baseImage)
        {
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var f = MakeFont())
            using (var b = new SolidBrush(Color))
            {
                var lines = Lines;
                float lh = LineHeight(f);
                for (int i = 0; i < lines.Length; i++)
                    g.DrawString(lines[i], f, b, Pos.X, Pos.Y + i * lh, Fmt);

                if (Editing)
                {
                    var size = Measure(g, f);
                    float pad = 4;
                    using (var p = new Pen(Color.FromArgb(160, 128, 128, 128), 1) { DashStyle = DashStyle.Dash })
                        g.DrawRectangle(p, Pos.X - pad, Pos.Y - pad, Math.Max(size.Width, f.Size * 0.6f) + pad * 2, size.Height + pad * 2);
                    if (CaretOn)
                    {
                        float x = Pos.X + g.MeasureString(lines[lines.Length - 1], f, PointF.Empty, Fmt).Width + 1;
                        float y = Pos.Y + (lines.Length - 1) * lh;
                        using (var p = new Pen(Color, Math.Max(1.5f, f.Size / 14f)))
                            g.DrawLine(p, x, y + lh * 0.1f, x, y + lh * 0.92f);
                    }
                }
            }
            g.TextRenderingHint = hint;
        }

        public override Rectangle Bounds
        {
            get
            {
                using (var bmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(bmp))
                using (var f = MakeFont())
                {
                    var s = Measure(g, f);
                    var r = new Rectangle(Pos.X, Pos.Y, (int)Math.Ceiling(Math.Max(s.Width, f.Size)) + 4, (int)Math.Ceiling(s.Height) + 4);
                    r.Inflate(8, 8);
                    return r;
                }
            }
        }

        public override bool Trivial { get { return Text.Trim().Length == 0; } }
    }
}
