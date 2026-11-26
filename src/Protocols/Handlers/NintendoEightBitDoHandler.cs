using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    // Nintendo Switch Pro / Joy-Con (0x057E) and 8BitDo D-Input (0x2DC8) HID handlers
    // (reference: HeyOkay/HaloBattery providers/nintendo.py, providers/eightbitdo.py).
    public sealed class NintendoEightBitDoHandler : IHidProtocolHandler
    {
        private const ushort VID_NINTENDO = 0x057E;
        private const ushort VID_8BITDO = 0x2DC8;

        public string Name
        {
            get { return "Nintendo Switch / 8BitDo HID"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            return group.VendorId == VID_NINTENDO || group.VendorId == VID_8BITDO;
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

            if (group.VendorId == VID_NINTENDO)
            {
                // Per HaloBattery: only query over Bluetooth to avoid changing USB state
                if (group.InferredChannel != ConnectionChannel.Bluetooth) return results;

                using (HidDeviceHandle h = HidDeviceHandle.Open(padNode.DevicePath, true, true, true))
                {
                    if (h == null) return results;
                    // First passively listen for 0x30/0x31/0x21
                    int len = Math.Max(49, (int)padNode.InputReportLength);
                    byte[] report = h.ReadReport(len, 100);
                    int pct; bool chg;
                    if (!ParseNintendoReport(report, out pct, out chg))
                    {
                        // Send read-only subcommand 0x02 (Request Device Info) with neutral rumble (00 01 40 40)
                        byte[] outPkt = new byte[Math.Max(49, (int)padNode.OutputReportLength)];
                        outPkt[0] = 0x01; // Output report: rumble + subcommand
                        outPkt[1] = 0x00;
                        outPkt[2] = 0x00; outPkt[3] = 0x01; outPkt[4] = 0x40; outPkt[5] = 0x40;
                        outPkt[6] = 0x00; outPkt[7] = 0x01; outPkt[8] = 0x40; outPkt[9] = 0x40;
                        outPkt[10] = 0x02; // Subcommand 0x02 = Device Info
                        h.WriteReport(outPkt, 150);

                        for (int i = 0; i < 5; i++)
                        {
                            report = h.ReadReport(len, 120);
                            if (ParseNintendoReport(report, out pct, out chg)) break;
                        }
                    }

                    if (pct >= 0)
                    {
                        string name = group.DisplayName;
                        if (group.ProductId == 0x2006) name = "Nintendo Joy-Con (L)";
                        else if (group.ProductId == 0x2007) name = "Nintendo Joy-Con (R)";
                        else if (group.ProductId == 0x2009) name = "Nintendo Switch Pro Controller";

                        DeviceBatteryInfo dev = new DeviceBatteryInfo();
                        dev.Id = string.Format("NINTENDO_{0:X4}_{1}", group.ProductId, group.GroupKey);
                        dev.ContainerId = group.ContainerId;
                        dev.Name = name;
                        dev.Category = DeviceCategory.Gamepad;
                        dev.GlyphType = DeviceGlyphType.GamepadNintendo;
                        dev.Channel = ConnectionChannel.Bluetooth;
                        dev.BatteryPercent = pct;
                        dev.IsCharging = chg;
                        dev.ProtocolName = "Nintendo Switch HID";
                        dev.Priority = 92;
                        results.Add(dev);
                    }
                }
            }
            else if (group.VendorId == VID_8BITDO)
            {
                // Per HaloBattery: passive listener only (never sends Feature 0x06 so DirectInput games are not broken)
                using (HidDeviceHandle h = HidDeviceHandle.Open(padNode.DevicePath, true, false, true))
                {
                    if (h == null) return results;
                    int len = Math.Max(16, (int)padNode.InputReportLength);
                    byte expectedId = (group.ProductId == 0x6009 || group.InferredChannel != ConnectionChannel.Bluetooth) ? (byte)0x04 : (byte)0x01;
                    for (int i = 0; i < 6; i++)
                    {
                        byte[] report = h.ReadReport(len, 80);
                        int pct; bool chg;
                        if (ParseEightBitDoEnhanced(report, expectedId, out pct, out chg))
                        {
                            DeviceBatteryInfo dev = new DeviceBatteryInfo();
                            dev.Id = string.Format("8BITDO_{0:X4}_{1}", group.ProductId, group.GroupKey);
                            dev.ContainerId = group.ContainerId;
                            dev.Name = group.DisplayName;
                            dev.Category = DeviceCategory.Gamepad;
                            dev.GlyphType = DeviceGlyphType.GamepadGeneric;
                            dev.Channel = group.InferredChannel;
                            dev.BatteryPercent = pct;
                            dev.IsCharging = chg;
                            dev.ProtocolName = "8BitDo Enhanced HID";
                            dev.Priority = 90;
                            results.Add(dev);
                            break;
                        }
                    }
                }
            }

            return results;
        }

        public static bool ParseNintendoReport(byte[] report, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (report == null || report.Length < 3) return false;
            if (report[0] != 0x21 && report[0] != 0x30 && report[0] != 0x31) return false;

            byte b = report[2];
            int levelSteps = (b >> 5) & 0x07; // 8->100%, 6->75%, 4->50%, 2->25%, 0->0% (Wait: 3 bits 0..7 -> high nibble 0xF0: bits 7..5 = 0..7? Wait: 0x80>>5 = 4!)
            // High nibble (b >> 4) & 0x0F: bits 3..1 = level (8,6,4,2,0), bit 0 = charging!
            int highNibble = (b >> 4) & 0x0F;
            int rawLevel = highNibble & 0x0E; // 8, 6, 4, 2, 0
            charging = (highNibble & 0x01) != 0;
            percent = Math.Max(0, Math.Min(100, rawLevel * 100 / 8));
            return true;
        }

        public static bool ParseEightBitDoEnhanced(byte[] report, byte expectedReportId, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            if (report == null || report.Length < 15) return false;
            if (report[0] != expectedReportId) return false;
            byte b = report[14];
            int level = b & 0x7F;
            if (level < 1 || level > 100) return false;
            percent = level;
            charging = (b & 0x80) != 0;
            return true;
        }
    }
}
