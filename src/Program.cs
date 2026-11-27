using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using NDConnect.Models;
using NDConnect.Native;
using NDConnect.Protocols;
using NDConnect.Protocols.Handlers;
using NDConnect.Services;
using NDConnect.UI;

namespace NDConnect
{
    public static class Program
    {
        private const string MutexName = @"Local\NDConnect_BatteryMonitor_Mutex";

        [STAThread]
        public static int Main(string[] args)
        {
            User32Native.EnablePerMonitorDpiAwareness();

            if (args != null && args.Length > 0)
            {
                string cmd = args[0].Trim().ToLowerInvariant();
                if (cmd == "--scan" || cmd == "--diag")
                {
                    if (!Console.IsOutputRedirected) User32Native.AttachConsole(-1);
                    return RunCliScan(cmd == "--diag");
                }
                if (cmd == "--selftest")
                {
                    if (!Console.IsOutputRedirected) User32Native.AttachConsole(-1);
                    return RunSelfTest();
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
                return 0;
            }
        }

        private static int RunCliScan(bool showDiag)
        {
            using (DeviceMonitorService svc = new DeviceMonitorService(null, 30))
            {
                List<DeviceBatteryInfo> devices = svc.RunPollOnce(true);
                if (showDiag)
                {
                    foreach (string line in svc.GetDiagnosticLog())
                    {
                        Console.WriteLine(line);
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("Detected Devices: " + devices.Count);
                foreach (DeviceBatteryInfo d in devices)
                {
                    string batText = d.BatteryPercent >= 0 ? (d.BatteryPercent + "%") : "--";
                    Console.WriteLine(string.Format(" - [{0}] {1} | Channel={2} | Battery={3} | Charging={4} | Asleep={5} | Protocol={6}",
                        d.Category, d.Name, d.Channel, batText, d.IsCharging, d.IsAsleep, d.ProtocolName));
                }
            }
            return 0;
        }

        private static int RunSelfTest()
        {
            int failures = 0;
            Console.WriteLine("=== NDConnect SelfTest ===");

            // 1. Battery color gradient verification
            Color c0 = RingIconRenderer.GetBatteryColor(0);
            Color c50 = RingIconRenderer.GetBatteryColor(50);
            Color c100 = RingIconRenderer.GetBatteryColor(100);
            if (c0.R != 255 || c0.G != 59 || c0.B != 48) { Console.WriteLine("FAIL: 0% color mismatch"); failures++; }
            if (c50.R != 255 || c50.G != 204 || c50.B != 0) { Console.WriteLine("FAIL: 50% color mismatch"); failures++; }
            if (c100.R != 34 || c100.G != 197 || c100.B != 94) { Console.WriteLine("FAIL: 100% color mismatch"); failures++; }

            // 2. Green breathing curve verification
            double p0 = RingIconRenderer.GetBreathPulse(0.0);
            double pPi = RingIconRenderer.GetBreathPulse(Math.PI);
            if (Math.Abs(p0 - 0.0) > 1e-6 || Math.Abs(pPi - 1.0) > 1e-6)
            {
                Console.WriteLine("FAIL: Breath pulse range"); failures++;
            }
            Color bLow = RingIconRenderer.GetChargingBreathColor(0.0);
            Color bHigh = RingIconRenderer.GetChargingBreathColor(Math.PI);
            if (!(bHigh.G > bLow.G)) { Console.WriteLine("FAIL: Charging breath luminance"); failures++; }

            // 3. Protocol parser tests
            int pct; bool chg; bool full;
            byte[] logi1004 = new byte[] { 0x11, 0x01, 0x05, 0x1A, 68, 0x04, 0x01, 0x00 };
            if (!LogitechHidppHandler.ParseUnifiedBattery(logi1004, out pct, out chg, out full) || pct != 68 || !chg)
            {
                Console.WriteLine("FAIL: Logitech 0x1004 parser"); failures++;
            }

            byte[] dsUsb = new byte[64];
            dsUsb[0] = 0x01;
            dsUsb[53] = 0x17; // charging=1, cap=7 (~75%)
            if (!SonyGamepadHandler.ParseDualSenseReport(dsUsb, false, out pct, out chg, out full) || pct != 75 || !chg)
            {
                Console.WriteLine("FAIL: DualSense USB parser"); failures++;
            }

            byte[] dsBt = new byte[78];
            dsBt[0] = 0x31;
            dsBt[54] = 0x09; // discharging, cap=9 (~95%)
            if (!SonyGamepadHandler.ParseDualSenseReport(dsBt, true, out pct, out chg, out full) || pct != 95 || chg)
            {
                Console.WriteLine("FAIL: DualSense BT parser"); failures++;
            }

            byte[] razerResp = new byte[91];
            razerResp[1] = 0x02; razerResp[7] = 0x07; razerResp[8] = 0x80; razerResp[10] = 204; // ~80%
            if (!RazerHidHandler.ParseRazerBatteryResponse(razerResp, out pct) || pct != 80)
            {
                Console.WriteLine("FAIL: Razer parser"); failures++;
            }

            byte[] evResp = new byte[64];
            evResp[0] = 0x04; evResp[1] = 0x2A; evResp[2] = 0x00; evResp[3] = 0x1A; evResp[8] = 0x3D; evResp[9] = 0x00;
            if (!EvisionTelinkHandler.ParseEvisionBatteryResponse(evResp, 0x002A, out pct, out chg) || pct != 61 || chg)
            {
                Console.WriteLine("FAIL: EVision live 0x3D parser"); failures++;
            }

            byte[] evPkt = EvisionTelinkHandler.BuildEvisionBatteryQuery(64, 0x1234);
            if (evPkt[0] != 0x04 || evPkt[1] != 0x34 || evPkt[2] != 0x12 || evPkt[3] != 0x1A)
            {
                Console.WriteLine("FAIL: EVision query packet builder"); failures++;
            }

            // HaloBattery-derived parser tests
            byte[] compxReq = CompxProtocolHandler.BuildRequest(17);
            byte[] compxResp = new byte[17];
            compxResp[0] = 0x08; compxResp[1] = 0x04; compxResp[6] = 80; compxResp[7] = 0x00; compxResp[8] = 0x0F; compxResp[9] = 0xAD;
            compxResp[16] = CompxProtocolHandler.Checksum(compxResp, 16);
            int mv;
            if (compxReq[0] != 0x08 || !CompxProtocolHandler.ParsePowerResponse(compxResp, out pct, out chg, out mv) || pct != 80 || chg || mv != 0x0FAD)
            {
                Console.WriteLine("FAIL: CompX parser"); failures++;
            }

            byte[] n83 = new byte[] { 0xA1, 0x00, 0x02, 0x02, 0x00, 0x83, 0x01, 0x4B };
            if (!NordicFeature83Handler.ParseFeature83(n83, out pct, out chg) || pct != 75 || !chg)
            {
                Console.WriteLine("FAIL: Nordic 0x83 parser"); failures++;
            }

            byte[] asus = new byte[] { 0x12, 0x07, 0x00, 0x00, 72, 0x00, 0x00, 0x00, 0x00, 0x01 };
            if (!AsusRogHandler.ParseRogMouseReply(asus, 0x00, out pct, out chg) || pct != 72 || !chg)
            {
                Console.WriteLine("FAIL: ASUS ROG parser"); failures++;
            }

            byte[] kc = new byte[64]; kc[0] = 0xB4; kc[1] = 0x06; kc[20] = 88;
            if (!KeychronRapooAudezeHandler.ParseKeychronReply(kc, out pct) || pct != 88)
            {
                Console.WriteLine("FAIL: Keychron parser"); failures++;
            }

            byte[] rapoo = new byte[] { 0xBD, 0x01, 0x00, 0x07, 0x00, 0x46, 0x00, 0x01, 84 };
            if (!KeychronRapooAudezeHandler.ParseRapooReport(rapoo, out pct, out chg) || pct != 84 || chg)
            {
                Console.WriteLine("FAIL: Rapoo 0xBD parser"); failures++;
            }

            byte[] nin = new byte[] { 0x21, 0x01, 0x70 }; // level=6 (75%), charging=1
            if (!NintendoEightBitDoHandler.ParseNintendoReport(nin, out pct, out chg) || pct != 75 || !chg)
            {
                Console.WriteLine("FAIL: Nintendo parser"); failures++;
            }

            byte[] ebd = new byte[16]; ebd[0] = 0x01; ebd[14] = 0x94; // 0x80 charging + 20%
            if (!NintendoEightBitDoHandler.ParseEightBitDoEnhanced(ebd, 0x01, out pct, out chg) || pct != 20 || !chg)
            {
                Console.WriteLine("FAIL: 8BitDo parser"); failures++;
            }

            byte[] voidElite = new byte[] { 0x64, 0x00, 0xCA, 0xB1, 0x04 }; // 0xCA & 0x7F = 74%, status=4 (charging)
            if (!CorsairHyperXHandler.ParseCorsairVoidElite(voidElite, out pct, out chg) || pct != 74 || !chg)
            {
                Console.WriteLine("FAIL: Corsair Void Elite parser"); failures++;
            }

            // Bluetooth MAC extraction & container-level connection aggregation tests
            string mac1 = PnpNative.ExtractBluetoothMacFromInstanceId(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&005D\A&33CC5751&0&80C3BA9C88BE_C00000000");
            if (mac1 != "80C3BA9C88BE")
            {
                Console.WriteLine("FAIL: BT MAC extraction: " + mac1); failures++;
            }

            List<PnpDeviceSnapshot> btSnaps = new List<PnpDeviceSnapshot>();
            PnpDeviceSnapshot sChildConn = new PnpDeviceSnapshot();
            sChildConn.InstanceId = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}\A&1&0&80C3BA9C88BE_C0";
            sChildConn.FriendlyName = "MOMENTUM 4 Hands-Free AG";
            sChildConn.ContainerId = "{6DEB6566-F571-57F9-A51E-86BE173F16C7}";
            sChildConn.BluetoothAddress = "80C3BA9C88BE";
            sChildConn.HasBatteryLevel = true;
            sChildConn.BatteryLevel = 90;
            sChildConn.IsAepConnectedKnown = true;
            sChildConn.IsAepConnected = false; // Child HFP node reports False on Win11!
            btSnaps.Add(sChildConn);

            PnpDeviceSnapshot sChildDisconn = new PnpDeviceSnapshot();
            sChildDisconn.InstanceId = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}\A&1&0&240130B59798_C0";
            sChildDisconn.FriendlyName = "Moondrop Golden Ages 2 Hands-Free AG";
            sChildDisconn.ContainerId = "{848538F8-3EA8-5469-9D58-401197640B1A}";
            sChildDisconn.BluetoothAddress = "240130B59798";
            sChildDisconn.HasBatteryLevel = true;
            sChildDisconn.BatteryLevel = 90;
            sChildDisconn.IsAepConnectedKnown = true;
            sChildDisconn.IsAepConnected = false;
            btSnaps.Add(sChildDisconn);

            HashSet<string> aepCids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "{6DEB6566-F571-57F9-A51E-86BE173F16C7}" };
            HashSet<string> aepMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "80C3BA9C88BE" };
            Dictionary<string, string> cNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> mNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "80C3BA9C88BE", "MOMENTUM 4" } };
            List<DeviceBatteryInfo> btOut = BluetoothChannelProvider.EvaluatePnpBatteryNodes(
                btSnaps,
                new Dictionary<string, BluetoothRadioDeviceState>(),
                cNames, mNames,
                new Dictionary<string, uint>(), new Dictionary<string, uint>(), new Dictionary<string, string>(),
                aepCids, aepMacs, new HashSet<string> { "80C3BA9C88BE" }, null);
            if (btOut.Count != 1 || btOut[0].Name != "MOMENTUM 4" || btOut[0].BatteryPercent != 90 || btOut[0].Category != DeviceCategory.Headset)
            {
                Console.WriteLine("FAIL: BT container aggregation test"); failures++;
            }

