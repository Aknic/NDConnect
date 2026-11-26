using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class EvisionTelinkHandler : IHidProtocolHandler
    {
        private const ushort VID_TELINK_EVISION = 0x320F;

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, DateTime> _lastQueryTime = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, DeviceBatteryInfo> _cachedResult = new Dictionary<string, DeviceBatteryInfo>();
        private static int _seqCounter = 0;
        public static bool ForceBypassCache { get; set; }

        public string Name
        {
            get { return "EVision / Telink 2.4G HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_TELINK_EVISION) return false;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF1C && n.OutputReportLength >= 32 && n.InputReportLength >= 32)
                {
                    return true;
                }
            }
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
                        (DateTime.Now - lastTime).TotalSeconds < 90)
                    {
                        results.Add(cached.Clone());
                        return results;
                    }
                }
            }

            HidDeviceNode ff1cNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF1C && n.OutputReportLength >= 32 && n.InputReportLength >= 32)
                {
                    ff1cNode = n;
                    break;
                }
            }

            if (ff1cNode == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(ff1cNode.DevicePath, true, true, true))
            {
                if (handle != null)
                {
                    int seq = System.Threading.Interlocked.Increment(ref _seqCounter) & 0xFFFF;
                    if (seq == 0) seq = 1;

                    byte[] pkt = BuildEvisionBatteryQuery(ff1cNode.OutputReportLength, seq);
                    if (handle.WriteReport(pkt, 200))
                    {
                        byte[] resp = null;
                        DateTime deadline = DateTime.UtcNow.AddMilliseconds(900);
                        while (DateTime.UtcNow < deadline)
                        {
                            byte[] candidate = handle.ReadReport(ff1cNode.InputReportLength, 120);
                            if (candidate == null) break;
                            int pct;
                            bool chg;
                            if (ParseEvisionBatteryResponse(candidate, seq, out pct, out chg))
                            {
                                resp = candidate;
                                DeviceBatteryInfo dev = BuildDevice(group, pct, chg, false, "EVision 0x04/0x1A");
                                lock (_cacheLock)
                                {
                                    _lastQueryTime[cacheKey] = DateTime.Now;
                                    _cachedResult[cacheKey] = dev.Clone();
                                }
                                if (diagLog != null)
                                {
                                    diagLog.Add(string.Format("  [EVision Diag] UP=0xFF1C seq=0x{0:X4} resp: {1} -> {2}%",
                                        seq, FormatHex(resp, 12), pct));
                                }
                                results.Add(dev);
                                return results;
                            }
                        }
                        if (diagLog != null)
                        {
                            diagLog.Add(string.Format("  [EVision Diag] UP=0xFF1C seq=0x{0:X4} no valid reply (asleep)", seq));
                        }
                    }
                    else if (diagLog != null)
                    {
                        diagLog.Add("  [EVision Diag] WriteFile(04/1A) failed");
                    }
                }
                else if (diagLog != null)
                {
                    diagLog.Add("  [EVision] Cannot open UP=0xFF1C");
                }
            }

            // Receiver is plugged in, but keyboard is asleep -> retain visible Asleep state
            int fallbackPct = -1;
            lock (_cacheLock)
            {
                DeviceBatteryInfo prev;
                if (_cachedResult.TryGetValue(cacheKey, out prev) && prev.BatteryPercent >= 0)
                {
                    fallbackPct = prev.BatteryPercent;
                }
            }
            DeviceBatteryInfo asleepDev = BuildDevice(group, fallbackPct, false, true, "2.4G 休眠/等待唤醒");
            results.Add(asleepDev);
            return results;
        }

        private static DeviceBatteryInfo BuildDevice(HidDeviceGroup group, int pct, bool chg, bool asleep, string extra)
        {
            DeviceBatteryInfo info = new DeviceBatteryInfo();
            info.Id = string.Format("EVISION_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, group.GroupKey);
            info.ContainerId = group.ContainerId;
            info.Name = group.DisplayName;
            info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Keyboard : group.InferredCategory;
            info.GlyphType = info.Category == DeviceCategory.Mouse ? DeviceGlyphType.Mouse : DeviceGlyphType.Keyboard;
            info.Channel = group.InferredChannel;
            info.BatteryPercent = pct;
            info.IsCharging = chg && !asleep;
            info.IsFull = !asleep && pct >= 100;
            info.IsAsleep = asleep;
            info.ProtocolName = "EVision / Telink HID";
            info.Priority = 90;
            info.ExtraInfo = extra;
            return info;
        }

        public static byte[] BuildEvisionBatteryQuery(int length, int sequence)
        {
            // HaloBattery / ATK HUB Evision packet: [0]=0x04 ReportID, [1..2]=sequence LE, [3]=0x1A (Battery command)
            byte[] pkt = new byte[Math.Max(32, length)];
            pkt[0] = 0x04;
            pkt[1] = (byte)(sequence & 0xFF);
            pkt[2] = (byte)((sequence >> 8) & 0xFF);
            pkt[3] = 0x1A;
            return pkt;
        }

        public static bool ParseEvisionBatteryResponse(byte[] resp, int sequence, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 10) return false;
            if (resp[0] != 0x04) return false;
            if (resp[1] != (byte)(sequence & 0xFF) || resp[2] != (byte)((sequence >> 8) & 0xFF)) return false;
            if (resp[3] != 0x1A || resp[5] != 0x00 || resp[6] != 0x00) return false;

            int level = resp[8] & 0x7F;
            byte state = resp[9];
            if (level < 1 || level > 100) return false;
            if (state != 0 && state != 1 && state != 2) return false;

            percent = level;
            charging = (state == 1 || state == 2) || ((resp[8] & 0x80) != 0);
            return true;
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
