using System;
using System.Collections.Generic;
using System.Text;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class LogitechHidppHandler : IHidProtocolHandler
    {
        private const ushort VID_LOGITECH = 0x046D;
        private const byte REPORT_SHORT = 0x10;
        private const byte REPORT_LONG = 0x11;
        private const byte SW_ID = 0x0A; // Distinct software ID for coexistence with G HUB / Options+

        public string Name
        {
            get { return "Logitech HID++ 2.0"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_LOGITECH) return false;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF00 && (n.InputReportLength == 7 || n.InputReportLength == 20))
                {
                    return true;
                }
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode shortNode = null;
            HidDeviceNode longNode = null;

            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage != 0xFF00) continue;
                if (n.InputReportLength == 20 && n.OutputReportLength == 20) longNode = n;
                else if (n.InputReportLength == 7 && n.OutputReportLength == 7) shortNode = n;
            }

            HidDeviceNode target = longNode ?? shortNode;
            if (target == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(target.DevicePath, true, true, true))
            {
                if (handle == null)
                {
                    if (diagLog != null) diagLog.Add("  [Logitech HID++] Failed to open " + target.DevicePath);
                    return results;
                }

                int reportLen = target.OutputReportLength;
                byte reportId = reportLen >= 20 ? REPORT_LONG : REPORT_SHORT;

                bool isReceiver = group.InferredChannel == ConnectionChannel.Wireless24G;
                byte[] indices = isReceiver
                    ? new byte[] { 1, 2, 3, 4, 5, 6 }
                    : new byte[] { 0xFF, 0x01 };

                foreach (byte devIdx in indices)
                {
                    DeviceBatteryInfo dev = ProbeDeviceIndex(handle, reportId, reportLen, devIdx, group, diagLog);
                    if (dev != null)
                    {
                        results.Add(dev);
                        if (!isReceiver) break;
                    }
                }
            }

            return results;
        }

        private static DeviceBatteryInfo ProbeDeviceIndex(
            HidDeviceHandle handle,
            byte reportId,
            int reportLen,
            byte devIdx,
            HidDeviceGroup group,
            List<string> diagLog)
        {
            // Ping via Root feature (0x0000) func 0x01 or GetFeature for 0x1004 / 0x1000 / 0x1001
            int idx1004 = GetFeatureIndex(handle, reportId, reportLen, devIdx, 0x1004);
            int idx1000 = idx1004 <= 0 ? GetFeatureIndex(handle, reportId, reportLen, devIdx, 0x1000) : -1;
            int idx1001 = (idx1004 <= 0 && idx1000 <= 0) ? GetFeatureIndex(handle, reportId, reportLen, devIdx, 0x1001) : -1;

            if (idx1004 <= 0 && idx1000 <= 0 && idx1001 <= 0)
            {
                // Fallback to HID++ 1.0 registers (0x07 Battery Status, 0x0D Battery Mileage) for legacy Unifying devices
                int legacyPct;
                bool legacyChg;
                if (TryQueryHidpp10Battery(handle, devIdx, out legacyPct, out legacyChg))
                {
                    DeviceBatteryInfo legacy = new DeviceBatteryInfo();
                    legacy.Id = string.Format("LOGI_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, devIdx);
                    legacy.ContainerId = group.InferredChannel == ConnectionChannel.Wireless24G ? string.Empty : group.ContainerId;
                    legacy.Name = group.DisplayName;
                    legacy.Category = group.InferredCategory;
                    legacy.Channel = group.InferredChannel;
                    legacy.BatteryPercent = legacyPct;
                    legacy.IsCharging = legacyChg;
                    legacy.ProtocolName = "Logitech HID++ 1.0";
                    legacy.Priority = 88;
                    legacy.ExtraInfo = "Slot " + devIdx;
                    return legacy;
                }
                return null;
            }

            int percent = -1;
            bool charging = false;
            bool isFull = false;
            string extra = string.Empty;

            if (idx1004 > 0)
            {
                byte[] resp = Transact(handle, reportId, reportLen, devIdx, (byte)idx1004, 0x10);
                if (ParseUnifiedBattery(resp, out percent, out charging, out isFull))
                {
                    extra = "UnifiedBattery(0x1004)";
                }
            }
            if (percent < 0 && idx1000 > 0)
            {
                byte[] resp = Transact(handle, reportId, reportLen, devIdx, (byte)idx1000, 0x00);
                if (ParseBatteryStatus(resp, out percent, out charging, out isFull))
                {
                    extra = "BatteryStatus(0x1000)";
                }
            }
            if (percent < 0 && idx1001 > 0)
            {
                byte[] resp = Transact(handle, reportId, reportLen, devIdx, (byte)idx1001, 0x00);
                int mv;
                if (ParseBatteryVoltage(resp, out percent, out charging, out mv))
                {
                    extra = string.Format("BatteryVoltage(0x1001, {0}mV)", mv);
                }
            }

            if (percent < 0) return null;

            string devName = group.DisplayName;
            DeviceCategory cat = group.InferredCategory;

            int idxName = GetFeatureIndex(handle, reportId, reportLen, devIdx, 0x0005);
            if (idxName > 0)
            {
                string queriedName = QueryDeviceName(handle, reportId, reportLen, devIdx, (byte)idxName);
                if (!string.IsNullOrEmpty(queriedName)) devName = queriedName;
                DeviceCategory queriedCat = QueryDeviceType(handle, reportId, reportLen, devIdx, (byte)idxName);
                if (queriedCat != DeviceCategory.Unknown) cat = queriedCat;
            }

            DeviceBatteryInfo info = new DeviceBatteryInfo();
            info.Id = string.Format("LOGI_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, devIdx);
            info.ContainerId = group.InferredChannel == ConnectionChannel.Wireless24G ? string.Empty : group.ContainerId;
            info.Name = devName;
            info.Category = cat;
            info.Channel = group.InferredChannel;
            info.BatteryPercent = percent;
            info.IsCharging = charging;
            info.IsFull = isFull;
            info.ProtocolName = "Logitech HID++ 2.0";
            info.Priority = 95;
            info.ExtraInfo = string.Format("Slot {0} {1}", devIdx, extra);

            if (diagLog != null)
            {
                diagLog.Add(string.Format("  [Logitech HID++] Slot {0} \"{1}\" {2}% Charging={3} ({4})",
                    devIdx, info.Name, info.BatteryPercent, info.IsCharging, extra));
            }
            return info;
        }

        private static int GetFeatureIndex(HidDeviceHandle handle, byte reportId, int reportLen, byte devIdx, ushort featureId)
        {
            byte[] resp = Transact(handle, reportId, reportLen, devIdx, 0x00, 0x00, (byte)(featureId >> 8), (byte)(featureId & 0xFF));
            if (resp == null || resp.Length < 5) return -1;
            if (resp[2] == 0xFF) return -1; // HID++ error
            return resp[4] > 0 ? resp[4] : -1;
        }

        private static string QueryDeviceName(HidDeviceHandle handle, byte reportId, int reportLen, byte devIdx, byte nameFeatIdx)
        {
            byte[] lenResp = Transact(handle, reportId, reportLen, devIdx, nameFeatIdx, 0x00);
            if (lenResp == null || lenResp.Length < 5) return string.Empty;
            int totalLen = lenResp[4];
            if (totalLen <= 0 || totalLen > 64) return string.Empty;

            StringBuilder sb = new StringBuilder();
            int offset = 0;
            while (offset < totalLen)
            {
                byte[] chunk = Transact(handle, reportId, reportLen, devIdx, nameFeatIdx, 0x10, (byte)offset);
                if (chunk == null || chunk.Length <= 4) break;
                int avail = chunk.Length - 4;
                for (int i = 0; i < avail && offset < totalLen; i++)
                {
                    byte b = chunk[4 + i];
                    if (b == 0) { offset = totalLen; break; }
                    sb.Append((char)b);
                    offset++;
                }
            }
            return sb.ToString().Trim();
        }

        private static DeviceCategory QueryDeviceType(HidDeviceHandle handle, byte reportId, int reportLen, byte devIdx, byte nameFeatIdx)
        {
            byte[] resp = Transact(handle, reportId, reportLen, devIdx, nameFeatIdx, 0x20);
            if (resp == null || resp.Length < 5) return DeviceCategory.Unknown;
            // HID++ DeviceType: 0=Keyboard, 1=Remote, 2=Numpad, 3=Mouse, 4=Touchpad, 5=Trackball, 6=Presenter, 7=Receiver, 8=Headset
            switch (resp[4])
            {
                case 0:
                case 2:
                    return DeviceCategory.Keyboard;
                case 3:
                case 4:
                case 5:
                    return DeviceCategory.Mouse;
                case 8:
                    return DeviceCategory.Headset;
                default:
                    return DeviceCategory.Unknown;
            }
        }

        private static byte[] Transact(HidDeviceHandle handle, byte reportId, int reportLen, byte devIdx, byte featIdx, byte funcAndSw, params byte[] args)
        {
            byte[] pkt = new byte[reportLen];
            pkt[0] = reportId;
            pkt[1] = devIdx;
            pkt[2] = featIdx;
            pkt[3] = (byte)((funcAndSw & 0xF0) | (SW_ID & 0x0F));
            for (int i = 0; i < args.Length && (4 + i) < pkt.Length; i++)
            {
                pkt[4 + i] = args[i];
            }

            return handle.SendAndReceive(pkt, reportLen, delegate(byte[] r)
            {
                if (r.Length < 5) return false;
                if (r[1] != devIdx) return false;
                if (r[2] == 0xFF && r[3] == featIdx) return true; // Error reply
                return r[2] == featIdx && (r[3] & 0x0F) == SW_ID;
            }, 160, 6);
        }

        public static bool ParseUnifiedBattery(byte[] resp, out int percent, out bool charging, out bool full)
        {
            percent = -1;
            charging = false;
            full = false;
            if (resp == null || resp.Length < 7 || resp[2] == 0xFF) return false;
            int soc = resp[4];
            if (soc > 100) return false;
            byte status = resp[6]; // 0=discharging, 1=recharging, 2=charge in final stage, 3=charge complete
            percent = soc;
            charging = (status == 1 || status == 2);
            full = (status == 3);
            if (full && percent == 0) percent = 100;
            return true;
        }

        public static bool ParseBatteryStatus(byte[] resp, out int percent, out bool charging, out bool full)
        {
            percent = -1;
            charging = false;
            full = false;
            if (resp == null || resp.Length < 7 || resp[2] == 0xFF) return false;
            int level = resp[4];
            if (level > 100) return false;
            byte status = resp[6]; // 1=recharging, 2=nearly full, 3=full, 4=slow recharge
            percent = level;
            charging = (status == 1 || status == 2 || status == 4);
            full = (status == 3);
            return true;
        }

        public static bool ParseBatteryVoltage(byte[] resp, out int percent, out bool charging, out int millivolts)
        {
            percent = -1;
            charging = false;
            millivolts = 0;
            if (resp == null || resp.Length < 7 || resp[2] == 0xFF) return false;
            millivolts = (resp[4] << 8) | resp[5];
            if (millivolts < 2500 || millivolts > 4500) return false;
            byte flags = resp[6];
            charging = (flags & 0x80) != 0;
            // Li-ion 3.4V..4.18V approximation
            int pct = (int)Math.Round((millivolts - 3400.0) / (4180.0 - 3400.0) * 100.0);
            percent = Math.Max(0, Math.Min(100, pct));
            return true;
        }

        private static bool TryQueryHidpp10Battery(HidDeviceHandle handle, byte devIdx, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;

            // Register 0x0D (Battery Mileage)
            byte[] reg0D = Transact(handle, REPORT_SHORT, 7, devIdx, 0x81, 0x0D);
            if (reg0D != null && reg0D.Length >= 7 && reg0D[2] == 0x81 && reg0D[3] == 0x0D && reg0D[4] <= 100)
            {
                percent = reg0D[4];
                charging = (reg0D[6] == 0x03 || reg0D[6] == 0x02);
                return true;
            }

            // Register 0x07 (Battery Status: level 1..7)
            byte[] reg07 = Transact(handle, REPORT_SHORT, 7, devIdx, 0x81, 0x07);
            if (reg07 != null && reg07.Length >= 7 && reg07[2] == 0x81 && reg07[3] == 0x07 && reg07[4] > 0)
            {
                int raw = reg07[4];
                if (raw == 1) percent = 10;
                else if (raw == 3) percent = 35;
                else if (raw == 5) percent = 70;
                else if (raw == 7) percent = 100;
                else percent = Math.Min(100, raw * 15);
                charging = (reg07[5] == 0x22 || reg07[5] == 0x24 || reg07[5] == 0x25);
                return true;
            }

            return false;
        }
    }
}