            // Fullscreen polling throttling tests
            if (DeviceMonitorService.ComputeEffectivePollInterval(15, true) != 120 ||
                DeviceMonitorService.ComputeEffectivePollInterval(30, true) != 120 ||
                DeviceMonitorService.ComputeEffectivePollInterval(60, true) != 120 ||
                DeviceMonitorService.ComputeEffectivePollInterval(30, false) != 30)
            {
                Console.WriteLine("FAIL: Fullscreen 120s throttling logic"); failures++;
            }
            bool fsProbe = User32Native.IsForegroundAppFullscreen();
            Console.WriteLine("Fullscreen detector probe (current foreground): " + fsProbe);

            // Minimalist theme color verification (Dark & Light)
            Color minDarkFull = RingIconRenderer.GetBatteryColor(80, false, 1);
            Color minLightFull = RingIconRenderer.GetBatteryColor(80, true, 1);
            if (minDarkFull.R < 200 || minLightFull.R > 60)
            {
                Console.WriteLine("FAIL: Minimalist theme color contrast"); failures++;
            }

            // ColorWheel HSV <-> RGB round-trip and CustomColorPalette tests
            Color[] hsvTargets = new Color[]
            {
                Color.FromArgb(255, 56, 189, 248),
                Color.FromArgb(255, 241, 91, 181),
                Color.FromArgb(255, 0, 245, 212)
            };
            foreach (Color tCol in hsvTargets)
            {
                double hh, ss, vv;
                ColorWheelControl.RgbToHsv(tCol, out hh, out ss, out vv);
                Color rt = ColorWheelControl.HsvToRgb(hh, ss, vv);
                if (Math.Abs(rt.R - tCol.R) > 1 || Math.Abs(rt.G - tCol.G) > 1 || Math.Abs(rt.B - tCol.B) > 1)
                {
                    Console.WriteLine("FAIL: HSV round-trip"); failures++;
                }
            }
            CustomColorPalette custom = new CustomColorPalette();
            custom.LowColorHex = "#FF00AA";
            custom.MidColorHex = "#0088FF";
            custom.HighColorHex = "#00FFAA";
            custom.ChargingColorHex = "#00BBF9";
            Color cLow = RingIconRenderer.GetBatteryColor(0, false, 2, custom);
            Color cMid = RingIconRenderer.GetBatteryColor(50, false, 2, custom);
            Color cHigh = RingIconRenderer.GetBatteryColor(100, false, 2, custom);
            if (cLow.R != 255 || cLow.B != 170 || cMid.G != 136 || cHigh.G != 255)
            {
                Console.WriteLine("FAIL: CustomColorPalette endpoints"); failures++;
            }

