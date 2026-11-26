using System;
using System.Runtime.InteropServices;

namespace NDConnect.Native
{
    public static class XInputNative
    {
        public const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;
        public const byte BATTERY_DEVTYPE_HEADSET = 0x01;

        public const byte BATTERY_TYPE_DISCONNECTED = 0x00;
        public const byte BATTERY_TYPE_WIRED = 0x01;
        public const byte BATTERY_TYPE_ALKALINE = 0x02;
        public const byte BATTERY_TYPE_NIMH = 0x03;
        public const byte BATTERY_TYPE_UNKNOWN = 0xFF;

        public const byte BATTERY_LEVEL_EMPTY = 0x00;
        public const byte BATTERY_LEVEL_LOW = 0x01;
        public const byte BATTERY_LEVEL_MEDIUM = 0x02;
        public const byte BATTERY_LEVEL_FULL = 0x03;

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        private static class XInput14
        {
            [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
            public static extern uint GetState(uint dwUserIndex, out XINPUT_STATE pState);

            [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
            public static extern uint GetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);
        }

        private static class XInput13
        {
            [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
            public static extern uint GetState(uint dwUserIndex, out XINPUT_STATE pState);

            [DllImport("xinput1_3.dll", EntryPoint = "XInputGetBatteryInformation")]
            public static extern uint GetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);
        }

        private static int _mode = 0; // 0=unknown, 1=1.4, 2=1.3, -1=unavailable

        public static bool TryGetState(uint userIndex, out XINPUT_STATE state)
        {
            state = new XINPUT_STATE();
            if (_mode == -1) return false;

            if (_mode == 0 || _mode == 1)
            {
                try
                {
                    uint rc = XInput14.GetState(userIndex, out state);
                    _mode = 1;
                    return rc == 0;
                }
                catch (DllNotFoundException)
                {
                    _mode = 2;
                }
                catch (EntryPointNotFoundException)
                {
                    _mode = 2;
                }
            }

            if (_mode == 2)
            {
                try
                {
                    uint rc = XInput13.GetState(userIndex, out state);
                    return rc == 0;
                }
                catch
                {
                    _mode = -1;
                }
            }

            return false;
        }

        public static bool TryGetBatteryInformation(uint userIndex, byte devType, out XINPUT_BATTERY_INFORMATION info)
        {
            info = new XINPUT_BATTERY_INFORMATION();
            if (_mode == -1) return false;

            if (_mode == 0 || _mode == 1)
            {
                try
                {
                    uint rc = XInput14.GetBatteryInformation(userIndex, devType, out info);
                    _mode = 1;
                    return rc == 0;
                }
                catch (DllNotFoundException)
                {
                    _mode = 2;
                }
                catch (EntryPointNotFoundException)
                {
                    _mode = 2;
                }
            }

            if (_mode == 2)
            {
                try
                {
                    uint rc = XInput13.GetBatteryInformation(userIndex, devType, out info);
                    return rc == 0;
                }
                catch
                {
                    _mode = -1;
                }
            }

            return false;
        }
    }
}
