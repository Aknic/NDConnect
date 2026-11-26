using System;
using System.Collections.Generic;
using System.Threading;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class MchoseRealtekHandler : IHidProtocolHandler
    {
        private const ushort VID_MCHOSE_REALTEK = 0x3837;
        private const ushort VID_MCHOSE_LEGACY = 0x5253;
        private const ushort VID_MCHOSE_G7 = 0xA8A5;
        private const ushort PID_MCHOSE_G7 = 0x2255;
        private const ushort PID_V9_DONGLE = 0x6008;
        private const ushort PID_V9_HEADSET = 0x600A;

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, DateTime> _lastQueryTime = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, DeviceBatteryInfo> _cachedResult = new Dictionary<string, DeviceBatteryInfo>();
        public static bool ForceBypassCache { get; set; }

        public string Name
        {
            get { return "MCHOSE (RealTek / G7 / V9) HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId == VID_MCHOSE_REALTEK || group.VendorId == VID_MCHOSE_LEGACY) return true;
            if (group.VendorId == VID_MCHOSE_G7 && group.ProductId == PID_MCHOSE_G7) return true;
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            string cacheKey = !string.IsNullOrEmpty(group.GroupKey)
                ? group.GroupKey
                : string.Format("{0:X4}_{1:X4}", group.VendorId, group.ProductId);

            if (!ForceBypassCache)
            {
                lock (_cacheLock)
                {
                    DateTime lastTime;
                    DeviceBatteryInfo cached;
                    if (_lastQueryTime.TryGetValue(cacheKey, out lastTime) &&
                        _cachedResult.TryGetValue(cacheKey, out cached) &&
                        !cached.IsAsleep &&
                        (DateTime.Now - lastTime).TotalSeconds < 20)
                    {
                        results.Add(cached.Clone());
                        return results;
                    }
                }
            }

            if (group.VendorId == VID_MCHOSE_G7 && group.ProductId == PID_MCHOSE_G7)
            {
                return QueryG7(group, cacheKey, diagLog);
            }

            if (group.VendorId == VID_MCHOSE_REALTEK &&
                (group.ProductId == PID_V9_DONGLE || group.ProductId == PID_V9_HEADSET))
            {
                return QueryV9Headset(group, cacheKey, diagLog);
            }

            // RealTek family (0x3837 and 0x5253): UP=0xFF01, Feature Reports 0x11 (20B) & 0x12 (64B)
            // Per HaloBattery: SetFeature MUST be re-sent on EVERY retry attempt, because a single
            // SetFeature followed by repeated GetFeature only alternates the echoed request.
            HidDeviceNode ff01Node = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF01 && n.FeatureReportLength >= 21)
                {
                    ff01Node = n;
                    break;
                }
            }

            if (ff01Node != null)
            {
                using (HidDeviceHandle handle = HidDeviceHandle.Open(ff01Node.DevicePath, true, true, true))
                {
                    if (handle != null)
                    {
                        // Try long report (0x12) and short report (0x11) across up to 4 exchanges
                        byte[] reportIds = new byte[] { 0x11, 0x12 };
                        for (int attempt = 0; attempt < 4; attempt++)
                        {
                            byte reqId = reportIds[attempt % 2];
                            byte[] req = new byte[ff01Node.FeatureReportLength];
                            req[0] = reqId;
                            req[1] = 0xF9; // 0x06 ^ 0xFF
                            for (int i = 2; i < req.Length; i++) req[i] = 0xFF;

                            if (!handle.SetFeature(req)) continue;
                            Thread.Sleep(attempt == 0 ? 75 : 110);

                            foreach (byte readId in new byte[] { 0x12, 0x11 })
                            {
                                byte[] resp = new byte[ff01Node.FeatureReportLength];
                                resp[0] = readId;
                                if (!handle.GetFeature(resp)) continue;

                                if (diagLog != null && attempt == 0)
                                {
                                    diagLog.Add(string.Format("  [MCHOSE Diag] UP=0xFF01 Query(0x{0:X2}/0xF9)->0x{1:X2}: {2}",
                                        reqId, readId, FormatHex(resp, 18)));
                                }

                                int pct;
                                bool chg;
                                if (TryParseMchose06Response(resp, false, out pct, out chg) ||
                                    TryParseMchose06Response(resp, true, out pct, out chg))
                                {
                                    DeviceBatteryInfo dev = BuildDevice(group, pct, chg, false, "RealTek 0x11/0x06");
                                    CacheAwakeDevice(cacheKey, dev);
                                    results.Add(dev);
                                    return results;
                                }
                            }
                        }
                    }
                    else if (diagLog != null)
                    {
                        diagLog.Add("  [MCHOSE] Cannot open UP=0xFF01");
                    }
                }

                // Receiver present, mouse sleeping -> retain Asleep state with last known battery %
                int fallbackPct = GetCachedPercent(cacheKey);
                DeviceBatteryInfo asleepDev = BuildDevice(group, fallbackPct, false, true, "2.4G 休眠/等待唤醒");
                results.Add(asleepDev);
                if (diagLog != null)
                {
                    diagLog.Add(string.Format("  [MCHOSE] Dongle present, mouse asleep (cached={0}%)", fallbackPct));
                }
            }

            return results;
        }

        private static List<DeviceBatteryInfo> QueryG7(HidDeviceGroup group, string cacheKey, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode target = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF01 && n.Usage == 0x0010 && n.OutputReportLength > 0 && n.InputReportLength > 0)
                {
                    target = n;
                    break;
                }
            }
            if (target == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(target.DevicePath, true, true, true))
            {
                if (handle == null) return results;
                byte[] req = new byte[Math.Max(65, (int)target.OutputReportLength)];
                byte[] prefix = new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01 };
                Array.Copy(prefix, req, prefix.Length);

                if (handle.WriteReport(req, 150))
                {
                    for (int i = 0; i < 12; i++)
                    {
                        byte[] resp = handle.ReadReport(Math.Max(64, (int)target.InputReportLength), 40);
                        if (resp == null) continue;
                        int pct;
                        bool chg;
                        if (TryParseG7Response(resp, out pct, out chg))
                        {
                            DeviceBatteryInfo dev = BuildDevice(group, pct, chg, false, "MCHOSE G7 0xAA30");
                            CacheAwakeDevice(cacheKey, dev);
                            results.Add(dev);
                            return results;
                        }
                    }
                }
            }

            int fallback = GetCachedPercent(cacheKey);
            results.Add(BuildDevice(group, fallback, false, true, "2.4G 休眠/等待唤醒"));
            return results;
        }

        private static List<DeviceBatteryInfo> QueryV9Headset(HidDeviceGroup group, string cacheKey, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode target = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00 && n.OutputReportLength >= 63 && n.InputReportLength >= 63)
                {
                    target = n;
                    break;
                }
            }
            if (target == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(target.DevicePath, true, true, true))
            {
                if (handle == null) return results;
                byte[][] payloads = new byte[][]
                {
                    new byte[] { 0x55, 0xAA, 0x01, 0x00, 0x01, 0x00, 0x01, 0x0B, 0x00 },
                    new byte[] { 0x55, 0x65, 0x01 }
                };
                foreach (byte[] p in payloads)
                {
                    byte[] pkt = new byte[Math.Max(64, (int)target.OutputReportLength)];
                    Array.Copy(p, pkt, Math.Min(p.Length, pkt.Length));
                    if (handle.WriteReport(pkt, 180))
                    {
                        byte[] resp = handle.ReadReport(Math.Max(64, (int)target.InputReportLength), 300);
                        int pct;
                        bool chg;
                        bool asleep;
                        if (TryParseV9Response(resp, out pct, out chg, out asleep))
                        {
                            DeviceBatteryInfo dev = BuildDevice(group, pct, chg, asleep, "MCHOSE V9 HID");
                            dev.Category = DeviceCategory.Headset;
                            dev.GlyphType = DeviceGlyphType.Headset;
                            if (!asleep) CacheAwakeDevice(cacheKey, dev);
                            results.Add(dev);
                            return results;
                        }
                    }
                }
            }
            return results;
        }

        private static void CacheAwakeDevice(string cacheKey, DeviceBatteryInfo dev)
        {
            lock (_cacheLock)
            {
                _lastQueryTime[cacheKey] = DateTime.Now;
                _cachedResult[cacheKey] = dev.Clone();
            }
        }

        private static int GetCachedPercent(string cacheKey)
        {
            lock (_cacheLock)
            {
                DeviceBatteryInfo prev;
                if (_cachedResult.TryGetValue(cacheKey, out prev) && prev.BatteryPercent >= 0)
                {
                    return prev.BatteryPercent;
                }
            }
            return -1;
        }

        private static DeviceBatteryInfo BuildDevice(HidDeviceGroup group, int pct, bool charging, bool asleep, string extra)
        {
            DeviceBatteryInfo info = new DeviceBatteryInfo();
            info.Id = string.Format("MCHOSE_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, group.GroupKey);
            info.ContainerId = group.ContainerId;
            info.Name = group.DisplayName;
            info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
            info.GlyphType = info.Category == DeviceCategory.Keyboard ? DeviceGlyphType.Keyboard : DeviceGlyphType.Mouse;
            info.Channel = group.InferredChannel;
            info.BatteryPercent = pct;
            info.IsCharging = charging && !asleep;
            info.IsFull = !asleep && pct >= 100;
            info.IsAsleep = asleep;
            info.ProtocolName = "MCHOSE RealTek HID";
            info.Priority = 90;
            info.ExtraInfo = extra;
            return info;
        }

        public static bool TryParseMchose06Response(byte[] raw, bool xorDecode, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (raw == null || raw.Length < 14) return false;

            byte[] data = raw;
            if (xorDecode)
            {
                data = new byte[raw.Length];
                data[0] = raw[0];
                for (int i = 1; i < raw.Length; i++) data[i] = (byte)(raw[i] ^ 0xFF);
            }

            // Accept VID 0x3837 (37 38 LE) or 0x5253 (53 52 LE)
            bool validVid = (data[2] == 0x37 && data[3] == 0x38) || (data[2] == 0x53 && data[3] == 0x52);
            if (data[0] == 0x11 && data[1] == 0x06 && validVid)
            {
                int bat = data[11];
                if (bat == 0 && data[13] > 0 && data[13] <= 100) bat = data[13];
                if (bat >= 1 && bat <= 100)
                {
                    percent = bat;
                    charging = (data[12] == 0x01 || data[12] == 0x02);
                    return true;
                }
            }
            return false;
        }

        public static bool TryParseG7Response(byte[] resp, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 10) return false;
            int off = (resp[0] == 0xAA && resp[1] == 0x30) ? 0 : ((resp.Length >= 11 && resp[1] == 0xAA && resp[2] == 0x30) ? 1 : -1);
            if (off < 0) return false;
            int bat = resp[off + 8];
            if (bat < 0 || bat > 100) return false;
            percent = bat;
            charging = resp[off + 9] != 0;
            return true;
        }

        public static bool TryParseV9Response(byte[] resp, out int percent, out bool charging, out bool asleep)
        {
            percent = -1;
            charging = false;
            asleep = false;
            if (resp == null || resp.Length < 6) return false;

            if (resp[0] == 0x55 && resp.Length >= 10 && resp[1] == 0xAA && resp[7] == 0x0B)
            {
                int bat = resp[9] & 0x7F;
                if (bat > 100) return false;
                percent = bat;
                charging = (resp[9] & 0x80) != 0;
                asleep = (resp[8] != 0 && bat == 0);
                return true;
            }
            if (resp[0] == 0x55 && resp.Length >= 4)
            {
                int bat = resp[1];
                byte st = resp[2];
                if (st == 26)
                {
                    asleep = true;
                    return true;
                }
                if (bat >= 0 && bat <= 100 && (st == 2 || st == 3 || st == 4))
                {
                    percent = bat;
                    charging = (st == 3);
                    return true;
                }
            }
            return false;
        }

        private static string FormatHex(byte[] buf, int max)
        {
            int n = Math.Min(buf.Length, max);
            string[] parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = buf[i].ToString("X2");
            return string.Join(" ", parts);
        }
    }
}
