using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    // CompX report-8 battery protocol used by Pulsar, ATK/VXE, G-Wolves 8K, Hitscan Hyperlight,
    // LAMZU Atlantis/Thorn, Redragon M991, and VGN Dragonfly (reference: HeyOkay/HaloBattery providers/compx.py).
    public sealed class CompxProtocolHandler : IHidProtocolHandler
    {
        private static readonly ushort[] CompxVids = new ushort[]
        {
            0x3554, // CompX (ATK/VXE, Pulsar, LAMZU Atlantis, Redragon M991, VGN Dragonfly)
            0x373B, // ATK / VXE 8K
            0x3710, // Pulsar CrazyLight / X2
            0x33E4, // G-Wolves 8K
            0x3770  // Hitscan Hyperlight
        };

        private const int PayloadLen = 17;
        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, DeviceBatteryInfo> _cached = new Dictionary<string, DeviceBatteryInfo>();

        public string Name
        {
            get { return "CompX Report-8 HID (Pulsar / ATK / VXE / G-Wolves / Hitscan)"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            bool vidMatch = false;
            for (int i = 0; i < CompxVids.Length; i++)
            {
                if (group.VendorId == CompxVids[i]) { vidMatch = true; break; }
            }
            if (!vidMatch) return false;

            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF02 && n.Usage == 0x0002 && n.OutputReportLength >= PayloadLen)
                {
                    return true;
                }
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode target = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF02 && n.Usage == 0x0002 && n.OutputReportLength >= PayloadLen && n.InputReportLength >= PayloadLen)
                {
                    target = n;
                    break;
                }
            }
            if (target == null) return results;

            string cacheKey = !string.IsNullOrEmpty(group.GroupKey)
                ? group.GroupKey
                : string.Format("{0:X4}_{1:X4}", group.VendorId, group.ProductId);

            using (HidDeviceHandle handle = HidDeviceHandle.Open(target.DevicePath, true, true, true))
            {
                if (handle != null)
                {
                    byte[] req = BuildRequest(target.OutputReportLength);
                    if (handle.WriteReport(req, 180))
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            byte[] resp = handle.ReadReport(Math.Max(PayloadLen, (int)target.InputReportLength), 250);
                            if (resp == null) break;

                            int pct;
                            bool chg;
                            int mv;
                            if (ParsePowerResponse(resp, out pct, out chg, out mv))
                            {
                                DeviceBatteryInfo dev = BuildDevice(group, pct, chg, false, string.Format("CompX 0x08/0x04 ({0}mV)", mv));
                                lock (_cacheLock) { _cached[cacheKey] = dev.Clone(); }
                                if (diagLog != null)
                                {
                                    diagLog.Add(string.Format("  [CompX] \"{0}\" Bat={1}% Chg={2} {3}mV", dev.Name, pct, chg, mv));
                                }
                                results.Add(dev);
                                return results;
                            }
                        }
                    }
                }
            }

            // Mouse idle/asleep: retain icon with last known battery %
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
            info.Id = string.Format("COMPX_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, group.GroupKey);
            info.ContainerId = group.ContainerId;
            info.Name = group.DisplayName;
            info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
            info.Channel = group.InferredChannel;
            info.BatteryPercent = pct;
            info.IsCharging = charging && !asleep;
            info.IsFull = !asleep && pct >= 100;
            info.IsAsleep = asleep;
            info.ProtocolName = "CompX 2.4G/USB HID";
            info.Priority = 92;
            info.ExtraInfo = extra;
            return info;
        }

        public static byte Checksum(byte[] frame, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum += frame[i];
            return (byte)((0x55 - (sum & 0xFF)) & 0xFF);
        }

        public static byte[] BuildRequest(int reportLength)
        {
            int len = Math.Max(PayloadLen, reportLength);
            byte[] frame = new byte[len];
            frame[0] = 0x08;
            frame[1] = 0x04;
            frame[PayloadLen - 1] = Checksum(frame, PayloadLen - 1);
            return frame;
        }

        public static bool ParsePowerResponse(byte[] resp, out int percent, out bool charging, out int millivolts)
        {
            percent = -1;
            charging = false;
            millivolts = 0;
            if (resp == null || resp.Length < PayloadLen) return false;
            if (resp[0] != 0x08 || resp[1] != 0x04) return false;
            if (resp[PayloadLen - 1] != Checksum(resp, PayloadLen - 1)) return false;
            int level = resp[6];
            if (level < 0 || level > 100) return false;
            percent = level;
            charging = resp[7] != 0;
            millivolts = (resp[8] << 8) | resp[9];
            return true;
        }
    }
}
