using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NDConnect.Models;
using NDConnect.Native;
using NDConnect.Protocols.Handlers;

namespace NDConnect.Protocols
{
    public sealed class HidChannelProvider : IChannelProvider
    {
        private readonly List<IHidProtocolHandler> _handlers;

        public string Name
        {
            get { return "HID (2.4GHz / USB / BT-HID)"; }
        }

        public HidChannelProvider()
        {
            _handlers = new List<IHidProtocolHandler>();
            _handlers.Add(new LogitechHidppHandler());
            _handlers.Add(new RazerHidHandler());
            _handlers.Add(new SonyGamepadHandler());
            _handlers.Add(new SteelSeriesHandler());
            _handlers.Add(new CorsairHyperXHandler());
            _handlers.Add(new AsusRogHandler());
            _handlers.Add(new CompxProtocolHandler());
            _handlers.Add(new NordicFeature83Handler());
            _handlers.Add(new MchoseRealtekHandler());
            _handlers.Add(new EvisionTelinkHandler());
            _handlers.Add(new KeychronRapooAudezeHandler());
            _handlers.Add(new NintendoEightBitDoHandler());
            _handlers.Add(new GenericHidBatteryHandler());
        }

        public List<DeviceBatteryInfo> Poll(List<string> diagLog)
        {
            List<DeviceBatteryInfo> results = new List<DeviceBatteryInfo>();
            List<HidDeviceNode> nodes = HidNative.EnumerateHidDevices();
            List<HidDeviceGroup> groups = GroupNodes(nodes);

            // Poll all HID groups concurrently so dual/multiple 2.4GHz dongles never block each other.
            List<Task<GroupPollOutput>> tasks = new List<Task<GroupPollOutput>>();
            foreach (HidDeviceGroup grp in groups)
            {
                HidDeviceGroup captured = grp;
                tasks.Add(Task.Factory.StartNew(delegate()
                {
                    return PollSingleGroup(captured);
                }));
            }

            if (tasks.Count > 0)
            {
                try
                {
                    Task.WaitAll(tasks.ToArray(), 3000);
                }
                catch
                {
                }
            }

            foreach (Task<GroupPollOutput> t in tasks)
            {
                if (!t.IsCompleted || t.IsFaulted || t.Result == null) continue;
                if (diagLog != null && t.Result.Logs.Count > 0)
                {
                    diagLog.AddRange(t.Result.Logs);
                }
                if (t.Result.Devices.Count > 0)
                {
                    results.AddRange(t.Result.Devices);
                }
            }

            return results;
        }

        private sealed class GroupPollOutput
        {
            public List<DeviceBatteryInfo> Devices = new List<DeviceBatteryInfo>();
            public List<string> Logs = new List<string>();
        }

