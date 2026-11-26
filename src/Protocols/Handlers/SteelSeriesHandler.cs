using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class SteelSeriesHandler : IHidProtocolHandler
    {
        private const ushort VID_STEELSERIES = 0x1038;

        public string Name
        {
            get { return "SteelSeries HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_STEELSERIES) return false;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00) return true;
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode vendorNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage >= 0xFF00 && n.OutputReportLength > 0 && n.InputReportLength > 0)
                {
                    vendorNode = n;
                    break;
                }
            }
            if (vendorNode == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(vendorNode.DevicePath, true, true, true))
            {
                if (handle == null) return results;

                // Try Arctis Nova / Arctis 7+ / Rival battery commands
                byte[][] probes = new byte[][]
                {
                    new byte[] { 0x00, 0xB0 }, // Arctis Nova 7 / 7P / 7X
                    new byte[] { 0x06, 0x18 }, // Arctis 7 / Pro Wireless
                    new byte[] { 0x00, 0xD2 }  // Aerox / Rival Wireless mice
                };

                foreach (byte[] p in probes)
                {
                    int outLen = Math.Max(64, (int)vendorNode.OutputReportLength);
                    byte[] pkt = new byte[outLen];
                    Array.Copy(p, pkt, Math.Min(p.Length, pkt.Length));

                    int inLen = Math.Max(64, (int)vendorNode.InputReportLength);
                    byte[] resp = handle.SendAndReceive(pkt, inLen, delegate(byte[] r)
                    {
                        return r != null && r.Length >= 4;
                    }, 150, 2);

                    int percent;
                    bool charging;
                    if (resp != null && ParseSteelSeriesResponse(p, resp, out percent, out charging))
                    {
                        DeviceBatteryInfo info = new DeviceBatteryInfo();
                        info.Id = string.Format("SS_{0:X4}_{1:X4}", group.VendorId, group.ProductId);
                        info.ContainerId = group.ContainerId;
                        info.Name = group.DisplayName;
                        info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Headset : group.InferredCategory;
                        info.Channel = group.InferredChannel;
                        info.BatteryPercent = percent;
                        info.IsCharging = charging;
                        info.ProtocolName = "SteelSeries HID";
                        info.Priority = 90;
                        results.Add(info);
                        break;
                    }
                }
            }

            return results;
        }

        private static bool ParseSteelSeriesResponse(byte[] probe, byte[] resp, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (resp == null || resp.Length < 4) return false;

            if (probe[1] == 0xB0 && (resp[1] == 0xB0 || resp[0] == 0xB0))
            {
                int off = resp[0] == 0xB0 ? 0 : 1;
                if (resp.Length > off + 2 && resp[off + 2] <= 100)
                {
                    int raw = resp[off + 2];
                    percent = raw <= 4 ? raw * 25 : Math.Min(100, raw);
                    charging = resp.Length > off + 3 && resp[off + 3] == 1;
                    return true;
                }
            }
            if (probe[0] == 0x06 && probe[1] == 0x18 && resp[0] == 0x06 && resp[1] == 0x18)
            {
                if (resp[2] <= 100)
                {
                    percent = resp[2];
                    return true;
                }
            }
            if (probe[1] == 0xD2 && resp.Length >= 3 && resp[2] <= 100)
            {
                percent = resp[2];
                charging = resp.Length >= 4 && resp[3] == 1;
                return true;
            }
            return false;
        }
    }
}
