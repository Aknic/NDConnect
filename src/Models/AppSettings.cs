using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NDConnect.Models
{
    [DataContract]
    public class CustomColorPalette
    {
        [DataMember]
        public string HighColorHex { get; set; }

        [DataMember]
        public string MidColorHex { get; set; }

        [DataMember]
        public string LowColorHex { get; set; }

        [DataMember]
        public string ChargingColorHex { get; set; }

        [DataMember]
        public string GlyphColorHex { get; set; }

        [DataMember]
        public bool UseAutoGlyphColor { get; set; }

        public CustomColorPalette()
        {
            ResetDefaults();
        }

        public void ResetDefaults()
        {
            HighColorHex = "#22C55E";
            MidColorHex = "#FFCC00";
            LowColorHex = "#FF3B30";
            ChargingColorHex = "#4AF58A";
            GlyphColorHex = "#F4F5F8";
            UseAutoGlyphColor = true;
        }

        public CustomColorPalette Clone()
        {
            CustomColorPalette c = new CustomColorPalette();
            c.HighColorHex = HighColorHex;
            c.MidColorHex = MidColorHex;
            c.LowColorHex = LowColorHex;
            c.ChargingColorHex = ChargingColorHex;
            c.GlyphColorHex = GlyphColorHex;
            c.UseAutoGlyphColor = UseAutoGlyphColor;
            return c;
        }

        public static Color ParseHex(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string s = hex.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 6)
            {
                int rgb;
                if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                {
                    return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                }
            }
            return fallback;
        }

        public static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }
    }

    [DataContract]
    public class CachedBatteryEntry
    {
        [DataMember]
        public string DeviceId { get; set; }
        [DataMember]
        public int Percent { get; set; }
    }

    [DataContract]
    public class AppSettings
    {
        private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "NDConnect";
        private const string LegacyRunValueName = "EasConnect";

        [DataMember]
        public int PollIntervalSeconds { get; set; }

        [DataMember]
        public bool ShowPercentageText { get; set; }

        [DataMember]
        public int IconThemeMode { get; set; } // 0=Auto, 1=Dark taskbar, 2=Light taskbar

        [DataMember]
        public int RingColorMode { get; set; } // 0=Red->Green gradient, 1=Minimalist theme color, 2=Custom Color Wheel

        [DataMember]
        public CustomColorPalette CustomPalette { get; set; }

        [DataMember]
        public List<string> HiddenDeviceIds { get; set; }

        [DataMember]
        public List<CachedBatteryEntry> LastKnownBatteries { get; set; }

        [IgnoreDataMember]
        public bool StartWithWindows
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegKey, false))
                    {
                        if (key == null) return false;
                        object val = key.GetValue(RunValueName);
                        return val != null && !string.IsNullOrEmpty(val.ToString());
                    }
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegKey, true))
                    {
                        if (key == null) return;
                        if (value)
                        {
                            string exePath = Application.ExecutablePath;
                            key.SetValue(RunValueName, "\"" + exePath + "\"");
                            key.DeleteValue(LegacyRunValueName, false);
                        }
                        else
                        {
                            key.DeleteValue(RunValueName, false);
                            key.DeleteValue(LegacyRunValueName, false);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        public AppSettings()
        {
            PollIntervalSeconds = 30;
            ShowPercentageText = false;
            IconThemeMode = 0;
            RingColorMode = 0;
            CustomPalette = new CustomColorPalette();
            HiddenDeviceIds = new List<string>();
            LastKnownBatteries = new List<CachedBatteryEntry>();
        }

        public int GetCachedBattery(string deviceId)
        {
            if (LastKnownBatteries == null || string.IsNullOrEmpty(deviceId)) return -1;
            foreach (CachedBatteryEntry e in LastKnownBatteries)
            {
                if (string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) return e.Percent;
            }
            return -1;
        }

        public bool SetCachedBattery(string deviceId, int percent)
        {
            if (string.IsNullOrEmpty(deviceId) || percent < 0 || percent > 100) return false;
            if (LastKnownBatteries == null) LastKnownBatteries = new List<CachedBatteryEntry>();
            foreach (CachedBatteryEntry e in LastKnownBatteries)
            {
                if (string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Percent == percent) return false;
                    e.Percent = percent;
                    return true;
                }
            }
            CachedBatteryEntry added = new CachedBatteryEntry();
            added.DeviceId = deviceId;
            added.Percent = percent;
            LastKnownBatteries.Add(added);
            return true;
        }

        private static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "NDConnect", "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                string path = GetSettingsPath();
                if (!File.Exists(path))
                {
                    string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasConnect", "settings.json");
                    if (File.Exists(legacy)) path = legacy;
                }
                if (File.Exists(path))
                {
                    using (FileStream fs = File.OpenRead(path))
                    {
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppSettings));
                        AppSettings loaded = ser.ReadObject(fs) as AppSettings;
                        if (loaded != null)
                        {
                            if (loaded.PollIntervalSeconds < 5) loaded.PollIntervalSeconds = 30;
                            if (loaded.CustomPalette == null) loaded.CustomPalette = new CustomColorPalette();
                            if (loaded.HiddenDeviceIds == null) loaded.HiddenDeviceIds = new List<string>();
                            if (loaded.LastKnownBatteries == null) loaded.LastKnownBatteries = new List<CachedBatteryEntry>();
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (FileStream fs = File.Create(path))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AppSettings));
                    ser.WriteObject(fs, this);
                }
            }
            catch
            {
            }
        }
    }
}
