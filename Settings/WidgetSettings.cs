using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Media.Control;
using Microsoft.Win32;

internal sealed class MetricPanel
{
    public MetricPanel(string[] textLines, Color[] colors, List<double>[] histories, double graphMax, bool autoScale)
    {
        this.TextLines = textLines;
        this.Colors = colors;
        this.Histories = histories;
        this.GraphMax = graphMax;
        this.AutoScale = autoScale;
    }

    public string[] TextLines { get; private set; }
    public Color[] Colors { get; private set; }
    public List<double>[] Histories { get; private set; }
    public double GraphMax { get; private set; }
    public bool AutoScale { get; private set; }
    public bool UseCompactValueFont { get; set; }
    public bool UseHardwareStackText { get; set; }
    public bool IsNetworkDisconnected { get; set; }
    public double[] CoreValues { get; set; }
    public double AlertPercent { get; set; }
    public bool AlertIconVisible { get; set; }
}

internal enum WidgetVisibilityMode
{
    DesktopOnly,
    AlwaysVisible,
    HideWhenFullscreen
}

internal enum ThermalTestMode
{
    Off,
    Simulate75,
    Simulate100
}

internal sealed class WidgetSettings
{
    public const string MetricCpu = "CPU";
    public const string MetricMemory = "Memory";
    public const string MetricDisk = "Disk";
    public const string MetricNetwork = "Network";
    public const string MetricGpu = "GPU";
    public const string MetricNpu = "NPU";
    public const int MinWidth = 260;
    public const int MaxWidth = 1800;
    public const int MinHeight = 86;
    public const int MaxHeight = 700;
    public const int MinClockWidth = 130;
    public const int MaxClockWidth = 900;
    public const int MinClockHeight = 44;
    public const int MaxClockHeight = 240;
    public const int MinDockIconSize = 24;
    public const int MaxDockIconSize = 288;
    public const int MinDockMagnificationPercent = 100;
    public const int MaxDockMagnificationPercent = 145;
    public const int MinDockBottomMargin = 0;
    public const int MaxDockBottomMargin = 240;
    public const int MinBackgroundTransparency = 0;
    public const int MaxBackgroundTransparency = 90;
    public const int DefaultBackgroundTransparency = 9;
    public static readonly string[] DefaultMetricOrder = new string[]
    {
        MetricCpu,
        MetricMemory,
        MetricDisk,
        MetricNetwork,
        MetricGpu,
        MetricNpu
    };

    public int Width { get; set; }
    public int Height { get; set; }
    public int LeftX { get; set; }
    public int BottomY { get; set; }
    public int BackgroundTransparencyPercent { get; set; }
    public int ApplicationTransparencyPercent { get; set; }
    public int ClockWidth { get; set; }
    public int ClockHeight { get; set; }
    public int ClockLeftX { get; set; }
    public int ClockBottomY { get; set; }
    public int ClockTransparencyPercent { get; set; }
    public bool ClockUse24Hour { get; set; }
    public bool ClockCalendarEnabled { get; set; }
    public bool ClockPowerEnabled { get; set; }
    public bool DockEnabled { get; set; }
    public int DockIconSize { get; set; }
    public int DockMagnificationPercent { get; set; }
    public int DockBottomMargin { get; set; }
    public string DockItemsText { get; set; }
    public string LaunchpadItemsText { get; set; }
    public WidgetVisibilityMode VisibilityMode { get; set; }
    public bool StartupEnabled { get; set; }
    public bool ShowCpu { get; set; }
    public bool ShowMemory { get; set; }
    public bool ShowDisk { get; set; }
    public bool ShowNetwork { get; set; }
    public bool ShowGpu { get; set; }
    public bool ShowNpu { get; set; }
    public bool AlertTestEnabled { get; set; }
    public ThermalTestMode ThermalTestMode { get; set; }
    public bool PowerSavingEnabled { get; set; }
    public bool HoverOpacityEnabled { get; set; }
    public string[] MetricOrder { get; set; }

    public static string SettingsPath
    {
        get { return Path.Combine(Logger.DirectoryPath, "settings.ini"); }
    }

