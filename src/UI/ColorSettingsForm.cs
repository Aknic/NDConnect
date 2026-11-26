using System;
using System.Drawing;
using System.Windows.Forms;
using NDConnect.Models;

namespace NDConnect.UI
{
    public sealed class ColorSettingsForm : Form
    {
        private readonly CustomColorPalette _working;
        private readonly Action<CustomColorPalette> _onApply;

        private readonly ColorWheelControl _wheel;
        private readonly Panel _swatchPanel;
        private readonly TextBox _hexBox;
        private readonly Label _rgbLabel;
        private readonly CheckBox _autoGlyphCheck;
        private readonly Button[] _slotButtons = new Button[5];
        private readonly Panel[] _slotChips = new Panel[5];
        private readonly Panel _previewPanel;
        private readonly Timer _previewBreathTimer;

        private int _selectedSlot = 0; // 0=High, 1=Mid, 2=Low, 3=Charging, 4=Glyph
        private bool _suppressWheelEvent;
        private bool _previewLightTaskbar;
        private double _previewPhase;

        public ColorSettingsForm(CustomColorPalette initial, Action<CustomColorPalette> onApply)
        {
            _working = initial != null ? initial.Clone() : new CustomColorPalette();
            _onApply = onApply;

            Text = "NDConnect - 自定义配色 (色轮)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 445);
            BackColor = Color.FromArgb(24, 24, 27);
            ForeColor = Color.FromArgb(244, 244, 245);
            Font = new Font("Microsoft YaHei UI", 9.0f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Left: HSV Color Wheel + Hex/RGB Editor
            Label wheelTitle = new Label();
            wheelTitle.Text = "色轮与明度调节";
            wheelTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            wheelTitle.Location = new Point(18, 14);
            wheelTitle.AutoSize = true;
            Controls.Add(wheelTitle);

            _wheel = new ColorWheelControl();
            _wheel.Location = new Point(16, 40);
            _wheel.Size = new Size(264, 224);
            _wheel.ColorChanged += OnWheelColorChanged;
            Controls.Add(_wheel);

            _swatchPanel = new Panel();
            _swatchPanel.Location = new Point(20, 276);
            _swatchPanel.Size = new Size(46, 36);
            _swatchPanel.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_swatchPanel);

            Label hexLbl = new Label();
            hexLbl.Text = "HEX";
            hexLbl.ForeColor = Color.FromArgb(161, 161, 170);
            hexLbl.Location = new Point(76, 274);
            hexLbl.AutoSize = true;
            Controls.Add(hexLbl);

            _hexBox = new TextBox();
            _hexBox.Location = new Point(76, 292);
            _hexBox.Size = new Size(92, 24);
            _hexBox.BackColor = Color.FromArgb(39, 39, 42);
            _hexBox.ForeColor = Color.White;
            _hexBox.BorderStyle = BorderStyle.FixedSingle;
            _hexBox.Font = new Font("Consolas", 9.5f);
            _hexBox.MaxLength = 7;
            _hexBox.TextChanged += OnHexBoxChanged;
            Controls.Add(_hexBox);

            _rgbLabel = new Label();
            _rgbLabel.Location = new Point(178, 295);
            _rgbLabel.Size = new Size(105, 20);
            _rgbLabel.ForeColor = Color.FromArgb(212, 212, 216);
            _rgbLabel.Font = new Font("Consolas", 8.8f);
            Controls.Add(_rgbLabel);

            // Presets row
            Label presetLbl = new Label();
            presetLbl.Text = "快速灵感预设";
            presetLbl.ForeColor = Color.FromArgb(161, 161, 170);
            presetLbl.Location = new Point(18, 330);
            presetLbl.AutoSize = true;
            Controls.Add(presetLbl);

            AddPresetButton("默认红绿", 18, 352, "#22C55E", "#FFCC00", "#FF3B30", "#4AF58A", "#F4F5F8");
            AddPresetButton("赛博霓虹", 86, 352, "#00F5D4", "#9B5DE5", "#F15BB5", "#00BBF9", "#FFFFFF");
            AddPresetButton("冰蓝极简", 154, 352, "#38BDF8", "#818CF8", "#FB7185", "#34D399", "#E2E8F0");
            AddPresetButton("暖阳橙金", 222, 352, "#FACC15", "#FB923C", "#EF4444", "#4ADE80", "#FEF3C7");

            // Right: Color target slots
            Label slotTitle = new Label();
            slotTitle.Text = "选择要修改的配色部位";
            slotTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            slotTitle.Location = new Point(304, 14);
            slotTitle.AutoSize = true;
            Controls.Add(slotTitle);

            string[] slotNames = new string[]
            {
                "满电环色 (100%)",
                "中电环色 (50%)",
                "低电环色 (0%)",
                "充电呼吸灯色",
                "中心设备图标色"
            };
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                Button btn = new Button();
                btn.Text = "      " + slotNames[i];
                btn.TextAlign = ContentAlignment.MiddleLeft;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
                btn.BackColor = Color.FromArgb(39, 39, 42);
                btn.ForeColor = Color.FromArgb(244, 244, 245);
                btn.Location = new Point(304, 42 + i * 36);
                btn.Size = new Size(316, 30);
                btn.Cursor = Cursors.Hand;
                btn.Click += delegate { SelectSlot(idx); };

                Panel chip = new Panel();
                chip.Size = new Size(16, 16);
                chip.Location = new Point(8, 7);
                chip.BorderStyle = BorderStyle.FixedSingle;
                chip.Enabled = false;
                btn.Controls.Add(chip);

                _slotButtons[i] = btn;
                _slotChips[i] = chip;
                Controls.Add(btn);
            }

