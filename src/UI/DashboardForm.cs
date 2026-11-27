using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NDConnect.Models;

namespace NDConnect.UI
{
    public sealed class DashboardForm : Form
    {
        private readonly Action _onRefresh;
        private readonly Action _onOpenDiag;
        private readonly Action _onOpenColor;
        private readonly float _dpiScale = 1.0f;
        private List<DeviceBatteryInfo> _devices = new List<DeviceBatteryInfo>();
        private double _breathPhase;
        private int _ringColorMode;
        private CustomColorPalette _customPalette;

        private readonly Panel _header;
        private readonly Label _titleLabel;
        private readonly Button _colorBtn;
        private readonly Button _refreshBtn;
        private readonly Button _diagBtn;
        private readonly Panel _canvasPanel;

        public DashboardForm(Action onRefresh, Action onOpenDiag, Action onOpenColor)
        {
            _onRefresh = onRefresh;
            _onOpenDiag = onOpenDiag;
            _onOpenColor = onOpenColor;

            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics())
            {
                _dpiScale = Math.Max(1.0f, g.DpiX / 96.0f);
            }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 27);
            ForeColor = Color.FromArgb(244, 244, 245);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            Padding = new Padding(1);
            Width = S(470);
            Height = S(300);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _header = new Panel();
            _header.Dock = DockStyle.Top;
            _header.Width = Width - Padding.Horizontal;
            _header.Height = S(52);
            _header.BackColor = Color.FromArgb(30, 30, 34);
            _header.Padding = new Padding(S(14), S(8), S(14), S(8));

            _titleLabel = new Label();
            _titleLabel.Text = "NDConnect 设备电量";
            _titleLabel.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            _titleLabel.ForeColor = Color.FromArgb(244, 244, 245);
            _titleLabel.AutoSize = true;

            _colorBtn = CreateHeaderButton("配色");
            _colorBtn.Click += delegate
            {
                Hide();
                if (_onOpenColor != null) _onOpenColor();
            };

            _refreshBtn = CreateHeaderButton("刷新");
            _refreshBtn.Click += delegate { if (_onRefresh != null) _onRefresh(); };

            _diagBtn = CreateHeaderButton("诊断");
            _diagBtn.Click += delegate
            {
                Hide();
                if (_onOpenDiag != null) _onOpenDiag();
            };

            _header.Controls.Add(_titleLabel);
            _header.Controls.Add(_colorBtn);
            _header.Controls.Add(_refreshBtn);
            _header.Controls.Add(_diagBtn);
            _header.Resize += delegate { LayoutHeaderControls(); };
            LayoutHeaderControls();

            _canvasPanel = new BufferedPanel();
            _canvasPanel.Dock = DockStyle.Fill;
            _canvasPanel.BackColor = Color.FromArgb(24, 24, 27);
            _canvasPanel.AutoScroll = true;
            _canvasPanel.Paint += OnCanvasPaint;
            _canvasPanel.Scroll += delegate { _canvasPanel.Invalidate(); };

            Controls.Add(_canvasPanel);
            Controls.Add(_header);