    public WidgetSettings()
    {
        WidgetSettings defaults = CreateDefaults();
        this.Width = defaults.Width;
        this.Height = defaults.Height;
        this.LeftX = defaults.LeftX;
        this.BottomY = defaults.BottomY;
        this.BackgroundTransparencyPercent = DefaultBackgroundTransparency;
        this.ApplicationTransparencyPercent = 0;
        this.ClockWidth = defaults.ClockWidth;
        this.ClockHeight = defaults.ClockHeight;
        this.ClockLeftX = defaults.ClockLeftX;
        this.ClockBottomY = defaults.ClockBottomY;
        this.ClockTransparencyPercent = DefaultBackgroundTransparency;
        this.ClockUse24Hour = true;
        this.ClockCalendarEnabled = false;
        this.ClockPowerEnabled = false;
        this.DockEnabled = true;
        this.DockIconSize = defaults.DockIconSize;
        this.DockMagnificationPercent = defaults.DockMagnificationPercent;
        this.DockBottomMargin = defaults.DockBottomMargin;
        this.DockItemsText = defaults.DockItemsText;
        this.LaunchpadItemsText = defaults.LaunchpadItemsText;
        this.VisibilityMode = WidgetVisibilityMode.DesktopOnly;
        this.StartupEnabled = Program.IsStartupEnabled();
        this.ShowCpu = true;
        this.ShowMemory = true;
        this.ShowDisk = true;
        this.ShowNetwork = true;
        this.ShowGpu = true;
        this.ShowNpu = true;
        this.AlertTestEnabled = false;
        this.ThermalTestMode = ThermalTestMode.Off;
        this.PowerSavingEnabled = true;
        this.HoverOpacityEnabled = false;
        this.MetricOrder = CloneMetricOrder(DefaultMetricOrder);
    }

    private WidgetSettings(bool skipDefaults)
    {
    }

    public static WidgetSettings CreateDefaults()
    {
        WidgetSettings settings = new WidgetSettings(true);
        float scale = GetPrimaryScale();
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int margin = (int)Math.Round(16.0f * scale);

        settings.Width = Clamp((int)Math.Round(392.0f * scale), MinWidth, MaxWidth);
        settings.Height = Clamp((int)Math.Round(116.0f * scale), MinHeight, MaxHeight);
        settings.LeftX = workArea.Right - settings.Width - margin;
        settings.BottomY = workArea.Bottom - margin - 1;
        settings.BackgroundTransparencyPercent = DefaultBackgroundTransparency;
        settings.ApplicationTransparencyPercent = 0;
        settings.ClockWidth = Clamp((int)Math.Round(192.0f * scale), MinClockWidth, MaxClockWidth);
        settings.ClockHeight = Clamp((int)Math.Round(58.0f * scale), MinClockHeight, MaxClockHeight);
        settings.ClockLeftX = workArea.Right - settings.ClockWidth - margin;
        settings.ClockBottomY = settings.BottomY - settings.Height - margin;
        settings.ClockTransparencyPercent = DefaultBackgroundTransparency;
        settings.ClockUse24Hour = true;
        settings.ClockCalendarEnabled = false;
        settings.ClockPowerEnabled = false;
        settings.DockEnabled = true;
        settings.DockIconSize = 96;
        settings.DockMagnificationPercent = 120;
        settings.DockBottomMargin = Clamp((int)Math.Round(6.0f * Math.Min(scale, 1.15f)), MinDockBottomMargin, MaxDockBottomMargin);
        settings.DockItemsText = GetDefaultDockItemsText();
        settings.LaunchpadItemsText = string.Empty;
        settings.VisibilityMode = WidgetVisibilityMode.DesktopOnly;
        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.ShowCpu = true;
        settings.ShowMemory = true;
        settings.ShowDisk = true;
        settings.ShowNetwork = true;
        settings.ShowGpu = true;
        settings.ShowNpu = true;
        settings.AlertTestEnabled = false;
        settings.ThermalTestMode = ThermalTestMode.Off;
        settings.PowerSavingEnabled = true;
        settings.HoverOpacityEnabled = false;
        settings.MetricOrder = CloneMetricOrder(DefaultMetricOrder);
        settings.Normalize();
        return settings;
    }

