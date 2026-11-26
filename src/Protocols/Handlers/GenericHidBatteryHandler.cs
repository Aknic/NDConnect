using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols.Handlers
{
    public sealed class GenericHidBatteryHandler : IHidProtocolHandler
    {
        public string Name
        {
            get { return "Standard HID Battery Usage"; }
        }

        public bool CanHandle(HidDeviceGroup group)
        {
            foreach (HidDeviceNode n in group.Nodes)
            {
                foreach (HidValueCapInfo vc in n.ValueCaps)
                {
                    if (IsBatteryUsage(vc)) return true;
                }
            }
            return false;
        }

        private static bool IsBatteryUsage(HidValueCapInfo vc)
        {
            // UsagePage 0x06 (Generic Device Controls) Usage 0x20 (Battery Strength)
            if (vc.UsagePage == 0x0006 && vc.UsageMin <= 0x0020 && vc.UsageMax >= 0x0020) return true;
            // UsagePage 0x85 (Battery System) Usage 0x66 (RemainingCapacity) or 0x65 (AbsoluteStateOfCharge)
            if (vc.UsagePage == 0x0085 && (vc.UsageMin == 0x0066 || vc.UsageMin == 0x0065 || vc.UsageMin == 0x0064)) return true;
            return false;
        }

        public List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            foreach (HidDeviceNode node in group.Nodes)
            {
                HidValueCapInfo batCap = null;
                foreach (HidValueCapInfo vc in node.ValueCaps)
                {
                    if (IsBatteryUsage(vc))
                    {
                        batCap = vc;
                        break;
                    }
                }
                if (batCap == null) continue;

                using (HidDeviceHandle handle = HidDeviceHandle.Open(node.DevicePath, true, false, true))
                {
                    if (handle == null) continue;

                    byte[] data = null;
                    if (batCap.ReportType == 2 && node.FeatureReportLength > 0)
                    {
                        data = new byte[node.FeatureReportLength];
                        data[0] = batCap.ReportID;
                        if (!handle.GetFeature(data)) data = null;
                    }
                    else if (batCap.ReportType == 0 && node.InputReportLength > 0)
                    {
                        data = new byte[node.InputReportLength];
                        data[0] = batCap.ReportID;
                        if (!handle.GetInputReport(data))
                        {
                            data = handle.ReadReport(node.InputReportLength, 120);
                        }
                    }

                    if (data != null && data.Length >= 2)
                    {
                        int raw = data[1];
                        int min = batCap.LogicalMin;
                        int max = batCap.LogicalMax > batCap.LogicalMin ? batCap.LogicalMax : 100;
                        int pct = (int)Math.Round((raw - min) * 100.0 / (max - min));
                        pct = Math.Max(0, Math.Min(100, pct));

                        DeviceBatteryInfo info = new DeviceBatteryInfo();
                        info.Id = string.Format("HIDBAT_{0:X4}_{1:X4}", group.VendorId, group.ProductId);
                        info.ContainerId = group.ContainerId;
                        info.Name = group.DisplayName;
                        info.Category = group.InferredCategory;
                        info.Channel = group.InferredChannel;
                        info.BatteryPercent = pct;
                        info.ProtocolName = "Standard HID Battery";
                        info.Priority = 65;

                        if (diagLog != null)
                        {
                            diagLog.Add(string.Format("  [Generic HID Battery] \"{0}\" Raw={1} -> {2}%", info.Name, raw, pct));
                        }

                        results.Add(info);
                        break;
                    }
                }
            }

            return results;
        }
    }
}
