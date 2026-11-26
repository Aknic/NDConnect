using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using NDConnect.Models;
using NDConnect.Services;

namespace NDConnect.UI
{
    public sealed class TrayApplicationContext : ApplicationContext
    {
        private sealed class DeviceTrayEntry : IDisposable
        {
            public NotifyIcon TrayIcon;
            public RenderedIcon CurrentIcon;
            public DeviceBatteryInfo Device;

            public void Dispose()
            {
                if (TrayIcon != null)
                {
                    TrayIcon.Visible = false;
                    TrayIcon.Dispose();
                    TrayIcon = null;
                }
                if (CurrentIcon != null)
                {
                    CurrentIcon.Dispose();
                    CurrentIcon = null;
                }
            }
        }

        private readonly AppSettings _settings;
        private readonly Form _marshalForm;
        private readonly DeviceMonitorService _monitor;
        private readonly Dictionary<string, DeviceTrayEntry> _entries = new Dictionary<string, DeviceTrayEntry>(StringComparer.OrdinalIgnoreCase);

        private NotifyIcon _standbyIcon;
        private RenderedIcon _standbyRendered;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _menuHeader;
        private readonly ToolStripMenuItem _menuModeGlyph;
        private readonly ToolStripMenuItem _menuModePercent;
        private readonly ToolStripMenuItem _menuThemeAuto;
        private readonly ToolStripMenuItem _menuThemeDark;
        private readonly ToolStripMenuItem _menuThemeLight;
        private readonly ToolStripMenuItem _menuColorGradient;
        private readonly ToolStripMenuItem _menuColorMinimal;
        private readonly ToolStripMenuItem _menuColorCustom;
        private readonly ToolStripMenuItem _menuAutoStart;
        private readonly Dictionary<int, ToolStripMenuItem> _pollMenuItems = new Dictionary<int, ToolStripMenuItem>();

        private readonly System.Windows.Forms.Timer _breathTimer;
        private double _breathPhase;
        private DashboardForm _dashboard;
        private DiagnosticsForm _diagnostics;
        private ColorSettingsForm _colorDialog;
        private string _lastRightClickedDeviceId = string.Empty;