            byte[] mchoseResp12 = new byte[] { 0x11, 0x06, 0x37, 0x38, 0x11, 0x40, 0x1E, 0x05, 0x03, 0x00, 0x09, 0x4E, 0x00, 0x4E };
            if (!MchoseRealtekHandler.TryParseMchose06Response(mchoseResp12, false, out pct, out chg) || pct != 78 || chg)
            {
                Console.WriteLine("FAIL: MCHOSE 0x12 parser"); failures++;
            }
            byte[] mchoseResp11 = new byte[] { 0x11, 0xF9, 0xC8, 0xC7, 0xEE, 0xBF, 0xE1, 0xFA, 0xFC, 0xFF, 0xF6, 0xB1, 0xFF, 0xB1 };
            if (!MchoseRealtekHandler.TryParseMchose06Response(mchoseResp11, true, out pct, out chg) || pct != 78 || chg)
            {
                Console.WriteLine("FAIL: MCHOSE 0x11 XOR parser"); failures++;
            }

            // 4. Deduplication priority & charging merge test
            List<DeviceBatteryInfo> raw = new List<DeviceBatteryInfo>();
            DeviceBatteryInfo d1 = new DeviceBatteryInfo();
            d1.Id = "A"; d1.ContainerId = "{11111111-1111-1111-1111-111111111111}"; d1.Name = "Mouse"; d1.BatteryPercent = 70; d1.Priority = 50;
            DeviceBatteryInfo d2 = new DeviceBatteryInfo();
            d2.Id = "B"; d2.ContainerId = "{11111111-1111-1111-1111-111111111111}"; d2.Name = "Mouse"; d2.BatteryPercent = 73; d2.IsCharging = true; d2.Priority = 95;
            raw.Add(d1); raw.Add(d2);
            List<DeviceBatteryInfo> deduped = DeviceMonitorService.Deduplicate(raw);
            if (deduped.Count != 1 || deduped[0].BatteryPercent != 73 || !deduped[0].IsCharging)
            {
                Console.WriteLine("FAIL: Deduplication"); failures++;
            }

