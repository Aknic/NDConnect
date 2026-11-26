using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NDConnect.Models;
using NDConnect.Native;
using NDConnect.Protocols;
using NDConnect.Protocols.Handlers;

namespace NDConnect.Services
{
    public sealed class DeviceMonitorService : IDisposable
    {
        public const int FullscreenPollIntervalSeconds = 120;

        private readonly List<IChannelProvider> _providers;
        private readonly Control _uiMarshalControl;
        private readonly AppSettings _settings;
        private readonly object _sync = new object();

        private System.Threading.Timer _pollTimer;
        private System.Threading.Timer _debounceTimer;
        private System.Threading.Timer _followupTimer;
        private DeviceChangeWindow _changeWindow;
        private int _pollIntervalSeconds = 30;
        private DateTime _lastPollUtc = DateTime.MinValue;
        private volatile bool _isFullscreenThrottled;
        private volatile bool _isPolling;
        private volatile bool _disposed;

        private List<DeviceBatteryInfo> _latestDevices = new List<DeviceBatteryInfo>();
        private List<string> _latestDiagLog = new List<string>();

        public event Action<List<DeviceBatteryInfo>> DevicesUpdated;
        public event Action SystemThemeChanged;
        public event Action<bool> FullscreenStateChanged;

        public bool IsFullscreenThrottled
        {
            get { return _isFullscreenThrottled; }
        }

        public static int ComputeEffectivePollInterval(int normalIntervalSeconds, bool isFullscreen)
        {
            return isFullscreen ? FullscreenPollIntervalSeconds : Math.Max(5, normalIntervalSeconds);
        }

        public DeviceMonitorService(Control uiMarshalControl, int pollIntervalSeconds)
            : this(uiMarshalControl, pollIntervalSeconds, null)
        {
        }

        public DeviceMonitorService(Control uiMarshalControl, int pollIntervalSeconds, AppSettings settings)
        {
            _uiMarshalControl = uiMarshalControl;
            _settings = settings;
            _pollIntervalSeconds = Math.Max(5, pollIntervalSeconds);
            _providers = new List<IChannelProvider>();
            _providers.Add(new HidChannelProvider());
            _providers.Add(new BluetoothChannelProvider());
            _providers.Add(new XInputChannelProvider());
        }

        public List<DeviceBatteryInfo> GetSnapshot()
        {
            lock (_sync)
            {
                List<DeviceBatteryInfo> copy = new List<DeviceBatteryInfo>(_latestDevices.Count);
                foreach (DeviceBatteryInfo d in _latestDevices) copy.Add(d.Clone());
                return copy;
            }
        }

        public List<string> GetDiagnosticLog()
        {
            lock (_sync)
            {
                return new List<string>(_latestDiagLog);
            }
        }

        public void Start()
        {
            if (_disposed) return;
            _changeWindow = new DeviceChangeWindow(OnHardwareChange, OnThemeSettingChange);
            // Lightweight 5-second scheduler checks fullscreen state and enforces 120s fixed interval during fullscreen apps/games.
            _pollTimer = new System.Threading.Timer(OnSchedulerTick, null, 250, 5000);
        }

        private void OnThemeSettingChange()
        {
            if (_disposed || _uiMarshalControl == null || _uiMarshalControl.IsDisposed || !_uiMarshalControl.IsHandleCreated) return;
            try
            {
                _uiMarshalControl.BeginInvoke(new Action(delegate()
                {
                    Action h = SystemThemeChanged;
                    if (h != null) h();
                }));
            }
            catch
            {
            }
        }

        public void UpdatePollInterval(int seconds)
        {
            _pollIntervalSeconds = Math.Max(5, seconds);
        }