            _autoGlyphCheck = new CheckBox();
            _autoGlyphCheck.Text = "中心图标自动跟随系统暗/亮任务栏";
            _autoGlyphCheck.Location = new Point(306, 226);
            _autoGlyphCheck.AutoSize = true;
            _autoGlyphCheck.Checked = _working.UseAutoGlyphColor;
            _autoGlyphCheck.CheckedChanged += delegate
            {
                _working.UseAutoGlyphColor = _autoGlyphCheck.Checked;
                _previewPanel.Invalidate();
            };
            Controls.Add(_autoGlyphCheck);

            // Live Taskbar Preview strip
            Label prevLbl = new Label();
            prevLbl.Text = "实时效果预览（100% / 50% / 15% / 充电 / 休眠）";
            prevLbl.ForeColor = Color.FromArgb(161, 161, 170);
            prevLbl.Location = new Point(304, 256);
            prevLbl.AutoSize = true;
            Controls.Add(prevLbl);

            Button toggleBgBtn = new Button();
            toggleBgBtn.Text = "切换预览底色";
            toggleBgBtn.FlatStyle = FlatStyle.Flat;
            toggleBgBtn.FlatAppearance.BorderColor = Color.FromArgb(72, 72, 80);
            toggleBgBtn.BackColor = Color.FromArgb(39, 39, 42);
            toggleBgBtn.ForeColor = Color.FromArgb(212, 212, 216);
            toggleBgBtn.Font = new Font("Microsoft YaHei UI", 8.0f);
            toggleBgBtn.Location = new Point(536, 252);
            toggleBgBtn.Size = new Size(84, 22);
            toggleBgBtn.Click += delegate
            {
                _previewLightTaskbar = !_previewLightTaskbar;
                _previewPanel.Invalidate();
            };
            Controls.Add(toggleBgBtn);

            _previewPanel = new BufferedPreviewPanel();
            _previewPanel.Location = new Point(304, 280);
            _previewPanel.Size = new Size(316, 98);
            _previewPanel.Paint += OnPreviewPaint;
            Controls.Add(_previewPanel);