            // 5. Tooltip length limit (WinForms NotifyIcon requires <= 63 chars)
            DeviceBatteryInfo longDev = new DeviceBatteryInfo();
            longDev.Name = "Extremely Long Wireless Mechanical Keyboard Name 2.4GHz Edition Pro Max";
            longDev.BatteryPercent = 88;
            longDev.Channel = ConnectionChannel.Wireless24G;
            if (TrayApplicationContext.BuildTooltipText(longDev).Length > 63)
            {
                Console.WriteLine("FAIL: Tooltip > 63 chars"); failures++;
            }

            // 6. 500-iteration GDI icon render/destroy leak test + embedded glyph resource checks
            DeviceGlyphType[] requiredEmbedded = new DeviceGlyphType[]
            {
                DeviceGlyphType.GamepadXbox,
                DeviceGlyphType.GamepadPlayStation,
                DeviceGlyphType.GamepadNintendo,
                DeviceGlyphType.BluetoothGeneric,
                DeviceGlyphType.Keyboard,
                DeviceGlyphType.Mouse,
                DeviceGlyphType.Headset,
                DeviceGlyphType.GamepadGeneric
            };
            foreach (DeviceGlyphType reqGlyph in requiredEmbedded)
            {
                if (!RingIconRenderer.HasEmbeddedGlyph(reqGlyph))
                {
                    Console.WriteLine("FAIL: Missing embedded glyph PNG resource for " + reqGlyph); failures++;
                }
            }