    public WidgetSettings Clone()
    {
        return new WidgetSettings(true)
        {
            Width = this.Width,
            Height = this.Height,
            LeftX = this.LeftX,
            BottomY = this.BottomY,
            BackgroundTransparencyPercent = this.BackgroundTransparencyPercent,
            ApplicationTransparencyPercent = this.ApplicationTransparencyPercent,
            ClockWidth = this.ClockWidth,
            ClockHeight = this.ClockHeight,
            ClockLeftX = this.ClockLeftX,
            ClockBottomY = this.ClockBottomY,
            ClockTransparencyPercent = this.ClockTransparencyPercent,
            ClockUse24Hour = this.ClockUse24Hour,
            ClockCalendarEnabled = this.ClockCalendarEnabled,
            ClockPowerEnabled = this.ClockPowerEnabled,
            DockEnabled = this.DockEnabled,
            DockIconSize = this.DockIconSize,
            DockMagnificationPercent = this.DockMagnificationPercent,
            DockBottomMargin = this.DockBottomMargin,
            DockItemsText = this.DockItemsText,
            LaunchpadItemsText = this.LaunchpadItemsText,
            VisibilityMode = this.VisibilityMode,
            StartupEnabled = this.StartupEnabled,
            ShowCpu = this.ShowCpu,
            ShowMemory = this.ShowMemory,
            ShowDisk = this.ShowDisk,
            ShowNetwork = this.ShowNetwork,
            ShowGpu = this.ShowGpu,
            ShowNpu = this.ShowNpu,
            AlertTestEnabled = this.AlertTestEnabled,
            ThermalTestMode = this.ThermalTestMode,
            PowerSavingEnabled = this.PowerSavingEnabled,
            HoverOpacityEnabled = this.HoverOpacityEnabled,
            MetricOrder = CloneMetricOrder(this.MetricOrder)
        };
    }

