using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NDConnect.Models;
using NDConnect.Native;

namespace NDConnect.Protocols
{
    public sealed class BluetoothChannelProvider : IChannelProvider
    {
        private static readonly string[] AudioServiceUuids = new string[]
        {
            "0000110B-", "0000111E-", "00001108-", "00001131-"
        };

        public string Name
        {
            get { return "Windows Bluetooth (PnP / Classic / BLE GATT)"; }
        }

        public List<DeviceBatteryInfo> Poll(List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            List<PnpDeviceSnapshot> pnpDevices = PnpNative.EnumeratePresentBluetoothPnpDevices();
            List<BluetoothRadioDeviceState> classicStates = PnpNative.EnumerateClassicBluetoothDevices();

            Dictionary<string, BluetoothRadioDeviceState> classicByMac = new Dictionary<string, BluetoothRadioDeviceState>(StringComparer.OrdinalIgnoreCase);
            foreach (BluetoothRadioDeviceState st in classicStates)
            {
                string addrHex = st.Address.ToString("X12").ToUpperInvariant();
                classicByMac[addrHex] = st;
                if (diagLog != null)
                {
                    diagLog.Add(string.Format("[ClassicBT] \"{0}\" MAC={1} Connected={2} CoD=0x{3:X6}",
                        st.Name, addrHex, st.Connected, st.ClassOfDevice));
                }
            }

            // Aggregate device metadata by ContainerId and by 12-hex MAC (like HaloBattery)
            Dictionary<string, string> containerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> macNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, uint> containerCod = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, uint> macCod = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> containerMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> containerAepConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> macAepConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> macHasAudioService = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PnpDeviceSnapshot snap in pnpDevices)
            {
                string cid = snap.ContainerId ?? string.Empty;
                bool hasCid = !string.IsNullOrEmpty(cid) && cid != Guid.Empty.ToString("D").ToUpperInvariant();
                string mac = snap.BluetoothAddress ?? string.Empty;
                bool hasMac = !string.IsNullOrEmpty(mac);
                string upperId = (snap.InstanceId ?? string.Empty).ToUpperInvariant();

                if (hasCid && hasMac && !containerMac.ContainsKey(cid))
                {
                    containerMac[cid] = mac;
                }
                if (snap.IsAepConnectedKnown && snap.IsAepConnected)
                {
                    if (hasCid) containerAepConnected.Add(cid);
                    if (hasMac) macAepConnected.Add(mac);
                }
                if (snap.ClassOfDevice != 0)
                {
                    if (hasCid && !containerCod.ContainsKey(cid)) containerCod[cid] = snap.ClassOfDevice;
                    if (hasMac && !macCod.ContainsKey(mac)) macCod[mac] = snap.ClassOfDevice;
                }
                if (hasMac)
                {
                    foreach (string uuid in AudioServiceUuids)
                    {
                        if (upperId.Contains(uuid))
                        {
                            macHasAudioService.Add(mac);
                            break;
                        }
                    }
                }

                // Prefer root DEV_ node FriendlyName as cleanest display name
                if (upperId.Contains("\\DEV_") && !string.IsNullOrEmpty(snap.FriendlyName))
                {
                    string cleanedRoot = CleanBluetoothName(snap.FriendlyName);
                    if (hasCid) containerNames[cid] = cleanedRoot;
                    if (hasMac) macNames[mac] = cleanedRoot;
                }
                else if (!string.IsNullOrEmpty(snap.FriendlyName))
                {
                    string cleaned = CleanBluetoothName(snap.FriendlyName);
                    if (!string.IsNullOrEmpty(cleaned) && !cleaned.Equals("Bluetooth Peripheral Device", StringComparison.OrdinalIgnoreCase))
                    {
                        if (hasCid && !containerNames.ContainsKey(cid)) containerNames[cid] = cleaned;
                        if (hasMac && !macNames.ContainsKey(mac)) macNames[mac] = cleaned;
                    }
                }
            }