            DeviceGlyphType[] leakGlyphs = requiredEmbedded;
            IntPtr proc = User32Native.GetCurrentProcess();
            uint gdiBefore = User32Native.GetGuiResources(proc, 0);
            DeviceBatteryInfo sample = new DeviceBatteryInfo();
            sample.Name = "Test Mouse"; sample.Category = DeviceCategory.Mouse; sample.BatteryPercent = 64; sample.IsCharging = true;
            for (int i = 0; i < 500; i++)
            {
                sample.GlyphType = leakGlyphs[i % leakGlyphs.Length];
                using (RenderedIcon icon = RingIconRenderer.RenderTrayIcon(sample, i * 0.2, (i % 5 == 0), 32))
                {
                    if (icon.HIcon == IntPtr.Zero) { Console.WriteLine("FAIL: Null HIcon"); failures++; break; }
                }
            }
            uint gdiAfter = User32Native.GetGuiResources(proc, 0);
            Console.WriteLine(string.Format("GDI Objects: before={0}, after={1}, delta={2}", gdiBefore, gdiAfter, (int)gdiAfter - (int)gdiBefore));
            if ((int)gdiAfter - (int)gdiBefore > 20)
            {
                Console.WriteLine("FAIL: Possible GDI leak"); failures++;
            }