        private GroupPollOutput PollSingleGroup(HidDeviceGroup grp)
        {
            GroupPollOutput output = new GroupPollOutput();
            output.Logs.Add(string.Format("[HID Group] {0:X4}:{1:X4} \"{2}\" Cat={3} Chan={4} Key={5} Cols={6}",
                grp.VendorId, grp.ProductId, grp.DisplayName, grp.InferredCategory, grp.InferredChannel, grp.GroupKey, grp.Nodes.Count));

            foreach (IHidProtocolHandler handler in _handlers)
            {
                if (!handler.CanHandle(grp)) continue;
                try
                {
                    List<DeviceBatteryInfo> found = handler.Query(grp, output.Logs);
                    if (found != null && found.Count > 0)
                    {
                        foreach (DeviceBatteryInfo dev in found)
                        {
                            EnsureGlyphType(dev);
                            output.Devices.Add(dev);
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    output.Logs.Add(string.Format("  [Handler Error] {0}: {1}", handler.Name, ex.Message));
                }
            }
            return output;
        }

        public static void EnsureGlyphType(DeviceBatteryInfo dev)
        {
            if (dev == null) return;
            string up = (dev.Name ?? string.Empty).ToUpperInvariant();
            if (dev.Category == DeviceCategory.Gamepad || up.Contains("CONTROLLER") || up.Contains("GAMEPAD"))
            {
                if (up.Contains("DUALSENSE") || up.Contains("DUALSHOCK") || up.Contains("PLAYSTATION") || up.Contains("SONY"))
                    dev.GlyphType = DeviceGlyphType.GamepadPlayStation;
                else if (up.Contains("XBOX"))
                    dev.GlyphType = DeviceGlyphType.GamepadXbox;
                else if (up.Contains("NINTENDO") || up.Contains("SWITCH") || up.Contains("JOY-CON") || up.Contains("PRO CONTROLLER"))
                    dev.GlyphType = DeviceGlyphType.GamepadNintendo;
                else
                    dev.GlyphType = DeviceGlyphType.GamepadGeneric;
                return;
            }
            if (up.Contains("MIC") || up.Contains("MICROPHONE") || up.Contains("BLUE YETI") || up.Contains("WAVE:"))
            {
                dev.GlyphType = DeviceGlyphType.Microphone;
                return;
            }
            switch (dev.Category)
            {
                case DeviceCategory.Mouse:
                    dev.GlyphType = DeviceGlyphType.Mouse;
                    break;
                case DeviceCategory.Keyboard:
                    dev.GlyphType = DeviceGlyphType.Keyboard;
                    break;
                case DeviceCategory.Headset:
                    dev.GlyphType = DeviceGlyphType.Headset;
                    break;
                default:
                    dev.GlyphType = dev.Channel == ConnectionChannel.Bluetooth
                        ? DeviceGlyphType.BluetoothGeneric
                        : DeviceGlyphType.Mouse;
                    break;
            }
        }

        private static List<HidDeviceGroup> GroupNodes(List<HidDeviceNode> nodes)
        {
            Dictionary<string, HidDeviceGroup> map = new Dictionary<string, HidDeviceGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceNode node in nodes)
            {
                if (node.VendorId == 0 && node.ProductId == 0) continue;

                string key;
                if (!string.IsNullOrEmpty(node.ParentUsbInstanceId))
                {
                    key = string.Format("{0:X4}_{1:X4}_{2}", node.VendorId, node.ProductId, node.ParentUsbInstanceId);
                }
                else if (!string.IsNullOrEmpty(node.ContainerId) && node.ContainerId != Guid.Empty.ToString("D").ToUpperInvariant())
                {
                    key = string.Format("{0:X4}_{1:X4}_{2}", node.VendorId, node.ProductId, node.ContainerId);
                }
                else
                {
                    key = string.Format("{0:X4}_{1:X4}", node.VendorId, node.ProductId);
                }

                HidDeviceGroup grp;
                if (!map.TryGetValue(key, out grp))
                {
                    grp = new HidDeviceGroup();
                    grp.GroupKey = key;
                    grp.ContainerId = node.ContainerId;
                    grp.VendorId = node.VendorId;
                    grp.ProductId = node.ProductId;
                    map[key] = grp;
                }
                grp.Nodes.Add(node);
            }

            foreach (HidDeviceGroup grp in map.Values)
            {
                grp.DisplayName = ResolveGroupName(grp);
                grp.InferredCategory = InferCategory(grp);
                grp.InferredChannel = InferChannel(grp);
            }

            return map.Values.ToList();
        }

        private static string ResolveGroupName(HidDeviceGroup grp)
        {
            string best = string.Empty;
            foreach (HidDeviceNode n in grp.Nodes)
            {
                if (!string.IsNullOrEmpty(n.Product) && n.Product.Length > best.Length)
                {
                    best = n.Product;
                }
            }
            if (string.IsNullOrEmpty(best))
            {
                foreach (HidDeviceNode n in grp.Nodes)
                {
                    if (!string.IsNullOrEmpty(n.BusReportedDesc))
                    {
                        best = n.BusReportedDesc;
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(best))
            {
                best = string.Format("HID Device {0:X4}:{1:X4}", grp.VendorId, grp.ProductId);
            }
            return CleanDeviceDisplayName(best);
        }

        public static string CleanDeviceDisplayName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string s = raw.Trim();
            string[] trimSuffixes = new string[]
            {
                " 2.4G Dongle",
                " 2.4GHz Dongle",
                " 2.4G Receiver",
                " Wireless Receiver",
                " Dongle",
                " Receiver"
            };
            foreach (string suffix in trimSuffixes)
            {
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && s.Length > suffix.Length + 2)
                {
                    s = s.Substring(0, s.Length - suffix.Length).Trim();
                }
            }
            return s;
        }

        private static DeviceCategory InferCategory(HidDeviceGroup grp)
        {
            string upper = (grp.DisplayName ?? string.Empty).ToUpperInvariant();
            if (upper.Contains("HEADSET") || upper.Contains("HEADPHONE") || upper.Contains("ARCTIS") ||
                upper.Contains("CLOUD") || upper.Contains("VIRTUOSO") || upper.Contains("VOID") ||
                upper.Contains("BLACKSHARK") || upper.Contains("BARRACUDA") || upper.Contains("EARPHONE"))
            {
                return DeviceCategory.Headset;
            }
            if (upper.Contains("CONTROLLER") || upper.Contains("DUALSENSE") || upper.Contains("DUALSHOCK") ||
                upper.Contains("XBOX") || upper.Contains("GAMEPAD"))
            {
                return DeviceCategory.Gamepad;
            }

            bool hasMouse = false;
            bool hasKeyboard = false;
            bool hasGamepad = false;
            bool hasVendorMouse = false;

            foreach (HidDeviceNode n in grp.Nodes)
            {
                if (n.UsagePage == 0x0001)
                {
                    if (n.Usage == 0x0002) hasMouse = true;
                    else if (n.Usage == 0x0006) hasKeyboard = true;
                    else if (n.Usage == 0x0004 || n.Usage == 0x0005) hasGamepad = true;
                }
                if (n.UsagePage == 0xFF01 || n.UsagePage == 0xFF0B) hasVendorMouse = true;
            }

            if (hasGamepad) return DeviceCategory.Gamepad;
            if (upper.Contains("MOUSE") || upper.Contains("VIPER") || upper.Contains("DEATHADDER") ||
                upper.Contains("BASILISK") || upper.Contains("SUPERLIGHT") || upper.Contains("A7X") ||
                upper.Contains("AEROX") || upper.Contains("RIVAL"))
            {
                return DeviceCategory.Mouse;
            }
            if (upper.Contains("KEYBOARD") || upper.Contains("V98") || upper.Contains("KEYCHRON") ||
                upper.Contains("BLACKWIDOW") || upper.Contains("HUNTSMAN") || upper.Contains("APEX"))
            {
                return DeviceCategory.Keyboard;
            }
            // Note: Many 2.4G mice expose both Mouse (UP=1 U=2) and Keyboard (UP=1 U=6) for side-button macros!
            if (hasMouse && !hasKeyboard) return DeviceCategory.Mouse;
            if (hasMouse && hasKeyboard) return hasVendorMouse ? DeviceCategory.Mouse : DeviceCategory.Keyboard;
            if (hasKeyboard) return DeviceCategory.Keyboard;
            return DeviceCategory.Unknown;
        }

        private static ConnectionChannel InferChannel(HidDeviceGroup grp)
        {
            foreach (HidDeviceNode n in grp.Nodes)
            {
                string p = (n.DevicePath ?? string.Empty).ToUpperInvariant();
                string iid = (n.InstanceId ?? string.Empty).ToUpperInvariant();
                if (p.Contains("BTHENUM") || p.Contains("BTHLE") || iid.Contains("BTHENUM") || iid.Contains("BTHLE"))
                {
                    return ConnectionChannel.Bluetooth;
                }
            }

            string combined = string.Empty;
            foreach (HidDeviceNode n in grp.Nodes)
            {
                combined += " " + n.Product + " " + n.BusReportedDesc;
            }
            string up = combined.ToUpperInvariant();
            if (up.Contains("2.4G") || up.Contains("DONGLE") || up.Contains("RECEIVER") ||
                up.Contains("LIGHTSPEED") || up.Contains("UNIFYING") || up.Contains("BOLT") ||
                up.Contains("HYPERSPEED") || up.Contains("SLIPSTREAM") || up.Contains("WIRELESS"))
            {
                // Sony DualSense Wireless Controller connected via USB still contains "Wireless Controller" in product string.
                if (grp.VendorId == 0x054C) return ConnectionChannel.UsbWired;
                return ConnectionChannel.Wireless24G;
            }

            // Known 2.4GHz wireless receiver PIDs
            if (grp.VendorId == 0x046D && (grp.ProductId == 0xC52B || grp.ProductId == 0xC532 ||
                grp.ProductId == 0xC534 || grp.ProductId == 0xC539 || grp.ProductId == 0xC53A ||
                grp.ProductId == 0xC53F || grp.ProductId == 0xC541 || grp.ProductId == 0xC545 ||
                grp.ProductId == 0xC547 || grp.ProductId == 0xC548))
            {
                return ConnectionChannel.Wireless24G;
            }
            if (grp.VendorId == 0x320F && grp.ProductId == 0x5088) return ConnectionChannel.Wireless24G;
            if (grp.VendorId == 0x3837 && (grp.ProductId == 0x1009 || grp.ProductId == 0x100B || grp.ProductId == 0x100C))
            {
                return ConnectionChannel.Wireless24G;
            }

            return ConnectionChannel.UsbWired;
        }
    }
}
