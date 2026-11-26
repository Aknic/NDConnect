using System;
using System.Collections.Generic;
using System.Threading;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    // Feature 0x83 protocol shared by WLmouse (0x36A7), LAMZU Maya X (0x373E), and MCHOSE A5 Pro Max (0x2023)
    // (reference: HeyOkay/HaloBattery providers/wlmouse.py, providers/lamzu.py, providers/mchose.py).
    public sealed class NordicFeature83Handler : IHidProtocolHandler
    {
        private const ushort VID_WLMOUSE = 0x36A7;
        private const ushort VID_LAMZU = 0x373E;
        private const ushort VID_MCHOSE_A5 = 0x2023;

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, DeviceBatteryInfo> _cached = new Dictionary<string, DeviceBatteryInfo>();

        public string Name
        {
            get { return "Nordic Feature 0x83 HID (WLmouse / LAMZU / MCHOSE A5)"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_WLMOUSE && group.VendorId != VID_LAMZU && group.VendorId != VID_MCHOSE_A5)
            {
                return false;
            }
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFFFF && n.FeatureReportLength >= 16) return true;
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            string cacheKey = !string.IsNullOrEmpty(group.GroupKey)
                ? group.GroupKey
                : string.Format("{0:X4}_{1:X4}", group.VendorId, group.ProductId);

            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage != 0xFFFF || n.FeatureReportLength < 16) continue;

                using (HidDeviceHandle handle = HidDeviceHandle.Open(n.DevicePath, true, true, true))
                {
                    if (handle == null) continue;

                    byte[] req = new byte[Math.Max(64, (int)n.FeatureReportLength)];
                    req[0] = 0x00;
                    req[1] = 0x00;
                    req[2] = 0x02;
                    req[3] = 0x02;
                    req[4] = 0x00;
                    req[5] = 0x83;

                    if (handle.SetFeature(req))
                    {
                        Thread.Sleep(35);
                        for (int attempt = 0; attempt < 6; attempt++)
                        {
                            byte[] resp = new byte[req.Length];
                            resp[0] = 0x00;
                            if (handle.GetFeature(resp))
                            {
                                int pct;
                                bool chg;
                                if (ParseFeature83(resp, out pct, out chg))
                                {
                                    DeviceBatteryInfo dev = BuildDevice(group, pct, chg, false, "Feature 0x83");
                                    lock (_cacheLock) { _cached[cacheKey] = dev.Clone(); }
                                    results.Add(dev);
                                    return results;
                                }
                            }
                            Thread.Sleep(30);
                        }
                    }

                    // Fallback: WLmouse heartbeat input report (03 00 <battery%> <charging>)
                    if (group.VendorId == VID_WLMOUSE && n.InputReportLength >= 4)
                    {
                        byte[] hb = handle.ReadReport(n.InputReportLength, 250);
                        int pct;
                        bool chg;
                        if (ParseHeartbeat(hb, out pct, out chg))
                        {
                            DeviceBatteryInfo dev = BuildDevice(group, pct, chg, false, "Heartbeat 0x03");
                            lock (_cacheLock) { _cached[cacheKey] = dev.Clone(); }
                            results.Add(dev);
                            return results;
                        }
                    }
                }
            }

            int fallback = -1;
            lock (_cacheLock)
            {
                DeviceBatteryInfo prev;
                if (_cached.TryGetValue(cacheKey, out prev) && prev.BatteryPercent >= 0) fallback = prev.BatteryPercent;
            }
            results.Add(BuildDevice(group, fallback, false, true, "2.4G 休眠/等待唤醒"));
            return results;
        }

        private static DeviceBatteryInfo BuildDevice(HidDeviceGroup group, int pct, bool charging, bool asleep, string extra)
        {
            DeviceBatteryInfo info = new DeviceBatteryInfo();
            info.Id = string.Format("N83_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, group.GroupKey);
            info.ContainerId = group.ContainerId;
            info.Name = group.DisplayName;
            info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
            info.Channel = group.InferredChannel;
            info.BatteryPercent = pct;
            info.IsCharging = charging && !asleep;
            info.IsFull = !asleep && pct >= 100;
            info.IsAsleep = asleep;
            info.ProtocolName = "Nordic Feature 0x83 HID";
            info.Priority = 92;
            info.ExtraInfo = extra;
            return info;
        }

        public static bool ParseFeature83(byte[] resp, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 8) return false;
            for (int i = 5; i < resp.Length - 2; i++)
            {
                if (resp[i] == 0x83 && resp[i - 1] == 0x00 && resp[i - 2] == 0x02 &&
                    (resp[i - 5] == 0xA1 || resp[i - 5] == 0xA2))
                {
                    byte chg = resp[i + 1];
                    int bat = resp[i + 2];
                    if (bat >= 0 && bat <= 100)
                    {
                        percent = bat;
                        charging = chg != 0;
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool ParseHeartbeat(byte[] data, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (data == null) return false;
            for (int off = 0; off <= 1; off++)
            {
                if (data.Length >= off + 4 && data[off] == 0x03 && data[off + 1] == 0x00)
                {
                    int bat = data[off + 2];
                    if (bat >= 0 && bat <= 100)
                    {
                        percent = bat;
                        charging = data[off + 3] != 0;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