            // 1. Evaluate Windows PnP DEVPKEY_Bluetooth_BatteryLevel using container/MAC-aggregated connection state
            List<DeviceBatteryInfo> pnpAccepted = EvaluatePnpBatteryNodes(
                pnpDevices, classicByMac, containerNames, macNames,
                containerCod, macCod, containerMac,
                containerAepConnected, macAepConnected, macHasAudioService, diagLog);
            results.AddRange(pnpAccepted);

            // 2. Enumerate BLE GATT Battery Service (0x180F)
            PollBleGattBatteryService(containerNames, macNames, containerCod, results, diagLog);

            return results;
        }

        public static List<DeviceBatteryInfo> EvaluatePnpBatteryNodes(
            List<PnpDeviceSnapshot> pnpDevices,
            Dictionary<string, BluetoothRadioDeviceState> classicByMac,
            Dictionary<string, string> containerNames,
            Dictionary<string, string> macNames,
            Dictionary<string, uint> containerCod,
            Dictionary<string, uint> macCod,
            Dictionary<string, string> containerMac,
            HashSet<string> containerAepConnected,
            HashSet<string> macAepConnected,
            HashSet<string> macHasAudioService,
            List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();

            foreach (PnpDeviceSnapshot snap in pnpDevices)
            {
                if (!snap.HasBatteryLevel || snap.BatteryLevel < 0 || snap.BatteryLevel > 100) continue;

                string cid = snap.ContainerId ?? string.Empty;
                bool hasCid = !string.IsNullOrEmpty(cid) && cid != Guid.Empty.ToString("D").ToUpperInvariant();
                string mac = snap.BluetoothAddress ?? string.Empty;
                if (string.IsNullOrEmpty(mac) && hasCid) containerMac.TryGetValue(cid, out mac);
                bool hasMac = !string.IsNullOrEmpty(mac);

                bool isAepConn = (snap.IsAepConnectedKnown && snap.IsAepConnected)
                    || (hasCid && containerAepConnected.Contains(cid))
                    || (hasMac && macAepConnected.Contains(mac));

                bool hasClassicEntry = false;
                bool isClassicConn = false;
                uint classicCod = 0;
                BluetoothRadioDeviceState classicState = null;
                if (hasMac && classicByMac.TryGetValue(mac, out classicState))
                {
                    hasClassicEntry = true;
                    isClassicConn = classicState.Connected;
                    classicCod = classicState.ClassOfDevice;
                }

                // Connected if EITHER Classic BT radio says fConnected OR any PnP node in the device's container/MAC says Aep_IsConnected=True.
                // Skip if known disconnected on both (prevents showing stale batteries of powered-off paired devices).
                bool isConnected;
                if (isClassicConn || isAepConn)
                {
                    isConnected = true;
                }
                else if (hasClassicEntry || snap.IsAepConnectedKnown)
                {
                    isConnected = false;
                }
                else
                {
                    isConnected = snap.IsPresentAndActive;
                }

                if (!isConnected)
                {
                    if (diagLog != null)
                    {
                        diagLog.Add(string.Format("[BT PnP Skip] \"{0}\" Bat={1}% MAC={2} (AepConn={3}, ClassicConn={4})",
                            snap.FriendlyName, snap.BatteryLevel, hasMac ? mac : "-", isAepConn, isClassicConn));
                    }
                    continue;
                }

                string name = string.Empty;
                if (hasMac) macNames.TryGetValue(mac, out name);
                if (string.IsNullOrEmpty(name) && hasCid) containerNames.TryGetValue(cid, out name);
                if (string.IsNullOrEmpty(name) && classicState != null && !string.IsNullOrEmpty(classicState.Name))
                {
                    name = CleanBluetoothName(classicState.Name);
                }
                if (string.IsNullOrEmpty(name)) name = CleanBluetoothName(snap.FriendlyName);
                if (string.IsNullOrEmpty(name)) name = "Bluetooth Device";

                uint cod = snap.ClassOfDevice;
                if (cod == 0 && hasMac) macCod.TryGetValue(mac, out cod);
                if (cod == 0 && hasCid) containerCod.TryGetValue(cid, out cod);
                if (cod == 0) cod = classicCod;

                DeviceCategory cat = ClassifyBluetoothDevice(cod, name, snap.InstanceId);
                if (cat == DeviceCategory.Unknown && hasMac && macHasAudioService.Contains(mac))
                {
                    cat = DeviceCategory.Headset;
                }

                DeviceBatteryInfo dev = new DeviceBatteryInfo();
                dev.ContainerId = cid;
                dev.Id = hasMac ? ("BT_MAC_" + mac) : (hasCid ? ("BT_" + cid) : ("BT_" + snap.InstanceId));
                dev.Name = name;
                dev.Category = cat;
                dev.Channel = ConnectionChannel.Bluetooth;
                dev.BatteryPercent = Math.Max(0, Math.Min(100, snap.BatteryLevel));
                dev.IsCharging = false;
                dev.IsAsleep = false;
                dev.ProtocolName = (snap.InstanceId ?? string.Empty).ToUpperInvariant().Contains("BTHHFENUM") ||
                                   (snap.InstanceId ?? string.Empty).ToUpperInvariant().Contains("0000111E-")
                    ? "Bluetooth HFP (PnP)"
                    : "Bluetooth PnP Battery";
                dev.Priority = 50;
                dev.ExtraInfo = snap.InstanceId;
                HidChannelProvider.EnsureGlyphType(dev);

                if (diagLog != null)
                {
                    diagLog.Add(string.Format("[BT PnP] \"{0}\" Cat={1} Bat={2}% CoD=0x{3:X6} MAC={4} ({5})",
                        dev.Name, dev.Category, dev.BatteryPercent, cod, hasMac ? mac : "-", dev.ProtocolName));
                }

                results.Add(dev);
            }

            return results;
        }

