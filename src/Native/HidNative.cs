using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NDConnect.Native
{
    public sealed class HidValueCapInfo
    {
        public int ReportType; // 0=Input, 1=Output, 2=Feature
        public ushort UsagePage;
        public byte ReportID;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort BitSize;
        public ushort ReportCount;
        public int LogicalMin;
        public int LogicalMax;
    }

    public sealed class HidDeviceNode
    {
        public string DevicePath { get; set; }
        public string InstanceId { get; set; }
        public string ParentUsbInstanceId { get; set; }
        public string ContainerId { get; set; }
        public string BusReportedDesc { get; set; }
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public ushort VersionNumber { get; set; }
        public ushort UsagePage { get; set; }
        public ushort Usage { get; set; }
        public ushort InputReportLength { get; set; }
        public ushort OutputReportLength { get; set; }
        public ushort FeatureReportLength { get; set; }
        public string Manufacturer { get; set; }
        public string Product { get; set; }
        public string SerialNumber { get; set; }
        public List<HidValueCapInfo> ValueCaps { get; set; }

        public HidDeviceNode()
        {
            DevicePath = string.Empty;
            InstanceId = string.Empty;
            ParentUsbInstanceId = string.Empty;
            ContainerId = string.Empty;
            BusReportedDesc = string.Empty;
            Manufacturer = string.Empty;
            Product = string.Empty;
            SerialNumber = string.Empty;
            ValueCaps = new List<HidValueCapInfo>();
        }
    }

    public sealed class HidDeviceHandle : IDisposable
    {
        private IntPtr _handle;
        private readonly bool _overlapped;

        private HidDeviceHandle(IntPtr handle, bool overlapped)
        {
            _handle = handle;
            _overlapped = overlapped;
        }

        public bool IsValid
        {
            get { return _handle != IntPtr.Zero && _handle != new IntPtr(-1); }
        }

        public static HidDeviceHandle Open(string path, bool readAccess, bool writeAccess, bool overlapped)
        {
            uint desiredAccess = 0;
            if (readAccess) desiredAccess |= HidNative.GENERIC_READ;
            if (writeAccess) desiredAccess |= HidNative.GENERIC_WRITE;

            uint flags = overlapped ? HidNative.FILE_FLAG_OVERLAPPED : 0;
            // Always share read/write so we coexist peacefully with vendor software and OS drivers.
            IntPtr h = HidNative.CreateFileW(
                path,
                desiredAccess,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero,
                HidNative.OPEN_EXISTING,
                flags,
                IntPtr.Zero);

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                return null;
            }
            return new HidDeviceHandle(h, overlapped);
        }

        public bool GetFeature(byte[] buffer)
        {
            if (!IsValid || buffer == null || buffer.Length == 0) return false;
            return HidNative.HidD_GetFeature(_handle, buffer, (uint)buffer.Length);
        }

        public bool SetFeature(byte[] buffer)
        {
            if (!IsValid || buffer == null || buffer.Length == 0) return false;
            return HidNative.HidD_SetFeature(_handle, buffer, (uint)buffer.Length);
        }

        public bool SetOutputReport(byte[] buffer)
        {
            if (!IsValid || buffer == null || buffer.Length == 0) return false;
            return HidNative.HidD_SetOutputReport(_handle, buffer, (uint)buffer.Length);
        }

        public bool GetInputReport(byte[] buffer)
        {
            if (!IsValid || buffer == null || buffer.Length == 0) return false;
            return HidNative.HidD_GetInputReport(_handle, buffer, (uint)buffer.Length);
        }

        public bool WriteReport(byte[] buffer, int timeoutMs)
        {
            if (!IsValid || buffer == null || buffer.Length == 0) return false;
            if (!_overlapped)
            {
                uint written;
                return HidNative.WriteFile(_handle, buffer, (uint)buffer.Length, out written, IntPtr.Zero);
            }

            IntPtr ev = HidNative.CreateEventW(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero) return false;
            try
            {
                NativeOverlapped ov = new NativeOverlapped();
                ov.EventHandle = ev;
                uint written;
                bool ok = HidNative.WriteFile(_handle, buffer, (uint)buffer.Length, out written, ref ov);
                if (ok) return true;
                int err = Marshal.GetLastWin32Error();
                if (err != HidNative.ERROR_IO_PENDING) return false;

                uint wait = HidNative.WaitForSingleObject(ev, (uint)Math.Max(10, timeoutMs));
                if (wait == HidNative.WAIT_OBJECT_0)
                {
                    return HidNative.GetOverlappedResult(_handle, ref ov, out written, false);
                }
                HidNative.CancelIoEx(_handle, ref ov);
                HidNative.GetOverlappedResult(_handle, ref ov, out written, true);
                return false;
            }
            finally
            {
                HidNative.CloseHandle(ev);
            }
        }

        public byte[] ReadReport(int bufferLength, int timeoutMs)
        {
            if (!IsValid || bufferLength <= 0) return null;
            byte[] buf = new byte[bufferLength];
            if (!_overlapped)
            {
                uint read;
                if (HidNative.ReadFile(_handle, buf, (uint)bufferLength, out read, IntPtr.Zero) && read > 0)
                {
                    if (read == bufferLength) return buf;
                    byte[] trimmed = new byte[read];
                    Array.Copy(buf, trimmed, (int)read);
                    return trimmed;
                }
                return null;
            }

            IntPtr ev = HidNative.CreateEventW(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero) return null;
            try
            {
                NativeOverlapped ov = new NativeOverlapped();
                ov.EventHandle = ev;
                uint read;
                bool ok = HidNative.ReadFile(_handle, buf, (uint)bufferLength, out read, ref ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != HidNative.ERROR_IO_PENDING) return null;
                    uint wait = HidNative.WaitForSingleObject(ev, (uint)Math.Max(10, timeoutMs));
                    if (wait != HidNative.WAIT_OBJECT_0)
                    {
                        HidNative.CancelIoEx(_handle, ref ov);
                        HidNative.GetOverlappedResult(_handle, ref ov, out read, true);
                        return null;
                    }
                    if (!HidNative.GetOverlappedResult(_handle, ref ov, out read, false))
                    {
                        return null;
                    }
                }
                if (read == 0) return null;
                if (read == bufferLength) return buf;
                byte[] trimmed = new byte[read];
                Array.Copy(buf, trimmed, (int)read);
                return trimmed;
            }
            finally
            {
                HidNative.CloseHandle(ev);
            }
        }

        public byte[] SendAndReceive(byte[] outReport, int inputLength, Func<byte[], bool> matcher, int timeoutMs, int maxReads)
        {
            if (!WriteReport(outReport, timeoutMs))
            {
                if (!SetOutputReport(outReport)) return null;
            }
            for (int i = 0; i < maxReads; i++)
            {
                byte[] data = ReadReport(inputLength, timeoutMs);
                if (data == null) break;
                if (matcher == null || matcher(data)) return data;
            }
            return null;
        }

        public void Dispose()
        {
            if (IsValid)
            {
                HidNative.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    public static class HidNative
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const uint WAIT_OBJECT_0 = 0x00000000;

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public uint Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid HidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetProductString(IntPtr HidDeviceObject, StringBuilder Buffer, uint BufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetManufacturerString(IntPtr HidDeviceObject, StringBuilder Buffer, uint BufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetSerialNumberString(IntPtr HidDeviceObject, StringBuilder Buffer, uint BufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetValueCaps(int ReportType, [Out] HIDP_VALUE_CAPS[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr HidDeviceObject, [Out] byte[] ReportBuffer, uint ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr HidDeviceObject, [In] byte[] ReportBuffer, uint ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(IntPtr HidDeviceObject, [In] byte[] ReportBuffer, uint ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(IntPtr HidDeviceObject, [Out] byte[] ReportBuffer, uint ReportBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        public static List<HidDeviceNode> EnumerateHidDevices()
        {
            List<HidDeviceNode> result = new List<HidDeviceNode>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr devInfoSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                    ifData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, index, ref ifData))
                    {
                        break;
                    }
                    index++;

                    uint reqSize;
                    SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    if (reqSize <= 4) continue;

                    IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        // On x64, SP_DEVICE_INTERFACE_DETAIL_DATA_W cbSize is 8 (4-byte DWORD + 2-byte TCHAR + 2-byte padding).
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                        SP_DEVINFO_DATA devInfo = new SP_DEVINFO_DATA();
                        devInfo.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                        uint ignored;
                        if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifData, detailBuf, reqSize, out ignored, ref devInfo))
                        {
                            continue;
                        }

                        string path = Marshal.PtrToStringUni(new IntPtr(detailBuf.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        HidDeviceNode node = ReadDeviceNode(path, devInfoSet, ref devInfo);
                        if (node != null)
                        {
                            result.Add(node);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuf);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return result;
        }

        private static HidDeviceNode ReadDeviceNode(string path, IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfo)
        {
            // Open with desiredAccess=0 (query-only) so even exclusive mouse/kbd collections return metadata.
            using (HidDeviceHandle h = HidDeviceHandle.Open(path, false, false, false))
            {
                if (h == null) return null;

                HidDeviceNode node = new HidDeviceNode();
                node.DevicePath = path;
                node.InstanceId = PnpNative.GetDeviceInstanceId(devInfoSet, ref devInfo);
                node.ParentUsbInstanceId = PnpNative.GetPhysicalUsbParentInstanceId(devInfo.DevInst);
                Guid cid;
                if (PnpNative.TryGetDevPropGuid(devInfoSet, ref devInfo, ref PnpNative.DEVPKEY_Device_ContainerId, out cid))
                {
                    node.ContainerId = cid.ToString("D").ToUpperInvariant();
                }
                string busDesc;
                if (PnpNative.TryGetDevPropString(devInfoSet, ref devInfo, ref PnpNative.DEVPKEY_Device_BusReportedDeviceDesc, out busDesc))
                {
                    node.BusReportedDesc = busDesc;
                }

                // Access internal handle via helper on HidNative
                IntPtr rawHandle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                {
                    try
                    {
                        HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                        attr.Size = (uint)Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (HidD_GetAttributes(rawHandle, ref attr))
                        {
                            node.VendorId = attr.VendorID;
                            node.ProductId = attr.ProductID;
                            node.VersionNumber = attr.VersionNumber;
                        }

                        StringBuilder sb = new StringBuilder(256);
                        if (HidD_GetProductString(rawHandle, sb, 512)) node.Product = CleanString(sb.ToString());
                        sb.Clear();
                        if (HidD_GetManufacturerString(rawHandle, sb, 512)) node.Manufacturer = CleanString(sb.ToString());
                        sb.Clear();
                        if (HidD_GetSerialNumberString(rawHandle, sb, 512)) node.SerialNumber = CleanString(sb.ToString());

                        IntPtr preparsed;
                        if (HidD_GetPreparsedData(rawHandle, out preparsed))
                        {
                            try
                            {
                                HIDP_CAPS caps;
                                HidP_GetCaps(preparsed, out caps);
                                node.UsagePage = caps.UsagePage;
                                node.Usage = caps.Usage;
                                node.InputReportLength = caps.InputReportByteLength;
                                node.OutputReportLength = caps.OutputReportByteLength;
                                node.FeatureReportLength = caps.FeatureReportByteLength;

                                ReadValueCaps(0, caps.NumberInputValueCaps, preparsed, node.ValueCaps);
                                ReadValueCaps(1, caps.NumberOutputValueCaps, preparsed, node.ValueCaps);
                                ReadValueCaps(2, caps.NumberFeatureValueCaps, preparsed, node.ValueCaps);
                            }
                            finally
                            {
                                HidD_FreePreparsedData(preparsed);
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(rawHandle);
                    }
                }

                return node;
            }
        }

        private static void ReadValueCaps(int reportType, ushort count, IntPtr preparsed, List<HidValueCapInfo> dest)
        {
            if (count == 0) return;
            HIDP_VALUE_CAPS[] arr = new HIDP_VALUE_CAPS[count];
            ushort len = count;
            HidP_GetValueCaps(reportType, arr, ref len, preparsed);
            for (int i = 0; i < len; i++)
            {
                HidValueCapInfo info = new HidValueCapInfo();
                info.ReportType = reportType;
                info.UsagePage = arr[i].UsagePage;
                info.ReportID = arr[i].ReportID;
                info.UsageMin = arr[i].UsageMin;
                info.UsageMax = arr[i].UsageMax;
                info.BitSize = arr[i].BitSize;
                info.ReportCount = arr[i].ReportCount;
                info.LogicalMin = arr[i].LogicalMin;
                info.LogicalMax = arr[i].LogicalMax;
                dest.Add(info);
            }
        }

        private static string CleanString(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            return s.Trim();
        }
    }
}