            Deactivate += delegate { Hide(); };
        }

        private int S(int px)
        {
            return (int)Math.Round(px * _dpiScale);
        }

        private void LayoutHeaderControls()
        {
            if (_header == null) return;
            int w = _header.ClientSize.Width;
            int h = _header.ClientSize.Height;
            int btnW = S(64);
            int btnH = S(30);
            int btnGap = S(8);
            int rightMargin = S(14);
            int btnY = Math.Max(4, (h - btnH) / 2);

            if (_diagBtn != null)
            {
                _diagBtn.Size = new Size(btnW, btnH);
                _diagBtn.Location = new Point(w - rightMargin - btnW, btnY);
            }
            if (_refreshBtn != null && _diagBtn != null)
            {
                _refreshBtn.Size = new Size(btnW, btnH);
                _refreshBtn.Location = new Point(_diagBtn.Left - btnGap - btnW, btnY);
            }
            if (_colorBtn != null && _refreshBtn != null)
            {
                _colorBtn.Size = new Size(btnW, btnH);
                _colorBtn.Location = new Point(_refreshBtn.Left - btnGap - btnW, btnY);
            }
            if (_titleLabel != null)
            {
                _titleLabel.Location = new Point(S(16), Math.Max(4, (h - _titleLabel.PreferredHeight) / 2));
            }
        }

        private Button CreateHeaderButton(string text)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
            btn.FlatAppearance.BorderSize = 1;
            btn.BackColor = Color.FromArgb(39, 39, 42);
            btn.ForeColor = Color.FromArgb(228, 228, 231);
            btn.Font = new Font("Microsoft YaHei UI", 8.8f);
            btn.Size = new Size(S(64), S(30));
            btn.UseCompatibleTextRendering = false;
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        public void UpdateTheme(int ringColorMode, CustomColorPalette palette)
        {
            _ringColorMode = ringColorMode;
            _customPalette = palette;
            _canvasPanel.Invalidate();
        }

        public void UpdateDevices(List<DeviceBatteryInfo> devices, double breathPhase)
        {
            _devices = devices ?? new List<DeviceBatteryInfo>();
            _breathPhase = breathPhase;

            int cardH = S(78);
            int cardGap = S(10);
            int topPad = S(12);
            int bottomPad = S(12);
            int count = _devices.Count;
            int contentHeight = count == 0
                ? S(140)
                : (topPad + count * cardH + Math.Max(0, count - 1) * cardGap + bottomPad);

            int maxFormHeight = Math.Min(S(640), Screen.PrimaryScreen.WorkingArea.Height - S(40));
            int targetHeight = Math.Min(maxFormHeight, _header.Height + contentHeight + Padding.Vertical);
            targetHeight = Math.Max(_header.Height + S(120), targetHeight);

            _canvasPanel.AutoScrollMinSize = new Size(0, contentHeight);
            if (Height != targetHeight)
            {
                Height = targetHeight;
                PositionNearTaskbar();
            }
            _canvasPanel.Invalidate();
        }

        public void UpdateBreathPhase(double breathPhase)
        {
            _breathPhase = breathPhase;
            _canvasPanel.Invalidate();
        }

        public void PositionNearTaskbar()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = S(12);
            int x = wa.Right - Width - margin;
            int y = wa.Bottom - Height - margin;
            if (x < wa.Left + margin) x = wa.Left + margin;
            if (y < wa.Top + margin) y = wa.Top + margin;
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(Color.FromArgb(63, 63, 70), 1f))
            {
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
        }

        private void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_devices.Count == 0)
            {
                using (StringFormat sf = new StringFormat())
                using (Brush b = new SolidBrush(Color.FromArgb(161, 161, 170)))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("暂未检测到支持电量读取的无线设备\n支持 2.4GHz 接收器 / USB 线缆 / Windows 蓝牙",
                        Font, b, new RectangleF(S(16), S(12), _canvasPanel.ClientSize.Width - S(32), _canvasPanel.ClientSize.Height - S(24)), sf);
                }
                return;
            }

            g.TranslateTransform(0, _canvasPanel.AutoScrollPosition.Y);

            int marginX = S(14);
            int y = S(12);
            int cardW = Math.Max(S(240), _canvasPanel.ClientSize.Width - marginX * 2);
            int cardH = S(78);
            int cardGap = S(10);
            int ringSize = S(54);
            int cardCorner = Math.Max(6, S(10));

            foreach (DeviceBatteryInfo dev in _devices)
            {
                Rectangle cardRect = new Rectangle(marginX, y, cardW, cardH);
                using (GraphicsPath path = CreateRoundedRect(cardRect, cardCorner))
                using (Brush bg = new SolidBrush(Color.FromArgb(39, 39, 42)))
                using (Pen border = new Pen(Color.FromArgb(56, 56, 62), 1f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                int ringX = cardRect.X + S(12);
                int ringY = cardRect.Y + (cardRect.Height - ringSize) / 2;
                Rectangle ringBounds = new Rectangle(ringX, ringY, ringSize, ringSize);
                RingIconRenderer.RenderCardRing(g, ringBounds, dev, _breathPhase, _ringColorMode, _customPalette);

                string statusText = dev.IsAsleep
                    ? (dev.BatteryPercent >= 0 ? dev.BatteryPercent + "% 休眠" : "休眠中")
                    : (dev.IsCharging ? "充电中 ⚡" : (dev.IsFull ? "已充满" : dev.BatteryPercent + "%"));
                Color statusColor = dev.IsAsleep
                    ? Color.FromArgb(148, 163, 184)
                    : (dev.IsCharging
                        ? RingIconRenderer.GetChargingBreathColor(Math.PI, false, _ringColorMode, _customPalette)
                        : RingIconRenderer.GetBatteryColor(dev.BatteryPercent, false, _ringColorMode, _customPalette));

                int badgeW;
                using (Font pctFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold))
                using (Brush pctBrush = new SolidBrush(statusColor))
                using (StringFormat rightSf = new StringFormat())
                {
                    rightSf.Alignment = StringAlignment.Far;
                    rightSf.LineAlignment = StringAlignment.Center;
                    rightSf.FormatFlags = StringFormatFlags.NoWrap;

                    SizeF statusSize = g.MeasureString(statusText, pctFont, S(300), rightSf);
                    badgeW = Math.Max(S(96), (int)Math.Ceiling(statusSize.Width) + S(8));
                    int badgeLeft = cardRect.Right - S(14) - badgeW;
                    RectangleF badgeRect = new RectangleF(badgeLeft, cardRect.Y + S(13), badgeW, S(26));
                    g.DrawString(statusText, pctFont, pctBrush, badgeRect, rightSf);
                }

                int textLeft = ringX + ringSize + S(14);
                int nameMaxW = Math.Max(S(80), (cardRect.Right - S(14) - badgeW) - textLeft - S(8));
                int subMaxW = Math.Max(S(80), cardRect.Right - S(14) - textLeft);

                using (Font nameFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (Font subFont = new Font("Microsoft YaHei UI", 8.2f))
                using (Brush nameBrush = new SolidBrush(Color.FromArgb(244, 244, 245)))
                using (Brush subBrush = new SolidBrush(Color.FromArgb(161, 161, 170)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    sf.LineAlignment = StringAlignment.Center;

                    RectangleF nameRect = new RectangleF(textLeft, cardRect.Y + S(13), nameMaxW, S(26));
                    g.DrawString(dev.Name, nameFont, nameBrush, nameRect, sf);

                    string sub = string.Format("{0} · {1} ({2})", dev.DisplayCategoryName(), dev.DisplayChannelName(), dev.ProtocolName);
                    RectangleF subRect = new RectangleF(textLeft, cardRect.Y + S(43), subMaxW, S(24));
                    g.DrawString(sub, subFont, subBrush, subRect, sf);
                }

                y += cardH + cardGap;
            }
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
            }
        }
    }
}