            // Bottom action buttons
            Button resetBtn = CreateActionBtn("恢复默认", 18, 398, 92, Color.FromArgb(52, 52, 58));
            resetBtn.Click += delegate
            {
                _working.ResetDefaults();
                _autoGlyphCheck.Checked = _working.UseAutoGlyphColor;
                RefreshSlotChips();
                LoadSelectedSlotIntoWheel();
                _previewPanel.Invalidate();
            };

            Button cancelBtn = CreateActionBtn("取消", 432, 398, 86, Color.FromArgb(52, 52, 58));
            cancelBtn.Click += delegate { Close(); };

            Button applyBtn = CreateActionBtn("应用并保存", 526, 398, 94, Color.FromArgb(22, 163, 74));
            applyBtn.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold);
            applyBtn.Click += delegate
            {
                if (_onApply != null) _onApply(_working.Clone());
                Close();
            };

            Controls.Add(resetBtn);
            Controls.Add(cancelBtn);
            Controls.Add(applyBtn);

            _previewBreathTimer = new Timer();
            _previewBreathTimer.Interval = 100;
            _previewBreathTimer.Tick += delegate
            {
                _previewPhase += 0.35;
                if (_previewPhase > Math.PI * 2.0) _previewPhase -= Math.PI * 2.0;
                _previewPanel.Invalidate();
            };
            _previewBreathTimer.Start();