            // 7. Render Dark & Light taskbar icon comparison sheets + Color Wheel preview
            string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview_icons");
            Directory.CreateDirectory(outDir);
            RenderIconSheet(Path.Combine(outDir, "icon_sheet_dark.png"), false);
            RenderIconSheet(Path.Combine(outDir, "icon_sheet_light.png"), true);
            using (Bitmap wheelBmp = ColorWheelControl.RenderWheelBitmap(200, 1.0))
            using (Bitmap cwPreview = new Bitmap(360, 220))
            using (Graphics g = Graphics.FromImage(cwPreview))
            {
                g.Clear(Color.FromArgb(24, 24, 27));
                g.DrawImageUnscaled(wheelBmp, 12, 10);
                DeviceBatteryInfo dWheel = new DeviceBatteryInfo();
                dWheel.GlyphType = DeviceGlyphType.Mouse;
                dWheel.BatteryPercent = 85;
                using (Bitmap r1 = RingIconRenderer.RenderDeviceBitmap(dWheel, 0, false, 48, false, 2, custom))
                {
                    g.DrawImage(r1, 244, 42);
                }
                dWheel.IsCharging = true;
                using (Bitmap r2 = RingIconRenderer.RenderDeviceBitmap(dWheel, Math.PI, false, 48, false, 2, custom))
                {
                    g.DrawImage(r2, 244, 118);
                }
                cwPreview.Save(Path.Combine(outDir, "color_wheel_preview.png"), ImageFormat.Png);
            }
            Console.WriteLine("Saved preview icon sheets to: " + outDir);

            Console.WriteLine(failures == 0 ? "ALL SELFTESTS PASSED" : ("FAILURES: " + failures));
            return failures == 0 ? 0 : 1;
        }

        private static void RenderIconSheet(string filePath, bool isLightTaskbar)
        {
            DeviceGlyphType[] glyphs = new DeviceGlyphType[]
            {
                DeviceGlyphType.Headset,
                DeviceGlyphType.Mouse,
                DeviceGlyphType.Keyboard,
                DeviceGlyphType.Microphone,
                DeviceGlyphType.GamepadGeneric,
                DeviceGlyphType.GamepadXbox,
                DeviceGlyphType.GamepadPlayStation,
                DeviceGlyphType.GamepadNintendo,
                DeviceGlyphType.BluetoothGeneric
            };

            // Columns: 100%, 75%, 50%, 30%, 15%, Charging(breath), Asleep
            int[] pcts = new int[] { 100, 75, 50, 30, 15, 65, 50 };
            bool[] chgs = new bool[] { false, false, false, false, false, true, false };
            bool[] asleeps = new bool[] { false, false, false, false, false, false, true };

            int colW = 68;
            int rowH = 74;
            int width = 150 + glyphs.Length * 0 + pcts.Length * colW + 20;
            int height = 50 + glyphs.Length * rowH + 20;

            Color bg = isLightTaskbar ? Color.FromArgb(240, 242, 245) : Color.FromArgb(28, 28, 30);
            Color fg = isLightTaskbar ? Color.FromArgb(30, 32, 36) : Color.FromArgb(228, 228, 231);

            using (Bitmap sheet = new Bitmap(width, height))
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font labelFont = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (Brush fgBrush = new SolidBrush(fg))
            {
                g.Clear(bg);
                string[] headers = new string[] { "100%", "75%", "50%", "30%", "15%", "Charging", "Asleep" };
                for (int c = 0; c < headers.Length; c++)
                {
                    g.DrawString(headers[c], labelFont, fgBrush, 150 + c * colW + 6, 16);
                }

                for (int r = 0; r < glyphs.Length; r++)
                {
                    int y = 46 + r * rowH;
                    g.DrawString(glyphs[r].ToString(), labelFont, fgBrush, 14, y + 18);

                    for (int c = 0; c < pcts.Length; c++)
                    {
                        DeviceBatteryInfo d = new DeviceBatteryInfo();
                        d.GlyphType = glyphs[r];
                        d.BatteryPercent = pcts[c];
                        d.IsCharging = chgs[c];
                        d.IsAsleep = asleeps[c];

                        int x = 150 + c * colW;
                        using (Bitmap big = RingIconRenderer.RenderDeviceBitmap(d, Math.PI * 0.85, false, 44, isLightTaskbar, 1))
                        {
                            g.DrawImage(big, x + 6, y);
                        }
                        using (Bitmap small = RingIconRenderer.RenderDeviceBitmap(d, Math.PI * 0.85, false, 20, isLightTaskbar, 0))
                        {
                            g.DrawImage(small, x + 18, y + 48);
                        }
                    }
                }
                sheet.Save(filePath, ImageFormat.Png);
            }
        }
    }
}