        public TrayApplicationContext()
        {
            _settings = AppSettings.Load();

            // Invisible message-only marshal form so background threads marshal cleanly to UI thread.
            _marshalForm = new Form();
            _marshalForm.ShowInTaskbar = false;
            _marshalForm.WindowState = FormWindowState.Minimized;
            _marshalForm.FormBorderStyle = FormBorderStyle.None;
            _marshalForm.Opacity = 0;
            IntPtr forceHandle = _marshalForm.Handle;

            _contextMenu = new ContextMenuStrip();
            _menuHeader = new ToolStripMenuItem("NDConnect 电量监测");
            _menuHeader.Enabled = false;
            _contextMenu.Items.Add(_menuHeader);
            _contextMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openDash = new ToolStripMenuItem("设备电量总览", null, delegate { ToggleDashboard(); });
            openDash.Font = new Font(_contextMenu.Font, FontStyle.Bold);
            _contextMenu.Items.Add(openDash);
            _contextMenu.Items.Add(new ToolStripMenuItem("立即刷新", null, delegate { _monitor.TriggerRefreshNow(true); }));

            ToolStripMenuItem displaySub = new ToolStripMenuItem("托盘中心显示");
            _menuModeGlyph = new ToolStripMenuItem("设备类型图标", null, delegate { SetShowPercentage(false); });
            _menuModePercent = new ToolStripMenuItem("电量百分比数字", null, delegate { SetShowPercentage(true); });
            displaySub.DropDownItems.Add(_menuModeGlyph);
            displaySub.DropDownItems.Add(_menuModePercent);
            _contextMenu.Items.Add(displaySub);

            ToolStripMenuItem themeSub = new ToolStripMenuItem("任务栏主题");
            _menuThemeAuto = new ToolStripMenuItem("自动跟随系统", null, delegate { SetIconThemeMode(0); });
            _menuThemeDark = new ToolStripMenuItem("暗色任务栏", null, delegate { SetIconThemeMode(1); });
            _menuThemeLight = new ToolStripMenuItem("亮色任务栏", null, delegate { SetIconThemeMode(2); });
            themeSub.DropDownItems.Add(_menuThemeAuto);
            themeSub.DropDownItems.Add(_menuThemeDark);
            themeSub.DropDownItems.Add(_menuThemeLight);
            _contextMenu.Items.Add(themeSub);

            ToolStripMenuItem colorSub = new ToolStripMenuItem("环形配色");
            _menuColorGradient = new ToolStripMenuItem("红绿电量渐变 (默认)", null, delegate { SetRingColorMode(0); });
            _menuColorMinimal = new ToolStripMenuItem("极简主题色 (黑白/黄/红)", null, delegate { SetRingColorMode(1); });
            _menuColorCustom = new ToolStripMenuItem("自定义配色 (色轮)", null, delegate { SetRingColorMode(2); });
            colorSub.DropDownItems.Add(_menuColorGradient);
            colorSub.DropDownItems.Add(_menuColorMinimal);
            colorSub.DropDownItems.Add(_menuColorCustom);
            colorSub.DropDownItems.Add(new ToolStripSeparator());
            colorSub.DropDownItems.Add(new ToolStripMenuItem("打开色轮自定义配色...", null, delegate { ShowColorSettings(); }));
            _contextMenu.Items.Add(colorSub);

            ToolStripMenuItem pollSub = new ToolStripMenuItem("轮询频率");
            int[] intervals = new int[] { 15, 30, 60, 120 };
            foreach (int sec in intervals)
            {
                int captured = sec;
                ToolStripMenuItem mi = new ToolStripMenuItem(sec + " 秒", null, delegate { SetPollInterval(captured); });
                _pollMenuItems[sec] = mi;
                pollSub.DropDownItems.Add(mi);
            }
            _contextMenu.Items.Add(pollSub);

            _menuAutoStart = new ToolStripMenuItem("开机自动启动", null, delegate
            {
                _settings.StartWithWindows = !_settings.StartWithWindows;
                SyncMenuStates();
            });
            _contextMenu.Items.Add(_menuAutoStart);
            _contextMenu.Items.Add(new ToolStripMenuItem("协议与设备诊断...", null, delegate { ShowDiagnostics(); }));
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitApplication(); }));
            _contextMenu.Opened += OnContextMenuOpened;
            foreach (ToolStripItem item in _contextMenu.Items)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems)
                {
                    mi.DropDown.Opened += OnSubMenuOpened;
                }
            }

            SyncMenuStates();
            EnsureStandbyIcon();

            _breathTimer = new System.Windows.Forms.Timer();
            _breathTimer.Interval = 120;
            _breathTimer.Tick += OnBreathTick;

            _monitor = new DeviceMonitorService(_marshalForm, _settings.PollIntervalSeconds, _settings);
            _monitor.DevicesUpdated += OnDevicesUpdated;
            _monitor.SystemThemeChanged += delegate
            {
                if (_settings.IconThemeMode == 0) RefreshAllIcons();
            };
            _monitor.FullscreenStateChanged += OnFullscreenStateChanged;
            _monitor.Start();
        }

        private void OnFullscreenStateChanged(bool isFullscreen)
        {
            if (isFullscreen)
            {
                if (_breathTimer.Enabled) _breathTimer.Stop();
            }
            else
            {
                bool anyCharging = false;
                foreach (DeviceTrayEntry entry in _entries.Values)
                {
                    if (entry.Device.IsCharging) { anyCharging = true; break; }
                }
                if (anyCharging && !_breathTimer.Enabled) _breathTimer.Start();
            }
        }

        private void SyncMenuStates()
        {
            _menuModeGlyph.Checked = !_settings.ShowPercentageText;
            _menuModePercent.Checked = _settings.ShowPercentageText;
            _menuThemeAuto.Checked = (_settings.IconThemeMode == 0);
            _menuThemeDark.Checked = (_settings.IconThemeMode == 1);
            _menuThemeLight.Checked = (_settings.IconThemeMode == 2);
            _menuColorGradient.Checked = (_settings.RingColorMode == 0);
            _menuColorMinimal.Checked = (_settings.RingColorMode == 1);
            _menuColorCustom.Checked = (_settings.RingColorMode == 2);
            _menuAutoStart.Checked = _settings.StartWithWindows;
            foreach (KeyValuePair<int, ToolStripMenuItem> kv in _pollMenuItems)
            {
                kv.Value.Checked = (kv.Key == _settings.PollIntervalSeconds);
            }
        }

        private void SetIconThemeMode(int mode)
        {
            _settings.IconThemeMode = mode;
            _settings.Save();
            SyncMenuStates();
            RefreshAllIcons();
        }

        private void SetRingColorMode(int mode)
        {
            _settings.RingColorMode = mode;
            _settings.Save();
            SyncMenuStates();
            RefreshAllIcons();
        }

        private void SetShowPercentage(bool showPercent)
        {
            _settings.ShowPercentageText = showPercent;
            _settings.Save();
            SyncMenuStates();
            RefreshAllIcons();
        }

        private void SetPollInterval(int seconds)
        {
            _settings.PollIntervalSeconds = seconds;
            _settings.Save();
            _monitor.UpdatePollInterval(seconds);
            SyncMenuStates();
        }

        private void EnsureStandbyIcon()
        {
            if (_standbyIcon == null)
            {
                int size = RingIconRenderer.GetPreferredTrayIconSize();
                bool light = RingIconRenderer.ResolveIsLightTaskbar(_settings.IconThemeMode);
                _standbyRendered = RingIconRenderer.RenderStandbyIcon(size, light);
                _standbyIcon = new NotifyIcon();
                _standbyIcon.Icon = _standbyRendered.Icon;
                _standbyIcon.Text = "NDConnect - 正在扫描无线外设...";
                _standbyIcon.MouseUp += delegate(object s, MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Left) ToggleDashboard();
                    else if (e.Button == MouseButtons.Right)
                    {
                        _lastRightClickedDeviceId = string.Empty;
                        _menuHeader.Text = "NDConnect (未检测到无线设备)";
                        ShowContextMenuAboveTaskbar();
                    }
                };
                _standbyIcon.Visible = true;
            }
        }

        private void OnDevicesUpdated(List<DeviceBatteryInfo> devices)
        {
            HashSet<string> activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyCharging = false;
            int iconSize = RingIconRenderer.GetPreferredTrayIconSize();

            foreach (DeviceBatteryInfo dev in devices)
            {
                if (_settings.HiddenDeviceIds != null && _settings.HiddenDeviceIds.Contains(dev.Id)) continue;
                activeIds.Add(dev.Id);
                if (dev.IsCharging) anyCharging = true;

                DeviceTrayEntry entry;
                if (!_entries.TryGetValue(dev.Id, out entry))
                {
                    entry = new DeviceTrayEntry();
                    entry.Device = dev.Clone();
                    entry.TrayIcon = new NotifyIcon();
                    string capturedId = dev.Id;
                    entry.TrayIcon.MouseUp += delegate(object s, MouseEventArgs e)
                    {
                        if (e.Button == MouseButtons.Left)
                        {
                            ToggleDashboard();
                        }
                        else if (e.Button == MouseButtons.Right)
                        {
                            _lastRightClickedDeviceId = capturedId;
                            DeviceTrayEntry clicked;
                            if (_entries.TryGetValue(capturedId, out clicked))
                            {
                                string status = clicked.Device.IsAsleep
                                    ? "休眠中"
                                    : (clicked.Device.IsCharging ? "充电中" : (clicked.Device.IsFull ? "已充满" : "使用中"));
                                string pctStr = clicked.Device.BatteryPercent >= 0 ? (clicked.Device.BatteryPercent + "% · ") : "";
                                _menuHeader.Text = string.Format("{0} ({1}{2})", clicked.Device.Name, pctStr, status);
                            }
                            ShowContextMenuAboveTaskbar();
                        }
                    };
                    _entries[dev.Id] = entry;
                }
                else
                {
                    entry.Device = dev.Clone();
                }

                UpdateTrayEntryVisual(entry, iconSize);
                entry.TrayIcon.Visible = true;
            }

            // Remove disconnected devices
            List<string> toRemove = new List<string>();
            foreach (string id in _entries.Keys)
            {
                if (!activeIds.Contains(id)) toRemove.Add(id);
            }
            foreach (string id in toRemove)
            {
                _entries[id].Dispose();
                _entries.Remove(id);
            }

            // Show standby icon only when zero active devices exist
            if (_entries.Count == 0)
            {
                EnsureStandbyIcon();
                _standbyIcon.Text = "NDConnect - 未检测到支持电量的无线设备";
                _standbyIcon.Visible = true;
            }
            else if (_standbyIcon != null)
            {
                _standbyIcon.Visible = false;
            }

            if (anyCharging && !_monitor.IsFullscreenThrottled && !_breathTimer.Enabled)
            {
                _breathTimer.Start();
            }
            else if ((!anyCharging || _monitor.IsFullscreenThrottled) && _breathTimer.Enabled)
            {
                _breathTimer.Stop();
            }

            if (_dashboard != null && _dashboard.Visible)
            {
                _dashboard.UpdateTheme(_settings.RingColorMode, _settings.CustomPalette);
                _dashboard.UpdateDevices(devices, _breathPhase);
            }
            if (_diagnostics != null && _diagnostics.Visible)
            {
                _diagnostics.UpdateLog(_monitor.GetDiagnosticLog());
            }
        }

        private void RefreshAllIcons()
        {
            int size = RingIconRenderer.GetPreferredTrayIconSize();
            bool light = RingIconRenderer.ResolveIsLightTaskbar(_settings.IconThemeMode);
            if (_standbyIcon != null && _standbyIcon.Visible)
            {
                RenderedIcon nextStandby = RingIconRenderer.RenderStandbyIcon(size, light);
                RenderedIcon prevStandby = _standbyRendered;
                _standbyRendered = nextStandby;
                _standbyIcon.Icon = nextStandby.Icon;
                if (prevStandby != null) prevStandby.Dispose();
            }
            foreach (DeviceTrayEntry entry in _entries.Values)
            {
                UpdateTrayEntryVisual(entry, size);
            }
        }

        private void UpdateTrayEntryVisual(DeviceTrayEntry entry, int iconSize)
        {
            bool light = RingIconRenderer.ResolveIsLightTaskbar(_settings.IconThemeMode);
            RenderedIcon next = RingIconRenderer.RenderTrayIcon(entry.Device, _breathPhase, _settings.ShowPercentageText, iconSize, light, _settings.RingColorMode, _settings.CustomPalette);
            RenderedIcon prev = entry.CurrentIcon;
            entry.CurrentIcon = next;
            entry.TrayIcon.Icon = next.Icon;
            entry.TrayIcon.Text = BuildTooltipText(entry.Device);
            if (prev != null) prev.Dispose();
        }

        private void OnBreathTick(object sender, EventArgs e)
        {
            _breathPhase += 0.35;
            if (_breathPhase > Math.PI * 2.0) _breathPhase -= Math.PI * 2.0;

            int size = RingIconRenderer.GetPreferredTrayIconSize();
            bool anyCharging = false;
            foreach (DeviceTrayEntry entry in _entries.Values)
            {
                if (entry.Device.IsCharging)
                {
                    anyCharging = true;
                    UpdateTrayEntryVisual(entry, size);
                }
            }

            if (_dashboard != null && _dashboard.Visible)
            {
                _dashboard.UpdateBreathPhase(_breathPhase);
            }

            if (!anyCharging)
            {
                _breathTimer.Stop();
            }
        }

        public static string BuildTooltipText(DeviceBatteryInfo dev)
        {
            string chan = dev.ShortChannelLabel();
            string tail;
            if (dev.IsAsleep)
            {
                tail = dev.BatteryPercent >= 0
                    ? string.Format("\n休眠中 ({0}%) · {1}", dev.BatteryPercent, chan)
                    : string.Format("\n休眠中 · {0}", chan);
            }
            else
            {
                string state = dev.IsCharging ? "充电中" : (dev.IsFull ? "已充满" : "电量");
                tail = string.Format("\n{0} {1}% · {2}", state, dev.BatteryPercent, chan);
            }
            int maxNameLen = 63 - tail.Length;
            string name = dev.Name ?? "Device";
            if (maxNameLen < 4) maxNameLen = 4;
            if (name.Length > maxNameLen)
            {
                name = name.Substring(0, maxNameLen - 1) + "…";
            }
            string full = name + tail;
            return full.Length > 63 ? full.Substring(0, 63) : full;
        }

        private void ShowContextMenuAboveTaskbar()
        {
            Point cursor = Cursor.Position;
            Screen scr = Screen.FromPoint(cursor);
            Rectangle wa = scr.WorkingArea;

            Size pref = _contextMenu.GetPreferredSize(Size.Empty);
            int w = Math.Max(pref.Width, _contextMenu.Width);
            int h = Math.Max(pref.Height, _contextMenu.Height);

            int x = Math.Max(wa.Left + 6, Math.Min(cursor.X, wa.Right - w - 6));
            int y;
            if (cursor.Y >= wa.Bottom - 8 || cursor.Y + h > wa.Bottom - 6)
            {
                // Taskbar at bottom (or menu would overlap bottom edge): place cleanly above working area bottom
                y = Math.Max(wa.Top + 6, wa.Bottom - h - 6);
            }
            else if (cursor.Y < wa.Top)
            {
                // Taskbar at top
                y = wa.Top + 6;
            }
            else
            {
                y = Math.Max(wa.Top + 6, Math.Min(cursor.Y, wa.Bottom - h - 6));
            }

            Native.User32Native.SetForegroundWindow(_marshalForm.Handle);
            UpdateSubMenuDirections(wa, x + w);
            _contextMenu.Show(new Point(x, y));
        }

        private void OnContextMenuOpened(object sender, EventArgs e)
        {
            Screen scr = Screen.FromPoint(_contextMenu.Location);
            Rectangle wa = scr.WorkingArea;
            int maxBottom = wa.Bottom - 6;
            int maxRight = wa.Right - 6;
            int newX = _contextMenu.Left;
            int newY = _contextMenu.Top;
            if (_contextMenu.Bottom > maxBottom)
            {
                newY = Math.Max(wa.Top + 6, maxBottom - _contextMenu.Height);
            }
            if (_contextMenu.Right > maxRight)
            {
                newX = Math.Max(wa.Left + 6, maxRight - _contextMenu.Width);
            }
            if (newX != _contextMenu.Left || newY != _contextMenu.Top)
            {
                _contextMenu.Location = new Point(newX, newY);
            }
            UpdateSubMenuDirections(wa, _contextMenu.Right);
        }

        private void UpdateSubMenuDirections(Rectangle wa, int parentRight)
        {
            foreach (ToolStripItem item in _contextMenu.Items)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi == null || !mi.HasDropDownItems) continue;
                int subW = Math.Max(mi.DropDown.GetPreferredSize(Size.Empty).Width, 170);
                mi.DropDownDirection = (parentRight + subW > wa.Right - 4)
                    ? ToolStripDropDownDirection.Left
                    : ToolStripDropDownDirection.Right;
            }
        }

        private void OnSubMenuOpened(object sender, EventArgs e)
        {
            ToolStripDropDown dd = sender as ToolStripDropDown;
            if (dd == null || !dd.Visible) return;

            Screen scr = Screen.FromPoint(_contextMenu.Location);
            Rectangle wa = scr.WorkingArea;

            int newX = dd.Left;
            int newY = dd.Top;

            if (dd.Right > wa.Right - 4)
            {
                newX = Math.Max(wa.Left + 4, _contextMenu.Left - dd.Width + 2);
            }
            if (newX < wa.Left + 4)
            {
                newX = Math.Min(wa.Right - dd.Width - 4, _contextMenu.Right - 2);
            }
            if (dd.Bottom > wa.Bottom - 6)
            {
                newY = Math.Max(wa.Top + 6, wa.Bottom - dd.Height - 6);
            }
            if (newY < wa.Top + 6)
            {
                newY = wa.Top + 6;
            }

            if (newX != dd.Left || newY != dd.Top)
            {
                dd.Location = new Point(newX, newY);
            }
        }

        private void ToggleDashboard()
        {
            if (_dashboard == null || _dashboard.IsDisposed)
            {
                _dashboard = new DashboardForm(
                    delegate { _monitor.TriggerRefreshNow(true); },
                    delegate { ShowDiagnostics(); },
                    delegate { ShowColorSettings(); });
            }

            if (_dashboard.Visible)
            {
                _dashboard.Hide();
            }
            else
            {
                _dashboard.UpdateTheme(_settings.RingColorMode, _settings.CustomPalette);
                _dashboard.UpdateDevices(_monitor.GetSnapshot(), _breathPhase);
                _dashboard.PositionNearTaskbar();
                _dashboard.Show();
                _dashboard.Activate();
            }
        }

        private void ShowColorSettings()
        {
            if (_colorDialog != null && !_colorDialog.IsDisposed)
            {
                _colorDialog.Activate();
                return;
            }
            _colorDialog = new ColorSettingsForm(_settings.CustomPalette, delegate(CustomColorPalette updated)
            {
                _settings.CustomPalette = updated;
                _settings.RingColorMode = 2;
                _settings.Save();
                SyncMenuStates();
                RefreshAllIcons();
                if (_dashboard != null && _dashboard.Visible)
                {
                    _dashboard.UpdateTheme(_settings.RingColorMode, _settings.CustomPalette);
                    _dashboard.UpdateDevices(_monitor.GetSnapshot(), _breathPhase);
                }
            });
            _colorDialog.Show();
            _colorDialog.Activate();
        }

        private void ShowDiagnostics()
        {
            if (_diagnostics == null || _diagnostics.IsDisposed)
            {
                _diagnostics = new DiagnosticsForm(delegate
                {
                    _monitor.TriggerRefreshNow(true);
                });
            }
            _diagnostics.UpdateLog(_monitor.GetDiagnosticLog());
            _diagnostics.Show();
            _diagnostics.Activate();
        }

        private void ExitApplication()
        {
            _breathTimer.Stop();
            _breathTimer.Dispose();
            _monitor.Dispose();

            foreach (DeviceTrayEntry entry in _entries.Values)
            {
                entry.Dispose();
            }
            _entries.Clear();

            if (_standbyIcon != null)
            {
                _standbyIcon.Visible = false;
                _standbyIcon.Dispose();
                _standbyIcon = null;
            }
            if (_standbyRendered != null)
            {
                _standbyRendered.Dispose();
                _standbyRendered = null;
            }
            if (_dashboard != null && !_dashboard.IsDisposed) _dashboard.Close();
            if (_diagnostics != null && !_diagnostics.IsDisposed) _diagnostics.Close();
            if (_colorDialog != null && !_colorDialog.IsDisposed) _colorDialog.Close();
            if (_marshalForm != null && !_marshalForm.IsDisposed) _marshalForm.Dispose();

            ExitThread();
        }
    }
}
