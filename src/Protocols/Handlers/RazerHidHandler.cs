using System;
using System.Collections.Generic;
using System.Threading;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class RazerHidHandler : IHidProtocolHandler
    {
        private const ushort VID_RAZER = 0x1532;

        public string Name
        {
            get { return "Razer Vendor HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_RAZER) return false;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.FeatureReportLength >= 90) return true;
            }
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode featNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.FeatureReportLength >= 90)
                {
                    featNode = n;
                    break;
                }
            }
            if (featNode == null) return results;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(featNode.DevicePath, true, true, false))
            {
                if (handle == null) return results;

                byte[] txIds = new byte[] { 0x1F, 0x3F, 0xFF };
                foreach (byte txId in txIds)
                {
                    byte[] batResp = SendRazerCommand(handle, featNode.FeatureReportLength, txId, 0x07, 0x80, 0x02);
                    int percent;
                    if (ParseRazerBatteryResponse(batResp, out percent))
                    {
                        bool charging = false;
                        byte[] chgResp = SendRazerCommand(handle, featNode.FeatureReportLength, txId, 0x07, 0x84, 0x02);
                        bool chgParsed;
                        if (ParseRazerChargingResponse(chgResp, out chgParsed))
                        {
                            charging = chgParsed;
                        }

                        DeviceBatteryInfo info = new DeviceBatteryInfo();
                        info.Id = string.Format("RAZER_{0:X4}_{1:X4}", group.VendorId, group.ProductId);
                        info.ContainerId = group.ContainerId;
                        info.Name = group.DisplayName;
                        info.Category = group.InferredCategory == DeviceCategory.Unknown ? DeviceCategory.Mouse : group.InferredCategory;
                        info.Channel = group.InferredChannel;
                        info.BatteryPercent = percent;
                        info.IsCharging = charging;
                        info.ProtocolName = "Razer HID";
                        info.Priority = 95;

                        if (diagLog != null)
                        {
                            diagLog.Add(string.Format("  [Razer HID] \"{0}\" tx=0x{1:X2} Bat={2}% Charging={3}",
                                info.Name, txId, info.BatteryPercent, info.IsCharging));
                        }

                        results.Add(info);
                        break;
                    }
                }
            }

            return results;
        }

        private static byte[] SendRazerCommand(HidDeviceHandle handle, int featLen, byte txId, byte cmdClass, byte cmdId, byte dataSize)
        {
            byte[] req = BuildRazerReport(featLen, txId, cmdClass, cmdId, dataSize);
            if (!handle.SetFeature(req)) return null;
            Thread.Sleep(45);
            byte[] resp = new byte[req.Length];
            resp[0] = req[0];
            if (!handle.GetFeature(resp)) return null;
            return resp;
        }

        public static byte[] BuildRazerReport(int featLen, byte txId, byte cmdClass, byte cmdId, byte dataSize)
        {
            // Windows HidD_SetFeature prepends 1 byte Report ID (0x00) before the 90-byte Razer struct.
            int len = Math.Max(91, featLen);
            byte[] buf = new byte[len];
            buf[0] = 0x00;      // Report ID
            buf[1] = 0x00;      // Status = New Command
            buf[2] = txId;      // Transaction ID
            buf[3] = 0x00;      // Remaining Packets High
            buf[4] = 0x00;      // Remaining Packets Low
            buf[5] = 0x00;      // Protocol Type
            buf[6] = dataSize;  // Data Size
            buf[7] = cmdClass;  // Command Class
            buf[8] = cmdId;     // Command ID

            byte crc = 0;
            for (int i = 3; i <= 88; i++)
            {
                crc ^= buf[i];
            }
            buf[89] = crc;
            buf[90] = 0x00;
            return buf;
        }

        public static bool ParseRazerBatteryResponse(byte[] resp, out int percent)
        {
            percent = -1;
            if (resp == null || resp.Length < 91) return false;
            if (resp[1] != 0x02) return false; // 0x02 = Successful response
            if (resp[7] != 0x07 || resp[8] != 0x80) return false;
            byte raw = resp[10]; // Argument byte 1 (0..255)
            percent = Math.Max(0, Math.Min(100, (int)Math.Round(raw * 100.0 / 255.0)));
            return true;
        }

        public static bool ParseRazerChargingResponse(byte[] resp, out bool charging)
        {
            charging = false;
            if (resp == null || resp.Length < 91) return false;
            if (resp[1] != 0x02) return false;
            if (resp[7] != 0x07 || resp[8] != 0x84) return false;
            charging = resp[10] != 0;
            return true;
        }
    }
}
