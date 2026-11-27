using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.UI
{
    public sealed class RenderedIcon : IDisposable
    {
        public Icon Icon { get; private set; }
        public IntPtr HIcon { get; private set; }

        public RenderedIcon(Icon icon, IntPtr hIcon)
        {
            Icon = icon;
            HIcon = hIcon;
        }

        public void Dispose()
        {
            if (Icon != null)
            {
                Icon.Dispose();
                Icon = null;
            }
            if (HIcon != IntPtr.Zero)
            {
                User32Native.DestroyIcon(HIcon);
                HIcon = IntPtr.Zero;
            }
        }
    }

    public static class RingIconRenderer
    {
        private static readonly object GlyphCacheLock = new object();
        private static readonly Dictionary<DeviceGlyphType, Bitmap> EmbeddedGlyphCache = new Dictionary<DeviceGlyphType, Bitmap>();

        // Smooth battery color stops: 0% Red -> 25% Orange-Red -> 50% Yellow -> 75% Yellow-Green -> 100% Green
        public static Color GetBatteryColor(int percent)
        {
            return GetBatteryColor(percent, false, 0);
        }

        public static Color GetBatteryColor(int percent, bool isLightTaskbar, int ringColorMode)
        {
            return GetBatteryColor(percent, isLightTaskbar, ringColorMode, null);
        }

        public static Color GetBatteryColor(int percent, bool isLightTaskbar, int ringColorMode, CustomColorPalette palette)
        {
            int p = Math.Max(0, Math.Min(100, percent));
            if (ringColorMode == 2 && palette != null)
            {
                Color low = CustomColorPalette.ParseHex(palette.LowColorHex, Color.FromArgb(255, 59, 48));
                Color mid = CustomColorPalette.ParseHex(palette.MidColorHex, Color.FromArgb(255, 204, 0));
                Color high = CustomColorPalette.ParseHex(palette.HighColorHex, Color.FromArgb(34, 197, 94));
                if (p <= 50) return LerpColor(low, mid, p / 50.0);
                return LerpColor(mid, high, (p - 50) / 50.0);
            }
            if (ringColorMode == 1)
            {
                // Reference-sheet minimalist palette: >35% theme fg, 21..35% amber, <=20% red
                if (p <= 20)
                {
                    return isLightTaskbar ? Color.FromArgb(220, 38, 38) : Color.FromArgb(239, 68, 68);
                }
                if (p <= 35)
                {
                    return isLightTaskbar ? Color.FromArgb(217, 130, 11) : Color.FromArgb(245, 166, 35);
                }
                return isLightTaskbar ? Color.FromArgb(28, 30, 33) : Color.FromArgb(242, 242, 245);
            }

            if (isLightTaskbar)
            {
                // Slightly deeper saturation for crisp contrast on white/light Windows taskbar
                if (p <= 25) return LerpColor(Color.FromArgb(220, 38, 38), Color.FromArgb(234, 88, 12), p / 25.0);
                if (p <= 50) return LerpColor(Color.FromArgb(234, 88, 12), Color.FromArgb(217, 150, 0), (p - 25) / 25.0);
                if (p <= 75) return LerpColor(Color.FromArgb(217, 150, 0), Color.FromArgb(101, 163, 13), (p - 50) / 25.0);
                return LerpColor(Color.FromArgb(101, 163, 13), Color.FromArgb(22, 163, 74), (p - 75) / 25.0);
            }

            if (p <= 25) return LerpColor(Color.FromArgb(255, 59, 48), Color.FromArgb(255, 115, 36), p / 25.0);
            if (p <= 50) return LerpColor(Color.FromArgb(255, 115, 36), Color.FromArgb(255, 204, 0), (p - 25) / 25.0);
            if (p <= 75) return LerpColor(Color.FromArgb(255, 204, 0), Color.FromArgb(140, 217, 38), (p - 50) / 25.0);
            return LerpColor(Color.FromArgb(140, 217, 38), Color.FromArgb(34, 197, 94), (p - 75) / 25.0);
        }

        // Smooth cosine breathing factor 0.0 -> 1.0 -> 0.0
        public static double GetBreathPulse(double phaseRadians)
        {
            return 0.5 - 0.5 * Math.Cos(phaseRadians);
        }

        public static Color GetChargingBreathColor(double phaseRadians)
        {
            return GetChargingBreathColor(phaseRadians, false, 0, null);
        }

        public static Color GetChargingBreathColor(double phaseRadians, bool isLightTaskbar)
        {
            return GetChargingBreathColor(phaseRadians, isLightTaskbar, 0, null);
        }

        public static Color GetChargingBreathColor(double phaseRadians, bool isLightTaskbar, int ringColorMode, CustomColorPalette palette)
        {
            double pulse = GetBreathPulse(phaseRadians);
            if (ringColorMode == 2 && palette != null)
            {
                Color peak = CustomColorPalette.ParseHex(palette.ChargingColorHex, Color.FromArgb(74, 245, 138));
                Color low = Color.FromArgb(255, (int)(peak.R * 0.52), (int)(peak.G * 0.52), (int)(peak.B * 0.52));
                return LerpColor(low, peak, pulse);
            }
            return isLightTaskbar
                ? LerpColor(Color.FromArgb(16, 135, 62), Color.FromArgb(34, 205, 96), pulse)
                : LerpColor(Color.FromArgb(22, 148, 72), Color.FromArgb(74, 245, 138), pulse);
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, bl);
        }

        public static int GetPreferredTrayIconSize()
        {
            return User32Native.GetTrayIconPixelSize(SystemInformation.SmallIconSize.Width);
        }

        public static bool ResolveIsLightTaskbar(int iconThemeMode)
        {
            if (iconThemeMode == 1) return false;
            if (iconThemeMode == 2) return true;
            return User32Native.IsSystemLightTaskbar();
        }

        public static RenderedIcon RenderTrayIcon(DeviceBatteryInfo dev, double breathPhase, bool showPercentageText, int targetSize)
        {
            return RenderTrayIcon(dev, breathPhase, showPercentageText, targetSize, false, 0);
        }

        public static RenderedIcon RenderTrayIcon(
            DeviceBatteryInfo dev,
            double breathPhase,
            bool showPercentageText,
            int targetSize,
            bool isLightTaskbar,
            int ringColorMode)
        {
            return RenderTrayIcon(dev, breathPhase, showPercentageText, targetSize, isLightTaskbar, ringColorMode, null);
        }

        public static RenderedIcon RenderTrayIcon(
            DeviceBatteryInfo dev,
            double breathPhase,
            bool showPercentageText,
            int targetSize,
            bool isLightTaskbar,
            int ringColorMode,
            CustomColorPalette palette)
        {
            using (Bitmap bmp = RenderDeviceBitmap(dev, breathPhase, showPercentageText, targetSize, isLightTaskbar, ringColorMode, palette))
            {
                IntPtr hIcon = User32Native.CreateTransparentIcon(bmp);
                if (hIcon == IntPtr.Zero) hIcon = bmp.GetHicon();
                Icon ic = Icon.FromHandle(hIcon);
                return new RenderedIcon(ic, hIcon);
            }
        }

        public static RenderedIcon RenderStandbyIcon(int targetSize, bool isLightTaskbar)
        {
            int size = Math.Max(16, targetSize);
            int ss = size * 2;
            using (Bitmap hi = new Bitmap(ss, ss, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(hi))
                {
                    ConfigureHighQuality(g);
                    g.Clear(Color.Transparent);

                    DeviceBatteryInfo dummy = new DeviceBatteryInfo();
                    dummy.GlyphType = DeviceGlyphType.BluetoothGeneric;
                    dummy.IsAsleep = true;
                    dummy.BatteryPercent = 100;
                    DrawRingInternal(g, new RectangleF(0, 0, ss, ss), dummy, 0.0, false, true, isLightTaskbar, 1, null);
                }
                using (Bitmap finalBmp = Downsample(hi, size))
                {
                    IntPtr hIcon = User32Native.CreateTransparentIcon(finalBmp);
                    if (hIcon == IntPtr.Zero) hIcon = finalBmp.GetHicon();
                    return new RenderedIcon(Icon.FromHandle(hIcon), hIcon);
                }
            }
        }

        public static Bitmap RenderDeviceBitmap(
            DeviceBatteryInfo dev,
            double breathPhase,
            bool showPercentageText,
            int targetSize)
        {
            return RenderDeviceBitmap(dev, breathPhase, showPercentageText, targetSize, false, 0);
        }

        public static Bitmap RenderDeviceBitmap(
            DeviceBatteryInfo dev,
            double breathPhase,
            bool showPercentageText,
            int targetSize,
            bool isLightTaskbar,
            int ringColorMode)
        {
            return RenderDeviceBitmap(dev, breathPhase, showPercentageText, targetSize, isLightTaskbar, ringColorMode, null);
        }

        public static Bitmap RenderDeviceBitmap(
            DeviceBatteryInfo dev,
            double breathPhase,
            bool showPercentageText,
            int targetSize,
            bool isLightTaskbar,
            int ringColorMode,
            CustomColorPalette palette)
        {
            int size = Math.Max(16, targetSize);
            int ss = size * 2; // 2x supersampling for crisp edges
            using (Bitmap hi = new Bitmap(ss, ss, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(hi))
                {
                    ConfigureHighQuality(g);
                    g.Clear(Color.Transparent);
                    DrawRingInternal(g, new RectangleF(0, 0, ss, ss), dev, breathPhase, showPercentageText, true, isLightTaskbar, ringColorMode, palette);
                }
                return Downsample(hi, size);
            }
        }

        public static void RenderCardRing(Graphics g, Rectangle bounds, DeviceBatteryInfo dev, double breathPhase, int ringColorMode)
        {
            RenderCardRing(g, bounds, dev, breathPhase, ringColorMode, null);
        }

        public static void RenderCardRing(Graphics g, Rectangle bounds, DeviceBatteryInfo dev, double breathPhase, int ringColorMode, CustomColorPalette palette)
        {
            ConfigureHighQuality(g);
            // Dashboard cards always sit on a dark zinc background (#27272A)
            DrawRingInternal(g, new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), dev, breathPhase, true, false, false, ringColorMode, palette);
        }

        private static void DrawRingInternal(
            Graphics g,
            RectangleF bounds,
            DeviceBatteryInfo dev,
            double breathPhase,
            bool showPercentageText,
            bool isTrayIcon,
            bool isLightTaskbar,
            int ringColorMode,
            CustomColorPalette palette)
        {
            float dim = Math.Min(bounds.Width, bounds.Height);
            float pad = dim * (isTrayIcon ? 0.056f : 0.095f);
            float stroke = Math.Max(2.2f, dim * (isTrayIcon ? 0.095f : 0.092f));

            RectangleF ringRect = new RectangleF(
                bounds.X + pad,
                bounds.Y + pad,
                dim - pad * 2f,
                dim - pad * 2f);

            bool asleep = dev != null && dev.IsAsleep;
            bool charging = dev != null && dev.IsCharging && !asleep;
            int pct = dev != null && dev.BatteryPercent >= 0 ? Math.Max(0, Math.Min(100, dev.BatteryPercent)) : 0;
            double pulse = charging ? GetBreathPulse(breathPhase) : 0.0;

            Color fgBase = isLightTaskbar ? Color.FromArgb(245, 26, 28, 32) : Color.FromArgb(245, 244, 245, 248);
            if (ringColorMode == 2 && palette != null && !palette.UseAutoGlyphColor)
            {
                fgBase = CustomColorPalette.ParseHex(palette.GlyphColorHex, fgBase);
            }

            if (asleep)
            {
                // Asleep state: draw subtle arc for known % (or full ring if unknown) with transparent interior and no black/gray fill
                Color asleepRing = isLightTaskbar ? Color.FromArgb(95, 70, 75, 85) : Color.FromArgb(100, 195, 200, 210);
                int asleepPct = (dev != null && dev.BatteryPercent > 0) ? Math.Min(100, dev.BatteryPercent) : 100;
                float asleepSweep = (asleepPct / 100.0f) * 360.0f;
                using (Pen asleepPen = new Pen(asleepRing, stroke))
                {
                    if (asleepPct < 100)
                    {
                        asleepPen.StartCap = LineCap.Round;
                        asleepPen.EndCap = LineCap.Round;
                    }
                    g.DrawArc(asleepPen, ringRect, -90.0f, asleepSweep);
                }
                Color asleepFg = Color.FromArgb(120, fgBase.R, fgBase.G, fgBase.B);
                DrawCenterVisual(g, bounds, dim, pad, stroke, dev, showPercentageText, isTrayIcon, asleepFg, pct);
                return;
            }

            // No unfilled gray track ring: keep the unfilled arc and center 100% transparent without gray fill or dark outline.
            Color arcColor = charging
                ? GetChargingBreathColor(breathPhase, isLightTaskbar, ringColorMode, palette)
                : GetBatteryColor(pct, isLightTaskbar, ringColorMode, palette);

            // Subtle outer breathing halo while charging (constrained inside bounds)
            if (charging)
            {
                int glowAlpha = (int)Math.Round(28 + 78 * pulse);
                float glowPad = pad;
                RectangleF glowRect = new RectangleF(
                    bounds.X + glowPad,
                    bounds.Y + glowPad,
                    dim - glowPad * 2f,
                    dim - glowPad * 2f);
                Color haloBase = (ringColorMode == 2 && palette != null)
                    ? CustomColorPalette.ParseHex(palette.ChargingColorHex, Color.FromArgb(48, 228, 118))
                    : Color.FromArgb(48, 228, 118);
                using (Pen glowPen = new Pen(Color.FromArgb(glowAlpha, haloBase.R, haloBase.G, haloBase.B), stroke * 1.16f))
                {
                    if (pct > 0 && pct < 100)
                    {
                        glowPen.StartCap = LineCap.Round;
                        glowPen.EndCap = LineCap.Round;
                        g.DrawArc(glowPen, glowRect, -90.0f, (pct / 100.0f) * 360.0f);
                    }
                    else
                    {
                        g.DrawEllipse(glowPen, glowRect);
                    }
                }
            }

            // Battery progress arc clockwise from 12 o'clock (-90 deg)
            if (pct > 0)
            {
                float sweep = (pct / 100.0f) * 360.0f;
                using (Pen arcPen = new Pen(arcColor, stroke))
                {
                    if (pct < 100)
                    {
                        arcPen.StartCap = LineCap.Round;
                        arcPen.EndCap = LineCap.Round;
                    }
                    g.DrawArc(arcPen, ringRect, -90.0f, sweep);
                }
            }

            DrawCenterVisual(g, bounds, dim, pad, stroke, dev, showPercentageText, isTrayIcon, fgBase, pct);
        }

        private static void DrawCenterVisual(
            Graphics g,
            RectangleF bounds,
            float dim,
            float pad,
            float stroke,
            DeviceBatteryInfo dev,
            bool showPercentageText,
            bool isTrayIcon,
            Color fgColor,
            int pct)
        {
            float cx = bounds.X + dim * 0.5f;
            float cy = bounds.Y + dim * 0.5f;
            float innerRadius = (dim - pad * 2f - stroke) * 0.5f;

            if (showPercentageText)
            {
                string txt = (dev != null && dev.IsAsleep && dev.BatteryPercent < 0)
                    ? "--"
                    : (pct >= 100 ? "100" : pct.ToString());
                float fontPx = innerRadius * (txt.Length >= 3 ? 0.82f : 1.08f);
                using (Font f = new Font("Segoe UI", Math.Max(6f, fontPx), FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    RectangleF textRect = new RectangleF(bounds.X, bounds.Y + dim * 0.02f, dim, dim);
                    using (Brush b = new SolidBrush(fgColor))
                    {
                        g.DrawString(txt, f, b, textRect, sf);
                    }
                }
                return;
            }

            DeviceGlyphType glyph = ResolveGlyphType(dev);
            DrawGlyph(g, cx, cy, innerRadius * 0.79f, glyph, fgColor);
        }

        private static DeviceGlyphType ResolveGlyphType(DeviceBatteryInfo dev)
        {
            if (dev == null) return DeviceGlyphType.BluetoothGeneric;
            if (dev.GlyphType != DeviceGlyphType.BluetoothGeneric) return dev.GlyphType;
            switch (dev.Category)
            {
                case DeviceCategory.Mouse: return DeviceGlyphType.Mouse;
                case DeviceCategory.Keyboard: return DeviceGlyphType.Keyboard;
                case DeviceCategory.Headset: return DeviceGlyphType.Headset;
                case DeviceCategory.Gamepad: return DeviceGlyphType.GamepadGeneric;
                default: return DeviceGlyphType.BluetoothGeneric;
            }
        }

        private static void DrawGlyph(Graphics g, float cx, float cy, float r, DeviceGlyphType glyph, Color color)
        {
            float lw = Math.Max(1.45f, r * 0.175f);
            using (Pen pen = new Pen(color, lw))
            using (Brush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch (glyph)
                {
                    case DeviceGlyphType.Headset:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.58f))
                            {
                                float hr = r * 0.74f;
                                RectangleF arc = new RectangleF(cx - hr, cy - hr * 0.88f, hr * 2f, hr * 1.76f);
                                g.DrawArc(pen, arc, 185f, 170f);
                                float cw = r * 0.30f;
                                float ch = r * 0.62f;
                                float cupY = cy + r * 0.04f;
                                DrawRoundedRect(g, pen, new RectangleF(cx - hr - cw * 0.20f, cupY, cw, ch), cw * 0.45f);
                                DrawRoundedRect(g, pen, new RectangleF(cx + hr - cw * 0.80f, cupY, cw, ch), cw * 0.45f);
                            }
                            break;
                        }
                    case DeviceGlyphType.Mouse:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.66f))
                            {
                                float mw = r * 1.06f;
                                float mh = r * 1.54f;
                                RectangleF mr = new RectangleF(cx - mw * 0.5f, cy - mh * 0.5f, mw, mh);
                                DrawRoundedRect(g, pen, mr, mw * 0.48f);
                                // Top seam & scroll wheel
                                g.DrawLine(pen, cx, mr.Top + mh * 0.04f, cx, mr.Top + mh * 0.24f);
                                float ww = Math.Max(1.6f, mw * 0.18f);
                                float wh = mh * 0.22f;
                                DrawRoundedRect(g, brush, new RectangleF(cx - ww * 0.5f, mr.Top + mh * 0.24f, ww, wh), ww * 0.5f);
                            }
                            break;
                        }
                    case DeviceGlyphType.Keyboard:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.78f))
                            {
                                float kw = r * 1.62f;
                                float kh = r * 1.06f;
                                RectangleF kr = new RectangleF(cx - kw * 0.5f, cy - kh * 0.5f, kw, kh);
                                DrawRoundedRect(g, pen, kr, kh * 0.18f);
                                using (Pen keyPen = new Pen(color, Math.Max(1.1f, lw * 0.85f)))
                                {
                                    keyPen.StartCap = LineCap.Round;
                                    keyPen.EndCap = LineCap.Round;
                                    // Row 1 (4 keys)
                                    float y1 = cy - kh * 0.21f;
                                    for (int i = 0; i < 4; i++)
                                    {
                                        float kx = kr.Left + kw * (0.16f + i * 0.20f);
                                        g.DrawLine(keyPen, kx, y1, kx + kw * 0.08f, y1);
                                    }
                                    // Row 2 (3 keys)
                                    float y2 = cy;
                                    for (int i = 0; i < 3; i++)
                                    {
                                        float kx = kr.Left + kw * (0.20f + i * 0.24f);
                                        g.DrawLine(keyPen, kx, y2, kx + kw * 0.12f, y2);
                                    }
                                    // Spacebar
                                    float y3 = cy + kh * 0.22f;
                                    g.DrawLine(keyPen, cx - kw * 0.24f, y3, cx + kw * 0.24f, y3);
                                }
                            }
                            break;
                        }
                    case DeviceGlyphType.Microphone:
                        {
                            float mw = r * 0.56f;
                            float mh = r * 0.92f;
                            DrawRoundedRect(g, pen, new RectangleF(cx - mw * 0.5f, cy - r * 0.80f, mw, mh), mw * 0.48f);
                            float cr = r * 0.52f;
                            g.DrawArc(pen, new RectangleF(cx - cr, cy - cr * 0.55f, cr * 2f, cr * 1.45f), 0f, 180f);
                            g.DrawLine(pen, cx, cy + cr * 0.90f, cx, cy + r * 0.78f);
                            g.DrawLine(pen, cx - r * 0.34f, cy + r * 0.78f, cx + r * 0.34f, cy + r * 0.78f);
                            break;
                        }
                    case DeviceGlyphType.GamepadGeneric:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.82f))
                            {
                                float gw = r * 1.56f;
                                float gh = r * 0.96f;
                                RectangleF gr = new RectangleF(cx - gw * 0.5f, cy - gh * 0.52f, gw, gh);
                                DrawRoundedRect(g, pen, gr, gh * 0.46f);
                                // Left D-pad cross
                                float dcx = cx - gw * 0.25f;
                                float dcy = cy - gh * 0.04f;
                                float ds = r * 0.20f;
                                g.DrawLine(pen, dcx - ds, dcy, dcx + ds, dcy);
                                g.DrawLine(pen, dcx, dcy - ds, dcx, dcy + ds);
                                // Right ABXY dots
                                float rcx = cx + gw * 0.25f;
                                float dot = Math.Max(1.1f, r * 0.11f);
                                float off = r * 0.16f;
                                g.FillEllipse(brush, rcx - dot, dcy - off - dot, dot * 2f, dot * 2f);
                                g.FillEllipse(brush, rcx - dot, dcy + off - dot, dot * 2f, dot * 2f);
                                g.FillEllipse(brush, rcx - off - dot, dcy - dot, dot * 2f, dot * 2f);
                                g.FillEllipse(brush, rcx + off - dot, dcy - dot, dot * 2f, dot * 2f);
                            }
                            break;
                        }
                    case DeviceGlyphType.GamepadXbox:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.62f))
                            {
                                float xr = r * 0.80f;
                                RectangleF cr = new RectangleF(cx - xr, cy - xr, xr * 2f, xr * 2f);
                                g.DrawEllipse(pen, cr);
                                using (Pen xPen = new Pen(color, Math.Max(1.4f, lw * 1.05f)))
                                {
                                    xPen.StartCap = LineCap.Round;
                                    xPen.EndCap = LineCap.Round;
                                    g.DrawArc(xPen, new RectangleF(cx - xr * 0.75f, cy - xr * 0.65f, xr * 1.50f, xr * 1.35f), 215f, 110f);
                                    g.DrawLine(xPen, cx - xr * 0.54f, cy - xr * 0.52f, cx + xr * 0.54f, cy + xr * 0.52f);
                                    g.DrawLine(xPen, cx + xr * 0.54f, cy - xr * 0.52f, cx - xr * 0.54f, cy + xr * 0.52f);
                                }
                            }
                            break;
                        }
                    case DeviceGlyphType.GamepadPlayStation:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.86f))
                            {
                                // Stylized P stem + S floor curve
                                g.DrawLine(pen, cx - r * 0.14f, cy - r * 0.72f, cx - r * 0.14f, cy + r * 0.62f);
                                g.DrawArc(pen, new RectangleF(cx - r * 0.14f, cy - r * 0.72f, r * 0.62f, r * 0.68f), 270f, 180f);
                                g.DrawArc(pen, new RectangleF(cx - r * 0.72f, cy + r * 0.16f, r * 1.44f, r * 0.46f), 15f, 175f);
                            }
                            break;
                        }
                    case DeviceGlyphType.GamepadNintendo:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, glyph, color, 1.48f))
                            {
                                // Paired Joy-Cons
                                float jw = r * 0.56f;
                                float jh = r * 1.38f;
                                float gap = r * 0.16f;
                                RectangleF leftJc = new RectangleF(cx - gap * 0.5f - jw, cy - jh * 0.5f, jw, jh);
                                RectangleF rightJc = new RectangleF(cx + gap * 0.5f, cy - jh * 0.5f, jw, jh);
                                DrawRoundedRect(g, pen, leftJc, jw * 0.42f);
                                DrawRoundedRect(g, pen, rightJc, jw * 0.42f);
                                float dot = Math.Max(1.3f, r * 0.13f);
                                g.FillEllipse(brush, leftJc.X + jw * 0.5f - dot, leftJc.Y + jh * 0.30f - dot, dot * 2f, dot * 2f);
                                g.FillEllipse(brush, rightJc.X + jw * 0.5f - dot, rightJc.Y + jh * 0.64f - dot, dot * 2f, dot * 2f);
                            }
                            break;
                        }
                    default:
                        {
                            if (!TryDrawEmbeddedGlyph(g, cx, cy, r, DeviceGlyphType.BluetoothGeneric, color, 1.66f))
                            {
                                // Bluetooth Rune
                                float bh = r * 0.78f;
                                float bw = r * 0.44f;
                                PointF[] pts = new PointF[]
                                {
                                    new PointF(cx - bw * 0.65f, cy - bh * 0.36f),
                                    new PointF(cx + bw, cy + bh * 0.36f),
                                    new PointF(cx, cy + bh),
                                    new PointF(cx, cy - bh),
                                    new PointF(cx + bw, cy - bh * 0.36f),
                                    new PointF(cx - bw * 0.65f, cy + bh * 0.36f)
                                };
                                g.DrawLines(pen, pts);
                            }
                            break;
                        }
                }
            }
        }

        public static bool HasEmbeddedGlyph(DeviceGlyphType glyph)
        {
            return GetEmbeddedGlyphBitmap(glyph) != null;
        }

        private static string GetEmbeddedGlyphResourceName(DeviceGlyphType glyph)
        {
            switch (glyph)
            {
                case DeviceGlyphType.GamepadXbox:
                    return "NDConnect.Assets.glyph_xbox.png";
                case DeviceGlyphType.GamepadPlayStation:
                    return "NDConnect.Assets.glyph_playstation.png";
                case DeviceGlyphType.GamepadNintendo:
                    return "NDConnect.Assets.glyph_nintendo.png";
                case DeviceGlyphType.BluetoothGeneric:
                    return "NDConnect.Assets.glyph_bluetooth.png";
                case DeviceGlyphType.Keyboard:
                    return "NDConnect.Assets.glyph_keyboard.png";
                case DeviceGlyphType.Mouse:
                    return "NDConnect.Assets.glyph_mouse.png";
                case DeviceGlyphType.Headset:
                    return "NDConnect.Assets.glyph_headset.png";
                case DeviceGlyphType.GamepadGeneric:
                    return "NDConnect.Assets.glyph_gamepad.png";
                default:
                    return null;
            }
        }

        private static Bitmap GetEmbeddedGlyphBitmap(DeviceGlyphType glyph)
        {
            string resName = GetEmbeddedGlyphResourceName(glyph);
            if (string.IsNullOrEmpty(resName)) return null;

            lock (GlyphCacheLock)
            {
                Bitmap cached;
                if (EmbeddedGlyphCache.TryGetValue(glyph, out cached))
                {
                    return cached;
                }

                Bitmap loaded = null;
                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    using (Stream s = asm.GetManifestResourceStream(resName))
                    {
                        if (s != null)
                        {
                            using (Bitmap tmp = new Bitmap(s))
                            {
                                // Copy into a detached 32bppArgb bitmap so the underlying stream can close safely.
                                Bitmap copy = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
                                using (Graphics cg = Graphics.FromImage(copy))
                                {
                                    cg.DrawImageUnscaled(tmp, 0, 0);
                                }
                                loaded = copy;
                            }
                        }
                    }
                }
                catch
                {
                    loaded = null;
                }

                EmbeddedGlyphCache[glyph] = loaded;
                return loaded;
            }
        }

        private static bool TryDrawEmbeddedGlyph(Graphics g, float cx, float cy, float r, DeviceGlyphType glyph, Color color, float diameterScale)
        {
            Bitmap bmp = GetEmbeddedGlyphBitmap(glyph);
            if (bmp == null) return false;

            float size = Math.Max(4f, r * diameterScale);
            float x = cx - size * 0.5f;
            float y = cy - size * 0.5f;

            float rf = color.R / 255f;
            float gf = color.G / 255f;
            float bf = color.B / 255f;
            float af = color.A / 255f;
            ColorMatrix cm = new ColorMatrix(new float[][]
            {
                new float[] { rf, 0f, 0f, 0f, 0f },
                new float[] { 0f, gf, 0f, 0f, 0f },
                new float[] { 0f, 0f, bf, 0f, 0f },
                new float[] { 0f, 0f, 0f, af, 0f },
                new float[] { 0f, 0f, 0f, 0f, 1f }
            });

            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                ia.SetWrapMode(WrapMode.TileFlipXY);
                PointF[] destPts = new PointF[]
                {
                    new PointF(x, y),
                    new PointF(x + size, y),
                    new PointF(x, y + size)
                };
                lock (GlyphCacheLock)
                {
                    g.DrawImage(bmp, destPts, new RectangleF(0f, 0f, bmp.Width, bmp.Height), GraphicsUnit.Pixel, ia);
                }
            }
            return true;
        }

        private static void DrawRoundedRect(Graphics g, Pen pen, RectangleF rect, float radius)
        {
            using (GraphicsPath path = CreateRoundedPath(rect, radius))
            {
                g.DrawPath(pen, path);
            }
        }

        private static void DrawRoundedRect(Graphics g, Brush brush, RectangleF rect, float radius)
        {
            using (GraphicsPath path = CreateRoundedPath(rect, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private static GraphicsPath CreateRoundedPath(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void ConfigureHighQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // HighQualityBilinear avoids dark negative bicubic ringing around bright strokes on transparent canvases.
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        }

        private static Bitmap Downsample(Bitmap src, int targetSize)
        {
            Bitmap dst = new Bitmap(targetSize, targetSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                ConfigureHighQuality(g);
                g.Clear(Color.Transparent);
                g.DrawImage(src, 0, 0, targetSize, targetSize);
            }
            return dst;
        }
    }
}
