using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NDConnect.UI
{
    public sealed class DiagnosticsForm : Form
    {
        private readonly TextBox _logBox;
        private readonly Action _onRefresh;

        public DiagnosticsForm(Action onRefresh)
        {
            _onRefresh = onRefresh;
            Text = "NDConnect - 协议与设备通道诊断";
            Size = new Size(780, 500);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 27);
            ForeColor = Color.FromArgb(244, 244, 245);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 44;
            topBar.BackColor = Color.FromArgb(32, 32, 36);

            Button refreshBtn = new Button();
            refreshBtn.Text = "重新扫描设备";
            refreshBtn.Location = new Point(12, 9);
            refreshBtn.Size = new Size(110, 26);
            refreshBtn.FlatStyle = FlatStyle.Flat;
            refreshBtn.BackColor = Color.FromArgb(52, 52, 58);
            refreshBtn.ForeColor = Color.White;
            refreshBtn.Click += delegate { if (_onRefresh != null) _onRefresh(); };

            Button copyBtn = new Button();
            copyBtn.Text = "复制诊断日志";
            copyBtn.Location = new Point(132, 9);
            copyBtn.Size = new Size(110, 26);
            copyBtn.FlatStyle = FlatStyle.Flat;
            copyBtn.BackColor = Color.FromArgb(52, 52, 58);
            copyBtn.ForeColor = Color.White;
            copyBtn.Click += delegate
            {
                if (!string.IsNullOrEmpty(_logBox.Text))
                {
                    Clipboard.SetText(_logBox.Text);
                }
            };

            topBar.Controls.Add(refreshBtn);
            topBar.Controls.Add(copyBtn);

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Both;
            _logBox.WordWrap = false;
            _logBox.Dock = DockStyle.Fill;
            _logBox.BackColor = Color.FromArgb(18, 18, 20);
            _logBox.ForeColor = Color.FromArgb(212, 212, 216);
            _logBox.Font = new Font("Consolas", 9.5f);

            Controls.Add(_logBox);
            Controls.Add(topBar);
        }

        public void UpdateLog(List<string> lines)
        {
            if (lines == null) return;
            _logBox.Text = string.Join(Environment.NewLine, lines.ToArray());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
