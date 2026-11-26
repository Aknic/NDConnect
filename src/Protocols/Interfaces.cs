using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols
{
    public interface IChannelProvider
    {
        string Name { get; }
        List<DeviceBatteryInfo> Poll(List<string> diagLog);
    }

    public sealed class HidDeviceGroup
    {
        public string GroupKey { get; set; }
        public string ContainerId { get; set; }
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public string DisplayName { get; set; }
        public DeviceCategory InferredCategory { get; set; }
        public ConnectionChannel InferredChannel { get; set; }
        public List<HidDeviceNode> Nodes { get; set; }

        public HidDeviceGroup()
        {
            GroupKey = string.Empty;
            ContainerId = string.Empty;
            DisplayName = string.Empty;
            Nodes = new List<HidDeviceNode>();
        }
    }

    public interface IHidProtocolHandler
    {
        string Name { get; }
        bool CanHandle(HidDeviceGroup group);
        List<DeviceBatteryInfo> Query(HidDeviceGroup group, List<string> diagLog);
    }
}
