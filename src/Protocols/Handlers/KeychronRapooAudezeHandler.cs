using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    // Keychron (0x3434), Rapoo (0x24AE), and Audeze Maxwell (0x3329) HID battery protocols
    // (reference: HeyOkay/HaloBattery providers/keychron.py, providers/rapoo.py, docs/protocols.md).
    public sealed class KeychronRapooAudezeHandler : IHidProtocolHandler
    {
        private const ushort VID_KEYCHRON = 0x3434;
        private const ushort VID_RAPOO = 0x24AE;
        private const ushort VID_AUDEZE = 0x3329;

        public string Name
        {
            get { return "Keychron / Rapoo / Audeze HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            return group.VendorId == VID_KEYCHRON || group.VendorId == VID_RAPOO || group.VendorId == VID_AUDEZE;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            if (group.VendorId == VID_KEYCHRON) return QueryKeychron(group);
            if (group.VendorId == VID_RAPOO) return QueryRapoo(group);
            if (group.VendorId == VID_AUDEZE) return QueryAudeze(group);

            return results;
        }

        private static List<DeviceBatteryInfo> QueryKeychron(HidDeviceGroup group)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode vendorNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if ((n.UsagePage >= 0xFF00 || n.InstanceId.ToUpperInvariant().Contains("MI_04")) &&
                    n.FeatureReportLength >= 64 && n.InputReportLength >= 64)
                {
                    vendorNode = n;
                    break;
                }
            }
            if (vendorNode == null) return results;

            using (HidDeviceHandle h = HidDeviceHandle.Open(vendorNode.DevicePath, true, true, true))
            {
                if (h == null) return results;
                byte[] req = new byte[Math.Max(64, (int)vendorNode.FeatureReportLength)];
                req[0] = 0xB3;
                req[1] = 0x06;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (!h.SetFeature(req)) break;
                    byte[] resp = h.ReadReport(Math.Max(64, (int)vendorNode.InputReportLength), 350);
                    int pct;
                    if (ParseKeychronReply(resp, out pct))
                    {
                        DeviceBatteryInfo info = new DeviceBatteryInfo();
                        info.Id = string.Format("KEYCHRON_{0:X4}_{1}", group.ProductId, group.GroupKey);
                        info.ContainerId = group.ContainerId;
                        info.Name = group.DisplayName;
                        info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
                        info.Channel = group.InferredChannel;
                        info.BatteryPercent = pct;
                        info.IsCharging = group.InferredChannel == ConnectionChannel.UsbWired;
                        info.ProtocolName = "Keychron HID";
                        info.Priority = 90;
                        results.Add(info);
                        return results;
                    }
                }
            }
            return results;
        }

        private static List<DeviceBatteryInfo> QueryRapoo(HidDeviceGroup group)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            // Rapoo pushes battery passively on vendor input report 0x07 or 0xBD without writes.
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage < 0xFF00 || n.InputReportLength < 9) continue;
                using (HidDeviceHandle h = HidDeviceHandle.Open(n.DevicePath, true, false, true))
                {
                    if (h == null) continue;
                    for (int i = 0; i < 3; i++)
                    {
                        byte[] resp = h.ReadReport(n.InputReportLength, 200);
                        int pct;
                        bool chg;
                        if (ParseRapooReport(resp, out pct, out chg))
                        {
                            DeviceBatteryInfo info = new DeviceBatteryInfo();
                            info.Id = string.Format("RAPOO_{0:X4}_{1}", group.ProductId, group.GroupKey);
                            info.ContainerId = group.ContainerId;
                            info.Name = group.DisplayName;
                            info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
                            info.Channel = group.InferredChannel;
                            info.BatteryPercent = pct;
                            info.IsCharging = chg;
                            info.ProtocolName = "Rapoo Push HID";
                            info.Priority = 90;
                            results.Add(info);
                            return results;
                        }
                    }
                }
            }
            return results;
        }

        private static List<DeviceBatteryInfo> QueryAudeze(HidDeviceGroup group)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            // Per HaloBattery docs/protocols.md: when dongle product string is "Audeze Maxwell Dongle", headset is off.
            foreach (HidDeviceNode n in group.Nodes)
            {
                if ((n.Product ?? string.Empty).IndexOf("Dongle", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return results;
                }
            }

            HidDeviceNode ff13 = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF13 && n.OutputReportLength >= 8 && n.InputReportLength >= 8) { ff13 = n; break; }
            }
            if (ff13 == null) return results;

            using (HidDeviceHandle h = HidDeviceHandle.Open(ff13.DevicePath, true, true, true))
            {
                if (h == null) return results;
                byte[] pkt = new byte[Math.Max(64, (int)ff13.OutputReportLength)];
                // HeadsetControl attribute 0x0CD6 battery query
                byte[] cmd = new byte[] { 0x06, 0x1D, 0x04, 0x00, 0x00, 0xD6, 0x0C };
                Array.Copy(cmd, pkt, cmd.Length);
                byte[] resp = h.SendAndReceive(pkt, Math.Max(64, (int)ff13.InputReportLength),
                    delegate(byte[] r) { return r != null && r.Length >= 7 && r[0] == 0x05 && r[1] == 0x5D && r[4] == 0xD6 && r[5] == 0x0C; }, 220, 4);
                if (resp != null && resp[6] <= 100)
                {
                    DeviceBatteryInfo hs = new DeviceBatteryInfo();
                    hs.Id = "AUDEZE_MAXWELL";
                    hs.ContainerId = group.ContainerId;
                    hs.Name = "Audeze Maxwell";
                    hs.Category = DeviceCategory.Headset;
                    hs.Channel = group.ProductId == 0x4B1A ? ConnectionChannel.UsbWired : ConnectionChannel.Wireless24G;
                    hs.BatteryPercent = resp[6];
                    hs.IsCharging = group.ProductId == 0x4B1A;
                    hs.ProtocolName = "Audeze Maxwell HID";
                    hs.Priority = 92;
                    results.Add(hs);
                }
            }
            return results;
        }

        public static bool ParseKeychronReply(byte[] resp, out int percent)
        {
            percent = -1;
            if (resp == null || resp.Length <= 20) return false;
            if (resp[0] != 0xB4 || resp[1] != 0x06) return false;
            int lvl = resp[20];
            if (lvl < 0 || lvl > 100) return false;
            percent = lvl;
            return true;
        }

        public static bool ParseRapooReport(byte[] resp, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 9) return false;

            if (resp[0] == 0x07 && (resp[6] == 1 || resp[6] == 2) && resp[7] <= 100)
            {
                percent = resp[7];
                charging = resp[6] == 2;
                return true;
            }
            if (resp[0] == 0xBD && resp[1] == 0x01 && (resp[7] == 1 || resp[7] == 2) && resp[8] <= 100)
            {
                percent = resp[8];
                charging = resp[7] == 2;
                return true;
            }
            return false;
        }
    }
}