        private static void PollBleGattBatteryService(
            Dictionary<string, string> containerNames,
            Dictionary<string, string> macNames,
            Dictionary<string, uint> containerCod,
            List<DeviceBatteryInfo> results,
            List<string> diagLog)
        {
            List<PnpNative.BleBatteryInterfaceInfo> bleIfaces = PnpNative.EnumerateBleGattBatteryInterfaces();
            if (diagLog != null)
            {
                diagLog.Add(string.Format("[BLE GATT] Enumerated {0} 0x180F interfaces", bleIfaces.Count));
            }
            foreach (PnpNative.BleBatteryInterfaceInfo iface in bleIfaces)
            {
                if (string.IsNullOrEmpty(iface.DevicePath)) continue;
                try
                {
                    int pct;
                    bool charging;
                    if (ReadBleBatteryInterface(iface.DevicePath, out pct, out charging))
                    {
                        string mac = PnpNative.ExtractBluetoothMacFromInstanceId(iface.InstanceId);
                        string name = string.Empty;
                        if (!string.IsNullOrEmpty(mac)) macNames.TryGetValue(mac, out name);
                        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(iface.ContainerId)) containerNames.TryGetValue(iface.ContainerId, out name);
                        if (string.IsNullOrEmpty(name)) name = CleanBluetoothName(iface.FriendlyName);
                        if (string.IsNullOrEmpty(name)) name = "BLE Device";

                        uint cod = 0;
                        if (!string.IsNullOrEmpty(iface.ContainerId)) containerCod.TryGetValue(iface.ContainerId, out cod);

                        DeviceBatteryInfo dev = new DeviceBatteryInfo();
                        dev.ContainerId = iface.ContainerId ?? string.Empty;
                        dev.Id = !string.IsNullOrEmpty(mac) ? ("BT_MAC_" + mac)
                            : (!string.IsNullOrEmpty(iface.ContainerId) ? "BLE_" + iface.ContainerId : "BLE_" + iface.InstanceId);
                        dev.Name = name;
                        dev.Category = ClassifyBluetoothDevice(cod, name, iface.InstanceId);
                        dev.Channel = ConnectionChannel.Bluetooth;
                        dev.BatteryPercent = pct;
                        dev.IsCharging = charging;
                        dev.IsAsleep = false;
                        dev.ProtocolName = "BLE GATT 0x180F";
                        dev.Priority = 75;
                        dev.ExtraInfo = iface.InstanceId;
                        HidChannelProvider.EnsureGlyphType(dev);

                        if (diagLog != null)
                        {
                            diagLog.Add(string.Format("[BLE GATT] \"{0}\" Bat={1}% Charging={2}", dev.Name, dev.BatteryPercent, dev.IsCharging));
                        }
                        results.Add(dev);
                    }
                    else if (diagLog != null)
                    {
                        diagLog.Add(string.Format("[BLE GATT] Interface \"{0}\" returned no 0x2A19 value", iface.FriendlyName));
                    }
                }
                catch (Exception ex)
                {
                    if (diagLog != null)
                    {
                        diagLog.Add(string.Format("[BLE GATT Error] \"{0}\": {1}", iface.FriendlyName, ex.Message));
                    }
                }
            }
        }

        private static bool ReadBleBatteryInterface(string devicePath, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;

            IntPtr h = HidNative.CreateFileW(
                devicePath,
                HidNative.GENERIC_READ,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero,
                HidNative.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                return false;
            }

            try
            {
                ushort svcCount = 0;
                PnpNative.BluetoothGATTGetServices(h, 0, null, out svcCount, 0);
                if (svcCount == 0) return false;

                PnpNative.BTH_LE_GATT_SERVICE[] svcs = new PnpNative.BTH_LE_GATT_SERVICE[svcCount];
                ushort actualSvcs;
                int hr = PnpNative.BluetoothGATTGetServices(h, svcCount, svcs, out actualSvcs, 0);
                if (hr != 0 || actualSvcs == 0) return false;

                for (int s = 0; s < actualSvcs; s++)
                {
                    PnpNative.BTH_LE_GATT_SERVICE svc = svcs[s];
                    ushort shortSvc = ExtractShortUuid(svc.ServiceUuid);
                    if (shortSvc != 0x180F && actualSvcs > 1) continue;

                    ushort charCount = 0;
                    PnpNative.BluetoothGATTGetCharacteristics(h, ref svc, 0, null, out charCount, 0);
                    if (charCount == 0) continue;

                    PnpNative.BTH_LE_GATT_CHARACTERISTIC[] chars = new PnpNative.BTH_LE_GATT_CHARACTERISTIC[charCount];
                    ushort actualChars;
                    if (PnpNative.BluetoothGATTGetCharacteristics(h, ref svc, charCount, chars, out actualChars, 0) != 0)
                    {
                        continue;
                    }

                    for (int c = 0; c < actualChars; c++)
                    {
                        PnpNative.BTH_LE_GATT_CHARACTERISTIC ch = chars[c];
                        ushort uuid16 = ExtractShortUuid(ch.CharacteristicUuid);
                        if (uuid16 != 0x2A19 && uuid16 != 0x2A1A) continue;

                        ushort reqSize = 0;
                        PnpNative.BluetoothGATTGetCharacteristicValue(h, ref ch, 0, IntPtr.Zero, out reqSize, 0);
                        if (reqSize < 5) continue;

                        IntPtr buf = Marshal.AllocHGlobal(reqSize);
                        try
                        {
                            ushort unused;
                            if (PnpNative.BluetoothGATTGetCharacteristicValue(h, ref ch, reqSize, buf, out unused, 0) == 0)
                            {
                                int dataSize = Marshal.ReadInt32(buf, 0);
                                if (dataSize >= 1)
                                {
                                    byte b0 = Marshal.ReadByte(buf, 4);
                                    if (uuid16 == 0x2A19 && b0 <= 100)
                                    {
                                        percent = b0;
                                    }
                                    else if (uuid16 == 0x2A1A)
                                    {
                                        int chargeState = (b0 >> 4) & 0x03;
                                        if (chargeState == 3) charging = true;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buf);
                        }
                    }
                }
            }
            finally
            {
                HidNative.CloseHandle(h);
            }

            return percent >= 0;
        }

        private static ushort ExtractShortUuid(PnpNative.BTH_LE_UUID uuid)
        {
            if (uuid.IsShortUuid != 0)
            {
                byte[] bytes = uuid.LongUuid.ToByteArray();
                return (ushort)(bytes[0] | (bytes[1] << 8));
            }
            byte[] g = uuid.LongUuid.ToByteArray();
            uint d1 = BitConverter.ToUInt32(g, 0);
            ushort d2 = BitConverter.ToUInt16(g, 4);
            ushort d3 = BitConverter.ToUInt16(g, 6);
            if (d2 == 0x0000 && d3 == 0x1000 && g[8] == 0x80 && g[9] == 0x00 &&
                g[10] == 0x00 && g[11] == 0x80 && g[12] == 0x5F && g[13] == 0x9B && g[14] == 0x34 && g[15] == 0xFB)
            {
                return (ushort)(d1 & 0xFFFF);
            }
            return 0;
        }

        public static string CleanBluetoothName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string s = raw.Trim();
            string[] suffixes = new string[]
            {
                " Hands-Free AG Audio",
                " Hands-Free AG",
                " Hands-Free",
                " Avrcp Transport",
                " Stereo"
            };
            foreach (string suf in suffixes)
            {
                if (s.EndsWith(suf, StringComparison.OrdinalIgnoreCase) && s.Length > suf.Length + 1)
                {
                    s = s.Substring(0, s.Length - suf.Length).Trim();
                }
            }
            return s;
        }

        public static DeviceCategory ClassifyBluetoothDevice(uint cod, string name, string instanceId)
        {
            uint major = (cod >> 8) & 0x1F;
            uint minor = (cod >> 2) & 0x3F;

            if (major == 0x04) // Audio/Video
            {
                if (minor == 1 || minor == 2 || minor == 6) return DeviceCategory.Headset;
                return DeviceCategory.Headset;
            }
            if (major == 0x05) // Peripheral
            {
                if ((minor & 0x0F) == 0x01 || (minor & 0x0F) == 0x02) return DeviceCategory.Gamepad;
                int sub = (int)(minor >> 4);
                if (sub == 2) return DeviceCategory.Mouse;
                if (sub == 1 || sub == 3) return DeviceCategory.Keyboard;
            }

            string up = ((name ?? string.Empty) + " " + (instanceId ?? string.Empty)).ToUpperInvariant();
            if (up.Contains("BTHHFENUM") || up.Contains("MOMENTUM") || up.Contains("MOONDROP") ||
                up.Contains("AIRPODS") || up.Contains("SONY WH-") || up.Contains("SONY WF-") ||
                up.Contains("HEADSET") || up.Contains("HEADPHONE") || up.Contains("BUDS") ||
                up.Contains("QC") || up.Contains("BOSE") || up.Contains("JBL") || up.Contains("EDIFIER") ||
                up.Contains("SONOFLOW") || up.Contains("1MORE"))
            {
                return DeviceCategory.Headset;
            }
            if (up.Contains("CONTROLLER") || up.Contains("GAMEPAD") || up.Contains("XBOX") ||
                up.Contains("DUALSENSE") || up.Contains("DUALSHOCK") || up.Contains("8BITDO") || up.Contains("PRO CONTROLLER"))
            {
                return DeviceCategory.Gamepad;
            }
            if (up.Contains("MOUSE") || up.Contains("MX MASTER") || up.Contains("MX ANYWHERE") || up.Contains("VIPER"))
            {
                return DeviceCategory.Mouse;
            }
            if (up.Contains("KEYBOARD") || up.Contains("MX KEYS") || up.Contains("KEYCHRON") || up.Contains("HHKB"))
            {
                return DeviceCategory.Keyboard;
            }

            return DeviceCategory.Unknown;
        }
    }
}
