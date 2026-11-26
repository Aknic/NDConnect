using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class CorsairHyperXHandler : IHidProtocolHandler
    {
        private const ushort VID_CORSAIR = 0x1B1C;
        private const ushort VID_HYPERX_KINGSTON = 0x0951;
        private const ushort VID_HYPERX_HP = 0x03F0;

        public string Name
        {
            get { return "Corsair / HyperX HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_CORSAIR && group.VendorId != VID_HYPERX_KINGSTON && group.VendorId != VID_HYPERX_HP)
            {
                return false;
            }
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00) return true;
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            if (group.VendorId == VID_CORSAIR)
            {
                return QueryCorsair(group, diagLog);
            }
            return QueryHyperX(group, diagLog);
        }

        private static List<DeviceBatteryInfo> QueryCorsair(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            // 1. Corsair Void Elite (0x1B1C:0x0A55, UP=0xFFC5): C9 64 -> 64 00 <level> B1 <status>
            HidDeviceNode ffc5 = FindVendorNode(group, 0xFFC5);
            if (ffc5 != null)
            {
                using (HidDeviceHandle h = HidDeviceHandle.Open(ffc5.DevicePath, true, true, true))
                {
                    if (h != null)
                    {
                        byte[] pkt = new byte[Math.Max(64, (int)ffc5.OutputReportLength)];
                        pkt[0] = 0xC9;
                        pkt[1] = 0x64;
                        byte[] resp = h.SendAndReceive(pkt, Math.Max(64, (int)ffc5.InputReportLength),
                            delegate(byte[] r) { return r != null && r.Length >= 5 && r[0] == 0x64 && r[3] == 0xB1; }, 160, 3);
                        int pct;
                        bool chg;
                        if (ParseCorsairVoidElite(resp, out pct, out chg))
                        {
                            results.Add(BuildDevice(group, pct, chg, "Corsair Void HID"));
                            return results;
                        }
                    }
                }
            }

            // 2. Corsair Slipstream / HS80 / Virtuoso / Dark Core (UP=0xFF42): 02 <target> 02 0F -> 01 <chan> 02 00 <lo> <hi> (tenths of %)
            HidDeviceNode ff42 = FindVendorNode(group, 0xFF42);
            if (ff42 == null) ff42 = FindAnyVendorIoNode(group);
            if (ff42 != null)
            {
                using (HidDeviceHandle h = HidDeviceHandle.Open(ff42.DevicePath, true, true, true))
                {
                    if (h != null)
                    {
                        byte target = group.InferredChannel == ConnectionChannel.UsbWired ? (byte)0x08 : (byte)0x09;
                        byte expectedChan = (byte)(target - 0x08);
                        int outLen = Math.Max(64, (int)ff42.OutputReportLength);
                        int inLen = Math.Max(64, (int)ff42.InputReportLength);

                        byte[] batCmd = new byte[outLen];
                        batCmd[0] = 0x02;
                        batCmd[1] = target;
                        batCmd[2] = 0x02;
                        batCmd[3] = 0x0F;
                        byte[] resp = h.SendAndReceive(batCmd, inLen,
                            delegate(byte[] r) { return r != null && r.Length >= 6 && r[0] == 0x01 && r[1] == expectedChan && r[2] == 0x02 && r[3] == 0x00; }, 180, 3);

                        int pct;
                        if (ParseCorsairSlipstreamLevel(resp, out pct))
                        {
                            bool chg = group.InferredChannel == ConnectionChannel.UsbWired;
                            byte[] chgCmd = new byte[outLen];
                            chgCmd[0] = 0x02;
                            chgCmd[1] = target;
                            chgCmd[2] = 0x02;
                            chgCmd[3] = 0x10;
                            byte[] chgResp = h.SendAndReceive(chgCmd, inLen,
                                delegate(byte[] r) { return r != null && r.Length >= 5 && r[0] == 0x01 && r[1] == expectedChan && r[2] == 0x02; }, 140, 2);
                            if (chgResp != null && chgResp[4] == 0x02) chg = true;

                            results.Add(BuildDevice(group, pct, chg, "Corsair Slipstream HID"));
                            return results;
                        }
                    }
                }
            }

            return results;
        }

        private static List<DeviceBatteryInfo> QueryHyperX(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode vendorNode = FindAnyVendorIoNode(group);
            if (vendorNode == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(vendorNode.DevicePath, true, true, true))
            {
                if (handle == null) return results;

                // Try Cloud III / Alpha Wireless / Cloud Flight commands
                byte[][] probes = new byte[][]
                {
                    new byte[] { 0x21, 0xBB, 0x03 },
                    new byte[] { 0x06, 0xFF, 0xBB, 0x02 },
                    new byte[] { 0x04, 0x51, 0x00, 0x00 }
                };

                int outLen = Math.Max(64, (int)vendorNode.OutputReportLength);
                int inLen = Math.Max(64, (int)vendorNode.InputReportLength);
                foreach (byte[] p in probes)
                {
                    byte[] pkt = new byte[outLen];
                    Array.Copy(p, pkt, p.Length);
                    byte[] resp = handle.SendAndReceive(pkt, inLen, delegate(byte[] r) { return r != null && r.Length >= 8; }, 160, 2);
                    if (resp == null) continue;

                    if (resp[0] == 0x21 && resp[7] <= 100)
                    {
                        results.Add(BuildDevice(group, resp[7], resp.Length > 8 && resp[8] == 1, "HyperX Cloud Flight HID"));
                        return results;
                    }
                    if (resp[0] == 0x06 && resp[1] == 0xFF && resp[7] <= 100)
                    {
                        results.Add(BuildDevice(group, resp[7], resp.Length > 8 && resp[8] == 1, "HyperX Wireless HID"));
                        return results;
                    }
                }
            }

            return results;
        }

        public static bool ParseCorsairVoidElite(byte[] resp, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 5) return false;
            if (resp[0] != 0x64 || resp[3] != 0xB1) return false;
            if (resp[4] == 0) return false; // 0 = no headset connected
            int lvl = resp[2] & 0x7F; // bit 7 is mic-lifted flag
            if (lvl > 100) return false;
            percent = lvl;
            charging = (resp[4] == 4 || resp[4] == 5);
            return true;
        }

        public static bool ParseCorsairSlipstreamLevel(byte[] resp, out int percent)
        {
            percent = -1;
            if (resp == null || resp.Length < 6) return false;
            if (resp[0] != 0x01 || resp[2] != 0x02 || resp[3] != 0x00) return false;
            int tenths = resp[4] | (resp[5] << 8);
            if (tenths < 0 || tenths > 1000) return false;
            percent = (int)Math.Round(tenths / 10.0);
            return true;
        }

        private static HidDeviceNode FindVendorNode(HidDeviceGroup group, ushort usagePage)
        {
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == usagePage && n.InputReportLength > 0 && n.OutputReportLength > 0) return n;
            }
            return null;
        }

        private static HidDeviceNode FindAnyVendorIoNode(HidDeviceGroup group)
        {
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00 && n.InputReportLength > 0 && n.OutputReportLength > 0) return n;
            }
            return null;
        }

        private static DeviceBatteryInfo BuildDevice(HidDeviceGroup group, int pct, bool charging, string proto)
        {
            DeviceBatteryInfo info = new DeviceBatteryInfo();
            info.Id = string.Format("CHX_{0:X4}_{1:X4}_{2}", group.VendorId, group.ProductId, group.GroupKey);
            info.ContainerId = group.ContainerId;
            info.Name = group.DisplayName;
            info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Headset : group.InferredCategory;
            info.Channel = group.InferredChannel;
            info.BatteryPercent = pct;
            info.IsCharging = charging;
            info.ProtocolName = proto;
            info.Priority = 90;
            return info;
        }
    }
}
