using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    // ASUS ROG / TUF wireless mice, keyboards, ROG OMNI receiver (0B05:1ACE), and Strix Go 2.4 headset
    // (reference: HeyOkay/HaloBattery providers/asus.py).
    public sealed class AsusRogHandler : IHidProtocolHandler
    {
        private const ushort VID_ASUS = 0x0B05;
        private const ushort PID_OMNI_RECEIVER = 0x1ACE;
        private const ushort PID_STRIX_GO_DONGLE = 0x18D6;
        private const ushort PID_STRIX_GO_CABLE = 0x18D7;

        public string Name
        {
            get { return "ASUS ROG / TUF HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_ASUS) return false;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00) return true;
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            if (group.ProductId == PID_STRIX_GO_DONGLE || group.ProductId == PID_STRIX_GO_CABLE)
            {
                QueryStrixGo(group, results);
                return results;
            }

            if (group.ProductId == PID_OMNI_RECEIVER)
            {
                QueryOmniReceiver(group, results);
                return results;
            }

            // Standard ROG / TUF wireless mouse or keyboard (Report ID 0x00, cmd 0x12 0x07)
            HidDeviceNode vendorNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00 && n.OutputReportLength >= 32 && n.InputReportLength >= 32)
                {
                    vendorNode = n;
                    break;
                }
            }
            if (vendorNode == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(vendorNode.DevicePath, true, true, true))
            {
                if (handle == null) return results;
                byte[] req = new byte[Math.Max(65, (int)vendorNode.OutputReportLength)];
                req[0] = 0x00;
                req[1] = 0x12;
                req[2] = 0x07;
                byte[] resp = handle.SendAndReceive(req, Math.Max(64, (int)vendorNode.InputReportLength),
                    delegate(byte[] r) { return r != null && r.Length >= 10; }, 180, 3);

                int pct;
                bool chg;
                if (ParseRogMouseReply(resp, 0x00, out pct, out chg))
                {
                    DeviceBatteryInfo info = new DeviceBatteryInfo();
                    info.Id = string.Format("ASUS_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, group.GroupKey);
                    info.ContainerId = group.ContainerId;
                    info.Name = group.DisplayName;
                    info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
                    info.Channel = group.InferredChannel;
                    info.BatteryPercent = pct;
                    info.IsCharging = chg;
                    info.ProtocolName = "ASUS ROG HID";
                    info.Priority = 92;
                    results.Add(info);
                }
            }

            return results;
        }

        private static void QueryOmniReceiver(HidDeviceGroup group, List<DeviceBatteryInfo> results)
        {
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0xFF01 && n.OutputReportLength >= 32)
                {
                    using (HidDeviceHandle h = HidDeviceHandle.Open(n.DevicePath, true, true, true))
                    {
                        if (h == null) continue;
                        byte[] req = new byte[Math.Max(64, (int)n.OutputReportLength)];
                        req[0] = 0x03; req[1] = 0x12; req[2] = 0x07;
                        byte[] resp = h.SendAndReceive(req, Math.Max(64, (int)n.InputReportLength), null, 180, 3);
                        int pct; bool chg;
                        if (ParseRogMouseReply(resp, 0x03, out pct, out chg))
                        {
                            DeviceBatteryInfo m = new DeviceBatteryInfo();
                            m.Id = "ASUS_OMNI_MOUSE_" + group.GroupKey;
                            m.Name = "ROG OMNI 鼠标";
                            m.Category = DeviceCategory.Mouse;
                            m.Channel = ConnectionChannel.Wireless24G;
                            m.BatteryPercent = pct;
                            m.IsCharging = chg;
                            m.ProtocolName = "ASUS ROG OMNI HID";
                            m.Priority = 92;
                            results.Add(m);
                        }
                    }
                }
                else if (n.UsagePage == 0xFF00 && n.OutputReportLength >= 32)
                {
                    using (HidDeviceHandle h = HidDeviceHandle.Open(n.DevicePath, true, true, true))
                    {
                        if (h == null) continue;
                        byte[] req = new byte[Math.Max(64, (int)n.OutputReportLength)];
                        req[0] = 0x02; req[1] = 0x12; req[2] = 0x01;
                        byte[] resp = h.SendAndReceive(req, Math.Max(64, (int)n.InputReportLength), null, 180, 3);
                        if (resp != null && resp.Length >= 10 && resp[0] == 0x02 && resp[1] == 0x12 && resp[2] == 0x01 && resp[6] >= 1 && resp[6] <= 100)
                        {
                            DeviceBatteryInfo kb = new DeviceBatteryInfo();
                            kb.Id = "ASUS_OMNI_KB_" + group.GroupKey;
                            kb.Name = "ROG OMNI 键盘";
                            kb.Category = DeviceCategory.Keyboard;
                            kb.Channel = ConnectionChannel.Wireless24G;
                            kb.BatteryPercent = resp[6];
                            kb.IsCharging = resp[9] != 0;
                            kb.ProtocolName = "ASUS ROG OMNI HID";
                            kb.Priority = 92;
                            results.Add(kb);
                        }
                    }
                }
            }
        }

        private static void QueryStrixGo(HidDeviceGroup group, List<DeviceBatteryInfo> results)
        {
            HidDeviceNode featNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.FeatureReportLength >= 64) { featNode = n; break; }
            }
            if (featNode == null) return;

            using (HidDeviceHandle h = HidDeviceHandle.Open(featNode.DevicePath, true, true, false))
            {
                if (h == null) return;
                byte[] req = new byte[Math.Max(64, (int)featNode.FeatureReportLength)];
                byte[] prefix = new byte[] { 0xFF, 0x08, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x03, 0x52, 0x01 };
                Array.Copy(prefix, req, prefix.Length);
                if (!h.SetFeature(req)) return;

                byte[] resp = new byte[req.Length];
                resp[0] = 0xFF;
                if (!h.GetFeature(resp)) return;
                if (resp.Length < 13 || resp[0] != 0xFF || resp[1] != 0x1B) return;

                int mv = resp[11] | (resp[12] << 8);
                if (mv < 2500 || mv > 4500) return;
                int pct = mv >= 4000 ? 100 : (mv >= 3800 ? 75 : (mv >= 3650 ? 50 : (mv >= 3500 ? 25 : 10)));
                bool chg = (resp[9] == 0x0A) || (group.ProductId == PID_STRIX_GO_CABLE);

                DeviceBatteryInfo hs = new DeviceBatteryInfo();
                hs.Id = "ASUS_STRIX_GO_" + group.GroupKey;
                hs.ContainerId = group.ContainerId;
                hs.Name = "ROG Strix Go 2.4";
                hs.Category = DeviceCategory.Headset;
                hs.Channel = group.ProductId == PID_STRIX_GO_CABLE ? ConnectionChannel.UsbWired : ConnectionChannel.Wireless24G;
                hs.BatteryPercent = pct;
                hs.IsCharging = chg;
                hs.ProtocolName = "ASUS ROG Strix Go HID";
                hs.Priority = 92;
                hs.ExtraInfo = mv + "mV";
                results.Add(hs);
            }
        }

        public static bool ParseRogMouseReply(byte[] resp, byte expectedReportId, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 10) return false;

            int off = -1;
            if (expectedReportId == 0x00)
            {
                if (resp[0] == 0x12 && resp[1] == 0x07) off = 0;
                else if (resp[0] == 0x00 && resp[1] == 0x12 && resp[2] == 0x07) off = 1;
            }
            else if (resp[0] == expectedReportId && resp[1] == 0x12 && resp[2] == 0x07)
            {
                off = 1;
            }
            if (off < 0 || resp.Length < off + 10) return false;

            int raw = resp[off + 4];
            bool chg = resp[off + 9] != 0;
            if (raw == 0 && !chg) return false; // 0 without charging = mouse in standby

            percent = raw <= 4 ? raw * 25 : Math.Min(100, raw);
            charging = chg;
            return true;
        }
    }
}