            RefreshSlotChips();
            SelectSlot(0);
        }

        private void AddPresetButton(string label, int x, int y, string high, string mid, string low, string chg, string glyph)
        {
            Button b = new Button();
            b.Text = label;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
            b.BackColor = Color.FromArgb(39, 39, 42);
            b.ForeColor = Color.FromArgb(228, 228, 231);
            b.Font = new Font("Microsoft YaHei UI", 8.0f);
            b.Location = new Point(x, y);
            b.Size = new Size(64, 26);
            b.Cursor = Cursors.Hand;
            b.Click += delegate
            {
                _working.HighColorHex = high;
                _working.MidColorHex = mid;
                _working.LowColorHex = low;
                _working.ChargingColorHex = chg;
                _working.GlyphColorHex = glyph;
                RefreshSlotChips();
                LoadSelectedSlotIntoWheel();
                _previewPanel.Invalidate();
            };
            Controls.Add(b);
        }

        private static Button CreateActionBtn(string text, int x, int y, int w, Color bg)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(w, 30);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(72, 72, 80);
            btn.BackColor = bg;
            btn.ForeColor = Color.White;
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private void SelectSlot(int idx)
        {
            _selectedSlot = idx;
            for (int i = 0; i < _slotButtons.Length; i++)
            {
                _slotButtons[i].BackColor = (i == idx) ? Color.FromArgb(55, 65, 81) : Color.FromArgb(39, 39, 42);
                _slotButtons[i].FlatAppearance.BorderColor = (i == idx)
                    ? Color.FromArgb(96, 165, 250)
                    : Color.FromArgb(63, 63, 70);
            }
            LoadSelectedSlotIntoWheel();
        }

        private string GetSlotHex(int idx)
        {
            switch (idx)
            {
                case 0: return _working.HighColorHex;
                case 1: return _working.MidColorHex;
                case 2: return _working.LowColorHex;
                case 3: return _working.ChargingColorHex;
                default: return _working.GlyphColorHex;
            }
        }

        private void SetSlotHex(int idx, string hex)
        {
            switch (idx)
            {
                case 0: _working.HighColorHex = hex; break;
                case 1: _working.MidColorHex = hex; break;
                case 2: _working.LowColorHex = hex; break;
                case 3: _working.ChargingColorHex = hex; break;
                default: _working.GlyphColorHex = hex; break;
            }
        }

        private void LoadSelectedSlotIntoWheel()
        {
            Color c = CustomColorPalette.ParseHex(GetSlotHex(_selectedSlot), Color.White);
            _suppressWheelEvent = true;
            _wheel.SelectedColor = c;
            _swatchPanel.BackColor = c;
            _hexBox.Text = CustomColorPalette.ToHex(c);
            _rgbLabel.Text = string.Format("R:{0} G:{1} B:{2}", c.R, c.G, c.B);
            _suppressWheelEvent = false;
        }

        private void RefreshSlotChips()
        {
            for (int i = 0; i < 5; i++)
            {
                _slotChips[i].BackColor = CustomColorPalette.ParseHex(GetSlotHex(i), Color.White);
            }
        }

        private void OnWheelColorChanged(object sender, EventArgs e)
        {
            if (_suppressWheelEvent) return;
            Color c = _wheel.SelectedColor;
            string hex = CustomColorPalette.ToHex(c);
            SetSlotHex(_selectedSlot, hex);
            if (_selectedSlot == 4 && _autoGlyphCheck.Checked)
            {
                _autoGlyphCheck.Checked = false;
                _working.UseAutoGlyphColor = false;
            }

            _suppressWheelEvent = true;
            _swatchPanel.BackColor = c;
            _hexBox.Text = hex;
            _rgbLabel.Text = string.Format("R:{0} G:{1} B:{2}", c.R, c.G, c.B);
            _suppressWheelEvent = false;

            RefreshSlotChips();
            _previewPanel.Invalidate();
        }

        private void OnHexBoxChanged(object sender, EventArgs e)
        {
            if (_suppressWheelEvent) return;
            string s = (_hexBox.Text ?? string.Empty).Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            if (s.Length != 7) return;
            Color parsed = CustomColorPalette.ParseHex(s, Color.Empty);
            if (parsed.IsEmpty) return;

            SetSlotHex(_selectedSlot, s.ToUpperInvariant());
            _suppressWheelEvent = true;
            _wheel.SelectedColor = parsed;
            _swatchPanel.BackColor = parsed;
            _rgbLabel.Text = string.Format("R:{0} G:{1} B:{2}", parsed.R, parsed.G, parsed.B);
            _suppressWheelEvent = false;
            RefreshSlotChips();
            _previewPanel.Invalidate();
        }

        private void OnPreviewPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = _previewLightTaskbar ? Color.FromArgb(240, 242, 246) : Color.FromArgb(30, 32, 36);
            g.Clear(bg);
            using (Pen border = new Pen(Color.FromArgb(63, 63, 70)))
            {
                g.DrawRectangle(border, 0, 0, _previewPanel.Width - 1, _previewPanel.Height - 1);
            }

            DeviceGlyphType[] glyphs = new DeviceGlyphType[]
            {
                DeviceGlyphType.Mouse,
                DeviceGlyphType.Keyboard,
                DeviceGlyphType.Headset,
                DeviceGlyphType.GamepadGeneric,
                DeviceGlyphType.Mouse
            };
            int[] pcts = new int[] { 100, 50, 15, 70, 80 };
            bool[] chgs = new bool[] { false, false, false, true, false };
            bool[] asleeps = new bool[] { false, false, false, false, true };

            for (int i = 0; i < 5; i++)
            {
                DeviceBatteryInfo d = new DeviceBatteryInfo();
                d.GlyphType = glyphs[i];
                d.BatteryPercent = pcts[i];
                d.IsCharging = chgs[i];
                d.IsAsleep = asleeps[i];

                int x = 14 + i * 60;
                using (Bitmap big = RingIconRenderer.RenderDeviceBitmap(d, _previewPhase, false, 44, _previewLightTaskbar, 2, _working))
                {
                    g.DrawImage(big, x, 12);
                }
                using (Bitmap sm = RingIconRenderer.RenderDeviceBitmap(d, _previewPhase, false, 20, _previewLightTaskbar, 2, _working))
                {
                    g.DrawImage(sm, x + 12, 66);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_previewBreathTimer != null)
            {
                _previewBreathTimer.Stop();
                _previewBreathTimer.Dispose();
            }
            base.OnFormClosed(e);
        }

        private sealed class BufferedPreviewPanel : Panel
        {
            public BufferedPreviewPanel()
            {
                DoubleBuffered = true;
            }
        }
    }
}
