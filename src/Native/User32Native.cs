using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace NDConnect.Native
{
    public static class User32Native
    {
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int DBT_DEVNODES_CHANGED = 0x0007;
        public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        public static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        public static readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public short dbcc_name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr NotificationFilter, uint Flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterDeviceNotification(IntPtr Handle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static int GetTrayIconPixelSize(int fallbackSize)
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi > 0)
                {
                    int sm = GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON
                    if (sm >= 16) return Math.Max(16, Math.Min(64, sm));
                }
            }
            catch
            {
            }
            return Math.Max(16, Math.Min(64, fallbackSize));
        }

        public static IntPtr CreateTransparentIcon(Bitmap bmp)
        {
            if (bmp == null) return IntPtr.Zero;
            IntPtr hColor = IntPtr.Zero;
            IntPtr hMask = IntPtr.Zero;
            try
            {
                hColor = bmp.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                hMask = CreateBitmap(bmp.Width, bmp.Height, 1, 1, IntPtr.Zero);
                if (hColor == IntPtr.Zero || hMask == IntPtr.Zero) return IntPtr.Zero;

                ICONINFO info = new ICONINFO();
                info.fIcon = true;
                info.xHotspot = 0;
                info.yHotspot = 0;
                info.hbmMask = hMask;
                info.hbmColor = hColor;
                return CreateIconIndirect(ref info);
            }
            finally
            {
                if (hColor != IntPtr.Zero) DeleteObject(hColor);
                if (hMask != IntPtr.Zero) DeleteObject(hMask);
            }
        }

        public static bool IsForegroundAppFullscreen()
        {
            try
            {
                int quns;
                if (SHQueryUserNotificationState(out quns) == 0)
                {
                    // QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4)
                    // Do not treat QUNS_BUSY (2) as fullscreen because Focus Assist / DND sets it on desktop.
                    if (quns == 3 || quns == 4) return true;
                }

                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)Process.GetCurrentProcess().Id) return false;

                StringBuilder cls = new StringBuilder(64);
                if (GetClassNameW(hwnd, cls, cls.Capacity) > 0)
                {
                    string c = cls.ToString();
                    if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" ||
                        c == "Shell_SecondaryTrayWnd" || c == "Windows.UI.Core.CoreWindow")
                    {
                        return false;
                    }
                }

                RECT winRect;
                if (!GetWindowRect(hwnd, out winRect)) return false;
                IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMon == IntPtr.Zero) return false;

                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfoW(hMon, ref mi)) return false;

                return winRect.Left <= mi.rcMonitor.Left &&
                       winRect.Top <= mi.rcMonitor.Top &&
                       winRect.Right >= mi.rcMonitor.Right &&
                       winRect.Bottom >= mi.rcMonitor.Bottom;
            }
            catch
            {
                return false;
            }
        }

        public static void EnablePerMonitorDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        public static IntPtr RegisterInterfaceNotification(IntPtr hwnd, Guid classGuid)
        {
            DEV_BROADCAST_DEVICEINTERFACE dbi = new DEV_BROADCAST_DEVICEINTERFACE();
            dbi.dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE));
            dbi.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
            dbi.dbcc_reserved = 0;
            dbi.dbcc_classguid = classGuid;

            IntPtr mem = Marshal.AllocHGlobal(dbi.dbcc_size);
            try
            {
                Marshal.StructureToPtr(dbi, mem, false);
                return RegisterDeviceNotification(hwnd, mem, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        public static bool IsSystemLightTaskbar()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("SystemUsesLightTheme");
                        if (v is int) return ((int)v) != 0;
                        if (v != null)
                        {
                            int parsed;
                            if (int.TryParse(v.ToString(), out parsed)) return parsed != 0;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