    public void Normalize()
    {
        this.Width = Clamp(this.Width, MinWidth, MaxWidth);
        this.Height = Clamp(this.Height, MinHeight, MaxHeight);
        this.BackgroundTransparencyPercent = Clamp(this.BackgroundTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ApplicationTransparencyPercent = Clamp(this.ApplicationTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ClockWidth = Clamp(this.ClockWidth, MinClockWidth, MaxClockWidth);
        this.ClockHeight = Clamp(this.ClockHeight, MinClockHeight, MaxClockHeight);
        this.ClockTransparencyPercent = Clamp(this.ClockTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.DockIconSize = Clamp(this.DockIconSize, MinDockIconSize, MaxDockIconSize);
        this.DockMagnificationPercent = Clamp(this.DockMagnificationPercent, MinDockMagnificationPercent, MaxDockMagnificationPercent);
        this.DockBottomMargin = Clamp(this.DockBottomMargin, MinDockBottomMargin, MaxDockBottomMargin);
        if (this.DockItemsText == null)
        {
            this.DockItemsText = GetDefaultDockItemsText();
        }

        if (this.LaunchpadItemsText == null)
        {
            this.LaunchpadItemsText = string.Empty;
        }

        this.MetricOrder = NormalizeMetricOrder(this.MetricOrder);
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        this.LeftX = Clamp(this.LeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.Width));
        this.BottomY = Clamp(this.BottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.Height - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
        this.ClockLeftX = Clamp(this.ClockLeftX, bounds.Left, Math.Max(bounds.Left, bounds.Right - this.ClockWidth));
        this.ClockBottomY = Clamp(this.ClockBottomY, Math.Min(bounds.Bottom - 1, bounds.Top + this.ClockHeight - 1), Math.Max(bounds.Top, bounds.Bottom - 1));
    }

    public static WidgetSettings Load()
    {
        WidgetSettings settings = new WidgetSettings();
        bool hasPixelPosition = false;

        try
        {
            if (File.Exists(SettingsPath))
            {
                string[] lines = File.ReadAllLines(SettingsPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    if (string.Equals(key, "LeftX", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "BottomY", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPixelPosition = true;
                    }

                    ApplyValue(settings, key, value);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (!hasPixelPosition)
        {
            WidgetSettings defaults = CreateDefaults();
            settings.Width = defaults.Width;
            settings.Height = defaults.Height;
            settings.LeftX = defaults.LeftX;
            settings.BottomY = defaults.BottomY;
        }

        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        this.Normalize();
        Directory.CreateDirectory(Logger.DirectoryPath);
        string[] lines = new string[]
        {
            "Version=3",
            "Width=" + this.Width,
            "Height=" + this.Height,
            "LeftX=" + this.LeftX,
            "BottomY=" + this.BottomY,
            "BackgroundTransparencyPercent=" + this.BackgroundTransparencyPercent,
            "ContentTransparencyPercent=" + this.ApplicationTransparencyPercent,
            "ApplicationTransparencyPercent=" + this.ApplicationTransparencyPercent,
            "ClockWidth=" + this.ClockWidth,
            "ClockHeight=" + this.ClockHeight,
            "ClockLeftX=" + this.ClockLeftX,
            "ClockBottomY=" + this.ClockBottomY,
            "ClockUse24Hour=" + this.ClockUse24Hour,
            "ClockCalendarEnabled=" + this.ClockCalendarEnabled,
            "ClockPowerEnabled=" + this.ClockPowerEnabled,
            "DockEnabled=" + this.DockEnabled,
            "DockIconSize=" + this.DockIconSize,
            "DockMagnificationPercent=" + this.DockMagnificationPercent,
            "DockBottomMargin=" + this.DockBottomMargin,
            "DockItemsText=" + EncodeSettingText(this.DockItemsText),
            "LaunchpadItemsText=" + EncodeSettingText(this.LaunchpadItemsText),
            "VisibilityMode=" + this.VisibilityMode,
            "StartupEnabled=" + this.StartupEnabled,
            "ShowCpu=" + this.ShowCpu,
            "ShowMemory=" + this.ShowMemory,
            "ShowDisk=" + this.ShowDisk,
            "ShowNetwork=" + this.ShowNetwork,
            "ShowGpu=" + this.ShowGpu,
            "ShowNpu=" + this.ShowNpu,
            "AlertTestEnabled=" + this.AlertTestEnabled,
            "ThermalTestMode=" + this.ThermalTestMode,
            "PowerSavingEnabled=" + this.PowerSavingEnabled,
            "HoverOpacityEnabled=" + this.HoverOpacityEnabled,
            "MetricOrder=" + string.Join(",", NormalizeMetricOrder(this.MetricOrder))
        };
        File.WriteAllLines(SettingsPath, lines);
    }

    private static void ApplyValue(WidgetSettings settings, string key, string value)
    {
        int intValue;
        bool boolValue;

        if (string.Equals(key, "Width", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.Width = intValue;
            return;
        }

        if (string.Equals(key, "Height", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.Height = intValue;
            return;
        }

        if (string.Equals(key, "LeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.LeftX = intValue;
            return;
        }

        if (string.Equals(key, "BottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.BottomY = intValue;
            return;
        }

        if ((string.Equals(key, "BackgroundTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "BackgroundTransparency", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.BackgroundTransparencyPercent = intValue;
            return;
        }

        if ((string.Equals(key, "ApplicationTransparencyPercent", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ContentTransparencyPercent", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(value, out intValue))
        {
            settings.ApplicationTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "ClockWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClockWidth = intValue;
            return;
        }

        if (string.Equals(key, "ClockHeight", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClockHeight = intValue;
            return;
        }

        if (string.Equals(key, "ClockLeftX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClockLeftX = intValue;
            return;
        }

        if (string.Equals(key, "ClockBottomY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClockBottomY = intValue;
            return;
        }

        if (string.Equals(key, "ClockTransparencyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.ClockTransparencyPercent = intValue;
            return;
        }

        if (string.Equals(key, "ClockUse24Hour", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ClockUse24Hour = boolValue;
            return;
        }

        if (string.Equals(key, "ClockCalendarEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ClockCalendarEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ClockPowerEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ClockPowerEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "DockEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.DockEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "DockIconSize", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.DockIconSize = intValue;
            return;
        }

        if (string.Equals(key, "DockMagnificationPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.DockMagnificationPercent = intValue;
            return;
        }

        if (string.Equals(key, "DockBottomMargin", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
        {
            settings.DockBottomMargin = intValue;
            return;
        }

        if (string.Equals(key, "DockItemsText", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "DockItems", StringComparison.OrdinalIgnoreCase))
        {
            settings.DockItemsText = DecodeSettingText(value);
            return;
        }

        if (string.Equals(key, "LaunchpadItemsText", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "LaunchpadItems", StringComparison.OrdinalIgnoreCase))
        {
            settings.LaunchpadItemsText = DecodeSettingText(value);
            return;
        }

        if (string.Equals(key, "StartupEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.StartupEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ShowCpu", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowCpu = boolValue;
            return;
        }

        if (string.Equals(key, "ShowMemory", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowMemory = boolValue;
            return;
        }

        if (string.Equals(key, "ShowDisk", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowDisk = boolValue;
            return;
        }

        if (string.Equals(key, "ShowNetwork", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowNetwork = boolValue;
            return;
        }

        if (string.Equals(key, "ShowGpu", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowGpu = boolValue;
            return;
        }

        if (string.Equals(key, "ShowNpu", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.ShowNpu = boolValue;
            return;
        }

        if (string.Equals(key, "AlertTestEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.AlertTestEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "ThermalTestMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.ThermalTestMode = (ThermalTestMode)Enum.Parse(typeof(ThermalTestMode), value, true);
            }
            catch
            {
                settings.ThermalTestMode = ThermalTestMode.Off;
            }

            return;
        }

        if (string.Equals(key, "PowerSavingEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerSavingEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "HoverOpacityEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.HoverOpacityEnabled = boolValue;
            return;
        }

        if (string.Equals(key, "MetricOrder", StringComparison.OrdinalIgnoreCase))
        {
            settings.MetricOrder = NormalizeMetricOrder(value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        if (string.Equals(key, "VisibilityMode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settings.VisibilityMode = (WidgetVisibilityMode)Enum.Parse(typeof(WidgetVisibilityMode), value, true);
            }
            catch
            {
            }
        }
    }

    public static string GetDefaultDockItemsText()
    {
        return
            "资源管理器|%WINDIR%\\explorer.exe\r\n" +
            "设置|ms-settings:\r\n" +
            "记事本|%WINDIR%\\System32\\notepad.exe\r\n" +
            "任务管理器|%WINDIR%\\System32\\Taskmgr.exe";
    }

    public string GetLaunchpadCommonItemsText()
    {
        return CombineItemTexts(this.LaunchpadItemsText, this.DockItemsText);
    }

    private static string CombineItemTexts(string first, string second)
    {
        first = first == null ? string.Empty : first.Trim();
        second = second == null ? string.Empty : second.Trim();
        if (first.Length == 0)
        {
            return second;
        }

        if (second.Length == 0)
        {
            return first;
        }

        return first + "\r\n" + second;
    }

    private static string EncodeSettingText(string value)
    {
        if (value == null)
        {
            value = string.Empty;
        }

        return "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string DecodeSettingText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value.Substring("base64:".Length));
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        return value.Replace("\\r\\n", "\r\n").Replace("\\n", "\r\n");
    }

    private static float GetPrimaryScale()
    {
        try
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return Math.Max(1.0f, g.DpiX / 96.0f);
            }
        }
        catch
        {
            return 1.0f;
        }
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    public static string MetricDisplayName(string metricId)
    {
        if (string.Equals(metricId, MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        if (string.Equals(metricId, MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return "Memory";
        }

        if (string.Equals(metricId, MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return "Disk";
        }

        if (string.Equals(metricId, MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return "Network";
        }

        if (string.Equals(metricId, MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        if (string.Equals(metricId, MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return "NPU";
        }

        return metricId;
    }

    private static string[] CloneMetricOrder(string[] order)
    {
        string[] normalized = NormalizeMetricOrder(order);
        string[] clone = new string[normalized.Length];
        Array.Copy(normalized, clone, normalized.Length);
        return clone;
    }

    private static string[] NormalizeMetricOrder(string[] order)
    {
        List<string> normalized = new List<string>();
        if (order != null)
        {
            for (int i = 0; i < order.Length; i++)
            {
                string canonical = CanonicalMetricId(order[i]);
                if (canonical.Length == 0 || ContainsMetric(normalized, canonical))
                {
                    continue;
                }

                normalized.Add(canonical);
            }
        }

        for (int i = 0; i < DefaultMetricOrder.Length; i++)
        {
            if (!ContainsMetric(normalized, DefaultMetricOrder[i]))
            {
                normalized.Add(DefaultMetricOrder[i]);
            }
        }

        return normalized.ToArray();
    }

    private static bool ContainsMetric(List<string> metrics, string metricId)
    {
        for (int i = 0; i < metrics.Count; i++)
        {
            if (string.Equals(metrics[i], metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CanonicalMetricId(string metricId)
    {
        if (string.IsNullOrEmpty(metricId))
        {
            return string.Empty;
        }

        string value = metricId.Trim();
        if (string.Equals(value, MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return MetricCpu;
        }

        if (string.Equals(value, MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return MetricMemory;
        }

        if (string.Equals(value, MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return MetricDisk;
        }

        if (string.Equals(value, MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return MetricNetwork;
        }

        if (string.Equals(value, MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return MetricGpu;
        }

        if (string.Equals(value, MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return MetricNpu;
        }

        return string.Empty;
    }
}
