using System;

namespace NDConnect.Models
{
    public enum DeviceCategory
    {
        Unknown = 0,
        Mouse = 1,
        Keyboard = 2,
        Headset = 3,
        Gamepad = 4
    }

    public enum DeviceGlyphType
    {
        BluetoothGeneric = 0,
        Mouse = 1,
        Keyboard = 2,
        Headset = 3,
        Microphone = 4,
        GamepadGeneric = 5,
        GamepadXbox = 6,
        GamepadPlayStation = 7,
        GamepadNintendo = 8
    }

    public enum ConnectionChannel
    {
        Wireless24G = 0,
        UsbWired = 1,
        Bluetooth = 2
    }

    public class DeviceBatteryInfo
    {
        public string Id { get; set; }
        public string ContainerId { get; set; }
        public string Name { get; set; }
        public DeviceCategory Category { get; set; }
        public DeviceGlyphType GlyphType { get; set; }
        public ConnectionChannel Channel { get; set; }
        public int BatteryPercent { get; set; }
        public bool IsCharging { get; set; }
        public bool IsFull { get; set; }
        public bool IsAsleep { get; set; }
        public string ProtocolName { get; set; }
        public int Priority { get; set; }
        public DateTime LastUpdated { get; set; }
        public string ExtraInfo { get; set; }

        public DeviceBatteryInfo()
        {
            Id = string.Empty;
            ContainerId = string.Empty;
            Name = string.Empty;
            Category = DeviceCategory.Unknown;
            GlyphType = DeviceGlyphType.BluetoothGeneric;
            Channel = ConnectionChannel.Wireless24G;
            BatteryPercent = -1;
            ProtocolName = string.Empty;
            ExtraInfo = string.Empty;
            LastUpdated = DateTime.Now;
        }

        public DeviceBatteryInfo Clone()
        {
            DeviceBatteryInfo copy = new DeviceBatteryInfo();
            copy.Id = Id;
            copy.ContainerId = ContainerId;
            copy.Name = Name;
            copy.Category = Category;
            copy.GlyphType = GlyphType;
            copy.Channel = Channel;
            copy.BatteryPercent = BatteryPercent;
            copy.IsCharging = IsCharging;
            copy.IsFull = IsFull;
            copy.IsAsleep = IsAsleep;
            copy.ProtocolName = ProtocolName;
            copy.Priority = Priority;
            copy.LastUpdated = LastUpdated;
            copy.ExtraInfo = ExtraInfo;
            return copy;
        }

        public string DisplayChannelName()
        {
            switch (Channel)
            {
                case ConnectionChannel.Wireless24G:
                    return "2.4GHz 接收器";
                case ConnectionChannel.UsbWired:
                    return "USB 线缆";
                case ConnectionChannel.Bluetooth:
                    return "Windows 蓝牙";
                default:
                    return "未知通道";
            }
        }

        public string ShortChannelLabel()
        {
            switch (Channel)
            {
                case ConnectionChannel.Wireless24G:
                    return "2.4G";
                case ConnectionChannel.UsbWired:
                    return "USB";
                case ConnectionChannel.Bluetooth:
                    return "蓝牙";
                default:
                    return "";
            }
        }

        public string DisplayCategoryName()
        {
            switch (Category)
            {
                case DeviceCategory.Mouse:
                    return "鼠标";
                case DeviceCategory.Keyboard:
                    return "键盘";
                case DeviceCategory.Headset:
                    return "耳机";
                case DeviceCategory.Gamepad:
                    return "游戏手柄";
                default:
                    return "无线外设";
            }
        }
    }
}
