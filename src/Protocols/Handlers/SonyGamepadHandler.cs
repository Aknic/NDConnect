using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class SonyGamepadHandler : IHidProtocolHandler
    {
        private const ushort VID_SONY = 0x054C;
        private const ushort PID_DS4_V1 = 0x05C4;
        private const ushort PID_DS4_V2 = 0x09CC;
        private const ushort PID_DS4_DONGLE = 0x0BA0;
        private const ushort PID_DUALSENSE = 0x0CE6;
        private const ushort PID_DUALSENSE_EDGE = 0x0DF2;

        public string Name
        {
            get { return "Sony DualSense / DualShock 4 HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            if (group.VendorId != VID_SONY) return false;
            return group.ProductId == PID_DUALSENSE || group.ProductId == PID_DUALSENSE_EDGE ||
                   group.ProductId == PID_DS4_V1 || group.ProductId == PID_DS4_V2 || group.ProductId == PID_DS4_DONGLE;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            HidDeviceNode padNode = null;
            foreach (HidDeviceNode n in group.Nodes)
            {
                if (n.UsagePage == 0x0001 && (n.Usage == 0x0005 || n.Usage == 0x0004))
                {
                    padNode = n;
                    break;
                }
            }
            if (padNode == null) return results;

            bool isDualSense = (group.ProductId == PID_DUALSENSE || group.ProductId == PID_DUALSENSE_EDGE);
            bool isBt = group.InferredChannel == ConnectionChannel.Bluetooth || padNode.InputReportLength >= 78;

            using (HidDeviceHandle handle = HidDeviceHandle.Open(padNode.DevicePath, true, false, true))
            {
                if (handle == null) return results;

                if (isDualSense && isBt)
                {
                    // Reading Feature Report 0x05 switches BT DualSense to full 0x31 input reports non-destructively.
                    byte[] feat05 = new byte[41];
                    feat05[0] = 0x05;
                    handle.GetFeature(feat05);
                }

                int readLen = Math.Max(64, (int)padNode.InputReportLength);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    byte[] report = handle.ReadReport(readLen, 180);
                    if (report == null) break;

                    int percent;
                    bool charging;
                    bool full;
                    bool parsed = isDualSense
                        ? ParseDualSenseReport(report, isBt, out percent, out charging, out full)
                        : ParseDualShock4Report(report, isBt, out percent, out charging, out full);

                    if (parsed)
                    {
                        DeviceBatteryInfo info = new DeviceBatteryInfo();
                        info.Id = string.Format("SONY_{0:X4}_{1}", group.ProductId, isBt ? "BT" : "USB");
                        info.ContainerId = group.ContainerId;
                        info.Name = group.ProductId == PID_DUALSENSE_EDGE
                            ? "DualSense Edge"
                            : (isDualSense ? "DualSense 手柄" : "DualShock 4 手柄");
                        info.Category = DeviceCategory.Gamepad;
                        info.Channel = isBt ? ConnectionChannel.Bluetooth : ConnectionChannel.UsbWired;
                        info.BatteryPercent = percent;
                        info.IsCharging = charging;
                        info.IsFull = full;
                        info.ProtocolName = isDualSense ? "Sony DualSense HID" : "Sony DualShock 4 HID";
                        info.Priority = 95;

                        if (diagLog != null)
                        {
                            diagLog.Add(string.Format("  [Sony HID] \"{0}\" Chan={1} ReportID=0x{2:X2} Bat={3}% Charging={4}",
                                info.Name, info.Channel, report[0], info.BatteryPercent, info.IsCharging));
                        }

                        results.Add(info);
                        break;
                    }
                }
            }

            return results;
        }

        public static bool ParseDualSenseReport(byte[] report, bool isBluetooth, out int percent, out bool charging, out bool full)
        {
            percent = -1;
            charging = false;
            full = false;
            if (report == null) return false;

            byte statusByte;
            if (!isBluetooth)
            {
                if (report.Length < 54 || report[0] != 0x01) return false;
                statusByte = report[53];
            }
            else
            {
                if (report.Length >= 55 && report[0] == 0x31)
                {
                    statusByte = report[54];
                }
                else
                {
                    return false;
                }
            }

            int rawCap = statusByte & 0x0F;
            int chargeState = (statusByte >> 4) & 0x0F;
            // rawCap: 0..10 (10% steps)
            if (chargeState == 0x02 || chargeState == 0x0B)
            {
                percent = 100;
                full = true;
                charging = false;
            }
            else
            {
                percent = rawCap >= 10 ? 100 : Math.Min(100, rawCap * 10 + 5);
                charging = (chargeState == 0x01);
            }
            return true;
        }

        public static bool ParseDualShock4Report(byte[] report, bool isBluetooth, out int percent, out bool charging, out bool full)
        {
            percent = -1;
            charging = false;
            full = false;
            if (report == null) return false;

            byte statusByte;
            if (!isBluetooth)
            {
                if (report.Length < 31 || report[0] != 0x01) return false;
                statusByte = report[30];
            }
            else
            {
                if (report.Length < 33 || report[0] != 0x11) return false;
                statusByte = report[32];
            }

            bool cable = (statusByte & 0x10) != 0;
            int raw = statusByte & 0x0F;
            if (cable && raw > 10)
            {
                percent = 100;
                full = true;
                charging = false;
            }
            else
            {
                percent = Math.Min(100, raw * 10 + (cable ? 0 : 5));
                charging = cable;
            }
            return true;
        }
    }
}
