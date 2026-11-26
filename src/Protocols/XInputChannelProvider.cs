using System;
using System.Collections.Generic;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols
{
    public sealed class XInputChannelProvider : IChannelProvider
    {
        public string Name
        {
            get { return "XInput Gamepad"; }
        }

        public List<DeviceBatteryInfo> Poll(List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            for (uint slot = 0; slot < 4; slot++)
            {
                XInputNative.XINPUT_STATE state;
                if (!XInputNative.TryGetState(slot, out state)) continue;

                XInputNative.XINPUT_BATTERY_INFORMATION bat;
                if (!XInputNative.TryGetBatteryInformation(slot, XInputNative.BATTERY_DEVTYPE_GAMEPAD, out bat))
                {
                    continue;
                }

                if (bat.BatteryType == XInputNative.BATTERY_TYPE_DISCONNECTED) continue;

                DeviceBatteryInfo info = ParseXInputBattery(slot, bat);
                if (info != null)
                {
                    if (diagLog != null)
                    {
                        diagLog.Add(string.Format("[XInput] Slot {0}: Type=0x{1:X2} Level=0x{2:X2} -> {3}% Charging={4}",
                            slot + 1, bat.BatteryType, bat.BatteryLevel, info.BatteryPercent, info.IsCharging));
                    }
                    results.Add(info);
                }
            }

            return results;
        }

        public static DeviceBatteryInfo ParseXInputBattery(uint slot, XInputNative.XINPUT_BATTERY_INFORMATION bat)
        {
            if (bat.BatteryType == XInputNative.BATTERY_TYPE_DISCONNECTED) return null;

            DeviceBatteryInfo info = new DeviceBatteryInfo();
            info.Id = "XINPUT_SLOT_" + slot;
            info.Name = "Xbox 手柄 #" + (slot + 1);
            info.Category = DeviceCategory.Gamepad;
            info.GlyphType = DeviceGlyphType.GamepadXbox;
            info.ProtocolName = "XInput Battery";
            info.Priority = 30; // Lower than BLE GATT or BT PnP if Xbox BT exposes exact %

            if (bat.BatteryType == XInputNative.BATTERY_TYPE_WIRED)
            {
                info.Channel = ConnectionChannel.UsbWired;
                info.BatteryPercent = 100;
                info.IsCharging = true;
                info.IsFull = true;
                return info;
            }

            info.Channel = ConnectionChannel.Wireless24G;
            switch (bat.BatteryLevel)
            {
                case XInputNative.BATTERY_LEVEL_EMPTY:
                    info.BatteryPercent = 5;
                    break;
                case XInputNative.BATTERY_LEVEL_LOW:
                    info.BatteryPercent = 25;
                    break;
                case XInputNative.BATTERY_LEVEL_MEDIUM:
                    info.BatteryPercent = 65;
                    break;
                case XInputNative.BATTERY_LEVEL_FULL:
                    info.BatteryPercent = 100;
                    info.IsFull = true;
                    break;
                default:
                    info.BatteryPercent = 50;
                    break;
            }
            return info;
        }
    }
}
