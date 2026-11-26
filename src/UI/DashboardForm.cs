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
        private List<DeviceBatteryInfo> _devices = new List<DeviceBatteryInfo>();
        private double _breathPhase;
        private int _ringColorMode;
        private CustomColorPalette _customPalette;

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

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 27);
            ForeColor = Color.FromArgb(244, 244, 245);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            Width = 370;
            Height = 280;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 46;
            header.BackColor = Color.FromArgb(30, 30, 34);
            header.Padding = new Padding(14, 8, 12, 8);

            _titleLabel = new Label();
            _titleLabel.Text = "NDConnect 设备电量";
            _titleLabel.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            _titleLabel.ForeColor = Color.FromArgb(244, 244, 245);
            _titleLabel.AutoSize = true;
            _titleLabel.Location = new Point(14, 12);

            _colorBtn = CreateHeaderButton("配色", 184);
            _colorBtn.Click += delegate
            {
                Hide();
                if (_onOpenColor != null) _onOpenColor();
            };

            _refreshBtn = CreateHeaderButton("刷新", 244);
            _refreshBtn.Click += delegate { if (_onRefresh != null) _onRefresh(); };

            _diagBtn = CreateHeaderButton("诊断", 304);
            _diagBtn.Click += delegate
            {
                Hide();
                if (_onOpenDiag != null) _onOpenDiag();
            };

            header.Controls.Add(_titleLabel);
            header.Controls.Add(_colorBtn);
            header.Controls.Add(_refreshBtn);
            header.Controls.Add(_diagBtn);

            _canvasPanel = new BufferedPanel();
            _canvasPanel.Dock = DockStyle.Fill;
            _canvasPanel.BackColor = Color.FromArgb(24, 24, 27);
            _canvasPanel.Paint += OnCanvasPaint;

            Controls.Add(_canvasPanel);
            Controls.Add(header);

            Deactivate += delegate { Hide(); };
        }

        private Button CreateHeaderButton(string text, int x)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
            btn.FlatAppearance.BorderSize = 1;
            btn.BackColor = Color.FromArgb(39, 39, 42);
            btn.ForeColor = Color.FromArgb(228, 228, 231);
            btn.Font = new Font("Microsoft YaHei UI", 8.5f);
            btn.Size = new Size(54, 26);
            btn.Location = new Point(x, 10);
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
            int cardHeight = 76;
            int count = Math.Max(1, _devices.Count);
            int targetHeight = Math.Min(560, 46 + count * cardHeight + 18);
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
            int x = wa.Right - Width - 12;
            int y = wa.Bottom - Height - 12;
            if (x < wa.Left) x = wa.Left + 12;
            if (y < wa.Top) y = wa.Top + 12;
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
                        Font, b, new RectangleF(16, 12, _canvasPanel.Width - 32, _canvasPanel.Height - 24), sf);
                }
                return;
            }

            int y = 10;
            int cardW = _canvasPanel.Width - 24;
            int cardH = 66;
            foreach (DeviceBatteryInfo dev in _devices)
            {
                Rectangle cardRect = new Rectangle(12, y, cardW, cardH);
                using (GraphicsPath path = CreateRoundedRect(cardRect, 10))
                using (Brush bg = new SolidBrush(Color.FromArgb(39, 39, 42)))
                using (Pen border = new Pen(Color.FromArgb(56, 56, 62), 1f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                Rectangle ringBounds = new Rectangle(cardRect.X + 10, cardRect.Y + 9, 48, 48);
                RingIconRenderer.RenderCardRing(g, ringBounds, dev, _breathPhase, _ringColorMode, _customPalette);

                using (Font nameFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (Font subFont = new Font("Microsoft YaHei UI", 8.2f))
                using (Brush nameBrush = new SolidBrush(Color.FromArgb(244, 244, 245)))
                using (Brush subBrush = new SolidBrush(Color.FromArgb(161, 161, 170)))
                {
                    RectangleF nameRect = new RectangleF(cardRect.X + 68, cardRect.Y + 11, cardW - 145, 22);
                    StringFormat sf = new StringFormat();
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(dev.Name, nameFont, nameBrush, nameRect, sf);

                    string sub = string.Format("{0} · {1} ({2})", dev.DisplayCategoryName(), dev.DisplayChannelName(), dev.ProtocolName);
                    RectangleF subRect = new RectangleF(cardRect.X + 68, cardRect.Y + 35, cardW - 80, 20);
                    g.DrawString(sub, subFont, subBrush, subRect, sf);
                    sf.Dispose();
                }

                // Right-side status badge
                string statusText = dev.IsAsleep
                    ? (dev.BatteryPercent >= 0 ? dev.BatteryPercent + "% 休眠" : "休眠中")
                    : (dev.IsCharging ? "充电中 ⚡" : (dev.IsFull ? "已充满" : dev.BatteryPercent + "%"));
                Color statusColor = dev.IsAsleep
                    ? Color.FromArgb(148, 163, 184)
                    : (dev.IsCharging
                        ? RingIconRenderer.GetChargingBreathColor(Math.PI, false, _ringColorMode, _customPalette)
                        : RingIconRenderer.GetBatteryColor(dev.BatteryPercent, false, _ringColorMode, _customPalette));
                using (Font pctFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold))
                using (Brush pctBrush = new SolidBrush(statusColor))
                using (StringFormat rightSf = new StringFormat())
                {
                    rightSf.Alignment = StringAlignment.Far;
                    g.DrawString(statusText, pctFont, pctBrush, new RectangleF(cardRect.Right - 82, cardRect.Y + 12, 72, 22), rightSf);
                }

                y += cardH + 10;
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