        public void TriggerRefreshNow(bool bypassCaches)
        {
            if (_disposed) return;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                RunPollOnce(bypassCaches);
            });
        }

        private void OnHardwareChange()
        {
            if (_disposed || _isFullscreenThrottled) return;
            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Threading.Timer(delegate(object s)
                {
                    if (!_isFullscreenThrottled) RunPollOnce(true);
                }, null, 1500, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(1500, Timeout.Infinite);
            }

            // Secondary follow-up scan 4.5s later so Bluetooth HFP battery properties populated after A2DP connect appear promptly.
            if (_followupTimer == null)
            {
                _followupTimer = new System.Threading.Timer(delegate(object s)
                {
                    if (!_isFullscreenThrottled) RunPollOnce(false);
                }, null, 4500, Timeout.Infinite);
            }
            else
            {
                _followupTimer.Change(4500, Timeout.Infinite);
            }
        }

        private void OnSchedulerTick(object state)
        {
            if (_disposed) return;
            bool isFs = User32Native.IsForegroundAppFullscreen();
            if (isFs != _isFullscreenThrottled)
            {
                _isFullscreenThrottled = isFs;
                if (_uiMarshalControl != null && !_uiMarshalControl.IsDisposed && _uiMarshalControl.IsHandleCreated)
                {
                    try
                    {
                        bool captured = isFs;
                        _uiMarshalControl.BeginInvoke(new Action(delegate()
                        {
                            Action<bool> h = FullscreenStateChanged;
                            if (h != null) h(captured);
                        }));
                    }
                    catch
                    {
                    }
                }
            }

            int effectiveSec = ComputeEffectivePollInterval(_pollIntervalSeconds, isFs);
            if (_lastPollUtc == DateTime.MinValue || (DateTime.UtcNow - _lastPollUtc).TotalSeconds >= effectiveSec)
            {
                RunPollOnce(false);
            }
        }

        public List<DeviceBatteryInfo> RunPollOnce(bool bypassCaches)
        {
            if (_disposed) return new List<DeviceBatteryInfo>();
            if (_isPolling) return GetSnapshot();

            _isPolling = true;
            _lastPollUtc = DateTime.UtcNow;
            try
            {
                EvisionTelinkHandler.ForceBypassCache = bypassCaches;
                MchoseRealtekHandler.ForceBypassCache = bypassCaches;
                List<string> diag = new List<string>();
                int effSec = ComputeEffectivePollInterval(_pollIntervalSeconds, _isFullscreenThrottled);
                diag.Add(string.Format("=== NDConnect Scan @ {0:yyyy-MM-dd HH:mm:ss} (Fullscreen={1}, Poll={2}s) ===",
                    DateTime.Now, _isFullscreenThrottled, effSec));

                List<DeviceBatteryInfo> raw = new List<DeviceBatteryInfo>();
                // Run all 3 channel providers concurrently
                List<Task<ProviderResult>> providerTasks = new List<Task<ProviderResult>>();
                foreach (IChannelProvider provider in _providers)
                {
                    IChannelProvider captured = provider;
                    providerTasks.Add(Task.Factory.StartNew(delegate()
                    {
                        ProviderResult pr = new ProviderResult();
                        pr.Name = captured.Name;
                        try
                        {
                            List<DeviceBatteryInfo> items = captured.Poll(pr.Logs);
                            if (items != null) pr.Items.AddRange(items);
                        }
                        catch (Exception ex)
                        {
                            pr.Logs.Add("  [Provider Error] " + ex.Message);
                        }
                        return pr;
                    }));
                }

                try
                {
                    Task.WaitAll(providerTasks.ToArray(), 3500);
                }
                catch
                {
                }

                foreach (Task<ProviderResult> pt in providerTasks)
                {
                    if (!pt.IsCompleted || pt.IsFaulted || pt.Result == null) continue;
                    diag.Add("--- Channel: " + pt.Result.Name + " ---");
                    diag.AddRange(pt.Result.Logs);
                    raw.AddRange(pt.Result.Items);
                }

                List<DeviceBatteryInfo> deduped = Deduplicate(raw);
                ApplyPersistentCache(deduped);
                diag.Add(string.Format("=== Summary: {0} raw -> {1} active devices ===", raw.Count, deduped.Count));

                lock (_sync)
                {
                    _latestDevices = deduped;
                    _latestDiagLog = diag;
                }

                if (_uiMarshalControl != null && !_uiMarshalControl.IsDisposed && _uiMarshalControl.IsHandleCreated)
                {
                    try
                    {
                        _uiMarshalControl.BeginInvoke(new Action(delegate()
                        {
                            Action<List<DeviceBatteryInfo>> handler = DevicesUpdated;
                            if (handler != null) handler(GetSnapshot());
                        }));
                    }
                    catch
                    {
                    }
                }

                return deduped;
            }
            finally
            {
                EvisionTelinkHandler.ForceBypassCache = false;
                MchoseRealtekHandler.ForceBypassCache = false;
                _isPolling = false;
            }
        }

        private sealed class ProviderResult
        {
            public string Name = string.Empty;
            public List<string> Logs = new List<string>();
            public List<DeviceBatteryInfo> Items = new List<DeviceBatteryInfo>();
        }

        private void ApplyPersistentCache(List<DeviceBatteryInfo> devices)
        {
            if (_settings == null || devices == null) return;
            bool changed = false;
            foreach (DeviceBatteryInfo dev in devices)
            {
                if (!dev.IsAsleep && dev.BatteryPercent >= 0)
                {
                    if (_settings.SetCachedBattery(dev.Id, dev.BatteryPercent)) changed = true;
                }
                else if (dev.IsAsleep && dev.BatteryPercent < 0)
                {
                    int cached = _settings.GetCachedBattery(dev.Id);
                    if (cached >= 0) dev.BatteryPercent = cached;
                }
            }
            if (changed) _settings.Save();
        }

        public static List<DeviceBatteryInfo> Deduplicate(List<DeviceBatteryInfo> raw)
        {
            List<DeviceBatteryInfo> result = new List<DeviceBatteryInfo>();
            if (raw == null) return result;

            foreach (DeviceBatteryInfo item in raw)
            {
                if (item == null) continue;
                if (item.BatteryPercent < 0 && !item.IsAsleep) continue;

                DeviceBatteryInfo existing = FindMatch(result, item);
                if (existing == null)
                {
                    result.Add(item.Clone());
                }
                else
                {
                    if (item.Priority > existing.Priority || (existing.IsAsleep && !item.IsAsleep))
                    {
                        bool mergedCharging = item.IsCharging || existing.IsCharging;
                        int idx = result.IndexOf(existing);
                        DeviceBatteryInfo replacement = item.Clone();
                        replacement.IsCharging = mergedCharging;
                        if (replacement.BatteryPercent < 0 && existing.BatteryPercent >= 0)
                        {
                            replacement.BatteryPercent = existing.BatteryPercent;
                        }
                        result[idx] = replacement;
                    }
                    else if (item.IsCharging && !existing.IsCharging)
                    {
                        existing.IsCharging = true;
                    }
                }
            }

            result.Sort(delegate(DeviceBatteryInfo a, DeviceBatteryInfo b)
            {
                int c = a.Category.CompareTo(b.Category);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        private static DeviceBatteryInfo FindMatch(List<DeviceBatteryInfo> list, DeviceBatteryInfo candidate)
        {
            string cid = (candidate.ContainerId ?? string.Empty).Trim();
            bool hasCid = !string.IsNullOrEmpty(cid) && cid != Guid.Empty.ToString("D").ToUpperInvariant();

            foreach (DeviceBatteryInfo existing in list)
            {
                if (hasCid && !string.IsNullOrEmpty(existing.ContainerId) &&
                    string.Equals(existing.ContainerId, cid, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
                if (!string.IsNullOrEmpty(candidate.Id) &&
                    string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
                // Cross-provider Bluetooth match (e.g., BT HID DualSense vs BT PnP) or Xbox BT vs XInput
                if (candidate.Channel == existing.Channel &&
                    candidate.Category == existing.Category &&
                    NormalizeName(candidate.Name) == NormalizeName(existing.Name) &&
                    !string.IsNullOrEmpty(NormalizeName(candidate.Name)))
                {
                    return existing;
                }
                // XInput vs Bluetooth Xbox Controller deduplication
                if (candidate.Category == DeviceCategory.Gamepad && existing.Category == DeviceCategory.Gamepad)
                {
                    string n1 = NormalizeName(candidate.Name);
                    string n2 = NormalizeName(existing.Name);
                    if ((n1.Contains("xbox") && n2.Contains("xbox")) && (candidate.ProtocolName.Contains("XInput") || existing.ProtocolName.Contains("XInput")))
                    {
                        return existing;
                    }
                }
            }
            return null;
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return name.Trim().ToLowerInvariant().Replace("wireless", "").Replace("controller", "").Replace(" ", "");
        }

        public void Dispose()
        {
            _disposed = true;
            if (_pollTimer != null) { _pollTimer.Dispose(); _pollTimer = null; }
            if (_debounceTimer != null) { _debounceTimer.Dispose(); _debounceTimer = null; }
            if (_followupTimer != null) { _followupTimer.Dispose(); _followupTimer = null; }
            if (_changeWindow != null) { _changeWindow.Dispose(); _changeWindow = null; }
        }

        private sealed class DeviceChangeWindow : NativeWindow, IDisposable
        {
            private readonly Action _onChanged;
            private readonly Action _onThemeChanged;
            private IntPtr _hidNotify;
            private IntPtr _usbNotify;

            public DeviceChangeWindow(Action onChanged, Action onThemeChanged)
            {
                _onChanged = onChanged;
                _onThemeChanged = onThemeChanged;
                CreateParams cp = new CreateParams();
                cp.Caption = "NDConnect_DeviceChangeSink";
                CreateHandle(cp);
                _hidNotify = User32Native.RegisterInterfaceNotification(Handle, User32Native.GUID_DEVINTERFACE_HID);
                _usbNotify = User32Native.RegisterInterfaceNotification(Handle, User32Native.GUID_DEVINTERFACE_USB_DEVICE);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == User32Native.WM_DEVICECHANGE)
                {
                    int w = m.WParam.ToInt32();
                    if (w == User32Native.DBT_DEVICEARRIVAL ||
                        w == User32Native.DBT_DEVICEREMOVECOMPLETE ||
                        w == User32Native.DBT_DEVNODES_CHANGED)
                    {
                        if (_onChanged != null) _onChanged();
                    }
                }
                else if (m.Msg == User32Native.WM_SETTINGCHANGE)
                {
                    if (_onThemeChanged != null) _onThemeChanged();
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (_hidNotify != IntPtr.Zero) { User32Native.UnregisterDeviceNotification(_hidNotify); _hidNotify = IntPtr.Zero; }
                if (_usbNotify != IntPtr.Zero) { User32Native.UnregisterDeviceNotification(_usbNotify); _usbNotify = IntPtr.Zero; }
                if (Handle != IntPtr.Zero) DestroyHandle();
            }
        }
    }
}
