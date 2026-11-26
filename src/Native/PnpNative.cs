using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NDConnect.Native
{
    public sealed class PnpDeviceSnapshot
    {
        public string InstanceId { get; set; }
        public string FriendlyName { get; set; }
        public string DeviceDesc { get; set; }
        public string BusReportedDesc { get; set; }
        public string ClassName { get; set; }
        public string ContainerId { get; set; }
        public string BluetoothAddress { get; set; }
        public uint ClassOfDevice { get; set; }
        public bool HasBatteryLevel { get; set; }
        public int BatteryLevel { get; set; }
        public bool IsAepConnectedKnown { get; set; }
        public bool IsAepConnected { get; set; }
        public bool IsPresentAndActive { get; set; }

        public PnpDeviceSnapshot()
        {
            InstanceId = string.Empty;
            FriendlyName = string.Empty;
            DeviceDesc = string.Empty;
            BusReportedDesc = string.Empty;
            ClassName = string.Empty;
            ContainerId = string.Empty;
            BluetoothAddress = string.Empty;
            BatteryLevel = -1;
        }
    }

    public sealed class BluetoothRadioDeviceState
    {
        public string Name { get; set; }
        public ulong Address { get; set; }
        public uint ClassOfDevice { get; set; }
        public bool Connected { get; set; }
        public bool Authenticated { get; set; }
        public bool Remembered { get; set; }
    }

    public static class PnpNative
    {
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        private const uint DEVPROP_TYPE_BYTE = 0x00000003;
        private const uint DEVPROP_TYPE_UINT32 = 0x00000007;
        private const uint DEVPROP_TYPE_GUID = 0x0000000D;
        private const uint DEVPROP_TYPE_STRING = 0x00000012;
        private const uint DEVPROP_TYPE_BOOLEAN = 0x00000011;

        private const uint DN_STARTED = 0x00000008;
        private const uint DN_HAS_PROBLEM = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;

            public DEVPROPKEY(string guidStr, uint pidVal)
            {
                fmtid = new Guid(guidStr);
                pid = pidVal;
            }
        }

        public static DEVPROPKEY DEVPKEY_Device_DeviceDesc = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        public static DEVPROPKEY DEVPKEY_Device_Class = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);
        public static DEVPROPKEY DEVPKEY_Device_FriendlyName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        public static DEVPROPKEY DEVPKEY_Device_ContainerId = new DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        public static DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new DEVPROPKEY("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);
        public static DEVPROPKEY DEVPKEY_Bluetooth_BatteryLevel = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        public static DEVPROPKEY DEVPKEY_Bluetooth_DeviceAddress = new DEVPROPKEY("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 1);
        public static DEVPROPKEY DEVPKEY_Bluetooth_ClassOfDevice = new DEVPROPKEY("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 4);
        public static DEVPROPKEY DEVPKEY_Bluetooth_ClassOfDevice_Alt = new DEVPROPKEY("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);
        public static DEVPROPKEY DEVPKEY_Aep_IsConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);

        public static readonly Guid GUID_BLE_GATT_BATTERY_SERVICE = new Guid("0000180F-0000-1000-8000-00805F9B34FB");

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref HidNative.SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr DeviceInfoSet,
            ref HidNative.SP_DEVINFO_DATA DeviceInfoData,
            StringBuilder DeviceInstanceId,
            int DeviceInstanceIdSize,
            out int RequiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(
            IntPtr DeviceInfoSet,
            ref HidNative.SP_DEVINFO_DATA DeviceInfoData,
            ref DEVPROPKEY PropertyKey,
            out uint PropertyType,
            byte[] PropertyBuffer,
            uint PropertyBufferSize,
            out uint RequiredSize,
            uint Flags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint CM_Get_Device_IDW(uint dnDevInst, StringBuilder Buffer, int BufferLen, uint ulFlags);

        public static string GetPhysicalUsbParentInstanceId(uint devInst)
        {
            uint current = devInst;
            string lastUsb = string.Empty;
            for (int depth = 0; depth < 6; depth++)
            {
                uint parent;
                if (CM_Get_Parent(out parent, current, 0) != 0) break;
                StringBuilder sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) == 0)
                {
                    string id = sb.ToString();
                    string upper = id.ToUpperInvariant();
                    if (upper.StartsWith("USB\\"))
                    {
                        lastUsb = upper;
                        // Stop at the composite/device node directly under USB\ROOT_HUB or hub port
                        if (!upper.Contains("&MI_")) return lastUsb;
                    }
                    else if (!string.IsNullOrEmpty(lastUsb))
                    {
                        return lastUsb;
                    }
                }
                current = parent;
            }
            return lastUsb;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fReturnConnected;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear;
            public ushort wMonth;
            public ushort wDayOfWeek;
            public ushort wDay;
            public ushort wHour;
            public ushort wMinute;
            public ushort wSecond;
            public ushort wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [DllImport("BluetoothAPIs.dll", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("BluetoothAPIs.dll", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("BluetoothAPIs.dll", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        // BLE GATT API (BluetoothAPIs.dll)
        [StructLayout(LayoutKind.Sequential)]
        public struct BTH_LE_UUID
        {
            public byte IsShortUuid;
            public Guid LongUuid; // When IsShortUuid != 0, lower 16/32 bits hold short UUID (union)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BTH_LE_GATT_SERVICE
        {
            public BTH_LE_UUID ServiceUuid;
            public ushort AttributeHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BTH_LE_GATT_CHARACTERISTIC
        {
            public ushort ServiceHandle;
            public BTH_LE_UUID CharacteristicUuid;
            public ushort AttributeHandle;
            public ushort CharacteristicValueHandle;
            public byte IsBroadcastable;
            public byte IsReadable;
            public byte IsWritable;
            public byte IsWritableWithoutResponse;
            public byte IsSignedWritable;
            public byte IsNotifiable;
            public byte IsIndicatable;
            public byte HasExtendedProperties;
        }

        [DllImport("BluetoothAPIs.dll", SetLastError = true)]
        public static extern int BluetoothGATTGetServices(
            IntPtr hDevice,
            ushort ServicesBufferCount,
            [Out] BTH_LE_GATT_SERVICE[] ServicesBuffer,
            out ushort ServicesBufferActual,
            uint Flags);

        [DllImport("BluetoothAPIs.dll", SetLastError = true)]
        public static extern int BluetoothGATTGetCharacteristics(
            IntPtr hDevice,
            ref BTH_LE_GATT_SERVICE Service,
            ushort CharacteristicsBufferCount,
            [Out] BTH_LE_GATT_CHARACTERISTIC[] CharacteristicsBuffer,
            out ushort CharacteristicsBufferActual,
            uint Flags);

        [DllImport("BluetoothAPIs.dll", SetLastError = true)]
        public static extern int BluetoothGATTGetCharacteristicValue(
            IntPtr hDevice,
            ref BTH_LE_GATT_CHARACTERISTIC Characteristic,
            uint CharacteristicValueDataSize,
            IntPtr CharacteristicValue,
            out ushort CharacteristicValueSizeRequired,
            uint Flags);

        public static string GetDeviceInstanceId(IntPtr devInfoSet, ref HidNative.SP_DEVINFO_DATA devInfo)
        {
            StringBuilder sb = new StringBuilder(512);
            int req;
            if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfo, sb, sb.Capacity, out req))
            {
                return sb.ToString();
            }
            return string.Empty;
        }

        public static bool TryGetDevPropString(IntPtr devInfoSet, ref HidNative.SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out string val)
        {
            val = string.Empty;
            byte[] buf = new byte[1024];
            uint propType;
            uint req;
            if (SetupDiGetDevicePropertyW(devInfoSet, ref devInfo, ref key, out propType, buf, (uint)buf.Length, out req, 0))
            {
                if (propType == DEVPROP_TYPE_STRING && req >= 2)
                {
                    string s = Encoding.Unicode.GetString(buf, 0, (int)req);
                    int nul = s.IndexOf('\0');
                    if (nul >= 0) s = s.Substring(0, nul);
                    val = s.Trim();
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetDevPropGuid(IntPtr devInfoSet, ref HidNative.SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out Guid val)
        {
            val = Guid.Empty;
            byte[] buf = new byte[16];
            uint propType;
            uint req;
            if (SetupDiGetDevicePropertyW(devInfoSet, ref devInfo, ref key, out propType, buf, 16, out req, 0))
            {
                if (propType == DEVPROP_TYPE_GUID && req == 16)
                {
                    val = new Guid(buf);
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetDevPropByte(IntPtr devInfoSet, ref HidNative.SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out byte val)
        {
            val = 0;
            byte[] buf = new byte[4];
            uint propType;
            uint req;
            if (SetupDiGetDevicePropertyW(devInfoSet, ref devInfo, ref key, out propType, buf, 4, out req, 0))
            {
                if (propType == DEVPROP_TYPE_BYTE && req >= 1)
                {
                    val = buf[0];
                    return true;
                }
                if (propType == DEVPROP_TYPE_UINT32 && req >= 4)
                {
                    uint u = BitConverter.ToUInt32(buf, 0);
                    if (u <= 255)
                    {
                        val = (byte)u;
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool TryGetDevPropUInt32(IntPtr devInfoSet, ref HidNative.SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out uint val)
        {
            val = 0;
            byte[] buf = new byte[4];
            uint propType;
            uint req;
            if (SetupDiGetDevicePropertyW(devInfoSet, ref devInfo, ref key, out propType, buf, 4, out req, 0))
            {
                if (propType == DEVPROP_TYPE_UINT32 && req == 4)
                {
                    val = BitConverter.ToUInt32(buf, 0);
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetDevPropBool(IntPtr devInfoSet, ref HidNative.SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out bool val)
        {
            val = false;
            byte[] buf = new byte[4];
            uint propType;
            uint req;
            if (SetupDiGetDevicePropertyW(devInfoSet, ref devInfo, ref key, out propType, buf, 4, out req, 0))
            {
                if (propType == DEVPROP_TYPE_BOOLEAN && req >= 1)
                {
                    val = buf[0] != 0;
                    return true;
                }
            }
            return false;
        }

        public static List<PnpDeviceSnapshot> EnumeratePresentBluetoothPnpDevices()
        {
            List<PnpDeviceSnapshot> list = new List<PnpDeviceSnapshot>();
            IntPtr devInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                return list;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    HidNative.SP_DEVINFO_DATA devInfo = new HidNative.SP_DEVINFO_DATA();
                    devInfo.cbSize = (uint)Marshal.SizeOf(typeof(HidNative.SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfo))
                    {
                        break;
                    }
                    index++;

                    string instanceId = GetDeviceInstanceId(devInfoSet, ref devInfo);
                    if (string.IsNullOrEmpty(instanceId)) continue;

                    string upper = instanceId.ToUpperInvariant();
                    if (!upper.StartsWith("BTH") && !upper.Contains("BTHENUM") && !upper.Contains("BTHLE"))
                    {
                        continue;
                    }

                    PnpDeviceSnapshot snap = new PnpDeviceSnapshot();
                    snap.InstanceId = instanceId;

                    string s;
                    if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Device_FriendlyName, out s)) snap.FriendlyName = s;
                    if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Device_DeviceDesc, out s)) snap.DeviceDesc = s;
                    if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Device_BusReportedDeviceDesc, out s)) snap.BusReportedDesc = s;
                    if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Device_Class, out s)) snap.ClassName = s;
                    if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Bluetooth_DeviceAddress, out s)) snap.BluetoothAddress = NormalizeMac(s);
                    if (string.IsNullOrEmpty(snap.BluetoothAddress))
                    {
                        snap.BluetoothAddress = ExtractBluetoothMacFromInstanceId(instanceId);
                    }

                    Guid cid;
                    if (TryGetDevPropGuid(devInfoSet, ref devInfo, ref DEVPKEY_Device_ContainerId, out cid))
                    {
                        snap.ContainerId = cid.ToString("D").ToUpperInvariant();
                    }

                    uint cod;
                    if (TryGetDevPropUInt32(devInfoSet, ref devInfo, ref DEVPKEY_Bluetooth_ClassOfDevice, out cod) ||
                        TryGetDevPropUInt32(devInfoSet, ref devInfo, ref DEVPKEY_Bluetooth_ClassOfDevice_Alt, out cod))
                    {
                        snap.ClassOfDevice = cod;
                    }

                    byte bat;
                    if (TryGetDevPropByte(devInfoSet, ref devInfo, ref DEVPKEY_Bluetooth_BatteryLevel, out bat))
                    {
                        snap.HasBatteryLevel = true;
                        snap.BatteryLevel = bat;
                    }

                    bool aepConn;
                    if (TryGetDevPropBool(devInfoSet, ref devInfo, ref DEVPKEY_Aep_IsConnected, out aepConn))
                    {
                        snap.IsAepConnectedKnown = true;
                        snap.IsAepConnected = aepConn;
                    }

                    uint status, problem;
                    if (CM_Get_DevNode_Status(out status, out problem, devInfo.DevInst, 0) == 0)
                    {
                        snap.IsPresentAndActive = (status & DN_STARTED) != 0 && (status & DN_HAS_PROBLEM) == 0;
                    }
                    else
                    {
                        snap.IsPresentAndActive = true;
                    }

                    list.Add(snap);
                }
            }
            finally
            {
                HidNative.SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return list;
        }

        private static readonly Regex _rxDevMac = new Regex(@"DEV_([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxAnyMac = new Regex(@"(?<![0-9A-F])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string NormalizeMac(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string hex = Regex.Replace(raw, @"[^0-9A-Fa-f]", "").ToUpperInvariant();
            if (hex.Length != 12 || hex == "000000000000" || hex == "FFFFFFFFFFFF") return string.Empty;
            return hex;
        }

        public static string ExtractBluetoothMacFromInstanceId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return string.Empty;
            string upper = instanceId.ToUpperInvariant();
            Match m = _rxDevMac.Match(upper);
            if (m.Success)
            {
                string mac = NormalizeMac(m.Groups[1].Value);
                if (!string.IsNullOrEmpty(mac)) return mac;
            }
            MatchCollection all = _rxAnyMac.Matches(upper);
            for (int i = all.Count - 1; i >= 0; i--)
            {
                string mac = NormalizeMac(all[i].Groups[1].Value);
                if (!string.IsNullOrEmpty(mac)) return mac;
            }
            return string.Empty;
        }

        public static List<BluetoothRadioDeviceState> EnumerateClassicBluetoothDevices()
        {
            List<BluetoothRadioDeviceState> result = new List<BluetoothRadioDeviceState>();
            BLUETOOTH_DEVICE_SEARCH_PARAMS sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
            sp.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
            sp.fReturnAuthenticated = true;
            sp.fReturnRemembered = true;
            sp.fReturnConnected = true;
            sp.fReturnUnknown = false;
            sp.fIssueInquiry = false;
            sp.cTimeoutMultiplier = 0;
            sp.hRadio = IntPtr.Zero;

            BLUETOOTH_DEVICE_INFO info = new BLUETOOTH_DEVICE_INFO();
            info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));

            IntPtr hFind = BluetoothFindFirstDevice(ref sp, ref info);
            if (hFind == IntPtr.Zero) return result;
            try
            {
                do
                {
                    BluetoothRadioDeviceState st = new BluetoothRadioDeviceState();
                    st.Name = info.szName ?? string.Empty;
                    st.Address = info.Address;
                    st.ClassOfDevice = info.ulClassofDevice;
                    st.Connected = info.fConnected;
                    st.Authenticated = info.fAuthenticated;
                    st.Remembered = info.fRemembered;
                    result.Add(st);

                    info = new BLUETOOTH_DEVICE_INFO();
                    info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                }
                while (BluetoothFindNextDevice(hFind, ref info));
            }
            finally
            {
                BluetoothFindDeviceClose(hFind);
            }
            return result;
        }

        public sealed class BleBatteryInterfaceInfo
        {
            public string DevicePath { get; set; }
            public string ContainerId { get; set; }
            public string FriendlyName { get; set; }
            public string InstanceId { get; set; }
        }

        public static List<BleBatteryInterfaceInfo> EnumerateBleGattBatteryInterfaces()
        {
            List<BleBatteryInterfaceInfo> result = new List<BleBatteryInterfaceInfo>();
            Guid gattBat = GUID_BLE_GATT_BATTERY_SERVICE;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref gattBat, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    HidNative.SP_DEVICE_INTERFACE_DATA ifData = new HidNative.SP_DEVICE_INTERFACE_DATA();
                    ifData.cbSize = (uint)Marshal.SizeOf(typeof(HidNative.SP_DEVICE_INTERFACE_DATA));
                    if (!HidNative.SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref gattBat, index, ref ifData))
                    {
                        break;
                    }
                    index++;

                    uint reqSize;
                    HidNative.SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    if (reqSize <= 4) continue;

                    IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                        HidNative.SP_DEVINFO_DATA devInfo = new HidNative.SP_DEVINFO_DATA();
                        devInfo.cbSize = (uint)Marshal.SizeOf(typeof(HidNative.SP_DEVINFO_DATA));

                        uint ignored;
                        if (!HidNative.SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifData, detailBuf, reqSize, out ignored, ref devInfo))
                        {
                            continue;
                        }

                        BleBatteryInterfaceInfo item = new BleBatteryInterfaceInfo();
                        item.DevicePath = Marshal.PtrToStringUni(new IntPtr(detailBuf.ToInt64() + 4)) ?? string.Empty;
                        item.InstanceId = GetDeviceInstanceId(devInfoSet, ref devInfo);
                        string name;
                        if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Device_FriendlyName, out name) && !string.IsNullOrEmpty(name))
                        {
                            item.FriendlyName = name;
                        }
                        else if (TryGetDevPropString(devInfoSet, ref devInfo, ref DEVPKEY_Device_DeviceDesc, out name))
                        {
                            item.FriendlyName = name;
                        }
                        Guid cid;
                        if (TryGetDevPropGuid(devInfoSet, ref devInfo, ref DEVPKEY_Device_ContainerId, out cid))
                        {
                            item.ContainerId = cid.ToString("D").ToUpperInvariant();
                        }
                        result.Add(item);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuf);
                    }
                }
            }
            finally
            {
                HidNative.SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return result;
        }
    }
}
