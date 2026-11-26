using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NDConnect.UI
{
    public sealed class ColorWheelControl : Control
    {
        private double _hue = 140.0;        // 0..360
        private double _saturation = 0.85;  // 0..1
        private double _value = 0.90;       // 0..1

        private Bitmap _wheelCache;
        private int _cachedSize = -1;
        private double _cachedValue = -1;

        private Bitmap _barCache;
        private int _cachedBarW = -1;
        private int _cachedBarH = -1;
        private double _cachedBarHVal = -1;
        private double _cachedBarSVal = -1;

        private int _dragMode = 0; // 0=none, 1=wheel, 2=valueBar

        public event EventHandler ColorChanged;

        public ColorWheelControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(256, 212);
            Cursor = Cursors.Cross;
        }

        public Color SelectedColor
        {
            get { return HsvToRgb(_hue, _saturation, _value); }
            set
            {
                double h, s, v;
                RgbToHsv(value, out h, out s, out v);
                _hue = h;
                _saturation = s;
                _value = v;
                Invalidate();
            }
        }

        private void GetGeometry(out Rectangle wheelRect, out Rectangle barRect)
        {
            int pad = 8;
            int barW = 20;
            int gap = 14;
            int availW = Math.Max(60, Width - pad * 2 - barW - gap);
            int availH = Math.Max(60, Height - pad * 2);
            int wheelSize = Math.Min(availW, availH);
            int wx = pad;
            int wy = pad + (availH - wheelSize) / 2;
            wheelRect = new Rectangle(wx, wy, wheelSize, wheelSize);
            barRect = new Rectangle(wx + wheelSize + gap, wy + 4, barW, Math.Max(40, wheelSize - 8));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Rectangle wheelRect, barRect;
            GetGeometry(out wheelRect, out barRect);
            Rectangle hitBar = new Rectangle(barRect.X - 6, barRect.Y - 6, barRect.Width + 12, barRect.Height + 12);
            if (hitBar.Contains(e.Location))
            {
                _dragMode = 2;
                UpdateFromBar(e.Y, barRect);
            }
            else
            {
                _dragMode = 1;
                UpdateFromWheel(e.X, e.Y, wheelRect);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragMode == 0) return;
            Rectangle wheelRect, barRect;
            GetGeometry(out wheelRect, out barRect);
            if (_dragMode == 1) UpdateFromWheel(e.X, e.Y, wheelRect);
            else if (_dragMode == 2) UpdateFromBar(e.Y, barRect);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragMode = 0;
        }

        private void UpdateFromWheel(int mx, int my, Rectangle wheelRect)
        {
            double cx = wheelRect.X + wheelRect.Width * 0.5;
            double cy = wheelRect.Y + wheelRect.Height * 0.5;
            double radius = wheelRect.Width * 0.5 - 2.0;
            if (radius <= 1.0) return;

            double dx = mx - cx;
            double dy = my - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double s = Math.Min(1.0, dist / radius);
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (angle < 0) angle += 360.0;

            _hue = angle;
            _saturation = s;
            Invalidate();
            EventHandler h = ColorChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void UpdateFromBar(int my, Rectangle barRect)
        {
            double t = 1.0 - (my - barRect.Y) / (double)Math.Max(1, barRect.Height);
            _value = Math.Max(0.0, Math.Min(1.0, t));
            Invalidate();
            EventHandler h = ColorChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle wheelRect, barRect;
            GetGeometry(out wheelRect, out barRect);

            EnsureWheelCache(wheelRect.Width);
            if (_wheelCache != null)
            {
                g.DrawImageUnscaled(_wheelCache, wheelRect.X, wheelRect.Y);
            }

            // Wheel border ring
            using (Pen borderPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.2f))
            {
                g.DrawEllipse(borderPen, wheelRect.X + 1.5f, wheelRect.Y + 1.5f, wheelRect.Width - 3f, wheelRect.Height - 3f);
            }

            // Brightness bar
            EnsureBarCache(barRect.Width, barRect.Height);
            if (_barCache != null)
            {
                g.DrawImageUnscaled(_barCache, barRect.X, barRect.Y);
            }
            using (Pen barBorder = new Pen(Color.FromArgb(80, 255, 255, 255), 1f))
            {
                g.DrawRectangle(barBorder, barRect);
            }

            // Wheel selector marker
            double cx = wheelRect.X + wheelRect.Width * 0.5;
            double cy = wheelRect.Y + wheelRect.Height * 0.5;
            double rad = (wheelRect.Width * 0.5 - 2.0) * _saturation;
            double angRad = _hue * Math.PI / 180.0;
            float sx = (float)(cx + Math.Cos(angRad) * rad);
            float sy = (float)(cy + Math.Sin(angRad) * rad);

            Color cur = SelectedColor;
            using (Brush fill = new SolidBrush(cur))
            using (Pen outer = new Pen(Color.White, 2.2f))
            using (Pen inner = new Pen(Color.FromArgb(180, 0, 0, 0), 1.0f))
            {
                g.FillEllipse(fill, sx - 6f, sy - 6f, 12f, 12f);
                g.DrawEllipse(outer, sx - 6f, sy - 6f, 12f, 12f);
                g.DrawEllipse(inner, sx - 7.2f, sy - 7.2f, 14.4f, 14.4f);
            }

            // Brightness bar thumb
            float by = (float)(barRect.Y + (1.0 - _value) * barRect.Height);
            RectangleF thumb = new RectangleF(barRect.X - 3f, by - 3.5f, barRect.Width + 6f, 7f);
            using (Brush tb = new SolidBrush(cur))
            using (Pen tp = new Pen(Color.White, 1.8f))
            {
                g.FillRectangle(tb, thumb);
                g.DrawRectangle(tp, thumb.X, thumb.Y, thumb.Width, thumb.Height);
            }
        }

        private void EnsureWheelCache(int size)
        {
            if (_wheelCache != null && _cachedSize == size && Math.Abs(_cachedValue - _value) < 0.005)
            {
                return;
            }
            if (_wheelCache != null) { _wheelCache.Dispose(); _wheelCache = null; }
            _wheelCache = RenderWheelBitmap(size, _value);
            _cachedSize = size;
            _cachedValue = _value;
        }

        private void EnsureBarCache(int w, int h)
        {
            if (_barCache != null && _cachedBarW == w && _cachedBarH == h &&
                Math.Abs(_cachedBarHVal - _hue) < 0.5 && Math.Abs(_cachedBarSVal - _saturation) < 0.01)
            {
                return;
            }
            if (_barCache != null) { _barCache.Dispose(); _barCache = null; }

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[Math.Abs(data.Stride) * h];
            for (int y = 0; y < h; y++)
            {
                double v = 1.0 - y / (double)Math.Max(1, h - 1);
                Color c = HsvToRgb(_hue, _saturation, v);
                int rowOff = y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    int idx = rowOff + x * 4;
                    pixels[idx] = c.B;
                    pixels[idx + 1] = c.G;
                    pixels[idx + 2] = c.R;
                    pixels[idx + 3] = 255;
                }
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bmp.UnlockBits(data);
            _barCache = bmp;
            _cachedBarW = w;
            _cachedBarH = h;
            _cachedBarHVal = _hue;
            _cachedBarSVal = _saturation;
        }

        public static Bitmap RenderWheelBitmap(int size, double value)
        {
            int s = Math.Max(16, size);
            Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[Math.Abs(data.Stride) * s];

            double cx = (s - 1) * 0.5;
            double cy = (s - 1) * 0.5;
            double radius = s * 0.5 - 2.0;

            for (int y = 0; y < s; y++)
            {
                int rowOff = y * data.Stride;
                double dy = y - cy;
                for (int x = 0; x < s; x++)
                {
                    double dx = x - cx;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    int idx = rowOff + x * 4;

                    if (dist > radius + 1.0)
                    {
                        pixels[idx + 3] = 0;
                        continue;
                    }

                    double sat = Math.Min(1.0, dist / radius);
                    double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    if (ang < 0) ang += 360.0;

                    Color c = HsvToRgb(ang, sat, value);
                    byte alpha = 255;
                    if (dist > radius)
                    {
                        alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round((1.0 - (dist - radius)) * 255.0)));
                    }
                    pixels[idx] = c.B;
                    pixels[idx + 1] = c.G;
                    pixels[idx + 2] = c.R;
                    pixels[idx + 3] = alpha;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        public static Color HsvToRgb(double h, double s, double v)
        {
            h = h % 360.0;
            if (h < 0) h += 360.0;
            s = Math.Max(0.0, Math.Min(1.0, s));
            v = Math.Max(0.0, Math.Min(1.0, v));

            double c = v * s;
            double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
            double m = v - c;

            double r1 = 0, g1 = 0, b1 = 0;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            int r = (int)Math.Round((r1 + m) * 255.0);
            int g = (int)Math.Round((g1 + m) * 255.0);
            int b = (int)Math.Round((b1 + m) * 255.0);
            return Color.FromArgb(255, Math.Max(0, Math.Min(255, r)), Math.Max(0, Math.Min(255, g)), Math.Max(0, Math.Min(255, b)));
        }

        public static void RgbToHsv(Color col, out double h, out double s, out double v)
        {
            double r = col.R / 255.0;
            double g = col.G / 255.0;
            double b = col.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            v = max;
            s = max <= 0.00001 ? 0.0 : delta / max;

            if (delta <= 0.00001)
            {
                h = 0.0;
                return;
            }

            if (Math.Abs(max - r) < 1e-6)
            {
                h = 60.0 * (((g - b) / delta) % 6.0);
            }
            else if (Math.Abs(max - g) < 1e-6)
            {
                h = 60.0 * (((b - r) / delta) + 2.0);
            }
            else
            {
                h = 60.0 * (((r - g) / delta) + 4.0);
            }
            if (h < 0) h += 360.0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_wheelCache != null) { _wheelCache.Dispose(); _wheelCache = null; }
                if (_barCache != null) { _barCache.Dispose(); _barCache = null; }
            }
            base.Dispose(disposing);
        }
    }
}
