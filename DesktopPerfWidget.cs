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

internal static class Program
{
    private const string MutexName = @"Local\DesktopPerfWidgetArm64";
    private const string StopEventName = @"Local\DesktopPerfWidgetArm64Stop";
    internal const string RunValueName = "DesktopPerfWidgetArm64";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool powerSavingModeKnown;
    private static bool powerSavingModeEnabled;

    [STAThread]
    private static int Main(string[] args)
    {
        bool useDesktopParent = HasArg(args, "--desktop-parent") || HasArg(args, "--workerw");
        LogInfo("Starting. Args=[" + string.Join(" ", args) + "], " + NativeMethods.DescribeProcessMachine());

        if (HasArg(args, "--stop"))
        {
            LogInfo("Stop requested.");
            SignalStop();
            return 0;
        }

        if (HasArg(args, "--install"))
        {
            LogInfo("Install requested.");
            InstallStartup(useDesktopParent);
            SignalStop();
            if (!HasArg(args, "--no-start"))
            {
                StartWidget(useDesktopParent);
            }
            return 0;
        }

        if (HasArg(args, "--uninstall"))
        {
            LogInfo("Uninstall requested.");
            RemoveStartup();
            SignalStop();
            return 0;
        }

        if (HasArg(args, "--test"))
        {
            return TestProbe();
        }

        bool createdNew;
        Mutex mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            LogInfo("Another instance is already running; exiting.");
            mutex.Dispose();
            return 0;
        }

        EventWaitHandle stopEvent = null;
        try
        {
            stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
            NativeMethods.TrySetDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            WidgetSettings settings = WidgetSettings.Load();
            SetPowerSavingEnabled(settings.PowerSavingEnabled);
            using (PdhSampler sampler = new PdhSampler())
            using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, useDesktopParent))
            {
                Application.Run(form);
            }

            LogInfo("Application loop exited.");
            return 0;
        }
        catch (Exception ex)
        {
            LogException(ex);
            try
            {
                MessageBox.Show(
                    "DesktopPerfWidget failed to start.\r\n\r\n" + ex.Message + "\r\n\r\nLog: " + Logger.LogPath,
                    "DesktopPerfWidget",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            return 1;
        }
        finally
        {
            if (stopEvent != null)
            {
                stopEvent.Dispose();
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            mutex.Dispose();
        }
    }

    private static bool HasArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static void InstallStartup(bool useDesktopParent)
    {
        string exePath = Application.ExecutablePath;
        string command = Quote(exePath);
        if (useDesktopParent)
        {
            command += " --desktop-parent";
        }

        using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (runKey == null)
            {
                throw new InvalidOperationException("Cannot open HKCU startup registry key.");
            }

            runKey.SetValue(RunValueName, command, RegistryValueKind.String);
            LogInfo("Startup registry value set: " + command);
        }
    }

    internal static void RemoveStartup()
    {
        using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
        {
            if (runKey != null)
            {
                runKey.DeleteValue(RunValueName, false);
                LogInfo("Startup registry value removed.");
            }
        }
    }

    private static void StartWidget(bool useDesktopParent)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = Application.ExecutablePath;
        startInfo.UseShellExecute = true;
        if (useDesktopParent)
        {
            startInfo.Arguments = "--desktop-parent";
        }

        Process.Start(startInfo);
        LogInfo("Started widget process.");
    }

    internal static void SetStartupEnabled(bool enabled, bool useDesktopParent)
    {
        if (enabled)
        {
            InstallStartup(useDesktopParent);
        }
        else
        {
            RemoveStartup();
        }
    }

    internal static void SetPowerSavingEnabled(bool enabled)
    {
        if (powerSavingModeKnown && powerSavingModeEnabled == enabled)
        {
            return;
        }

        bool throttlingSet = NativeMethods.TrySetProcessPowerThrottling(enabled);
        bool prioritySet = false;
        try
        {
            Process.GetCurrentProcess().PriorityClass = enabled ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
            prioritySet = true;
        }
        catch
        {
        }

        powerSavingModeKnown = true;
        powerSavingModeEnabled = enabled;
        LogInfo(string.Format(
            "Power saving mode {0}. PowerThrottling={1}, Priority={2}",
            enabled ? "enabled" : "disabled",
            throttlingSet,
            prioritySet));
    }

    internal static bool IsStartupEnabled()
    {
        try
        {
            using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (runKey == null)
                {
                    return false;
                }

                object value = runKey.GetValue(RunValueName);
                return value != null && value.ToString().Length > 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static void SignalStop()
    {
        try
        {
            using (EventWaitHandle stop = EventWaitHandle.OpenExisting(StopEventName))
            {
                stop.Set();
                LogInfo("Stop signal sent.");
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int TestProbe()
    {
        NativeMethods.AttachToParentConsole();
        try
        {
            using (PdhSampler sampler = new PdhSampler())
            {
                Thread.Sleep(1100);
                PerfSnapshot snapshot = sampler.Sample();
                string sampleText = string.Format(
                    "{0} {1:0}% {2} | Memory {3:0.0}/{4:0.0} GB ({5:0}%) | Disk {6:0}% | GPU {7:0}% {8:0.0}/{9:0.#} GB | NPU {10:0}% {11:0.0}/{12:0.#} GB | Network {13} UP {14} DL {15}",
                    snapshot.CpuName,
                    snapshot.CpuPercent,
                    FormatCpuFrequencyPair(snapshot.CpuFrequencyGhz, snapshot.CpuBaseFrequencyGhz),
                    snapshot.MemoryUsedGb,
                    snapshot.MemoryTotalGb,
                    snapshot.MemoryPercent,
                    snapshot.DiskPercent,
                    snapshot.GpuPercent,
                    snapshot.GpuMemoryUsedGb,
                    snapshot.GpuMemoryTotalGb,
                    snapshot.NpuPercent,
                    snapshot.NpuMemoryUsedGb,
                    snapshot.NpuMemoryTotalGb,
                    snapshot.NetworkConnected ? "connected" : "disconnected",
                    NetworkRateFormatter.Format(snapshot.NetworkSentBytesPerSecond),
                    NetworkRateFormatter.Format(snapshot.NetworkReceivedBytesPerSecond));
                Console.WriteLine(sampleText);
                LogInfo("Test sample: " + sampleText);
                Console.WriteLine("Process: {0}", NativeMethods.DescribeProcessMachine());
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            LogException(ex);
            return 1;
        }
    }

    private static string FormatCpuFrequencyPair(double currentGhz, double baseGhz)
    {
        if (currentGhz <= 0.0 && baseGhz <= 0.0)
        {
            return string.Empty;
        }

        if (baseGhz <= 0.0)
        {
            return string.Format("{0:0.00}GHz/--GHz", currentGhz);
        }

        return string.Format("{0:0.00}GHz/{1:0.00}GHz", currentGhz, baseGhz);
    }

    internal static void LogInfo(string message)
    {
        Logger.Info(message);
    }

    internal static void LogException(Exception ex)
    {
        Logger.Error(ex);
    }
}

internal static class DrawingUtil
{
    public static void DrawImageWithAlpha(Graphics target, Bitmap image, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            target.DrawImageUnscaled(image, 0, 0);
            return;
        }

        using (ImageAttributes attributes = new ImageAttributes())
        {
            ColorMatrix matrix = new ColorMatrix();
            matrix.Matrix33 = alpha / 255.0f;
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            target.DrawImage(
                image,
                new Rectangle(0, 0, image.Width, image.Height),
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
    }

    public static void DrawImageWithAlpha(Graphics target, Image image, RectangleF destination, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            target.DrawImage(image, destination);
            return;
        }

        using (ImageAttributes attributes = new ImageAttributes())
        {
            ColorMatrix matrix = new ColorMatrix();
            matrix.Matrix33 = alpha / 255.0f;
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            Rectangle rectangle = Rectangle.Round(destination);
            target.DrawImage(
                image,
                rectangle,
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
    }
}

internal static class NetworkRateFormatter
{
    public static string Format(double bytesPerSecond)
    {
        double kbps = Math.Max(0.0, bytesPerSecond) * 8.0 / 1000.0;
        string unit = "Kbps";
        double divisor = 1.0;

        if (kbps >= 1000000.0)
        {
            unit = "Gbps";
            divisor = 1000000.0;
        }
        else if (kbps >= 1000.0)
        {
            unit = "Mbps";
            divisor = 1000.0;
        }

        double value = kbps / divisor;
        double roundedOneDecimal = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (roundedOneDecimal >= 10.0)
        {
            return string.Format("{0:0} {1}", value, unit);
        }

        return string.Format("{0:0.0} {1}", value, unit);
    }
}

internal sealed class WidgetForm : Form
{
    private static readonly string[] HardwareVendorPrefixes = new string[]
    {
        "Western Digital",
        "Hewlett-Packard",
        "SK hynix",
        "Snapdragon(R)",
        "Snapdragon",
        "Qualcomm(R)",
        "Qualcomm",
        "Intel(R)",
        "Intel",
        "AMD",
        "NVIDIA",
        "Samsung",
        "SAMSUNG",
        "Micron",
        "KIOXIA",
        "Toshiba",
        "TOSHIBA",
        "Seagate",
        "Kingston",
        "SanDisk",
        "Realtek",
        "MediaTek",
        "Broadcom",
        "Marvell",
        "WDC",
        "WD",
        "Dell",
        "Lenovo",
        "ASUS",
        "HP"
    };

    private readonly PdhSampler sampler;
    private readonly EventWaitHandle stopEvent;
    private readonly bool useDesktopParent;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly List<double> cpuHistory;
    private readonly List<double> memoryHistory;
    private readonly List<double> diskHistory;
    private readonly List<double> diskCapacityHistory;
    private readonly List<double> networkSentHistory;
    private readonly List<double> networkReceivedHistory;
    private readonly List<double> networkHistory;
    private readonly List<double> gpuHistory;
    private readonly List<double> gpuMemoryHistory;
    private readonly List<double> npuHistory;
    private readonly List<double> npuMemoryHistory;
    private NotifyIcon notifyIcon;
    private Icon notifyIconImage;
    private SettingsForm settingsForm;
    private WidgetSettings savedSettings;
    private WidgetSettings currentSettings;
    private PerfSnapshot snapshot;
    private float scale;
    private int tickCount;
    private DateTime memoryCriticalSinceUtc;
    private DateTime diskCriticalSinceUtc;
    private DateTime gpuCriticalSinceUtc;
    private DateTime npuCriticalSinceUtc;
    private bool memoryAlertIconActive;
    private bool diskAlertIconActive;
    private bool gpuAlertIconActive;
    private bool npuAlertIconActive;
    private bool desktopAttached;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private ClockForm clockForm;
    private DockForm dockForm;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;

    public WidgetForm(PdhSampler sampler, EventWaitHandle stopEvent, WidgetSettings settings, bool useDesktopParent)
    {
        this.sampler = sampler;
        this.stopEvent = stopEvent;
        this.useDesktopParent = useDesktopParent;
        this.savedSettings = settings.Clone();
        this.currentSettings = settings.Clone();
        this.cpuHistory = new List<double>();
        this.memoryHistory = new List<double>();
        this.diskHistory = new List<double>();
        this.diskCapacityHistory = new List<double>();
        this.networkSentHistory = new List<double>();
        this.networkReceivedHistory = new List<double>();
        this.networkHistory = new List<double>();
        this.gpuHistory = new List<double>();
        this.gpuMemoryHistory = new List<double>();
        this.npuHistory = new List<double>();
        this.npuMemoryHistory = new List<double>();
        this.snapshot = new PerfSnapshot();

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(18, 19, 22);
        this.Opacity = 1.0;
        this.MinimumSize = new Size(WidgetSettings.MinWidth, WidgetSettings.MinHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxWidth, WidgetSettings.MaxHeight);
        this.Size = new Size(this.currentSettings.Width, this.currentSettings.Height);
        this.ContextMenuStrip = BuildContextMenu();
        BuildNotifyIcon();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = 1000;
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = 30;
        this.hoverTimer.Tick += OnHoverTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Program.LogInfo("Widget shown. Handle=0x" + this.Handle.ToInt64().ToString("X"));
        ApplyRuntimeSettings(this.currentSettings);
        PositionWidget();

        if (this.useDesktopParent)
        {
            AttachToDesktopLayer();
            PositionWidget();
        }
        else
        {
            Program.LogInfo("Desktop parent mode disabled; using stable visible desktop mode.");
        }

        this.clockForm = new ClockForm(this.currentSettings);
        this.clockForm.Show(this);
        EnsureDockForm();
        this.timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        if (this.settingsForm != null)
        {
            this.settingsForm.OwnerFormClosing = true;
            this.settingsForm.Close();
            this.settingsForm = null;
        }

        if (this.clockForm != null)
        {
            this.clockForm.Close();
            this.clockForm = null;
        }

        if (this.dockForm != null)
        {
            this.dockForm.Close();
            this.dockForm = null;
        }

        if (this.notifyIcon != null)
        {
            this.notifyIcon.Visible = false;
            this.notifyIcon.Dispose();
            this.notifyIcon = null;
        }

        if (this.notifyIconImage != null)
        {
            this.notifyIconImage.Dispose();
            this.notifyIconImage = null;
        }

        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(13)))
        {
            this.Region = new Region(path);
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionWidget();
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("设置...", null, delegate { OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { this.Close(); });
        return menu;
    }

    private void BuildNotifyIcon()
    {
        this.notifyIconImage = CreateNotifyIcon();
        this.notifyIcon = new NotifyIcon();
        this.notifyIcon.Icon = this.notifyIconImage;
        this.notifyIcon.Text = "DesktopPerfWidget";
        this.notifyIcon.ContextMenuStrip = BuildNotifyIconMenu();
        this.notifyIcon.Visible = true;
        this.notifyIcon.DoubleClick += delegate { OpenSettings(); };
    }

    private ContextMenuStrip BuildNotifyIconMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("设置...", null, delegate { OpenSettings(); });
        menu.Items.Add("退出", null, delegate { this.Close(); });
        return menu;
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        if (this.stopEvent.WaitOne(0))
        {
            this.Close();
            return;
        }

        try
        {
            UpdateVisibilityForMode();
            this.snapshot = this.sampler.Sample();
            AddHistory(this.cpuHistory, this.snapshot.CpuPercent);
            AddHistory(this.memoryHistory, this.snapshot.MemoryPercent);
            AddHistory(this.diskHistory, this.snapshot.DiskPercent);
            AddHistory(this.diskCapacityHistory, this.snapshot.DiskCapacityPercent);
            AddHistory(this.networkSentHistory, this.snapshot.NetworkSentBytesPerSecond * 8.0 / 1000.0);
            AddHistory(this.networkReceivedHistory, this.snapshot.NetworkReceivedBytesPerSecond * 8.0 / 1000.0);
            AddHistory(
                this.networkHistory,
                (this.snapshot.NetworkSentBytesPerSecond + this.snapshot.NetworkReceivedBytesPerSecond) * 8.0 / 1000.0);
            AddHistory(this.gpuHistory, this.snapshot.GpuPercent);
            AddHistory(this.gpuMemoryHistory, this.snapshot.GpuMemoryPercent);
            AddHistory(this.npuHistory, this.snapshot.NpuPercent);
            AddHistory(this.npuMemoryHistory, this.snapshot.NpuMemoryPercent);
            UpdateAlertIconStates();
            RenderLayeredWindow();

            if (this.tickCount % 30 == 0)
            {
                Program.LogInfo(string.Format(
                    "Sample CPU={0:0}% Memory={1:0}% Disk={2:0}% GPU={3:0}% GPUMem={4:0}% NPU={5:0}% NPUMem={6:0}% NetConnected={7} NetSent={8:0.0}Bps NetRecv={9:0.0}Bps",
                    this.snapshot.CpuPercent,
                    this.snapshot.MemoryPercent,
                    this.snapshot.DiskPercent,
                    this.snapshot.GpuPercent,
                    this.snapshot.GpuMemoryPercent,
                    this.snapshot.NpuPercent,
                    this.snapshot.NpuMemoryPercent,
                    this.snapshot.NetworkConnected,
                    this.snapshot.NetworkSentBytesPerSecond,
                    this.snapshot.NetworkReceivedBytesPerSecond));
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        this.tickCount++;
        if (this.tickCount % 10 == 0)
        {
            PositionWidget();
            UpdateVisibilityForMode();
        }
    }

    private static void AddHistory(List<double> history, double value)
    {
        const int MaxPoints = 34;
        history.Add(value);
        while (history.Count > MaxPoints)
        {
            history.RemoveAt(0);
        }
    }

    private void UpdateAlertIconStates()
    {
        DateTime now = DateTime.UtcNow;
        UpdateAlertIconState(this.snapshot.MemoryPercent, now, ref this.memoryCriticalSinceUtc, ref this.memoryAlertIconActive);
        UpdateAlertIconState(this.snapshot.DiskPercent, now, ref this.diskCriticalSinceUtc, ref this.diskAlertIconActive);
        UpdateAlertIconState(
            Math.Max(this.snapshot.GpuPercent, this.snapshot.GpuMemoryPercent),
            now,
            ref this.gpuCriticalSinceUtc,
            ref this.gpuAlertIconActive);
        UpdateAlertIconState(
            Math.Max(this.snapshot.NpuPercent, this.snapshot.NpuMemoryPercent),
            now,
            ref this.npuCriticalSinceUtc,
            ref this.npuAlertIconActive);
    }

    private static void UpdateAlertIconState(double value, DateTime now, ref DateTime criticalSinceUtc, ref bool active)
    {
        if (value >= 98.0)
        {
            if (criticalSinceUtc == DateTime.MinValue)
            {
                criticalSinceUtc = now;
            }

            active = (now - criticalSinceUtc).TotalSeconds >= 3.0;
            return;
        }

        criticalSinceUtc = DateTime.MinValue;
        active = false;
    }

    private void AttachToDesktopLayer()
    {
        if (this.desktopAttached)
        {
            return;
        }

        IntPtr desktopHost = NativeMethods.FindDesktopHostWindow();
        if (desktopHost == IntPtr.Zero)
        {
            Program.LogInfo("Desktop host window was not found; using normal window parent.");
            return;
        }

        NativeMethods.SetParent(this.Handle, desktopHost);
        int style = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_STYLE);
        style = (style | NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE) & ~NativeMethods.WS_POPUP;
        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_STYLE, style);
        this.desktopAttached = true;
        Program.LogInfo("Attached to desktop host. Host=0x" + desktopHost.ToInt64().ToString("X"));
    }

    private void PositionWidget()
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Point location = CalculateLocation(workArea);
        int left = location.X;
        int top = location.Y;
        this.Location = new Point(left, top);
        uint flags =
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED;

        if (!this.hiddenForFullscreen)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }

        if (this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly && !this.useDesktopParent)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            flags);

        if (this.tickCount == 0 || this.tickCount % 30 == 0)
        {
            Program.LogInfo(string.Format(
                "Positioned widget at {0},{1},{2},{3}. DesktopAttached={4}",
                left,
                top,
                this.Width,
                this.Height,
                this.desktopAttached));
        }
    }

    private Point CalculateLocation(Rectangle workArea)
    {
        int left = this.currentSettings.LeftX;
        int top = this.currentSettings.BottomY - this.Height + 1;
        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - this.Width));
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        return new Point(left, top);
    }

    internal void PreviewSettings(WidgetSettings settings)
    {
        ApplyRuntimeSettings(settings);
    }

    internal void SaveSettings(WidgetSettings settings)
    {
        this.savedSettings = settings.Clone();
        ApplyRuntimeSettings(settings);
        this.savedSettings.Save();
        Program.SetStartupEnabled(this.savedSettings.StartupEnabled, false);
        Program.LogInfo("Settings saved.");
    }

    internal void RevertSettings(WidgetSettings settings)
    {
        ApplyRuntimeSettings(settings);
        Program.LogInfo("Settings reverted.");
    }

    private void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        Program.SetPowerSavingEnabled(this.currentSettings.PowerSavingEnabled);

        Size desiredSize = new Size(this.currentSettings.Width, this.currentSettings.Height);
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();

        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionWidget();
        UpdateVisibilityForMode();
        if (this.clockForm != null && !this.clockForm.IsDisposed)
        {
            this.clockForm.ApplyRuntimeSettings(this.currentSettings);
        }

        EnsureDockForm();
        RenderLayeredWindow();
    }

    private void EnsureDockForm()
    {
        if (!this.currentSettings.DockEnabled)
        {
            if (this.dockForm != null)
            {
                this.dockForm.Close();
                this.dockForm = null;
            }

            return;
        }

        if (this.dockForm == null || this.dockForm.IsDisposed)
        {
            this.dockForm = new DockForm(
                this.currentSettings,
                delegate { OpenSettings(); },
                delegate { this.Close(); });
            this.dockForm.Show(this);
        }
        else
        {
            this.dockForm.ApplyRuntimeSettings(this.currentSettings);
        }

        if (this.dockForm != null && !this.dockForm.IsDisposed)
        {
            this.dockForm.SetHiddenForFullscreen(this.hiddenForFullscreen);
        }
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        if (UpdateHoverOpacityAnimation())
        {
            RenderLayeredWindow();
        }
    }

    private void UpdateHoverAnimationTimer()
    {
        if (this.currentSettings.HoverOpacityEnabled)
        {
            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow();
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered =
            this.currentSettings.HoverOpacityEnabled &&
            !this.hiddenForFullscreen &&
            this.Visible &&
            this.Bounds.Contains(Cursor.Position);

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private void UpdateVisibilityForMode()
    {
        bool hideForFullscreen =
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.HideWhenFullscreen &&
            NativeMethods.IsForegroundWindowFullscreen(this.Handle);

        if (hideForFullscreen)
        {
            this.hiddenForFullscreen = true;
            if (this.Visible)
            {
                this.Hide();
            }

            if (this.clockForm != null && !this.clockForm.IsDisposed)
            {
                this.clockForm.SetHiddenForFullscreen(true);
            }

            if (this.dockForm != null && !this.dockForm.IsDisposed)
            {
                this.dockForm.SetHiddenForFullscreen(true);
            }

            return;
        }

        this.hiddenForFullscreen = false;
        if (!this.Visible)
        {
            this.Show();
        }

        if (this.clockForm != null && !this.clockForm.IsDisposed)
        {
            this.clockForm.SetHiddenForFullscreen(false);
        }

        if (this.dockForm != null && !this.dockForm.IsDisposed)
        {
            this.dockForm.SetHiddenForFullscreen(false);
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }
    }

    private void OpenSettings()
    {
        if (this.settingsForm != null && !this.settingsForm.IsDisposed)
        {
            this.settingsForm.Activate();
            return;
        }

        WidgetSettings baseline = this.savedSettings.Clone();
        baseline.Normalize();
        this.settingsForm = new SettingsForm(this, baseline);
        this.settingsForm.FormClosed += delegate { this.settingsForm = null; };
        this.settingsForm.Show();
        this.settingsForm.Activate();
    }

    private Icon CreateNotifyIcon()
    {
        using (Bitmap bitmap = new Bitmap(32, 32))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush background = new SolidBrush(Color.FromArgb(25, 27, 32)))
            using (Pen border = new Pen(Color.FromArgb(120, 255, 255, 255), 2.0f))
            {
                g.FillEllipse(background, 2, 2, 28, 28);
                g.DrawEllipse(border, 2, 2, 28, 28);
            }

            using (Pen cpu = new Pen(Color.FromArgb(82, 211, 255), 3.0f))
            using (Pen memory = new Pen(Color.FromArgb(226, 126, 255), 3.0f))
            using (Pen disk = new Pen(Color.FromArgb(134, 238, 100), 3.0f))
            {
                g.DrawLine(cpu, 9, 21, 9, 12);
                g.DrawLine(memory, 16, 21, 16, 8);
                g.DrawLine(disk, 23, 21, 23, 15);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                return icon;
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawWidget(e.Graphics);
    }

    private void DrawWidget(Graphics g)
    {
        DrawWidgetBackground(g);
        DrawWidgetContentLayer(g);
    }

    private void ConfigureWidgetGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawWidgetBackground(Graphics g)
    {
        ConfigureWidgetGraphics(g);
        int backgroundAlpha = GetBackgroundOpacityAlpha();

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(13)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(backgroundAlpha, 18, 19, 22)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawWidgetContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawWidgetContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawWidgetContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawWidgetContent(Graphics g)
    {
        ConfigureWidgetGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(13)))
        using (Pen outline = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        int margin = S(13);
        int gap = S(14);
        int rowGap = S(8);
        List<MetricPanel> panels = BuildMetricPanels();
        if (panels.Count == 0)
        {
            using (Font font = new Font("Segoe UI", 13.0f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(220, 230, 235)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString("No metrics enabled", font, brush, this.ClientRectangle, format);
            }

            return;
        }

        int columns = panels.Count == 1 ? 1 : 2;
        int rows = (panels.Count + columns - 1) / columns;
        int colWidth = (this.ClientSize.Width - margin * 2 - gap * (columns - 1)) / columns;
        int rowHeight = (this.ClientSize.Height - margin * 2 - rowGap * (rows - 1)) / rows;

        for (int i = 0; i < panels.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            RectangleF area = new RectangleF(
                margin + column * (colWidth + gap),
                margin + row * (rowHeight + rowGap),
                colWidth,
                rowHeight);
            DrawMetric(g, area, panels[i]);
        }
    }

    private void RenderLayeredWindow()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawWidgetBackground(g);
                DrawWidgetContentLayer(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap, GetApplicationOpacityAlpha()))
                {
                    if (!this.layeredUpdateFailureLogged)
                    {
                        this.layeredUpdateFailureLogged = true;
                        Program.LogInfo("UpdateLayeredWindow failed; falling back to normal paint.");
                    }

                    this.Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private List<MetricPanel> BuildMetricPanels()
    {
        List<MetricPanel> panels = new List<MetricPanel>();
        string[] order = this.currentSettings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        for (int i = 0; i < order.Length; i++)
        {
            AddMetricPanel(panels, order[i]);
        }

        return panels;
    }

    private void AddMetricPanel(List<MetricPanel> panels, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowCpu)
        {
            MetricPanel cpuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.CpuName), string.Format("CPU {0:0}%", this.snapshot.CpuPercent), FormatCpuFrequencyPair(this.snapshot.CpuFrequencyGhz, this.snapshot.CpuBaseFrequencyGhz) },
                new Color[] { Color.FromArgb(82, 211, 255) },
                new List<double>[] { this.cpuHistory },
                100.0,
                false);
            cpuPanel.CoreValues = this.snapshot.CpuCorePercents;
            cpuPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                cpuPanel.AlertPercent = 100.0;
                cpuPanel.AlertIconVisible = true;
            }

            panels.Add(cpuPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowMemory)
        {
            MetricPanel memoryPanel = new MetricPanel(
                new string[] { FormatMemoryTitleForPanel(this.snapshot.MemoryManufacturer, this.snapshot.MemorySpeedMtps), string.Format("MEM {0:0}%", this.snapshot.MemoryPercent), FormatGbPair(this.snapshot.MemoryUsedGb, this.snapshot.MemoryTotalGb) },
                new Color[] { Color.FromArgb(226, 126, 255) },
                new List<double>[] { this.memoryHistory },
                100.0,
                false);
            memoryPanel.AlertPercent = this.snapshot.MemoryPercent;
            memoryPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                memoryPanel.AlertPercent = 100.0;
                memoryPanel.AlertIconVisible = true;
            }
            else
            {
                memoryPanel.AlertIconVisible = this.memoryAlertIconActive;
            }

            panels.Add(memoryPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowDisk)
        {
            MetricPanel diskPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.DiskName), string.Format("R/W {0:0}%", this.snapshot.DiskPercent), FormatGbPair(this.snapshot.DiskUsedGb, this.snapshot.DiskTotalGb) },
                new Color[] { Color.FromArgb(134, 238, 100), Color.FromArgb(255, 207, 82) },
                new List<double>[] { this.diskHistory, this.diskCapacityHistory },
                100.0,
                false);
            diskPanel.AlertPercent = this.snapshot.DiskPercent;
            diskPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                diskPanel.AlertPercent = 100.0;
                diskPanel.AlertIconVisible = true;
            }
            else
            {
                diskPanel.AlertIconVisible = this.diskAlertIconActive;
            }

            panels.Add(diskPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowNetwork)
        {
            MetricPanel networkPanel = new MetricPanel(
                this.snapshot.NetworkConnected ?
                    new string[] { this.snapshot.NetworkName, "UP " + FormatRate(this.snapshot.NetworkSentBytesPerSecond), "DL " + FormatRate(this.snapshot.NetworkReceivedBytesPerSecond) } :
                    new string[] { "Network", "网络已断开", "" },
                new Color[] { Color.FromArgb(82, 211, 255), Color.FromArgb(255, 80, 96) },
                new List<double>[] { this.networkSentHistory, this.networkReceivedHistory },
                1.0,
                true);
            networkPanel.UseCompactValueFont = true;
            networkPanel.IsNetworkDisconnected = !this.snapshot.NetworkConnected;
            if (this.currentSettings.AlertTestEnabled)
            {
                networkPanel.AlertPercent = 100.0;
                networkPanel.AlertIconVisible = true;
            }

            panels.Add(networkPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowGpu)
        {
            MetricPanel gpuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.GpuName), string.Format("GPU {0:0}%", this.snapshot.GpuPercent), FormatGbPair(this.snapshot.GpuMemoryUsedGb, this.snapshot.GpuMemoryTotalGb) },
                new Color[] { Color.FromArgb(82, 211, 255), Color.FromArgb(226, 126, 255) },
                new List<double>[] { this.gpuHistory, this.gpuMemoryHistory },
                100.0,
                false);
            gpuPanel.AlertPercent = Math.Max(this.snapshot.GpuPercent, this.snapshot.GpuMemoryPercent);
            gpuPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                gpuPanel.AlertPercent = 100.0;
                gpuPanel.AlertIconVisible = true;
            }
            else
            {
                gpuPanel.AlertIconVisible = this.gpuAlertIconActive;
            }

            panels.Add(gpuPanel);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase) && this.currentSettings.ShowNpu)
        {
            MetricPanel npuPanel = new MetricPanel(
                new string[] { FormatHardwareNameForPanel(this.snapshot.NpuName), string.Format("NPU {0:0}%", this.snapshot.NpuPercent), FormatGbPair(this.snapshot.NpuMemoryUsedGb, this.snapshot.NpuMemoryTotalGb) },
                new Color[] { Color.FromArgb(255, 207, 82), Color.FromArgb(226, 126, 255) },
                new List<double>[] { this.npuHistory, this.npuMemoryHistory },
                100.0,
                false);
            npuPanel.AlertPercent = Math.Max(this.snapshot.NpuPercent, this.snapshot.NpuMemoryPercent);
            npuPanel.UseHardwareStackText = true;
            if (this.currentSettings.AlertTestEnabled)
            {
                npuPanel.AlertPercent = 100.0;
                npuPanel.AlertIconVisible = true;
            }
            else
            {
                npuPanel.AlertIconVisible = this.npuAlertIconActive;
            }

            panels.Add(npuPanel);
        }
    }

    private void DrawMetric(Graphics g, RectangleF area, MetricPanel panel)
    {
        float graphW = Math.Min(S(86), Math.Max(S(58), area.Width * 0.34f));
        float graphH = Math.Max(S(32), area.Height - S(8));
        RectangleF graphRect = new RectangleF(area.X, area.Y + Math.Max(0, (area.Height - graphH) / 2), graphW, graphH);
        DrawGraph(g, graphRect, panel.Colors, panel.Histories, panel.GraphMax, panel.AutoScale, panel.IsNetworkDisconnected, panel.CoreValues, panel.AlertPercent, panel.AlertIconVisible);

        if (panel.IsNetworkDisconnected)
        {
            DrawDisconnectedCross(g, graphRect);
        }

        float textX = graphRect.Right + S(9);
        float textWidth = Math.Max(20, area.Right - textX);
        float lineH = Math.Max(S(14), area.Height / 3.0f);

        using (Font smallFont = new Font("Segoe UI", 10.5f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font valueFont = new Font("Segoe UI", 11.5f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font compactFont = new Font("Segoe UI", 10.0f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(224, 235, 241)))
        using (SolidBrush valueBrush = new SolidBrush(Color.FromArgb(244, 248, 250)))
        using (SolidBrush alertBrush = new SolidBrush(Color.FromArgb(255, 92, 104)))
        {
            RectangleF first = new RectangleF(textX, area.Y, textWidth, lineH);
            RectangleF second = new RectangleF(textX, area.Y + lineH, textWidth, lineH);
            RectangleF third = new RectangleF(textX, area.Y + lineH * 2, textWidth, lineH);
            if (panel.UseHardwareStackText && panel.TextLines[0].IndexOf('\n') >= 0)
            {
                DrawHardwareStackText(g, area, textX, textWidth, panel, smallFont, valueFont, titleBrush, valueBrush);
                return;
            }

            DrawTitleText(g, panel.TextLines[0], smallFont, titleBrush, first);
            if (panel.UseCompactValueFont)
            {
                DrawFixedText(g, panel.TextLines[1], compactFont, panel.IsNetworkDisconnected ? alertBrush : valueBrush, second);
                DrawFixedText(g, panel.TextLines[2], compactFont, valueBrush, third);
            }
            else
            {
                DrawFittedText(g, panel.TextLines[1], valueFont, valueBrush, second);
                DrawFittedText(g, panel.TextLines[2], valueFont, valueBrush, third);
            }
        }
    }

    private void DrawGraph(Graphics g, RectangleF rect, Color[] accents, List<double>[] histories, double graphMax, bool autoScale, bool dimmed, double[] coreValues, double alertPercent, bool alertIconVisible)
    {
        Color borderColor = accents.Length > 0 ? accents[0] : Color.FromArgb(180, 220, 230);
        int backgroundAlpha = GetBackgroundOpacityAlpha();
        int fillAlpha = dimmed ? Math.Min(backgroundAlpha, 128) : backgroundAlpha;
        int borderAlpha = dimmed ? 90 : 180;
        using (SolidBrush fill = new SolidBrush(Color.FromArgb(fillAlpha, 26, 31, 36)))
        using (Pen border = new Pen(Color.FromArgb(borderAlpha, borderColor), Math.Max(1.0f, 1.5f * this.scale)))
        {
            g.FillRectangle(fill, rect);
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
        }

        DrawUsageAlertLayer(g, rect, alertPercent, alertIconVisible);
        DrawCoreBars(g, rect, coreValues);

        double max = autoScale ? MaxValue(histories) : graphMax;
        if (max < 1.0)
        {
            max = 1.0;
        }

        for (int h = 0; h < histories.Length; h++)
        {
            List<double> history = histories[h];
            if (history == null || history.Count < 2)
            {
                continue;
            }

            PointF[] points = new PointF[history.Count];
            for (int i = 0; i < history.Count; i++)
            {
                double normalized = Clamp(history[i] / max, 0.0, 1.0);
                float x = rect.Left + (rect.Width - 2) * i / Math.Max(1, history.Count - 1) + 1;
                float y = rect.Bottom - 1 - (float)(normalized * (rect.Height - 2));
                points[i] = new PointF(x, y);
            }

            Color accent = accents[Math.Min(h, accents.Length - 1)];
            if (dimmed)
            {
                accent = Color.FromArgb(110, accent);
            }

            using (Pen line = new Pen(accent, Math.Max(1.0f, 2.0f * this.scale)))
            {
                line.LineJoin = LineJoin.Round;
                g.DrawLines(line, points);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.BackgroundTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!this.currentSettings.HoverOpacityEnabled || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    private void DrawUsageAlertLayer(Graphics g, RectangleF rect, double alertPercent, bool alertIconVisible)
    {
        if (alertPercent < 80.0)
        {
            return;
        }

        double progress = Clamp((alertPercent - 80.0) / 20.0, 0.0, 1.0);
        int redAlpha = (int)Math.Round(179.0 * progress);
        using (SolidBrush redOverlay = new SolidBrush(Color.FromArgb(redAlpha, 255, 44, 58)))
        {
            g.FillRectangle(redOverlay, rect);
        }

        if (!alertIconVisible)
        {
            return;
        }

        float size = Math.Min(rect.Width, rect.Height) * 0.48f;
        size = Math.Max(14.0f * this.scale, Math.Min(size, 28.0f * this.scale));
        float centerX = rect.Left + rect.Width * 0.5f;
        float centerY = rect.Top + rect.Height * 0.52f;
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.58f),
            new PointF(centerX - size * 0.58f, centerY + size * 0.48f),
            new PointF(centerX + size * 0.58f, centerY + size * 0.48f)
        };

        int warningAlpha = (this.tickCount % 2 == 0) ? 77 : 179;
        using (Pen triangleBorder = new Pen(Color.FromArgb(warningAlpha, 255, 207, 82), Math.Max(1.0f, 3.0f * this.scale)))
        {
            triangleBorder.LineJoin = LineJoin.Round;
            g.DrawPolygon(triangleBorder, triangle);
        }

        using (Font markFont = new Font("Segoe UI", Math.Max(9.0f, size * 0.7f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush markBrush = new SolidBrush(Color.FromArgb(warningAlpha, 255, 207, 82)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            RectangleF markRect = new RectangleF(centerX - size * 0.5f, centerY - size * 0.36f, size, size * 0.92f);
            g.DrawString("!", markFont, markBrush, markRect, format);
        }
    }

    private void DrawCoreBars(Graphics g, RectangleF rect, double[] values)
    {
        if (values == null || values.Length == 0)
        {
            return;
        }

        float left = rect.Left + Math.Max(2.0f, 2.0f * this.scale);
        float bottom = rect.Bottom - Math.Max(2.0f, 2.0f * this.scale);
        float width = Math.Max(1.0f, rect.Width - Math.Max(4.0f, 4.0f * this.scale));
        float height = Math.Max(1.0f, rect.Height - Math.Max(4.0f, 4.0f * this.scale));
        float slot = width / values.Length;
        float gap = slot >= 4.0f ? Math.Min(2.0f * this.scale, slot * 0.28f) : 0.0f;
        float barWidth = Math.Max(1.0f, slot - gap);

        using (SolidBrush normalBrush = new SolidBrush(Color.FromArgb(115, 82, 211, 255)))
        using (SolidBrush warningBrush = new SolidBrush(Color.FromArgb(210, 255, 207, 82)))
        using (SolidBrush criticalBrush = new SolidBrush(Color.FromArgb(225, 255, 80, 96)))
        {
            for (int i = 0; i < values.Length; i++)
            {
                double value = Clamp(values[i], 0.0, 100.0);
                float x = left + slot * i + gap / 2.0f;
                float valueTop = bottom - (float)(height * value / 100.0);

                if (value > 95.0)
                {
                    g.FillRectangle(criticalBrush, x, valueTop, barWidth, bottom - valueTop);
                    continue;
                }

                float normalValue = (float)Math.Min(value, 80.0);
                if (normalValue > 0.0f)
                {
                    float normalTop = bottom - height * normalValue / 100.0f;
                    g.FillRectangle(normalBrush, x, normalTop, barWidth, bottom - normalTop);
                }

                if (value > 80.0)
                {
                    float warningTop = valueTop;
                    float warningBottom = bottom - height * 80.0f / 100.0f;
                    g.FillRectangle(warningBrush, x, warningTop, barWidth, warningBottom - warningTop);
                }
            }
        }
    }

    private void DrawDisconnectedCross(Graphics g, RectangleF rect)
    {
        float padding = Math.Max(3.0f, 4.0f * this.scale);
        using (Pen cross = new Pen(Color.FromArgb(255, 72, 86), Math.Max(2.0f, 3.2f * this.scale)))
        {
            cross.StartCap = LineCap.Round;
            cross.EndCap = LineCap.Round;
            g.DrawLine(cross, rect.Left + padding, rect.Top + padding, rect.Right - padding, rect.Bottom - padding);
            g.DrawLine(cross, rect.Right - padding, rect.Top + padding, rect.Left + padding, rect.Bottom - padding);
        }
    }

    private void DrawFixedText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(text, font, brush, rect, format);
        }
    }

    private void DrawHardwareStackText(Graphics g, RectangleF area, float textX, float textWidth, MetricPanel panel, Font titleFont, Font valueFont, Brush titleBrush, Brush valueBrush)
    {
        string[] titleLines = panel.TextLines[0].Replace("\r", string.Empty).Split('\n');
        string titleFirst = titleLines.Length > 0 ? titleLines[0] : string.Empty;
        string titleSecond = titleLines.Length > 1 ? titleLines[1] : string.Empty;
        float stackLineH = Math.Max(S(10), area.Height / 4.0f);
        float stackTop = area.Y + Math.Max(0, (area.Height - stackLineH * 4.0f) / 2.0f);

        RectangleF titleFirstRect = new RectangleF(textX, stackTop, textWidth, stackLineH);
        RectangleF titleSecondRect = new RectangleF(textX, stackTop + stackLineH, textWidth, stackLineH);
        RectangleF valueRect = new RectangleF(textX, stackTop + stackLineH * 2.0f, textWidth, stackLineH);
        RectangleF detailRect = new RectangleF(textX, stackTop + stackLineH * 3.0f, textWidth, stackLineH);

        DrawFittedText(g, titleFirst, titleFont, titleBrush, titleFirstRect);
        DrawFittedText(g, titleSecond, titleFont, titleBrush, titleSecondRect);
        DrawFittedText(g, panel.TextLines[1], valueFont, valueBrush, valueRect);
        DrawFittedText(g, panel.TextLines[2], valueFont, valueBrush, detailRect);
    }

    private void DrawTitleText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\n') < 0)
        {
            DrawFittedText(g, text, baseFont, brush, rect);
            return;
        }

        string[] lines = text.Replace("\r", string.Empty).Split('\n');
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 7.0f * this.scale && !TitleTextFits(g, lines, drawFont, rect))
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.6f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private bool TitleTextFits(Graphics g, string[] lines, Font font, RectangleF rect)
    {
        float maxWidth = 0.0f;
        int visibleLines = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            visibleLines++;
            maxWidth = Math.Max(maxWidth, g.MeasureString(lines[i], font).Width);
        }

        visibleLines = Math.Max(1, visibleLines);
        float totalHeight = font.GetHeight(g) * visibleLines;
        return maxWidth <= rect.Width && totalHeight <= rect.Height * 1.02f;
    }

    private void DrawFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;

            while (size > 8.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.7f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        return NetworkRateFormatter.Format(bytesPerSecond);
    }

    private static string FormatGbPair(double usedGb, double totalGb)
    {
        if (totalGb <= 0.0)
        {
            return string.Format("{0:0.0}/-- GB", usedGb);
        }

        return string.Format("{0:0.0}/{1:0.#} GB", usedGb, totalGb);
    }

    private static string FormatCpuFrequencyPair(double currentGhz, double baseGhz)
    {
        if (currentGhz <= 0.0 && baseGhz <= 0.0)
        {
            return string.Empty;
        }

        if (baseGhz <= 0.0)
        {
            return string.Format("{0:0.00}GHz/--GHz", currentGhz);
        }

        return string.Format("{0:0.00}GHz/{1:0.00}GHz", currentGhz, baseGhz);
    }

    private static string FormatMemoryTitleForPanel(string manufacturer, int speedMtps)
    {
        string first = string.IsNullOrWhiteSpace(manufacturer) ? "Memory" : CollapseWhitespace(manufacturer.Trim());
        string second = speedMtps > 0 ? speedMtps.ToString() + " MT/s" : "-- MT/s";
        return first + "\n" + second;
    }

    private static string FormatHardwareNameForPanel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string text = CollapseWhitespace(name.Trim());
        if (text.IndexOf('\n') >= 0)
        {
            return text;
        }

        for (int i = 0; i < HardwareVendorPrefixes.Length; i++)
        {
            string vendor = HardwareVendorPrefixes[i];
            if (!StartsWithVendorPrefix(text, vendor))
            {
                continue;
            }

            string remainder = text.Substring(vendor.Length).TrimStart(' ', '\t', '-', '_');
            if (remainder.Length == 0)
            {
                return text;
            }

            return text.Substring(0, vendor.Length).Trim() + "\n" + remainder;
        }

        return text;
    }

    private static bool StartsWithVendorPrefix(string text, string vendor)
    {
        if (!text.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == vendor.Length)
        {
            return true;
        }

        char next = text[vendor.Length];
        return char.IsWhiteSpace(next) || next == '-' || next == '_' || next == '(';
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        bool previousWhitespace = false;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                    previousWhitespace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        return builder.ToString();
    }

    private static double MaxValue(List<double> values)
    {
        double max = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }

        return max;
    }

    private static double MaxValue(List<double>[] histories)
    {
        double max = 0.0;
        for (int i = 0; i < histories.Length; i++)
        {
            if (histories[i] == null)
            {
                continue;
            }

            double value = MaxValue(histories[i]);
            if (value > max)
            {
                max = value;
            }
        }

        return max;
    }

    private static double Clamp(double value, double min, double max)
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

    private int S(int value)
    {
        return (int)Math.Round(value * this.scale);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ClockForm : Form
{
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly Dictionary<string, DateTime> thermalCriticalSinceUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private PowerReading cachedPowerReading;
    private DateTime cachedPowerReadingUtc;
    private List<ThermalReading> cachedThermalReadings = new List<ThermalReading>();
    private DateTime cachedThermalReadingsUtc;
    private int renderTickCount;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;

    private struct PowerReading
    {
        public bool StatusKnown;
        public bool IsCharging;
        public bool WattsKnown;
        public double Watts;
    }

    private sealed class ThermalReading
    {
        public string Name { get; set; }
        public double Celsius { get; set; }
        public bool CriticalActive { get; set; }
    }

    public ClockForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(18, 19, 22);
        this.MinimumSize = new Size(WidgetSettings.MinClockWidth, WidgetSettings.MinClockHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxClockWidth, WidgetSettings.MaxClockHeight + S(32));
        this.Size = new Size(this.currentSettings.ClockWidth, this.currentSettings.ClockHeight);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = 250;
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = 30;
        this.hoverTimer.Tick += OnHoverTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        this.timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        this.renderTickCount++;
        Size desiredSize = GetDesiredClockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
            PositionClock();
        }

        RenderLayeredWindow();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(12)))
        {
            this.Region = new Region(path);
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionClock();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        ThermalTestMode oldThermalTestMode = this.currentSettings.ThermalTestMode;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        if (oldThermalTestMode != this.currentSettings.ThermalTestMode)
        {
            this.thermalCriticalSinceUtc.Clear();
            this.cachedThermalReadingsUtc = DateTime.MinValue;
        }

        Size desiredSize = GetDesiredClockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();
        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionClock();
        RenderLayeredWindow();
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        if (UpdateHoverOpacityAnimation())
        {
            RenderLayeredWindow();
        }
    }

    private void UpdateHoverAnimationTimer()
    {
        if (this.currentSettings.HoverOpacityEnabled)
        {
            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow();
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered =
            this.currentSettings.HoverOpacityEnabled &&
            !this.hiddenForFullscreen &&
            this.Visible &&
            this.Bounds.Contains(Cursor.Position);

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            if (this.Visible)
            {
                this.Hide();
            }

            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionClock();
        RenderLayeredWindow();
    }

    private void PositionClock()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredClockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.ClockLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinClockHeight, this.currentSettings.ClockHeight);
        int top = this.currentSettings.ClockBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - baseHeight));
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredClockSize()
    {
        int extraHeight = GetThermalAlerts().Count > 0 ? GetThermalAlertExtraHeight() : 0;
        return new Size(this.currentSettings.ClockWidth, this.currentSettings.ClockHeight + extraHeight);
    }

    private int GetThermalAlertExtraHeight()
    {
        return Math.Max(S(24), Math.Min(S(32), (int)Math.Round(this.currentSettings.ClockHeight * 0.42f)));
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawClock(e.Graphics);
    }

    private void DrawClock(Graphics g)
    {
        DrawClockBackground(g);
        DrawClockContentLayer(g);
    }

    private void ConfigureClockGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawClockBackground(Graphics g)
    {
        ConfigureClockGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(12)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(alpha, 18, 19, 22)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawClockContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawClockContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawClockContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawClockContent(Graphics g)
    {
        ConfigureClockGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(12)))
        using (Pen outline = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        string formatText = this.currentSettings.ClockUse24Hour ? "HH:mm:ss" : "h:mm:ss tt";
        string timeText = DateTime.Now.ToString(formatText);
        List<ThermalReading> thermalAlerts = GetThermalAlerts();
        float baseHeight = Math.Min(this.currentSettings.ClockHeight, this.Height);
        float thermalReserve = thermalAlerts.Count > 0 ? Math.Max(S(18), GetThermalAlertExtraHeight() - S(6)) : 0.0f;
        RectangleF textRect = new RectangleF(
            S(12),
            S(5),
            Math.Max(10, this.Width - S(24)),
            Math.Max(10, baseHeight - S(10)));

        if (this.currentSettings.ClockCalendarEnabled || this.currentSettings.ClockPowerEnabled)
        {
            DrawClockWithInfo(g, timeText, textRect);
        }
        else
        {
            float fontSize = Math.Max(14.0f, Math.Min(textRect.Height * 0.74f, this.Width * 0.16f));

            using (Font baseFont = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(244, 248, 250)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                Font drawFont = baseFont;
                bool disposeFont = false;
                float size = baseFont.Size;
                while (size > 10.0f * this.scale && g.MeasureString(timeText, drawFont).Width > textRect.Width)
                {
                    if (disposeFont)
                    {
                        drawFont.Dispose();
                    }

                    size -= 1.0f * this.scale;
                    drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                    disposeFont = true;
                }

                g.DrawString(timeText, drawFont, brush, textRect, format);

                if (disposeFont)
                {
                    drawFont.Dispose();
                }
            }
        }

        if (thermalAlerts.Count > 0)
        {
            RectangleF thermalRect = new RectangleF(
                S(12),
                baseHeight + S(3),
                Math.Max(10, this.Width - S(24)),
                thermalReserve);
            DrawThermalAlerts(g, thermalRect, thermalAlerts);
        }
    }

    private void DrawClockWithInfo(Graphics g, string timeText, RectangleF bounds)
    {
        DateTime now = DateTime.Now;
        bool showCalendar = this.currentSettings.ClockCalendarEnabled;
        bool showPower = this.currentSettings.ClockPowerEnabled;
        float gap = S(6);
        float rightWidth = showCalendar ? Math.Min(bounds.Width * 0.30f, Math.Max(S(96), bounds.Width * 0.23f)) : 0;
        float powerWidth = showPower ? Math.Min(bounds.Width * 0.24f, Math.Max(S(74), bounds.Width * 0.18f)) : 0;
        float usedGap = 0;
        if (showPower)
        {
            usedGap += gap;
        }

        if (showCalendar)
        {
            usedGap += gap;
        }

        float timeWidth = Math.Max(10, bounds.Width - rightWidth - powerWidth - usedGap);
        RectangleF timeRect = new RectangleF(bounds.Left, bounds.Top, timeWidth, bounds.Height);
        float x = timeRect.Right + (showPower ? gap : 0);
        RectangleF powerRect = showPower ? new RectangleF(x, bounds.Top, powerWidth, bounds.Height) : RectangleF.Empty;
        x = showPower ? powerRect.Right + (showCalendar ? gap : 0) : x;
        RectangleF dateRect = showCalendar ? new RectangleF(x, bounds.Top, rightWidth, bounds.Height * 0.50f) : RectangleF.Empty;
        RectangleF dayRect = showCalendar ? new RectangleF(x, bounds.Top + bounds.Height * 0.50f, rightWidth, bounds.Height * 0.50f) : RectangleF.Empty;

        string dateText = FormatClockDate(now);
        string dayText = FormatClockWeekday(now);
        float timeFontSize = Math.Max(14.0f, Math.Min(bounds.Height * 0.66f, timeRect.Width * 0.25f));
        float detailFontSize = Math.Max(9.0f, Math.Min(bounds.Height * 0.28f, rightWidth * 0.19f));
        float powerLabelFontSize = Math.Max(8.5f, Math.Min(bounds.Height * 0.22f, powerWidth * 0.20f));
        float powerValueFontSize = Math.Max(9.0f, Math.Min(bounds.Height * 0.26f, powerWidth * 0.22f));

        using (Font timeFont = new Font("Segoe UI", timeFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font detailFont = new Font("Segoe UI", detailFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font powerLabelFont = new Font("Segoe UI", powerLabelFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font powerValueFont = new Font("Segoe UI", powerValueFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush timeBrush = new SolidBrush(Color.FromArgb(244, 248, 250)))
        using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(224, 235, 241)))
        {
            DrawClockFittedText(g, timeText, timeFont, timeBrush, timeRect, StringAlignment.Near);

            if (showPower)
            {
                DrawClockPower(g, powerRect, powerLabelFont, powerValueFont);
            }

            if (showCalendar)
            {
                DrawClockFittedText(g, dateText, detailFont, detailBrush, dateRect, StringAlignment.Far);
                DrawClockFittedText(g, dayText, detailFont, detailBrush, dayRect, StringAlignment.Far);
            }
        }
    }

    private void DrawClockPower(Graphics g, RectangleF bounds, Font labelFont, Font valueFont)
    {
        PowerReading reading = GetPowerReading();
        bool charging = reading.StatusKnown && reading.IsCharging;
        string labelText = charging ? "Charging" : "Power";
        string valueText = reading.WattsKnown ? FormatWatts(reading.Watts) : "-- W";
        Color accent = charging ? Color.FromArgb(170, 238, 190) : Color.FromArgb(255, 178, 186);
        RectangleF labelRect = new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height * 0.48f);
        RectangleF valueRect = new RectangleF(bounds.Left, bounds.Top + bounds.Height * 0.45f, bounds.Width, bounds.Height * 0.55f);

        using (SolidBrush brush = new SolidBrush(accent))
        {
            DrawClockFittedText(g, labelText, labelFont, brush, labelRect, StringAlignment.Center);
            DrawClockFittedText(g, valueText, valueFont, brush, valueRect, StringAlignment.Center);
        }
    }

    private void DrawThermalAlerts(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int visibleSensors = Math.Min(3, total);
        bool hasMore = total > 3;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(6);
        float chipHeight = Math.Max(S(16), bounds.Height - S(2));
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height - chipHeight) / 2.0f);

        using (Font chipFont = new Font("Segoe UI", Math.Max(8.0f, 9.5f * this.scale), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float moreWidth = 0.0f;
            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString();
                moreWidth = Math.Max(S(30), g.MeasureString(moreText, chipFont).Width + S(18));
                moreWidth = Math.Min(moreWidth, bounds.Width * 0.28f);
                RectangleF moreRect = new RectangleF(bounds.Right - moreWidth, chipTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }

            float sensorAreaRight = hasMore ? bounds.Right - moreWidth - gap : bounds.Right;
            float sensorAreaWidth = Math.Max(S(30), sensorAreaRight - bounds.Left);
            float slotWidth = Math.Max(S(30), (sensorAreaWidth - gap * 2.0f) / 3.0f);
            float x = bounds.Left;
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float desiredWidth = g.MeasureString(text, chipFont).Width + S(alerts[i].CriticalActive ? 32 : 20);
                float width = Math.Min(slotWidth, Math.Max(S(30), desiredWidth));
                RectangleF chipRect = new RectangleF(x, chipTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                x += slotWidth + gap;
            }
        }
    }

    private void DrawThermalChip(Graphics g, RectangleF rect, string text, double celsius, bool criticalActive, Font font)
    {
        float radius = Math.Min(rect.Height / 2.0f, S(11));
        int redAlpha = GetThermalRedAlpha(celsius);
        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush baseBrush = new SolidBrush(Color.FromArgb(160, 28, 28, 30)))
        using (SolidBrush redBrush = new SolidBrush(Color.FromArgb(redAlpha, 255, 44, 58)))
        using (Pen border = new Pen(Color.FromArgb(45, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(baseBrush, path);
            g.FillPath(redBrush, path);
            g.DrawPath(border, path);
        }

        RectangleF textRect = rect;
        if (criticalActive)
        {
            float iconSize = Math.Max(S(12), Math.Min(rect.Height * 0.70f, S(17)));
            RectangleF iconRect = new RectangleF(rect.Right - iconSize - S(7), rect.Top + (rect.Height - iconSize) / 2.0f, iconSize, iconSize);
            DrawSmallWarningIcon(g, iconRect);
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - iconSize - S(18)), rect.Height);
        }
        else
        {
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - S(16)), rect.Height);
        }

        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(246, 246, 246)))
        {
            DrawClockFittedText(g, text, font, textBrush, textRect, StringAlignment.Near);
        }
    }

    private void DrawSmallWarningIcon(Graphics g, RectangleF rect)
    {
        int warningAlpha = (this.renderTickCount % 2 == 0) ? 77 : 179;
        float centerX = rect.Left + rect.Width / 2.0f;
        float centerY = rect.Top + rect.Height / 2.0f;
        float size = Math.Min(rect.Width, rect.Height);
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.46f),
            new PointF(centerX - size * 0.48f, centerY + size * 0.42f),
            new PointF(centerX + size * 0.48f, centerY + size * 0.42f)
        };

        using (Pen pen = new Pen(Color.FromArgb(warningAlpha, 255, 207, 82), Math.Max(1.0f, 2.0f * this.scale)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPolygon(pen, triangle);
        }

        using (Font markFont = new Font("Segoe UI", Math.Max(7.0f, size * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush markBrush = new SolidBrush(Color.FromArgb(warningAlpha, 255, 207, 82)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("!", markFont, markBrush, rect, format);
        }
    }

    private static int GetThermalRedAlpha(double celsius)
    {
        double progress = (celsius - 70.0) / 30.0;
        if (progress < 0.0)
        {
            progress = 0.0;
        }
        else if (progress > 1.0)
        {
            progress = 1.0;
        }

        double alpha = 0.30 + progress * (0.85 - 0.30);
        return (int)Math.Round(alpha * 255.0);
    }

    private static string FormatThermalSensorName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "TZ";
        }

        return name.Trim();
    }

    private void DrawClockFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 8.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.8f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private static string FormatClockDate(DateTime value)
    {
        string[] months = new string[]
        {
            "Jan.", "Feb.", "Mar.", "Apr.", "May.", "Jun.",
            "Jul.", "Aug.", "Sep.", "Oct.", "Nov.", "Dec."
        };

        return months[value.Month - 1] + value.Day.ToString() + GetOrdinalSuffix(value.Day);
    }

    private static string FormatClockWeekday(DateTime value)
    {
        string[] days = new string[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        return days[(int)value.DayOfWeek];
    }

    private PowerReading GetPowerReading()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedPowerReadingUtc).TotalSeconds < 2.0)
        {
            return this.cachedPowerReading;
        }

        this.cachedPowerReading = ReadPowerReading();
        this.cachedPowerReadingUtc = now;
        return this.cachedPowerReading;
    }

    private List<ThermalReading> GetThermalAlerts()
    {
        DateTime now = DateTime.UtcNow;
        if (this.currentSettings.ThermalTestMode != ThermalTestMode.Off)
        {
            List<ThermalReading> simulated = BuildSimulatedThermalReadings(this.currentSettings.ThermalTestMode);
            UpdateThermalCriticalStates(simulated, now, true);
            simulated.Sort(CompareThermalReading);
            return simulated;
        }

        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
            UpdateThermalCriticalStates(this.cachedThermalReadings, now, false);
        }

        List<ThermalReading> alerts = new List<ThermalReading>();
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            if (this.cachedThermalReadings[i].Celsius >= 70.0)
            {
                alerts.Add(this.cachedThermalReadings[i]);
            }
        }

        alerts.Sort(CompareThermalReading);
        return alerts;
    }

    private void UpdateThermalCriticalStates(List<ThermalReading> readings, DateTime now, bool instantCritical)
    {
        if (readings == null)
        {
            return;
        }

        HashSet<string> activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < readings.Count; i++)
        {
            ThermalReading reading = readings[i];
            if (reading == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(reading.Name))
            {
                continue;
            }

            activeNames.Add(reading.Name);
            if (reading.Celsius >= 95.0)
            {
                DateTime since;
                if (!this.thermalCriticalSinceUtc.TryGetValue(reading.Name, out since))
                {
                    since = instantCritical ? now.AddSeconds(-3.0) : now;
                    this.thermalCriticalSinceUtc[reading.Name] = since;
                }

                reading.CriticalActive = (now - since).TotalSeconds >= 3.0;
            }
            else
            {
                this.thermalCriticalSinceUtc.Remove(reading.Name);
                reading.CriticalActive = false;
            }
        }

        List<string> stale = new List<string>();
        foreach (string name in this.thermalCriticalSinceUtc.Keys)
        {
            if (!activeNames.Contains(name))
            {
                stale.Add(name);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            this.thermalCriticalSinceUtc.Remove(stale[i]);
        }
    }

    private static int CompareThermalReading(ThermalReading left, ThermalReading right)
    {
        int value = right.Celsius.CompareTo(left.Celsius);
        if (value != 0)
        {
            return value;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ThermalReading> ReadThermalReadings()
    {
        List<ThermalReading> readings = new List<ThermalReading>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(GetManagementValue(item, "Name"));
                    double celsius = ConvertThermalZoneCelsius(
                        GetManagementValue(item, "Temperature"),
                        GetManagementValue(item, "HighPrecisionTemperature"));
                    if (string.IsNullOrEmpty(name) || celsius <= 0.0)
                    {
                        continue;
                    }

                    readings.Add(new ThermalReading
                    {
                        Name = name.Trim(),
                        Celsius = celsius,
                        CriticalActive = false
                    });
                }
            }
        }
        catch
        {
        }

        return readings;
    }

    private List<ThermalReading> BuildSimulatedThermalReadings(ThermalTestMode mode)
    {
        double celsius = mode == ThermalTestMode.Simulate100 ? 100.0 : 75.0;
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0 || this.cachedThermalReadings.Count == 0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
        }

        List<ThermalReading> readings = new List<ThermalReading>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            string name = this.cachedThermalReadings[i].Name;
            if (string.IsNullOrEmpty(name) || !usedNames.Add(name))
            {
                continue;
            }

            readings.Add(new ThermalReading
            {
                Name = name,
                Celsius = celsius,
                CriticalActive = false
            });
        }

        if (readings.Count > 0)
        {
            return readings;
        }

        for (int i = 0; i < 6; i++)
        {
            readings.Add(new ThermalReading
            {
                Name = @"\_SB.TZ" + i.ToString(),
                Celsius = celsius,
                CriticalActive = false
            });
        }

        return readings;
    }

    private static double ConvertThermalZoneCelsius(object temperature, object highPrecisionTemperature)
    {
        double highPrecision = ToPositiveDouble(highPrecisionTemperature);
        if (highPrecision > 0.0)
        {
            return highPrecision / 10.0 - 273.15;
        }

        double standard = ToPositiveDouble(temperature);
        if (standard > 0.0)
        {
            return standard - 273.15;
        }

        return 0.0;
    }

    private static PowerReading ReadPowerReading()
    {
        PowerReading reading = new PowerReading();
        try
        {
            PowerLineStatus lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                reading.StatusKnown = true;
                reading.IsCharging = lineStatus == PowerLineStatus.Online;
            }
        }
        catch
        {
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    double chargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "ChargeRate"));
                    double dischargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "DischargeRate"));
                    object charging = GetManagementValue(item, "Charging");
                    object discharging = GetManagementValue(item, "Discharging");
                    object powerOnline = GetManagementValue(item, "PowerOnline");

                    if (chargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = true;
                        reading.WattsKnown = true;
                        reading.Watts = chargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (dischargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        reading.WattsKnown = true;
                        reading.Watts = dischargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (charging != null)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = Convert.ToBoolean(charging);
                    }

                    if (discharging != null && Convert.ToBoolean(discharging))
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                    }

                    if (powerOnline != null)
                    {
                        reading.StatusKnown = true;
                        if (!Convert.ToBoolean(powerOnline))
                        {
                            reading.IsCharging = false;
                        }
                    }

                    return reading;
                }
            }
        }
        catch
        {
        }

        return reading;
    }

    private static object GetManagementValue(ManagementBaseObject item, string name)
    {
        try
        {
            PropertyData property = item.Properties[name];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static double ToPositiveDouble(object value)
    {
        if (value == null)
        {
            return 0.0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            return number > 0.0 ? number : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static double ToPositiveMilliwatts(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            if (number <= 0 || number >= 4294967294.0)
            {
                return 0;
            }

            return number;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatWatts(double watts)
    {
        if (watts >= 100.0)
        {
            return watts.ToString("0") + " W";
        }

        return watts.ToString("0.0") + " W";
    }

    private static string GetOrdinalSuffix(int day)
    {
        int lastTwo = day % 100;
        if (lastTwo >= 11 && lastTwo <= 13)
        {
            return "th";
        }

        switch (day % 10)
        {
            case 1:
                return "st";
            case 2:
                return "nd";
            case 3:
                return "rd";
            default:
                return "th";
        }
    }

    private void RenderLayeredWindow()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawClockBackground(g);
                DrawClockContentLayer(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap, GetApplicationOpacityAlpha()))
                {
                    if (!this.layeredUpdateFailureLogged)
                    {
                        this.layeredUpdateFailureLogged = true;
                        Program.LogInfo("Clock UpdateLayeredWindow failed; falling back to normal paint.");
                    }

                    this.Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.BackgroundTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!this.currentSettings.HoverOpacityEnabled || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    private int S(int value)
    {
        return (int)Math.Round(value * this.scale);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DockItem
{
    public DockItem(string label, string command)
    {
        this.Label = label ?? string.Empty;
        this.Command = command ?? string.Empty;
    }

    public string Label { get; private set; }
    public string Command { get; private set; }

    public static List<DockItem> ParseItems(string text)
    {
        List<DockItem> items = new List<DockItem>();
        if (string.IsNullOrEmpty(text))
        {
            return items;
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split(new char[] { '\n' }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string label;
            string command;
            int split = line.IndexOf('|');
            if (split >= 0)
            {
                label = line.Substring(0, split).Trim();
                command = line.Substring(split + 1).Trim();
            }
            else
            {
                command = line;
                label = BuildLabelFromCommand(command);
            }

            if (command.Length == 0)
            {
                continue;
            }

            if (label.Length == 0)
            {
                label = BuildLabelFromCommand(command);
            }

            items.Add(new DockItem(label, command));
        }

        return items;
    }

    private static string BuildLabelFromCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return "App";
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim().Trim('"');
        try
        {
            string fileName = Path.GetFileNameWithoutExtension(value);
            if (!string.IsNullOrEmpty(fileName))
            {
                return fileName;
            }
        }
        catch
        {
        }

        int colon = value.IndexOf(':');
        if (colon > 1)
        {
            return value.Substring(0, colon);
        }

        return value.Length > 0 ? value : "App";
    }
}

internal sealed class DockForm : Form
{
    private const int QuotaTailChunkBytes = 1024 * 1024;
    private const int MaxQuotaRolloutFilesToScan = 80;
    private const int MaxMediaThumbnailBytes = 4 * 1024 * 1024;
    private const double MediaTitleFlipCycleMs = 5000.0;
    private const double MediaTitleFlipDurationMs = 210.0;
    private const double DockResizeAnimationMs = 210.0;
    private const double DockItemEnterAnimationMs = 210.0;
    private const double DockItemExitAnimationMs = 190.0;
    private const int DockIdleTimerIntervalMs = 250;
    private const int DockActiveTimerIntervalMs = 16;
    private readonly Action openSettingsAction;
    private readonly Action exitAction;
    private readonly System.Windows.Forms.Timer timer;
    private readonly List<DockRuntimeItem> runtimeItems;
    private DockPreviewForm previewForm;
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private bool suppressDockSizeChangedRender;
    private Point lastCursorPosition;
    private string loadedItemsText;
    private string loadedRunningSignature;
    private DateTime lastRunningRefreshUtc;
    private DateTime lastMediaRefreshUtc;
    private DateTime lastMediaAnimationRenderUtc;
    private DateTime mediaButtonPressAnimationUtc;
    private DateTime lastQuotaRefreshUtc;
    private IntPtr lastForegroundWindow;
    private DockMediaInfo mediaInfo;
    private DockQuotaSnapshot quotaSnapshot;
    private bool mediaRefreshInFlight;
    private bool mediaSessionUnavailableLogged;
    private int previewItemIndex;
    private int runtimePinnedCount;
    private RectangleF mediaPreviousRect;
    private RectangleF mediaPlayPauseRect;
    private RectangleF mediaNextRect;
    private Bitmap mediaAppBitmap;
    private Rectangle mediaAppBitmapVisibleBounds;
    private string mediaAppIconKey;
    private bool mediaBitmapIsArtwork;
    private bool mediaTitleOverflow;
    private int mediaButtonPressKind;
    private int mediaTitlePageCount;
    private int mediaTitlePageIndex;
    private MediaTitleLayoutCache mediaTitleLayoutCache;
    private bool dockResizeAnimating;
    private bool startPendingEntriesAfterResize;
    private DateTime dockResizeStartedUtc;
    private Size dockResizeStartSize;
    private Size dockResizeTargetSize;
    private float dockVisualWidth;

    private enum DockItemAnimationState
    {
        Normal,
        EnteringPending,
        Entering,
        Exiting
    }

    private sealed class MediaTitleLayoutCache
    {
        public string Title { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public float FontSize { get; set; }
        public bool FitsSingleLine { get; set; }
        public List<string> Pages { get; set; }
    }

    private sealed class DockRuntimeItem : IDisposable
    {
        public DockItem Item { get; set; }
        public Bitmap Bitmap { get; set; }
        public bool IsRunning { get; set; }
        public bool IsFocused { get; set; }
        public int InstanceCount { get; set; }
        public IntPtr WindowHandle { get; set; }
        public int ProcessId { get; set; }
        public string ExecutablePath { get; set; }
        public string WindowTitle { get; set; }
        public string AnimationKey { get; set; }
        public DockItemAnimationState AnimationState { get; set; }
        public DateTime AnimationStartedUtc { get; set; }

        public void Dispose()
        {
            if (this.Bitmap != null)
            {
                this.Bitmap.Dispose();
                this.Bitmap = null;
            }
        }
    }

    private sealed class DockLayout
    {
        public RectangleF[] SystemButtonRects { get; set; }
        public RectangleF[] ItemRects { get; set; }
        public RectangleF SeparatorRect { get; set; }
        public RectangleF MediaRect { get; set; }
        public RectangleF QuotaRect { get; set; }
        public RectangleF PreviousRect { get; set; }
        public RectangleF PlayPauseRect { get; set; }
        public RectangleF NextRect { get; set; }
    }

    private sealed class DockMediaInfo
    {
        public string SourceAppUserModelId { get; set; }
        public string AppName { get; set; }
        public string IconPath { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public byte[] ThumbnailBytes { get; set; }
        public string ThumbnailKey { get; set; }
        public bool HasSession { get; set; }
        public bool IsPlaying { get; set; }

        public static DockMediaInfo Empty()
        {
            return new DockMediaInfo
            {
                SourceAppUserModelId = string.Empty,
                AppName = string.Empty,
                IconPath = string.Empty,
                Title = string.Empty,
                Artist = string.Empty,
                ThumbnailBytes = null,
                ThumbnailKey = string.Empty,
                HasSession = false,
                IsPlaying = false
            };
        }

        public bool SameContent(DockMediaInfo other)
        {
            if (other == null)
            {
                return false;
            }

            return string.Equals(this.SourceAppUserModelId, other.SourceAppUserModelId, StringComparison.Ordinal) &&
                   string.Equals(this.AppName, other.AppName, StringComparison.Ordinal) &&
                   string.Equals(this.IconPath, other.IconPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(this.Title, other.Title, StringComparison.Ordinal) &&
                   string.Equals(this.Artist, other.Artist, StringComparison.Ordinal) &&
                   string.Equals(this.ThumbnailKey, other.ThumbnailKey, StringComparison.Ordinal) &&
                   this.HasSession == other.HasSession &&
                   this.IsPlaying == other.IsPlaying;
        }
    }

    private sealed class DockQuotaSnapshot
    {
        public int FiveHourPercent { get; set; }
        public int WeeklyPercent { get; set; }
        public DateTime FiveHourResetLocal { get; set; }
        public DateTime WeeklyResetLocal { get; set; }

        public static DockQuotaSnapshot CreateDefault()
        {
            DateTime now = DateTime.Now;
            DateTime nextWeek = now.Date.AddDays(7);
            return new DockQuotaSnapshot
            {
                FiveHourPercent = 100,
                WeeklyPercent = 100,
                FiveHourResetLocal = now.AddHours(5),
                WeeklyResetLocal = nextWeek
            };
        }
    }

    private sealed class DockQuotaEvent
    {
        public DockQuotaSnapshot Snapshot { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public DockForm(WidgetSettings settings, Action openSettingsAction, Action exitAction)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.openSettingsAction = openSettingsAction;
        this.exitAction = exitAction;
        this.runtimeItems = new List<DockRuntimeItem>();
        this.lastCursorPosition = Point.Empty;
        this.mediaInfo = DockMediaInfo.Empty();
        this.mediaAppIconKey = string.Empty;
        this.mediaButtonPressKind = -1;
        this.mediaTitlePageIndex = -1;
        this.quotaSnapshot = DockQuotaSnapshot.CreateDefault();
        this.previewItemIndex = -1;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(18, 19, 22);
        this.ContextMenuStrip = BuildContextMenu();
        this.Size = GetDesiredDockSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = DockIdleTimerIntervalMs;
        this.timer.Tick += OnTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        EnsurePreviewForm();
        this.timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        ClosePreviewForm();
        DisposeMediaAppBitmap();
        DisposeRuntimeItems();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(24)))
        {
            this.Region = new Region(path);
        }

        if (this.suppressDockSizeChangedRender)
        {
            return;
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionDock();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdatePreviewFromCursor(Cursor.Position);
        RenderLayeredWindow();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdatePreviewFromCursor(Cursor.Position);
        RenderLayeredWindow();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        PointF point = new PointF(e.X, e.Y);
        int systemButtonIndex = FindSystemButtonAtPoint(point);
        if (systemButtonIndex >= 0)
        {
            HidePreview();
            ActivateSystemButton(systemButtonIndex);
            return;
        }

        if (HandleMediaClick(point))
        {
            HidePreview();
            return;
        }

        int index = FindItemAtPoint(point);
        if (index >= 0 && index < this.runtimeItems.Count)
        {
            HidePreview();
            ActivateOrLaunchItem(this.runtimeItems[index]);
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        string oldItemsText = this.loadedItemsText;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();

        if (!string.Equals(oldItemsText, this.currentSettings.DockItemsText, StringComparison.Ordinal))
        {
            RebuildItems();
        }

        Size desiredSize = GetDesiredDockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionDock();
        RenderLayeredWindow();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            HidePreview();
            if (this.Visible)
            {
                this.Hide();
            }

            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionDock();
        RenderLayeredWindow();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("设置...", null, delegate
        {
            if (this.openSettingsAction != null)
            {
                this.openSettingsAction();
            }
        });
        menu.Items.Add("刷新 Dock", null, delegate
        {
            RebuildItems();
            PositionDock();
            RenderLayeredWindow();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate
        {
            if (this.exitAction != null)
            {
                this.exitAction();
            }
        });
        return menu;
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        bool changed = RefreshRunningItemsIfNeeded();
        RefreshMediaInfoIfNeeded();
        RefreshQuotaInfoIfNeeded();
        UpdatePreviewFromCursor(Cursor.Position);
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindowHandle();
        bool foregroundChanged = foregroundWindow != this.lastForegroundWindow;
        if (foregroundChanged)
        {
            this.lastForegroundWindow = foregroundWindow;
            UpdateFocusedRuntimeItems(foregroundWindow);
        }

        Point cursor = Cursor.Position;
        bool wasInside = this.Bounds.Contains(this.lastCursorPosition);
        bool isInside = this.Bounds.Contains(cursor);
        DateTime now = DateTime.UtcNow;
        bool dockAnimationChanged = UpdateDockAnimations(now);
        bool pressAnimationActive = this.mediaButtonPressKind >= 0 &&
            (now - this.mediaButtonPressAnimationUtc).TotalMilliseconds < 190.0;
        bool clearPressAnimation = this.mediaButtonPressKind >= 0 && !pressAnimationActive;
        if (clearPressAnimation)
        {
            this.mediaButtonPressKind = -1;
        }

        bool titleFlipActive = false;
        if (this.mediaTitleOverflow && this.mediaTitlePageCount > 1)
        {
            int titlePageIndex = GetMediaTitlePageIndex(this.mediaTitlePageCount, now);
            double titlePhase = GetMediaTitleFlipPhase(now);
            titleFlipActive =
                titlePageIndex != this.mediaTitlePageIndex ||
                titlePhase < MediaTitleFlipDurationMs + 40.0;
        }

        bool shouldAnimateMedia =
            (titleFlipActive || pressAnimationActive) &&
            (now - this.lastMediaAnimationRenderUtc).TotalMilliseconds >= DockActiveTimerIntervalMs;
        if (shouldAnimateMedia)
        {
            this.lastMediaAnimationRenderUtc = now;
        }

        UpdateDockTimerInterval(now, dockAnimationChanged || pressAnimationActive || titleFlipActive);

        if (changed || foregroundChanged || cursor != this.lastCursorPosition || wasInside != isInside || shouldAnimateMedia || clearPressAnimation || dockAnimationChanged)
        {
            this.lastCursorPosition = cursor;
            RenderLayeredWindow();
        }
    }

    private void UpdateDockTimerInterval(DateTime now, bool activeAnimation)
    {
        SetDockTimerInterval(GetPreferredDockTimerInterval(now, activeAnimation));
    }

    private int GetPreferredDockTimerInterval(DateTime now, bool activeAnimation)
    {
        if (activeAnimation || HasRuntimeItemAnimation())
        {
            return DockActiveTimerIntervalMs;
        }

        if (this.mediaTitleOverflow && this.mediaTitlePageCount > 1)
        {
            double phase = GetMediaTitleFlipPhase(now);
            double activeWindow = MediaTitleFlipDurationMs + 40.0;
            if (phase < activeWindow)
            {
                return DockActiveTimerIntervalMs;
            }

            double untilNextFlip = MediaTitleFlipCycleMs - phase;
            if (untilNextFlip < DockIdleTimerIntervalMs)
            {
                return Math.Max(DockActiveTimerIntervalMs, (int)Math.Ceiling(untilNextFlip));
            }
        }

        return DockIdleTimerIntervalMs;
    }

    private bool HasRuntimeItemAnimation()
    {
        if (this.dockResizeAnimating)
        {
            return true;
        }

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockItemAnimationState state = this.runtimeItems[i].AnimationState;
            if (state == DockItemAnimationState.Entering ||
                state == DockItemAnimationState.Exiting ||
                state == DockItemAnimationState.EnteringPending)
            {
                return true;
            }
        }

        return false;
    }

    private void SetDockTimerInterval(int interval)
    {
        interval = Math.Max(1, interval);
        if (this.timer != null && this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private void RebuildItems()
    {
        RebuildItems(GetRunningWindowInfos());
    }

    private void RebuildItems(List<NativeMethods.ApplicationWindowInfo> runningWindows)
    {
        int pinnedCount;
        List<DockRuntimeItem> rebuiltItems = BuildRuntimeItems(runningWindows, out pinnedCount);
        this.loadedItemsText = this.currentSettings.DockItemsText;
        this.loadedRunningSignature = BuildRunningSignature(runningWindows);
        this.lastRunningRefreshUtc = DateTime.UtcNow;
        this.runtimePinnedCount = pinnedCount;
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindowHandle();
        this.lastForegroundWindow = foregroundWindow;

        DisposeRuntimeItems();
        this.runtimeItems.AddRange(rebuiltItems);
        ResetDockItemAnimations();
        UpdateFocusedRuntimeItems(foregroundWindow);
    }

    private List<DockRuntimeItem> BuildRuntimeItems(List<NativeMethods.ApplicationWindowInfo> runningWindows, out int pinnedCount)
    {
        List<DockRuntimeItem> runtimeItems = new List<DockRuntimeItem>();
        pinnedCount = 0;
        List<DockItem> pinnedItems = DockItem.ParseItems(this.currentSettings.DockItemsText);
        int count = Math.Min(pinnedItems.Count, 24);
        for (int i = 0; i < count; i++)
        {
            DockRuntimeItem runtimeItem = new DockRuntimeItem();
            runtimeItem.Item = pinnedItems[i];
            runtimeItem.Bitmap = LoadItemBitmap(pinnedItems[i]);
            runtimeItem.IsRunning = false;
            runtimeItem.IsFocused = false;
            runtimeItem.InstanceCount = 0;
            runtimeItem.WindowHandle = IntPtr.Zero;
            runtimeItem.ProcessId = 0;
            runtimeItem.ExecutablePath = ResolveExecutablePath(pinnedItems[i].Command);
            runtimeItem.WindowTitle = string.Empty;
            runtimeItem.AnimationKey = BuildPinnedAnimationKey(i, pinnedItems[i]);
            runtimeItem.AnimationState = DockItemAnimationState.Normal;
            runtimeItems.Add(runtimeItem);
            pinnedCount++;
        }

        int runningCount = 0;
        for (int i = 0; i < runningWindows.Count && runningCount < 24; i++)
        {
            NativeMethods.ApplicationWindowInfo window = runningWindows[i];
            string executablePath = GetProcessExecutablePath(window.ProcessId);
            if (TryMarkPinnedRunning(runtimeItems, pinnedCount, executablePath, window))
            {
                continue;
            }

            if (TryIncrementRunningProcess(runtimeItems, pinnedCount, window.ProcessId, executablePath, window))
            {
                continue;
            }

            string label = BuildRunningLabel(window, executablePath);
            DockRuntimeItem runtimeItem = new DockRuntimeItem();
            runtimeItem.Item = new DockItem(label, executablePath);
            runtimeItem.Bitmap = LoadItemBitmap(executablePath, label);
            runtimeItem.IsRunning = true;
            runtimeItem.IsFocused = false;
            runtimeItem.InstanceCount = 1;
            runtimeItem.WindowHandle = window.Handle;
            runtimeItem.ProcessId = window.ProcessId;
            runtimeItem.ExecutablePath = executablePath;
            runtimeItem.WindowTitle = window.Title;
            runtimeItem.AnimationKey = BuildRunningAnimationKey(runtimeItem);
            runtimeItem.AnimationState = DockItemAnimationState.Normal;
            runtimeItems.Add(runtimeItem);
            runningCount++;
        }

        return runtimeItems;
    }

    private static string BuildPinnedAnimationKey(int index, DockItem item)
    {
        string command = item == null ? string.Empty : (item.Command ?? string.Empty);
        return "P|" + index.ToString(CultureInfo.InvariantCulture) + "|" + command.ToUpperInvariant();
    }

    private static string BuildRunningAnimationKey(DockRuntimeItem item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(item.ExecutablePath))
        {
            return "R|" + item.ExecutablePath.ToUpperInvariant();
        }

        if (item.ProcessId > 0)
        {
            return "R|PID|" + item.ProcessId.ToString(CultureInfo.InvariantCulture);
        }

        if (item.WindowHandle != IntPtr.Zero)
        {
            return "R|HWND|" + item.WindowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        }

        return "R|" + (item.Item == null ? string.Empty : item.Item.Label ?? string.Empty).ToUpperInvariant();
    }

    private void ResetDockItemAnimations()
    {
        this.dockResizeAnimating = false;
        this.startPendingEntriesAfterResize = false;
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            this.runtimeItems[i].AnimationState = DockItemAnimationState.Normal;
            this.runtimeItems[i].AnimationStartedUtc = DateTime.MinValue;
        }
    }

    private bool RefreshRunningItemsIfNeeded()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.lastRunningRefreshUtc).TotalSeconds < 2.0)
        {
            return false;
        }

        List<NativeMethods.ApplicationWindowInfo> runningWindows = GetRunningWindowInfos();
        string signature = BuildRunningSignature(runningWindows);
        this.lastRunningRefreshUtc = now;
        if (string.Equals(signature, this.loadedRunningSignature, StringComparison.Ordinal))
        {
            return false;
        }

        int pinnedCount;
        List<DockRuntimeItem> rebuiltItems = BuildRuntimeItems(runningWindows, out pinnedCount);
        ApplyAnimatedRuntimeItems(rebuiltItems, pinnedCount, signature);
        HidePreview();
        return true;
    }

    private void ApplyAnimatedRuntimeItems(List<DockRuntimeItem> rebuiltItems, int newPinnedCount, string signature)
    {
        DateTime now = DateTime.UtcNow;
        Dictionary<string, DockRuntimeItem> rebuiltByKey = new Dictionary<string, DockRuntimeItem>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rebuiltItems.Count; i++)
        {
            DockRuntimeItem item = rebuiltItems[i];
            if (!string.IsNullOrEmpty(item.AnimationKey) && !rebuiltByKey.ContainsKey(item.AnimationKey))
            {
                rebuiltByKey.Add(item.AnimationKey, item);
            }
        }

        HashSet<string> usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DockRuntimeItem> mergedItems = new List<DockRuntimeItem>();
        bool hasEntering = false;
        bool hasExiting = false;

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockRuntimeItem oldItem = this.runtimeItems[i];
            DockRuntimeItem rebuiltItem;
            if (!string.IsNullOrEmpty(oldItem.AnimationKey) &&
                rebuiltByKey.TryGetValue(oldItem.AnimationKey, out rebuiltItem) &&
                !usedKeys.Contains(oldItem.AnimationKey))
            {
                TransferRuntimeBitmap(oldItem, rebuiltItem);
                if (oldItem.AnimationState == DockItemAnimationState.Entering ||
                    oldItem.AnimationState == DockItemAnimationState.EnteringPending)
                {
                    rebuiltItem.AnimationState = oldItem.AnimationState;
                    rebuiltItem.AnimationStartedUtc = oldItem.AnimationStartedUtc;
                }
                else
                {
                    rebuiltItem.AnimationState = DockItemAnimationState.Normal;
                    rebuiltItem.AnimationStartedUtc = DateTime.MinValue;
                }

                mergedItems.Add(rebuiltItem);
                usedKeys.Add(oldItem.AnimationKey);
                continue;
            }

            if (i >= this.runtimePinnedCount && oldItem.IsRunning)
            {
                if (oldItem.AnimationState != DockItemAnimationState.Exiting)
                {
                    oldItem.AnimationState = DockItemAnimationState.Exiting;
                    oldItem.AnimationStartedUtc = now;
                }

                mergedItems.Add(oldItem);
                hasExiting = true;
            }
            else
            {
                oldItem.Dispose();
            }
        }

        for (int i = 0; i < rebuiltItems.Count; i++)
        {
            DockRuntimeItem item = rebuiltItems[i];
            if (!string.IsNullOrEmpty(item.AnimationKey) && usedKeys.Contains(item.AnimationKey))
            {
                continue;
            }

            item.AnimationState = i >= newPinnedCount ? DockItemAnimationState.EnteringPending : DockItemAnimationState.Normal;
            item.AnimationStartedUtc = DateTime.MinValue;
            if (item.AnimationState == DockItemAnimationState.EnteringPending)
            {
                hasEntering = true;
            }

            mergedItems.Add(item);
        }

        this.runtimeItems.Clear();
        this.runtimeItems.AddRange(mergedItems);
        this.runtimePinnedCount = newPinnedCount;
        this.loadedRunningSignature = signature;
        this.lastRunningRefreshUtc = now;
        this.lastForegroundWindow = NativeMethods.GetForegroundWindowHandle();
        UpdateFocusedRuntimeItems(this.lastForegroundWindow);

        Size targetSize = GetDesiredDockSize();
        if (targetSize != this.Size)
        {
            BeginDockResize(targetSize, hasEntering);
        }
        else if (hasEntering)
        {
            StartPendingEnterAnimations(now);
        }

        if (hasExiting || hasEntering)
        {
            HidePreview();
        }
    }

    private static void TransferRuntimeBitmap(DockRuntimeItem source, DockRuntimeItem target)
    {
        if (source == null || target == null || source.Bitmap == null)
        {
            return;
        }

        if (target.Bitmap != null && !object.ReferenceEquals(target.Bitmap, source.Bitmap))
        {
            target.Bitmap.Dispose();
        }

        target.Bitmap = source.Bitmap;
        source.Bitmap = null;
    }

    private void BeginDockResize(Size targetSize, bool startEntriesAfterResize)
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        float startWidth = GetDockVisualWidth();
        if (Math.Abs(startWidth - targetSize.Width) < 0.5f)
        {
            this.dockResizeAnimating = false;
            this.dockVisualWidth = 0.0f;
            if (this.Size != targetSize)
            {
                ApplyDockBounds(targetSize, true);
            }

            if (startEntriesAfterResize)
            {
                StartPendingEnterAnimations(DateTime.UtcNow);
            }

            return;
        }

        this.dockResizeStartSize = new Size(Math.Max(1, (int)Math.Round(startWidth)), this.Height);
        this.dockResizeTargetSize = targetSize;
        this.dockVisualWidth = startWidth;
        this.dockResizeStartedUtc = DateTime.UtcNow;
        this.dockResizeAnimating = true;
        this.startPendingEntriesAfterResize = startEntriesAfterResize;
        Size outerSize = new Size(Math.Max(this.Width, targetSize.Width), Math.Max(this.Height, targetSize.Height));
        if (this.Size != outerSize)
        {
            ApplyDockBounds(outerSize, true);
        }

        SetDockTimerInterval(DockActiveTimerIntervalMs);
    }

    private bool UpdateDockAnimations(DateTime now)
    {
        bool changed = false;
        if (this.dockResizeAnimating)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - this.dockResizeStartedUtc).TotalMilliseconds / DockResizeAnimationMs));
            double eased = EaseOutCubic(progress);
            this.dockVisualWidth = (float)(this.dockResizeStartSize.Width + (this.dockResizeTargetSize.Width - this.dockResizeStartSize.Width) * eased);
            changed = true;

            if (progress >= 1.0)
            {
                this.dockResizeAnimating = false;
                this.dockVisualWidth = this.dockResizeTargetSize.Width;
                if (this.Size != this.dockResizeTargetSize)
                {
                    ApplyDockBounds(this.dockResizeTargetSize, true);
                }

                this.dockVisualWidth = 0.0f;
                if (this.startPendingEntriesAfterResize)
                {
                    this.startPendingEntriesAfterResize = false;
                    StartPendingEnterAnimations(now);
                }
            }
        }

        bool removedExitingItems = false;
        for (int i = this.runtimeItems.Count - 1; i >= 0; i--)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            if (item.AnimationState == DockItemAnimationState.Entering)
            {
                if ((now - item.AnimationStartedUtc).TotalMilliseconds >= DockItemEnterAnimationMs)
                {
                    item.AnimationState = DockItemAnimationState.Normal;
                    item.AnimationStartedUtc = DateTime.MinValue;
                    changed = true;
                }
            }
            else if (item.AnimationState == DockItemAnimationState.Exiting)
            {
                if ((now - item.AnimationStartedUtc).TotalMilliseconds >= DockItemExitAnimationMs)
                {
                    item.Dispose();
                    this.runtimeItems.RemoveAt(i);
                    removedExitingItems = true;
                    changed = true;
                }
                else
                {
                    changed = true;
                }
            }
            else if (item.AnimationState == DockItemAnimationState.EnteringPending)
            {
                changed = true;
            }
        }

        if (removedExitingItems)
        {
            Size targetSize = GetDesiredDockSize();
            if (targetSize != this.Size)
            {
                BeginDockResize(targetSize, false);
            }
        }

        return changed;
    }

    private void StartPendingEnterAnimations(DateTime now)
    {
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            if (item.AnimationState == DockItemAnimationState.EnteringPending)
            {
                item.AnimationState = DockItemAnimationState.Entering;
                item.AnimationStartedUtc = now;
                SetDockTimerInterval(DockActiveTimerIntervalMs);
            }
        }
    }

    private static double EaseOutCubic(double value)
    {
        value = Math.Max(0.0, Math.Min(1.0, value));
        double inverse = 1.0 - value;
        return 1.0 - inverse * inverse * inverse;
    }

    private static double EaseInCubic(double value)
    {
        value = Math.Max(0.0, Math.Min(1.0, value));
        return value * value * value;
    }

    private void RefreshQuotaInfoIfNeeded()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.lastQuotaRefreshUtc).TotalSeconds < 30.0)
        {
            return;
        }

        this.lastQuotaRefreshUtc = now;
        this.quotaSnapshot = ReadQuotaSnapshot();
        RenderLayeredWindow();
    }

    private static DockQuotaSnapshot ReadQuotaSnapshot()
    {
        DockQuotaSnapshot snapshot;
        if (TryReadCodexSessionQuota(out snapshot))
        {
            return snapshot;
        }

        return ReadQuotaIniSnapshot();
    }

    private static bool TryReadCodexSessionQuota(out DockQuotaSnapshot snapshot)
    {
        snapshot = null;
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profilePath))
        {
            return false;
        }

        string sessionsPath = Path.Combine(Path.Combine(profilePath, ".codex"), "sessions");
        if (!Directory.Exists(sessionsPath))
        {
            return false;
        }

        List<string> rolloutFiles = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name != null && name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                {
                    rolloutFiles.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        if (rolloutFiles.Count == 0)
        {
            return false;
        }

        rolloutFiles.Sort(delegate(string left, string right)
        {
            return SafeGetLastWriteTimeUtc(right).CompareTo(SafeGetLastWriteTimeUtc(left));
        });

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;

        DockQuotaEvent latestEvent = null;
        int count = Math.Min(rolloutFiles.Count, MaxQuotaRolloutFilesToScan);
        for (int i = 0; i < count; i++)
        {
            string file = rolloutFiles[i];
            if (latestEvent != null && SafeGetLastWriteTimeUtc(file) < latestEvent.UpdatedUtc)
            {
                break;
            }

            DockQuotaEvent quotaEvent;
            if (TryParseLatestQuotaEventFromFile(file, serializer, out quotaEvent) &&
                (latestEvent == null || quotaEvent.UpdatedUtc > latestEvent.UpdatedUtc))
            {
                latestEvent = quotaEvent;
            }
        }

        if (latestEvent == null)
        {
            return false;
        }

        snapshot = latestEvent.Snapshot;
        return snapshot != null;
    }

    private static bool TryParseLatestQuotaEventFromFile(string path, JavaScriptSerializer serializer, out DockQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long offset = stream.Length;
                byte[] tail = new byte[0];
                while (offset > 0)
                {
                    int readSize = (int)Math.Min(QuotaTailChunkBytes, offset);
                    offset -= readSize;
                    stream.Seek(offset, SeekOrigin.Begin);

                    byte[] chunk = new byte[readSize];
                    int read = stream.Read(chunk, 0, readSize);
                    if (read <= 0)
                    {
                        continue;
                    }

                    byte[] expandedTail = new byte[read + tail.Length];
                    Buffer.BlockCopy(chunk, 0, expandedTail, 0, read);
                    if (tail.Length > 0)
                    {
                        Buffer.BlockCopy(tail, 0, expandedTail, read, tail.Length);
                    }

                    tail = expandedTail;
                    string text = Encoding.UTF8.GetString(tail, 0, tail.Length);
                    if (TryParseLatestQuotaEventFromText(text, path, serializer, out quotaEvent))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryParseLatestQuotaEventFromText(string text, string path, JavaScriptSerializer serializer, out DockQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 ||
                line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                continue;
            }

            if (root == null ||
                !string.Equals(GetQuotaString(root, "type"), "event_msg", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> payload = GetQuotaObject(root, "payload");
            if (payload == null ||
                !string.Equals(GetQuotaString(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> rateLimits = GetQuotaObject(payload, "rate_limits");
            DockQuotaSnapshot snapshot;
            if (rateLimits == null || !TryBuildQuotaSnapshot(rateLimits, out snapshot))
            {
                continue;
            }

            DateTime updatedLocal;
            DateTime updatedUtc = SafeGetLastWriteTimeUtc(path);
            if (TryGetQuotaDate(root, "timestamp", out updatedLocal))
            {
                updatedUtc = updatedLocal.ToUniversalTime();
            }
            else if (updatedUtc == DateTime.MinValue)
            {
                updatedUtc = DateTime.UtcNow;
            }

            quotaEvent = new DockQuotaEvent();
            quotaEvent.Snapshot = snapshot;
            quotaEvent.UpdatedUtc = updatedUtc;
            return true;
        }

        return false;
    }

    private static bool TryBuildQuotaSnapshot(Dictionary<string, object> rateLimits, out DockQuotaSnapshot snapshot)
    {
        snapshot = DockQuotaSnapshot.CreateDefault();
        bool found = false;
        found = ApplyQuotaSlot(rateLimits, "primary", snapshot) || found;
        found = ApplyQuotaSlot(rateLimits, "secondary", snapshot) || found;
        return found;
    }

    private static bool ApplyQuotaSlot(Dictionary<string, object> rateLimits, string key, DockQuotaSnapshot snapshot)
    {
        Dictionary<string, object> slot = GetQuotaObject(rateLimits, key);
        if (slot == null)
        {
            return false;
        }

        double usedPercent;
        if (!TryGetQuotaNumber(slot, "used_percent", out usedPercent) &&
            !TryGetQuotaNumber(slot, "used_percentage", out usedPercent))
        {
            return false;
        }

        double windowMinutes;
        bool hasWindowMinutes = TryGetQuotaNumber(slot, "window_minutes", out windowMinutes);
        bool isFiveHour = string.Equals(key, "primary", StringComparison.OrdinalIgnoreCase);
        if (hasWindowMinutes)
        {
            isFiveHour = windowMinutes <= 300.0;
        }

        int remainingPercent = ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool hasReset = TryGetQuotaDate(slot, "resets_at", out resetLocal);
        if (isFiveHour)
        {
            snapshot.FiveHourPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.FiveHourResetLocal = resetLocal;
            }
        }
        else
        {
            snapshot.WeeklyPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.WeeklyResetLocal = resetLocal;
            }
        }

        return true;
    }

    private static DockQuotaSnapshot ReadQuotaIniSnapshot()
    {
        DockQuotaSnapshot snapshot = DockQuotaSnapshot.CreateDefault();
        string path = Path.Combine(Logger.DirectoryPath, "quota.ini");
        if (!File.Exists(path))
        {
            return snapshot;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
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
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.FiveHourPercent = ClampPercent(percent);
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                }
                else if (string.Equals(key, "FiveHourReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.FiveHourResetLocal = dateTime;
                }
                else if (string.Equals(key, "WeeklyReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.WeeklyResetLocal = dateTime;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return snapshot;
    }

    private static Dictionary<string, object> GetQuotaObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static string GetQuotaString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGetQuotaNumber(Dictionary<string, object> values, string key, out double number)
    {
        number = 0.0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaNumber(value, out number);
    }

    private static bool TryReadQuotaNumber(object value, out double number)
    {
        number = 0.0;
        if (value == null)
        {
            return false;
        }

        string text = value as string;
        if (text != null)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            number = 0.0;
            return false;
        }
    }

    private static bool TryGetQuotaDate(Dictionary<string, object> values, string key, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaDate(value, out localDate);
    }

    private static bool TryReadQuotaDate(object value, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        double seconds;
        if (TryReadQuotaNumber(value, out seconds))
        {
            if (seconds > 10000000000.0)
            {
                seconds /= 1000.0;
            }

            try
            {
                DateTimeOffset epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
                localDate = epoch.AddSeconds(seconds).LocalDateTime;
                return true;
            }
            catch
            {
                localDate = DateTime.MinValue;
                return false;
            }
        }

        string text = value as string;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        DateTimeOffset offsetDate;
        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out offsetDate))
        {
            localDate = offsetDate.LocalDateTime;
            return true;
        }

        DateTime dateTime;
        if (DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dateTime))
        {
            localDate = dateTime.ToLocalTime();
            return true;
        }

        return false;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private void RefreshMediaInfoIfNeeded()
    {
        RefreshMediaInfoIfNeeded(false);
    }

    private async void RefreshMediaInfoIfNeeded(bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (this.mediaRefreshInFlight || (!force && (now - this.lastMediaRefreshUtc).TotalSeconds < 2.0))
        {
            return;
        }

        this.lastMediaRefreshUtc = now;
        this.mediaRefreshInFlight = true;
        try
        {
            DockMediaInfo next = await ReadCurrentMediaInfoAsync();
            if (!this.IsDisposed)
            {
                ApplyMediaInfo(next);
            }
        }
        catch (Exception ex)
        {
            if (!this.mediaSessionUnavailableLogged)
            {
                this.mediaSessionUnavailableLogged = true;
                Program.LogException(ex);
            }

            if (!this.IsDisposed)
            {
                ApplyMediaInfo(DockMediaInfo.Empty());
            }
        }
        finally
        {
            this.mediaRefreshInFlight = false;
        }
    }

    private async void RefreshMediaInfoSoon(int delayMs)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        if (!this.IsDisposed)
        {
            RefreshMediaInfoIfNeeded(true);
        }
    }

    private void ToggleMediaPlaybackStateOptimistically()
    {
        if (this.mediaInfo == null || !this.mediaInfo.HasSession)
        {
            return;
        }

        this.mediaInfo.IsPlaying = !this.mediaInfo.IsPlaying;
        this.lastMediaRefreshUtc = DateTime.MinValue;
        RenderLayeredWindow();
    }

    private void ApplyMediaInfo(DockMediaInfo next)
    {
        if (next == null)
        {
            next = DockMediaInfo.Empty();
        }

        if (this.mediaInfo != null && this.mediaInfo.SameContent(next))
        {
            return;
        }

        UpdateMediaAppBitmap(next);
        this.mediaInfo = next;
        RenderLayeredWindow();
    }

    private void UpdateMediaAppBitmap(DockMediaInfo next)
    {
        string iconPath = next == null || !next.HasSession ? string.Empty : (next.IconPath ?? string.Empty);
        string appName = next == null || !next.HasSession ? string.Empty : (next.AppName ?? string.Empty);
        string thumbnailKey = next == null || !next.HasSession ? string.Empty : (next.ThumbnailKey ?? string.Empty);
        string iconKey = thumbnailKey + "|" + iconPath + "|" + appName;
        if (string.Equals(this.mediaAppIconKey, iconKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeMediaAppBitmap();
        this.mediaAppIconKey = iconKey;
        if (next == null || !next.HasSession)
        {
            return;
        }

        if (next.ThumbnailBytes != null && next.ThumbnailBytes.Length > 0)
        {
            this.mediaAppBitmap = LoadBitmapFromBytes(next.ThumbnailBytes);
            this.mediaBitmapIsArtwork = this.mediaAppBitmap != null;
        }

        if (this.mediaAppBitmap == null && !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            this.mediaAppBitmap = LoadItemBitmap(iconPath, appName);
        }

        if (this.mediaAppBitmap == null)
        {
            this.mediaAppBitmap = CreateFallbackIcon(string.IsNullOrEmpty(appName) ? "Media" : appName);
        }

        CacheMediaAppBitmapBounds();
    }

    private void CacheMediaAppBitmapBounds()
    {
        this.mediaAppBitmapVisibleBounds = Rectangle.Empty;
        if (this.mediaAppBitmap == null)
        {
            return;
        }

        Rectangle bounds = GetVisibleBitmapBounds(this.mediaAppBitmap);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new Rectangle(0, 0, this.mediaAppBitmap.Width, this.mediaAppBitmap.Height);
        }

        this.mediaAppBitmapVisibleBounds = bounds;
    }

    private static Bitmap LoadBitmapFromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }
        catch
        {
            return null;
        }
    }

    private void DisposeMediaAppBitmap()
    {
        if (this.mediaAppBitmap != null)
        {
            this.mediaAppBitmap.Dispose();
            this.mediaAppBitmap = null;
        }

        this.mediaAppBitmapVisibleBounds = Rectangle.Empty;
        this.mediaBitmapIsArtwork = false;
    }

    private static async Task<DockMediaInfo> ReadCurrentMediaInfoAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (manager == null)
        {
            return DockMediaInfo.Empty();
        }

        GlobalSystemMediaTransportControlsSession session = manager.GetCurrentSession();
        if (session == null)
        {
            return DockMediaInfo.Empty();
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties properties =
            await session.TryGetMediaPropertiesAsync();
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
        DockMediaInfo info = new DockMediaInfo();
        info.HasSession = true;
        info.IsPlaying = playbackInfo != null &&
            playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        info.SourceAppUserModelId = CleanMediaText(session.SourceAppUserModelId);
        info.AppName = BuildMediaAppName(info.SourceAppUserModelId);
        info.IconPath = ResolveMediaIconPath(info.SourceAppUserModelId, info.AppName);
        info.Title = properties == null ? string.Empty : CleanMediaText(properties.Title);
        info.Artist = properties == null ? string.Empty : CleanMediaText(properties.Artist);
        info.ThumbnailBytes = await ReadMediaThumbnailBytesAsync(properties);
        info.ThumbnailKey = BuildMediaThumbnailKey(info.ThumbnailBytes);

        if (string.IsNullOrEmpty(info.AppName))
        {
            info.AppName = "Media";
        }

        return info;
    }

    private static async Task<byte[]> ReadMediaThumbnailBytesAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (properties == null || properties.Thumbnail == null)
        {
            return null;
        }

        try
        {
            using (Windows.Storage.Streams.IRandomAccessStreamWithContentType thumbnailStream = await properties.Thumbnail.OpenReadAsync())
            using (Stream stream = thumbnailStream.AsStreamForRead())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (memory.Length + read > MaxMediaThumbnailBytes)
                    {
                        return null;
                    }

                    memory.Write(buffer, 0, read);
                }

                return memory.Length == 0 ? null : memory.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

    private static string BuildMediaThumbnailKey(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619;
            }

            return bytes.Length.ToString(CultureInfo.InvariantCulture) + "-" + hash.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private void DisposeRuntimeItems()
    {
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            this.runtimeItems[i].Dispose();
        }

        this.runtimeItems.Clear();
    }

    private List<NativeMethods.ApplicationWindowInfo> GetRunningWindowInfos()
    {
        return NativeMethods.EnumerateApplicationWindows(this.Handle);
    }

    private void UpdateFocusedRuntimeItems(IntPtr foregroundWindow)
    {
        int foregroundProcessId = 0;
        string foregroundExecutablePath = string.Empty;
        if (foregroundWindow != IntPtr.Zero && NativeMethods.TryGetWindowProcessId(foregroundWindow, out foregroundProcessId))
        {
            foregroundExecutablePath = GetProcessExecutablePath(foregroundProcessId);
        }

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            bool isFocused = false;
            if (item.IsRunning && foregroundWindow != IntPtr.Zero)
            {
                isFocused = item.WindowHandle == foregroundWindow;
                if (!isFocused && foregroundProcessId > 0 && item.ProcessId == foregroundProcessId)
                {
                    isFocused = true;
                }

                if (!isFocused &&
                    !string.IsNullOrEmpty(foregroundExecutablePath) &&
                    !string.IsNullOrEmpty(item.ExecutablePath) &&
                    string.Equals(item.ExecutablePath, foregroundExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    isFocused = true;
                }
            }

            item.IsFocused = isFocused;
        }
    }

    private static string BuildRunningSignature(List<NativeMethods.ApplicationWindowInfo> runningWindows)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < runningWindows.Count; i++)
        {
            builder.Append(runningWindows[i].ProcessId);
            builder.Append(':');
            builder.Append(runningWindows[i].Handle.ToInt64().ToString("X"));
            builder.Append(';');
        }

        return builder.ToString();
    }

    private bool TryMarkPinnedRunning(List<DockRuntimeItem> items, int pinnedCount, string executablePath, NativeMethods.ApplicationWindowInfo window)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        for (int i = 0; i < pinnedCount && i < items.Count; i++)
        {
            string pinnedPath = items[i].ExecutablePath;
            if (!string.IsNullOrEmpty(pinnedPath) &&
                string.Equals(pinnedPath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                DockRuntimeItem item = items[i];
                item.IsRunning = true;
                item.InstanceCount++;
                if (item.WindowHandle == IntPtr.Zero)
                {
                    item.WindowHandle = window.Handle;
                    item.ProcessId = window.ProcessId;
                    item.WindowTitle = window.Title;
                }
                else if (string.IsNullOrEmpty(item.WindowTitle))
                {
                    item.WindowTitle = window.Title;
                }

                item.ExecutablePath = executablePath;
                return true;
            }
        }

        return false;
    }

    private bool TryIncrementRunningProcess(List<DockRuntimeItem> items, int pinnedCount, int processId, string executablePath, NativeMethods.ApplicationWindowInfo window)
    {
        for (int i = pinnedCount; i < items.Count; i++)
        {
            DockRuntimeItem item = items[i];
            if (!item.IsRunning)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(executablePath) &&
                !string.IsNullOrEmpty(item.ExecutablePath) &&
                string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                item.InstanceCount++;
                if (item.WindowHandle == IntPtr.Zero && window != null)
                {
                    item.WindowHandle = window.Handle;
                    item.ProcessId = window.ProcessId;
                    item.WindowTitle = window.Title;
                }
                else if (window != null && string.IsNullOrEmpty(item.WindowTitle))
                {
                    item.WindowTitle = window.Title;
                }

                return true;
            }

            if (processId > 0 && item.ProcessId == processId)
            {
                item.InstanceCount++;
                if (item.WindowHandle == IntPtr.Zero && window != null)
                {
                    item.WindowHandle = window.Handle;
                    item.WindowTitle = window.Title;
                }
                else if (window != null && string.IsNullOrEmpty(item.WindowTitle))
                {
                    item.WindowTitle = window.Title;
                }

                return true;
            }
        }

        return false;
    }

    private static string BuildRunningLabel(NativeMethods.ApplicationWindowInfo window, string executablePath)
    {
        if (window != null && !string.IsNullOrEmpty(window.Title))
        {
            return window.Title.Trim();
        }

        if (!string.IsNullOrEmpty(executablePath))
        {
            string name = Path.GetFileNameWithoutExtension(executablePath);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        if (window != null && window.ProcessId > 0)
        {
            try
            {
                using (Process process = Process.GetProcessById(window.ProcessId))
                {
                    if (!string.IsNullOrEmpty(process.ProcessName))
                    {
                        return process.ProcessName;
                    }
                }
            }
            catch
            {
            }
        }

        return "App";
    }

    private static string ResolveMediaIconPath(string sourceAppUserModelId, string appName)
    {
        string source = CleanMediaText(sourceAppUserModelId);
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        string fileName;
        string arguments;
        SplitCommandLine(source, out fileName, out arguments);
        string resolved = ResolveExecutablePath(fileName);
        if (!string.IsNullOrEmpty(resolved))
        {
            return resolved;
        }

        string packagePrefix = source;
        int bang = packagePrefix.IndexOf('!');
        if (bang > 0)
        {
            packagePrefix = packagePrefix.Substring(0, bang);
        }

        string packageFamily = packagePrefix;
        int underscore = packageFamily.IndexOf('_');
        if (underscore > 0)
        {
            packageFamily = packageFamily.Substring(0, underscore);
        }

        string fromRunningProcess = FindRunningExecutableForMediaApp(packagePrefix, packageFamily, appName);
        if (!string.IsNullOrEmpty(fromRunningProcess))
        {
            return fromRunningProcess;
        }

        if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveExecutablePath(source);
        }

        return string.Empty;
    }

    private static string FindRunningExecutableForMediaApp(string packagePrefix, string packageFamily, string appName)
    {
        string normalizedAppName = CleanProcessName(appName);
        try
        {
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    string path = string.Empty;
                    try
                    {
                        if (process.MainModule != null)
                        {
                            path = process.MainModule.FileName;
                        }
                    }
                    catch
                    {
                    }

                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(packagePrefix) &&
                        path.IndexOf(packagePrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return path;
                    }

                    if (!string.IsNullOrEmpty(packageFamily) &&
                        path.IndexOf(packageFamily, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return path;
                    }

                    string processName = CleanProcessName(process.ProcessName);
                    if (!string.IsNullOrEmpty(normalizedAppName) &&
                        string.Equals(processName, normalizedAppName, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string CleanProcessName(string value)
    {
        value = CleanMediaText(value);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileNameWithoutExtension(value);
        if (!string.IsNullOrEmpty(fileName))
        {
            value = fileName;
        }

        return value.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
    }

    private static string BuildMediaAppName(string sourceAppUserModelId)
    {
        string value = CleanMediaText(sourceAppUserModelId);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int bang = value.IndexOf('!');
        if (bang > 0)
        {
            value = value.Substring(0, bang);
        }

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(value);
            if (!string.IsNullOrEmpty(fileName) && !string.Equals(fileName, value, StringComparison.OrdinalIgnoreCase))
            {
                value = fileName;
            }
        }
        catch
        {
        }

        int underscore = value.IndexOf('_');
        if (underscore > 0)
        {
            value = value.Substring(0, underscore);
        }

        int dot = value.LastIndexOf('.');
        if (dot >= 0 && dot < value.Length - 1)
        {
            value = value.Substring(dot + 1);
        }

        value = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return value.Length == 0 ? string.Empty : value;
    }

    private static string CleanMediaText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string BuildMediaDisplayTitle(DockMediaInfo info)
    {
        if (info == null || !info.HasSession)
        {
            return "Nothing is playing";
        }

        if (!string.IsNullOrEmpty(info.Title))
        {
            return info.Title;
        }

        return info.IsPlaying ? "Playing" : "Paused";
    }

    private static string BuildMediaDisplaySubtitle(DockMediaInfo info)
    {
        if (info == null || !info.HasSession)
        {
            return string.Empty;
        }

        return info.Artist ?? string.Empty;
    }

    private static string GetProcessExecutablePath(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                if (process.MainModule != null)
                {
                    return process.MainModule.FileName;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private void PositionDock()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        Size desiredSize = GetDesiredDockSize();
        ApplyDockBounds(desiredSize);
    }

    private void ApplyDockBounds(Size desiredSize)
    {
        ApplyDockBounds(desiredSize, false);
    }

    private void ApplyDockBounds(Size desiredSize, bool suppressSizeChangedRender)
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int left = workArea.Left + (workArea.Width - desiredSize.Width) / 2;
        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - desiredSize.Width));
        int top = workArea.Bottom - desiredSize.Height - this.currentSettings.DockBottomMargin;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - desiredSize.Height));
        if (this.Size != desiredSize)
        {
            bool previousSuppress = this.suppressDockSizeChangedRender;
            this.suppressDockSizeChangedRender = suppressSizeChangedRender || previousSuppress;
            try
            {
                this.Size = desiredSize;
            }
            finally
            {
                this.suppressDockSizeChangedRender = previousSuppress;
            }
        }

        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            desiredSize.Width,
            desiredSize.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredDockSize()
    {
        int count = this.runtimeItems.Count;
        int maxIcon = GetMaxIconSize();
        int baseItem = GetBaseDockItemSize();
        int gap = GetItemGap();
        int padX = GetDockPadding();
        int mediaWidth = GetMediaWidth(baseItem);
        int mediaGap = count > 0 ? S(8) : 0;
        int separatorSpace = HasPinnedRunningSeparator() ? S(14) : 0;
        int systemReserved = GetSystemButtonsWidth(baseItem);
        int systemSectionGap = count > 0 ? GetSystemSectionGap() : 0;
        int quotaGap = GetItemGap();
        int rightReserved = quotaGap + GetQuotaWidgetWidth(baseItem);
        int itemGaps = Math.Max(0, this.runtimePinnedCount - 1) * gap +
            Math.Max(0, count - this.runtimePinnedCount - 1) * gap;
        int width = padX * 2 + systemReserved + systemSectionGap + count * maxIcon + itemGaps + separatorSpace + mediaGap + mediaWidth + rightReserved;
        int maxWidth = Math.Max(S(260), Screen.PrimaryScreen.WorkingArea.Width - S(16));
        width = Math.Min(width, maxWidth);
        int height = Math.Max(maxIcon + GetDockPadding() * 2, GetMediaHeight(baseItem) + GetDockPadding() * 2);
        return new Size(Math.Max(S(150), width), Math.Max(S(34), height));
    }

    private int GetBaseDockItemSize()
    {
        return Math.Max(S(24), this.currentSettings.DockIconSize);
    }

    private int GetMaxIconSize()
    {
        double multiplier = Math.Max(1.0, this.currentSettings.DockMagnificationPercent / 100.0);
        int size = (int)Math.Round(this.currentSettings.DockIconSize * multiplier);
        return Math.Max(S(28), size);
    }

    private int GetItemGap()
    {
        return S(8);
    }

    private int GetSystemButtonCount()
    {
        return 2;
    }

    private int GetSystemButtonsWidth(float itemSize)
    {
        int count = GetSystemButtonCount();
        if (count <= 0)
        {
            return 0;
        }

        return (int)Math.Round(count * itemSize + Math.Max(0, count - 1) * GetItemGap());
    }

    private int GetQuotaWidgetWidth(float itemSize)
    {
        return GetSystemButtonsWidth(itemSize);
    }

    private int GetSystemSectionGap()
    {
        return S(10);
    }

    private int GetDockPadding()
    {
        return S(6);
    }

    private int GetMediaWidth(float itemSize)
    {
        return (int)Math.Round(itemSize * 4.55f + GetItemGap() * 2.0f);
    }

    private int GetMediaHeight(float itemSize)
    {
        return Math.Max(S(24), (int)Math.Round(itemSize));
    }

    private bool HasPinnedRunningSeparator()
    {
        return this.runtimePinnedCount > 0 && this.runtimeItems.Count > this.runtimePinnedCount;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawDock(e.Graphics);
    }

    private void DrawDock(Graphics g)
    {
        ConfigureDockGraphics(g);
        DrawDockBackground(g);
        DrawDockContent(g);
    }

    private void ConfigureDockGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawDockBackground(Graphics g)
    {
        int alpha = GetBackgroundOpacityAlpha();
        RectangleF shellRect = GetDockVisualShellRect();
        using (GraphicsPath shell = RoundedRectangle(shellRect, Math.Min(shellRect.Height / 2.0f, S(25))))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(alpha, 18, 19, 22)))
        using (Pen outline = new Pen(Color.FromArgb(95, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, shell);
            g.DrawPath(outline, shell);
        }
    }

    private float GetDockVisualWidth()
    {
        float width = this.dockResizeAnimating && this.dockVisualWidth > 0.0f ? this.dockVisualWidth : this.Width;
        width = Math.Max(S(150), width);
        return Math.Min(width, Math.Max(1, this.Width));
    }

    private float GetDockVisualLeft(float visualWidth)
    {
        return Math.Max(0.0f, (this.Width - visualWidth) / 2.0f);
    }

    private RectangleF GetDockVisualShellRect()
    {
        float visualWidth = GetDockVisualWidth();
        float left = GetDockVisualLeft(visualWidth);
        return new RectangleF(left, S(1), Math.Max(1.0f, visualWidth - 1.0f), Math.Max(1.0f, this.Height - S(2)));
    }

    private void DrawDockContent(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha < 255)
        {
            using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
            using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
            {
                ConfigureDockGraphics(contentGraphics);
                contentGraphics.Clear(Color.Transparent);
                DrawDockItems(contentGraphics);
                DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
            }

            return;
        }

        DrawDockItems(g);
    }

    private void DrawDockItems(Graphics g)
    {
        Point cursor = Cursor.Position;
        Point client = this.PointToClient(cursor);
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(new PointF(client.X, client.Y), this.Bounds.Contains(cursor), out hoverIndex);
        DrawSystemButtons(g, layout.SystemButtonRects, new PointF(client.X, client.Y), this.Bounds.Contains(cursor));
        RectangleF[] rects = layout.ItemRects;
        DrawDockSeparator(g, layout.SeparatorRect);
        DateTime now = DateTime.UtcNow;

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            RectangleF rect = rects[i];
            DockRuntimeItem item = this.runtimeItems[i];
            float alpha = 1.0f;
            float yOffset = 0.0f;
            if (!GetRuntimeItemAnimationVisuals(item, rect, now, out alpha, out yOffset))
            {
                continue;
            }

            RectangleF animatedRect = new RectangleF(rect.Left, rect.Top + yOffset, rect.Width, rect.Height);
            bool hovered = hoverIndex == i && item.AnimationState == DockItemAnimationState.Normal;
            DrawRuntimeItem(g, item, animatedRect, hovered, alpha);
        }

        DrawMediaControls(g, layout);
        DrawQuotaWidget(g, layout.QuotaRect);
    }

    private bool GetRuntimeItemAnimationVisuals(DockRuntimeItem item, RectangleF rect, DateTime now, out float alpha, out float yOffset)
    {
        alpha = 1.0f;
        yOffset = 0.0f;
        if (item == null)
        {
            return false;
        }

        if (item.AnimationState == DockItemAnimationState.EnteringPending)
        {
            alpha = 0.0f;
            yOffset = rect.Height * 0.62f;
            return false;
        }

        if (item.AnimationState == DockItemAnimationState.Entering)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - item.AnimationStartedUtc).TotalMilliseconds / DockItemEnterAnimationMs));
            double eased = EaseOutCubic(progress);
            alpha = (float)eased;
            yOffset = (float)(rect.Height * 0.62f * (1.0 - eased));
            return alpha > 0.01f;
        }

        if (item.AnimationState == DockItemAnimationState.Exiting)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - item.AnimationStartedUtc).TotalMilliseconds / DockItemExitAnimationMs));
            double eased = EaseInCubic(progress);
            alpha = (float)(1.0 - eased);
            yOffset = (float)(rect.Height * 0.62f * eased);
            return alpha > 0.01f;
        }

        return true;
    }

    private void DrawRuntimeItem(Graphics g, DockRuntimeItem item, RectangleF rect, bool hovered, float alpha)
    {
        int alphaByte = Math.Max(0, Math.Min(255, (int)Math.Round(255.0f * alpha)));
        if (alphaByte <= 0)
        {
            return;
        }

        DrawDockItemTile(g, rect, hovered, alphaByte);

        Bitmap bitmap = item.Bitmap;
        if (bitmap != null)
        {
            RectangleF iconRect = GetIconRect(rect);
            DrawingUtil.DrawImageWithAlpha(g, bitmap, iconRect, alphaByte);
        }

        if (item.IsRunning)
        {
            DrawRunningIndicator(g, rect, item.IsFocused, alphaByte);
        }

        if (item.InstanceCount > 1)
        {
            DrawInstanceBadge(g, rect, item.InstanceCount, alphaByte);
        }
    }

    private void DrawSystemButtons(Graphics g, RectangleF[] rects, PointF cursor, bool hasCursor)
    {
        if (rects == null)
        {
            return;
        }

        for (int i = 0; i < rects.Length; i++)
        {
            RectangleF rect = rects[i];
            bool hovered = hasCursor && rect.Contains(cursor.X, cursor.Y);
            DrawDockItemTile(g, rect, hovered);
            if (i == 0)
            {
                DrawShowDesktopGlyph(g, rect);
            }
            else if (i == 1)
            {
                DrawStartMenuGlyph(g, rect);
            }
        }
    }

    private void DrawDockItemTile(Graphics g, RectangleF rect, bool hovered)
    {
        DrawDockItemTile(g, rect, hovered, 255);
    }

    private void DrawDockItemTile(Graphics g, RectangleF rect, bool hovered, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0)
        {
            return;
        }

        float radius = Math.Max(S(5), rect.Height * 0.25f);
        RectangleF shadow = new RectangleF(rect.Left + rect.Width * 0.12f, rect.Bottom - S(2), rect.Width * 0.76f, S(4));
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(ScaleAlpha(80, alpha), 0, 0, 0)))
        {
            g.FillEllipse(shadowBrush, shadow);
        }

        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush background = new SolidBrush(hovered ? Color.FromArgb(ScaleAlpha(116, alpha), 82, 211, 255) : Color.FromArgb(ScaleAlpha(74, alpha), 248, 250, 255)))
        using (Pen border = new Pen(Color.FromArgb(ScaleAlpha(hovered ? 100 : 45, alpha), 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }
    }

    private static int ScaleAlpha(int value, int alpha)
    {
        return Math.Max(0, Math.Min(255, (int)Math.Round(value * alpha / 255.0)));
    }

    private RectangleF GetIconRect(RectangleF tileRect)
    {
        float inset = Math.Max(2.0f, tileRect.Height * 0.14f);
        return new RectangleF(
            tileRect.Left + inset,
            tileRect.Top + inset,
            Math.Max(1.0f, tileRect.Width - inset * 2.0f),
            Math.Max(1.0f, tileRect.Height - inset * 2.0f));
    }

    private void DrawShowDesktopGlyph(Graphics g, RectangleF rect)
    {
        RectangleF icon = GetIconRect(rect);
        float stroke = Math.Max(1.5f, icon.Width * 0.055f);
        RectangleF monitor = new RectangleF(
            icon.Left + icon.Width * 0.14f,
            icon.Top + icon.Height * 0.18f,
            icon.Width * 0.72f,
            icon.Height * 0.50f);
        using (Pen pen = new Pen(Color.FromArgb(235, 238, 245, 250), stroke))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawRectangle(pen, monitor.X, monitor.Y, monitor.Width, monitor.Height);
            float standX = monitor.Left + monitor.Width / 2.0f;
            float standTop = monitor.Bottom;
            float standBottom = icon.Top + icon.Height * 0.82f;
            g.DrawLine(pen, standX, standTop, standX, standBottom);
            g.DrawLine(pen, icon.Left + icon.Width * 0.32f, standBottom, icon.Left + icon.Width * 0.68f, standBottom);
        }

        using (SolidBrush brush = new SolidBrush(Color.FromArgb(235, 85, 211, 255)))
        {
            PointF[] arrow = new PointF[]
            {
                new PointF(icon.Left + icon.Width * 0.50f, icon.Top + icon.Height * 0.38f),
                new PointF(icon.Left + icon.Width * 0.64f, icon.Top + icon.Height * 0.52f),
                new PointF(icon.Left + icon.Width * 0.56f, icon.Top + icon.Height * 0.52f),
                new PointF(icon.Left + icon.Width * 0.56f, icon.Top + icon.Height * 0.65f),
                new PointF(icon.Left + icon.Width * 0.44f, icon.Top + icon.Height * 0.65f),
                new PointF(icon.Left + icon.Width * 0.44f, icon.Top + icon.Height * 0.52f),
                new PointF(icon.Left + icon.Width * 0.36f, icon.Top + icon.Height * 0.52f)
            };
            g.FillPolygon(brush, arrow);
        }
    }

    private void DrawStartMenuGlyph(Graphics g, RectangleF rect)
    {
        RectangleF icon = GetIconRect(rect);
        float gap = Math.Max(2.0f, icon.Width * 0.06f);
        float paneWidth = (icon.Width - gap) / 2.0f;
        float paneHeight = (icon.Height - gap) / 2.0f;
        using (LinearGradientBrush brush = new LinearGradientBrush(icon, Color.FromArgb(245, 32, 189, 255), Color.FromArgb(245, 68, 126, 255), LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(brush, icon.Left, icon.Top, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left + paneWidth + gap, icon.Top, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left, icon.Top + paneHeight + gap, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left + paneWidth + gap, icon.Top + paneHeight + gap, paneWidth, paneHeight);
        }
    }

    private DockLayout CalculateDockLayout(PointF cursor, bool hasCursor, out int hoverIndex)
    {
        hoverIndex = -1;
        int count = this.runtimeItems.Count;
        DockLayout layout = new DockLayout();
        layout.SystemButtonRects = new RectangleF[GetSystemButtonCount()];
        layout.ItemRects = new RectangleF[count];
        float padX = GetDockPadding();
        float gap = GetItemGap();
        float mediaGap = count > 0 ? S(8) : 0;
        bool hasSeparator = HasPinnedRunningSeparator();
        float separatorSpace = hasSeparator ? S(14) : 0;
        int pinnedCount = Math.Min(this.runtimePinnedCount, count);
        int runningCount = Math.Max(0, count - pinnedCount);
        float itemGaps = Math.Max(0, pinnedCount - 1) * gap + Math.Max(0, runningCount - 1) * gap;
        float configuredBaseIcon = GetBaseDockItemSize();
        float systemButtonSize = configuredBaseIcon;
        float systemReserved = GetSystemButtonsWidth(systemButtonSize);
        float systemSectionGap = count > 0 ? GetSystemSectionGap() : 0;
        float quotaGap = GetItemGap();
        float quotaWidth = GetQuotaWidgetWidth(systemButtonSize);
        float rightReserved = quotaGap + quotaWidth;
        float mediaItemSize = configuredBaseIcon;
        float mediaWidth = GetMediaWidth(mediaItemSize);
        float mediaHeight = GetMediaHeight(mediaItemSize);
        float visualWidth = GetDockVisualWidth();
        float visualLeft = GetDockVisualLeft(visualWidth);
        float availableIcons = 0;
        float maxIcon = 0;
        float baseIcon = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            mediaWidth = GetMediaWidth(mediaItemSize);
            mediaHeight = GetMediaHeight(mediaItemSize);
            availableIcons = Math.Max(S(24), visualWidth - padX * 2.0f - systemReserved - systemSectionGap - itemGaps - separatorSpace - mediaGap - mediaWidth - rightReserved);
            maxIcon = count > 0 ? Math.Max(S(24), Math.Min(GetMaxIconSize(), availableIcons / count)) : 0;
            baseIcon = count > 0 ? Math.Max(S(24), Math.Min(configuredBaseIcon, maxIcon)) : configuredBaseIcon;
            mediaItemSize = baseIcon;
        }

        float total = systemReserved + systemSectionGap + count * maxIcon + itemGaps + separatorSpace + mediaGap + mediaWidth + rightReserved;
        float x = visualLeft + Math.Max(padX, (visualWidth - total) / 2.0f);
        float itemCenterY = this.Height / 2.0f;
        float influence = Math.Max(baseIcon * 1.8f, S(48));
        double bestProgress = 0.0;

        for (int i = 0; i < layout.SystemButtonRects.Length; i++)
        {
            layout.SystemButtonRects[i] = new RectangleF(x, itemCenterY - systemButtonSize / 2.0f, systemButtonSize, systemButtonSize);
            x += systemButtonSize + (i < layout.SystemButtonRects.Length - 1 ? gap : 0);
        }

        x += systemSectionGap;

        for (int i = 0; i < pinnedCount; i++)
        {
            layout.ItemRects[i] = CalculateItemRect(i, x, maxIcon, baseIcon, itemCenterY, influence, cursor, hasCursor, ref hoverIndex, ref bestProgress);
            x += maxIcon + (i < pinnedCount - 1 ? gap : 0);
        }

        if (hasSeparator)
        {
            float lineX = x + separatorSpace / 2.0f;
            float lineHeight = Math.Max(S(18), this.Height - S(14));
            layout.SeparatorRect = new RectangleF(lineX, (this.Height - lineHeight) / 2.0f, Math.Max(1.0f, this.scale), lineHeight);
            x += separatorSpace;
        }

        for (int i = 0; i < runningCount; i++)
        {
            int itemIndex = pinnedCount + i;
            layout.ItemRects[itemIndex] = CalculateItemRect(itemIndex, x, maxIcon, baseIcon, itemCenterY, influence, cursor, hasCursor, ref hoverIndex, ref bestProgress);
            x += maxIcon + (i < runningCount - 1 ? gap : 0);
        }

        if (bestProgress <= 0.05)
        {
            hoverIndex = -1;
        }

        if (count > 0)
        {
            x += mediaGap;
        }

        layout.MediaRect = new RectangleF(x, itemCenterY - mediaHeight / 2.0f, mediaWidth, mediaHeight);
        layout.QuotaRect = new RectangleF(layout.MediaRect.Right + quotaGap, itemCenterY - mediaHeight / 2.0f, quotaWidth, mediaHeight);
        CalculateMediaButtonRects(layout);
        return layout;
    }

    private RectangleF CalculateItemRect(
        int itemIndex,
        float slotLeft,
        float maxIcon,
        float baseIcon,
        float itemCenterY,
        float influence,
        PointF cursor,
        bool hasCursor,
        ref int hoverIndex,
        ref double bestProgress)
    {
        float itemCenterX = slotLeft + maxIcon / 2.0f;
        double progress = 0.0;
        if (hasCursor && maxIcon > 0)
        {
            double distance = Math.Abs(cursor.X - itemCenterX);
            if (distance < influence)
            {
                progress = 1.0 - distance / influence;
                progress = Math.Sin(progress * Math.PI / 2.0);
            }
        }

        if (progress > bestProgress)
        {
            bestProgress = progress;
            hoverIndex = itemIndex;
        }

        double maxFactor = baseIcon <= 0 ? 1.0 : Math.Max(1.0, maxIcon / baseIcon);
        float iconSize = (float)Math.Min(maxIcon, baseIcon * (1.0 + (maxFactor - 1.0) * progress));
        return new RectangleF(
            itemCenterX - iconSize / 2.0f,
            itemCenterY - iconSize / 2.0f,
            iconSize,
            iconSize);
    }

    private int FindItemAtPoint(PointF point)
    {
        int hoverIndex;
        RectangleF[] rects = CalculateDockLayout(point, true, out hoverIndex).ItemRects;
        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindSystemButtonAtPoint(PointF point)
    {
        int hoverIndex;
        RectangleF[] rects = CalculateDockLayout(point, true, out hoverIndex).SystemButtonRects;
        if (rects == null)
        {
            return -1;
        }

        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    private void ActivateSystemButton(int index)
    {
        if (index == 0)
        {
            NativeMethods.ToggleDesktop();
        }
        else if (index == 1)
        {
            NativeMethods.OpenStartMenu();
        }
    }

    private void UpdatePreviewFromCursor(Point cursor)
    {
        if (this.hiddenForFullscreen || !this.Visible)
        {
            HidePreview();
            return;
        }

        if (this.Bounds.Contains(cursor))
        {
            Point client = this.PointToClient(cursor);
            int hoverIndex;
            DockLayout layout = CalculateDockLayout(new PointF(client.X, client.Y), true, out hoverIndex);
            int index = FindItemAtPoint(new PointF(client.X, client.Y));
            if (index >= 0 && index < this.runtimeItems.Count)
            {
                DockRuntimeItem item = this.runtimeItems[index];
                if (item.IsRunning && item.WindowHandle != IntPtr.Zero)
                {
                    ShowPreviewForItem(index, item, layout.ItemRects[index]);
                    return;
                }
            }

            HidePreview();
            return;
        }

        if (this.previewForm != null && !this.previewForm.IsDisposed && this.previewForm.ContainsScreenPoint(cursor))
        {
            return;
        }

        HidePreview();
    }

    private void ShowPreviewForItem(int index, DockRuntimeItem item, RectangleF itemRect)
    {
        if (item == null || item.WindowHandle == IntPtr.Zero)
        {
            HidePreview();
            return;
        }

        if (this.previewForm == null || this.previewForm.IsDisposed)
        {
            EnsurePreviewForm();
        }

        string title = !string.IsNullOrEmpty(item.WindowTitle) ? item.WindowTitle : item.Item.Label;
        Rectangle anchor = Rectangle.Round(itemRect);
        anchor.Location = this.PointToScreen(anchor.Location);
        bool topMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        this.previewForm.ShowPreview(this, item.WindowHandle, title, anchor, topMost);
        this.previewItemIndex = index;
    }

    private void EnsurePreviewForm()
    {
        if (this.previewForm != null && !this.previewForm.IsDisposed)
        {
            return;
        }

        this.previewForm = new DockPreviewForm();
        this.previewForm.PrepareHidden();
    }

    private void HidePreview()
    {
        this.previewItemIndex = -1;
        if (this.previewForm != null && !this.previewForm.IsDisposed)
        {
            this.previewForm.HidePreview();
        }
    }

    private void ClosePreviewForm()
    {
        if (this.previewForm != null)
        {
            this.previewForm.Close();
            this.previewForm.Dispose();
            this.previewForm = null;
        }

        this.previewItemIndex = -1;
    }

    private void DrawDockSeparator(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.DrawLine(pen, rect.Left, rect.Top, rect.Left, rect.Bottom);
        }
    }

    private void DrawRunningIndicator(Graphics g, RectangleF iconRect, bool focused)
    {
        DrawRunningIndicator(g, iconRect, focused, 255);
    }

    private void DrawRunningIndicator(Graphics g, RectangleF iconRect, bool focused, int alpha)
    {
        float height = Math.Max(S(3), iconRect.Width * 0.10f);
        float width = focused ? Math.Max(S(12), iconRect.Width * 0.50f) : height;
        RectangleF dot = new RectangleF(iconRect.Left + (iconRect.Width - width) / 2.0f, this.Height - S(5), width, height);
        using (GraphicsPath path = RoundedRectangle(dot, height / 2.0f))
        using (SolidBrush brush = new SolidBrush(focused ? Color.FromArgb(ScaleAlpha(238, alpha), 82, 211, 255) : Color.FromArgb(ScaleAlpha(210, alpha), 142, 149, 158)))
        {
            g.FillPath(brush, path);
        }
    }

    private void DrawInstanceBadge(Graphics g, RectangleF iconRect, int count)
    {
        DrawInstanceBadge(g, iconRect, count, 255);
    }

    private void DrawInstanceBadge(Graphics g, RectangleF iconRect, int count, int alpha)
    {
        string text = Math.Min(count, 99).ToString(CultureInfo.InvariantCulture);
        float height = Math.Max(S(14), iconRect.Height * 0.28f);
        float width = Math.Max(height, height + Math.Max(0, text.Length - 1) * S(6));
        RectangleF badge = new RectangleF(iconRect.Right - width * 0.78f, iconRect.Bottom - height * 0.76f, width, height);
        using (GraphicsPath path = RoundedRectangle(badge, height / 2.0f))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(ScaleAlpha(230, alpha), 154, 160, 170)))
        using (Pen border = new Pen(Color.FromArgb(ScaleAlpha(150, alpha), 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        using (Font font = new Font("Segoe UI", Math.Max(8.0f, height * 0.58f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(ScaleAlpha(245, alpha), 248, 250, 252)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, brush, badge, format);
        }
    }

    private void CalculateMediaButtonRects(DockLayout layout)
    {
        RectangleF media = layout.MediaRect;
        RectangleF content = GetMediaRightContentRect(media);
        float buttonGap = Math.Max(S(5), Math.Min(S(9), content.Width * 0.045f));
        float buttonHeight = Math.Max(S(22), Math.Min(S(34), media.Height * 0.39f));
        float buttonTop = media.Bottom - buttonHeight - S(5);
        float buttonLeft = content.Left + buttonGap;
        float buttonWidth = Math.Max(1.0f, content.Width - buttonGap * 4.0f);
        float third = buttonWidth / 3.0f;

        layout.PreviousRect = new RectangleF(buttonLeft, buttonTop, third, buttonHeight);
        layout.PlayPauseRect = new RectangleF(buttonLeft + third + buttonGap, buttonTop, third, buttonHeight);
        layout.NextRect = new RectangleF(buttonLeft + (third + buttonGap) * 2.0f, buttonTop, third, buttonHeight);
        this.mediaPreviousRect = layout.PreviousRect;
        this.mediaPlayPauseRect = layout.PlayPauseRect;
        this.mediaNextRect = layout.NextRect;
    }

    private RectangleF GetMediaRightContentRect(RectangleF media)
    {
        float leftSectionWidth = media.Width / 3.0f;
        RectangleF rightSection = new RectangleF(media.Left + leftSectionWidth, media.Top, media.Width - leftSectionWidth, media.Height);
        float inset = S(7);
        return new RectangleF(
            rightSection.Left + inset,
            rightSection.Top,
            Math.Max(1.0f, rightSection.Width - inset * 2.0f),
            rightSection.Height);
    }

    private RectangleF GetMediaVisualRect(RectangleF logoArea, Bitmap bitmap, bool isArtwork)
    {
        if (isArtwork)
        {
            float pad = S(3);
            RectangleF bounds = new RectangleF(
                logoArea.Left + pad,
                logoArea.Top + pad,
                Math.Max(1.0f, logoArea.Width - pad * 2.0f),
                Math.Max(1.0f, logoArea.Height - pad * 2.0f));
            float aspect = 16.0f / 9.0f;
            if (bitmap != null && bitmap.Width > bitmap.Height && bitmap.Height > 0)
            {
                float bitmapAspect = bitmap.Width / (float)bitmap.Height;
                if (bitmapAspect >= 1.20f && bitmapAspect <= 2.20f)
                {
                    aspect = bitmapAspect;
                }
            }

            float width = bounds.Width;
            float height = width / aspect;
            if (height > bounds.Height)
            {
                height = bounds.Height;
                width = height * aspect;
            }

            return new RectangleF(
                bounds.Left + (bounds.Width - width) / 2.0f,
                bounds.Top + (bounds.Height - height) / 2.0f,
                width,
                height);
        }

        float maxIconSize = Math.Max(1.0f, Math.Min(logoArea.Width - S(2), logoArea.Height - S(2)));
        float iconSize = Math.Min(maxIconSize, Math.Max(S(48), Math.Min(Math.Min(logoArea.Width, logoArea.Height) * 0.90f, S(76))));
        return new RectangleF(
            logoArea.Left + (logoArea.Width - iconSize) / 2.0f,
            logoArea.Top + (logoArea.Height - iconSize) / 2.0f,
            iconSize,
            iconSize);
    }

    private void DrawMediaControls(Graphics g, DockLayout layout)
    {
        RectangleF rect = layout.MediaRect;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Point cursor = this.PointToClient(Cursor.Position);
        DrawDockItemTile(g, rect, rect.Contains(cursor.X, cursor.Y));

        float leftSectionWidth = rect.Width / 3.0f;
        RectangleF logoArea = new RectangleF(rect.Left, rect.Top, leftSectionWidth, rect.Height);
        RectangleF content = GetMediaRightContentRect(rect);
        RectangleF iconRect = GetMediaVisualRect(logoArea, this.mediaAppBitmap, this.mediaBitmapIsArtwork);
        DrawMediaThumbnail(g, iconRect, this.mediaAppBitmap, this.mediaBitmapIsArtwork, this.mediaAppBitmapVisibleBounds);

        RectangleF titleRect = new RectangleF(
            content.Left,
            content.Top + S(3),
            content.Width,
            Math.Max(S(22), layout.PreviousRect.Top - content.Top - S(5)));
        string title = BuildMediaDisplayTitle(this.mediaInfo);

        using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(244, 244, 247, 251)))
        {
            DrawMediaTitle(g, title, titleRect, titleBrush);
        }

        bool isPlaying = this.mediaInfo != null && this.mediaInfo.HasSession && this.mediaInfo.IsPlaying;
        DrawMediaButton(g, layout.PreviousRect, 0, isPlaying);
        DrawMediaButton(g, layout.PlayPauseRect, 1, isPlaying);
        DrawMediaButton(g, layout.NextRect, 2, isPlaying);
    }

    private void DrawMediaTitle(Graphics g, string title, RectangleF rect, Brush brush)
    {
        if (string.IsNullOrEmpty(title) || rect.Width <= 1.0f || rect.Height <= 1.0f)
        {
            this.mediaTitleOverflow = false;
            this.mediaTitlePageCount = 0;
            this.mediaTitlePageIndex = -1;
            return;
        }

        MediaTitleLayoutCache layout = GetMediaTitleLayout(g, title, rect);
        if (layout == null || layout.Pages == null || layout.Pages.Count <= 0)
        {
            this.mediaTitleOverflow = false;
            this.mediaTitlePageCount = 0;
            this.mediaTitlePageIndex = -1;
            return;
        }

        this.mediaTitleOverflow = !layout.FitsSingleLine && layout.Pages.Count > 1;
        this.mediaTitlePageCount = layout.Pages.Count;

        using (Font font = new Font("Segoe UI", layout.FontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            format.FormatFlags = StringFormatFlags.NoWrap;

            if (layout.FitsSingleLine)
            {
                this.mediaTitlePageIndex = 0;
                g.DrawString(layout.Pages[0], font, brush, rect, format);
                return;
            }

            int pageIndex = GetMediaTitlePageIndex(layout.Pages.Count, DateTime.UtcNow);
            this.mediaTitlePageIndex = pageIndex;

            GraphicsState state = g.Save();
            g.SetClip(rect);
            double phase = GetMediaTitleFlipPhase(DateTime.UtcNow);
            if (layout.Pages.Count > 1 && phase < MediaTitleFlipDurationMs)
            {
                int previousIndex = pageIndex == 0 ? layout.Pages.Count - 1 : pageIndex - 1;
                double progress = Math.Max(0.0, Math.Min(1.0, phase / MediaTitleFlipDurationMs));
                double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
                float offset = (float)(rect.Height * eased);
                RectangleF previousRect = new RectangleF(rect.Left, rect.Top - offset, rect.Width, rect.Height);
                RectangleF currentRect = new RectangleF(rect.Left, rect.Top + rect.Height - offset, rect.Width, rect.Height);
                g.DrawString(layout.Pages[previousIndex], font, brush, previousRect, format);
                g.DrawString(layout.Pages[pageIndex], font, brush, currentRect, format);
            }
            else
            {
                g.DrawString(layout.Pages[pageIndex], font, brush, rect, format);
            }

            g.Restore(state);
        }
    }

    private MediaTitleLayoutCache GetMediaTitleLayout(Graphics g, string title, RectangleF rect)
    {
        int width = Math.Max(1, (int)Math.Round(rect.Width));
        int height = Math.Max(1, (int)Math.Round(rect.Height));
        if (this.mediaTitleLayoutCache != null &&
            this.mediaTitleLayoutCache.Width == width &&
            this.mediaTitleLayoutCache.Height == height &&
            string.Equals(this.mediaTitleLayoutCache.Title, title, StringComparison.Ordinal))
        {
            return this.mediaTitleLayoutCache;
        }

        float maxSize = Math.Max(18.0f, Math.Min(27.0f, rect.Height * 0.88f));
        float minSize = Math.Max(12.0f, Math.Min(16.0f, rect.Height * 0.56f));
        MediaTitleLayoutCache layout = new MediaTitleLayoutCache();
        layout.Title = title;
        layout.Width = width;
        layout.Height = height;
        layout.Pages = new List<string>();

        for (float size = maxSize; size >= minSize; size -= 1.0f)
        {
            using (Font font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF measured = g.MeasureString(title, font);
                if (measured.Width <= rect.Width)
                {
                    layout.FontSize = size;
                    layout.FitsSingleLine = true;
                    layout.Pages.Add(title);
                    this.mediaTitleLayoutCache = layout;
                    return layout;
                }
            }
        }

        layout.FontSize = minSize;
        layout.FitsSingleLine = false;
        using (Font font = new Font("Segoe UI", minSize, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            layout.Pages = BuildMediaTitlePages(g, title, font, rect.Width);
        }

        if (layout.Pages.Count <= 0)
        {
            layout.Pages.Add(title);
        }

        this.mediaTitleLayoutCache = layout;
        return layout;
    }

    private static int GetMediaTitlePageIndex(int pageCount, DateTime now)
    {
        if (pageCount <= 1)
        {
            return 0;
        }

        double cycleIndex = Math.Floor(now.TimeOfDay.TotalMilliseconds / MediaTitleFlipCycleMs);
        return ((int)cycleIndex) % pageCount;
    }

    private static double GetMediaTitleFlipPhase(DateTime now)
    {
        return now.TimeOfDay.TotalMilliseconds % MediaTitleFlipCycleMs;
    }

    private static List<string> BuildMediaTitlePages(Graphics g, string title, Font font, float maxWidth)
    {
        List<string> pages = new List<string>();
        if (string.IsNullOrEmpty(title))
        {
            return pages;
        }

        int start = 0;
        while (start < title.Length)
        {
            while (start < title.Length && char.IsWhiteSpace(title[start]))
            {
                start++;
            }

            if (start >= title.Length)
            {
                break;
            }

            int remaining = title.Length - start;
            int low = 1;
            int high = remaining;
            int best = 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                string candidate = title.Substring(start, mid).Trim();
                SizeF measured = g.MeasureString(candidate, font);
                if (measured.Width <= maxWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            int take = best;
            if (start + take < title.Length)
            {
                int lastSpace = title.LastIndexOf(' ', start + take - 1, take);
                if (lastSpace > start + Math.Max(4, take / 3))
                {
                    take = lastSpace - start + 1;
                }
            }

            string page = title.Substring(start, Math.Max(1, take)).Trim();
            if (page.Length == 0)
            {
                page = title.Substring(start, 1);
                take = 1;
            }

            pages.Add(page);
            start += Math.Max(1, take);
        }

        return pages;
    }

    private void DrawQuotaWidget(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        DockQuotaSnapshot snapshot = this.quotaSnapshot ?? DockQuotaSnapshot.CreateDefault();
        float rowGap = S(4);
        float rowHeight = (rect.Height - rowGap) / 2.0f;
        RectangleF firstRow = new RectangleF(rect.Left + S(5), rect.Top + S(4), rect.Width - S(10), rowHeight - S(2));
        RectangleF secondRow = new RectangleF(rect.Left + S(5), firstRow.Bottom + rowGap, rect.Width - S(10), rowHeight - S(2));
        DrawQuotaRow(
            g,
            firstRow,
            snapshot.FiveHourPercent,
            snapshot.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture));
        DrawQuotaRow(
            g,
            secondRow,
            snapshot.WeeklyPercent,
            snapshot.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture));
    }

    private void DrawQuotaRow(Graphics g, RectangleF rect, int percent, string resetText)
    {
        percent = ClampPercent(percent);
        float ringSize = Math.Max(S(25), Math.Min(rect.Height, S(36)));
        RectangleF ringRect = new RectangleF(rect.Left, rect.Top + (rect.Height - ringSize) / 2.0f, ringSize, ringSize);
        Color ringColor = GetQuotaColor(percent);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen backgroundPen = new Pen(Color.FromArgb(78, 255, 255, 255), stroke))
        using (Pen valuePen = new Pen(ringColor, stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (percent > 0)
            {
                g.DrawArc(valuePen, arcRect, -90.0f, 360.0f * percent / 100.0f);
            }
        }

        using (Font numberFont = new Font("Segoe UI", Math.Max(8.0f, ringSize * 0.34f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush numberBrush = new SolidBrush(Color.FromArgb(248, 255, 255, 255)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            g.DrawString(percent.ToString(CultureInfo.InvariantCulture), numberFont, numberBrush, ringRect, center);
        }

        RectangleF textRect = new RectangleF(
            ringRect.Right + S(6),
            rect.Top,
            Math.Max(1.0f, rect.Right - ringRect.Right - S(6)),
            rect.Height);
        using (Font resetFont = new Font("Segoe UI", Math.Max(10.0f, ringSize * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(226, 240, 244, 248)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(resetText, resetFont, textBrush, textRect, format);
        }
    }

    private static Color GetQuotaColor(int percent)
    {
        if (percent >= 80)
        {
            return Color.FromArgb(235, 76, 214, 116);
        }

        if (percent >= 30)
        {
            return Color.FromArgb(238, 242, 202, 73);
        }

        if (percent <= 5)
        {
            return Color.FromArgb(238, 126, 18, 28);
        }

        return Color.FromArgb(238, 226, 117, 49);
    }

    private void DrawMediaThumbnail(Graphics g, RectangleF rect, Bitmap appBitmap, bool isArtwork, Rectangle cachedContentBounds)
    {
        RectangleF inner = appBitmap != null
            ? rect
            : new RectangleF(rect.Left + S(4), rect.Top + S(4), rect.Width - S(8), rect.Height - S(8));
        using (GraphicsPath path = RoundedRectangle(inner, Math.Max(S(6), inner.Height * 0.22f)))
        {
            if (appBitmap != null)
            {
                using (SolidBrush background = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
                {
                    g.FillPath(background, path);
                }

                Rectangle contentBounds = cachedContentBounds;
                if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
                {
                    contentBounds = new Rectangle(0, 0, appBitmap.Width, appBitmap.Height);
                }

                Rectangle target;
                Rectangle sourceBounds;
                if (isArtwork)
                {
                    target = Rectangle.Round(rect);
                    sourceBounds = CropToFill(contentBounds, target);
                }
                else
                {
                    float iconPadding = Math.Max(1.0f, rect.Width * 0.04f);
                    Rectangle targetArea = Rectangle.Round(new RectangleF(
                        rect.Left + iconPadding,
                        rect.Top + iconPadding,
                        Math.Max(1.0f, rect.Width - iconPadding * 2.0f),
                        Math.Max(1.0f, rect.Height - iconPadding * 2.0f)));
                    target = FitInsideSquare(contentBounds.Size, targetArea);
                    sourceBounds = contentBounds;
                }

                GraphicsState state = g.Save();
                g.SetClip(path);
                g.DrawImage(appBitmap, target, sourceBounds, GraphicsUnit.Pixel);
                g.Restore(state);
            }
            else
            {
                GraphicsState state = g.Save();
                g.SetClip(path);
                using (LinearGradientBrush background = new LinearGradientBrush(inner, Color.FromArgb(255, 33, 43, 60), Color.FromArgb(255, 69, 148, 203), LinearGradientMode.ForwardDiagonal))
                {
                    g.FillRectangle(background, inner);
                }

                using (SolidBrush glow = new SolidBrush(Color.FromArgb(62, 255, 255, 255)))
                {
                    g.FillEllipse(glow, inner.Left - inner.Width * 0.35f, inner.Top - inner.Height * 0.35f, inner.Width * 0.95f, inner.Height * 0.95f);
                }

                g.Restore(state);
                DrawMediaWaveGlyph(g, inner);
            }

            using (Pen border = new Pen(Color.FromArgb(75, 255, 255, 255), Math.Max(1.0f, this.scale)))
            {
                g.DrawPath(border, path);
            }
        }
    }

    private void DrawMediaWaveGlyph(Graphics g, RectangleF rect)
    {
        float centerY = rect.Top + rect.Height / 2.0f;
        float left = rect.Left + rect.Width * 0.22f;
        float gap = rect.Width * 0.16f;
        float[] heights = new float[] { 0.28f, 0.58f, 0.38f };
        using (Pen pen = new Pen(Color.FromArgb(235, 236, 248, 255), Math.Max(1.2f, rect.Width * 0.10f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            for (int i = 0; i < heights.Length; i++)
            {
                float h = rect.Height * heights[i];
                float x = left + gap * i;
                g.DrawLine(pen, x, centerY - h / 2.0f, x, centerY + h / 2.0f);
            }
        }
    }

    private void DrawMediaButton(Graphics g, RectangleF rect, int kind, bool isPlaying)
    {
        Point cursor = this.PointToClient(Cursor.Position);
        bool hovered = rect.Contains(cursor.X, cursor.Y);
        bool activePlayButton = kind == 1 && isPlaying;
        double pressElapsed = (DateTime.UtcNow - this.mediaButtonPressAnimationUtc).TotalMilliseconds;
        bool pressAnimating = this.mediaButtonPressKind == kind && pressElapsed >= 0.0 && pressElapsed < 190.0;
        double pressProgress = pressAnimating ? Math.Max(0.0, Math.Min(1.0, pressElapsed / 190.0)) : 1.0;
        Color backgroundColor = activePlayButton
            ? Color.FromArgb(66, 82, 211, 255)
            : (hovered ? Color.FromArgb(74, 255, 255, 255) : Color.FromArgb(48, 255, 255, 255));

        using (SolidBrush background = new SolidBrush(backgroundColor))
        using (Pen border = new Pen(Color.FromArgb(66, 255, 255, 255), Math.Max(1.0f, this.scale)))
        using (GraphicsPath path = RoundedRectangle(rect, S(5)))
        {
            g.FillPath(background, path);
            if (pressAnimating)
            {
                DrawMediaButtonPressAnimation(g, rect, pressProgress);
            }

            g.DrawPath(border, path);
        }

        using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 244, 248, 250)))
        using (Pen pen = new Pen(Color.FromArgb(238, 244, 248, 250), Math.Max(1.6f, 2.1f * this.scale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            if (kind == 0)
            {
                DrawPreviousGlyph(g, rect, brush, pen);
            }
            else if (kind == 1)
            {
                DrawPlayPauseGlyph(g, rect, brush, isPlaying);
            }
            else
            {
                DrawNextGlyph(g, rect, brush, pen);
            }
        }
    }

    private void DrawMediaButtonPressAnimation(Graphics g, RectangleF rect, double progress)
    {
        float size = (float)(Math.Max(rect.Width, rect.Height) * (0.10 + progress * 0.95));
        RectangleF ripple = new RectangleF(
            rect.Left + rect.Width / 2.0f - size / 2.0f,
            rect.Top + rect.Height / 2.0f - size / 2.0f,
            size,
            size);
        int alpha = Math.Max(0, (int)Math.Round(105.0 * (1.0 - progress)));
        using (GraphicsPath clipPath = RoundedRectangle(rect, S(5)))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
        {
            GraphicsState state = g.Save();
            g.SetClip(clipPath);
            g.FillEllipse(brush, ripple);
            g.Restore(state);
        }
    }

    private void DrawPreviousGlyph(Graphics g, RectangleF rect, Brush brush, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float w = rect.Width * 0.20f;
        float h = rect.Height * 0.30f;
        g.DrawLine(pen, cx - w * 1.15f, cy - h, cx - w * 1.15f, cy + h);
        PointF[] triangle = new PointF[]
        {
            new PointF(cx + w * 0.75f, cy - h),
            new PointF(cx - w * 0.55f, cy),
            new PointF(cx + w * 0.75f, cy + h)
        };
        g.FillPolygon(brush, triangle);
    }

    private void DrawPlayPauseGlyph(Graphics g, RectangleF rect, Brush brush, bool isPlaying)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        if (isPlaying)
        {
            float barWidth = Math.Max(2.0f, rect.Width * 0.09f);
            float barGap = Math.Max(3.0f, rect.Width * 0.10f);
            float barHeight = rect.Height * 0.52f;
            RectangleF leftBar = new RectangleF(cx - barGap / 2.0f - barWidth, cy - barHeight / 2.0f, barWidth, barHeight);
            RectangleF rightBar = new RectangleF(cx + barGap / 2.0f, cy - barHeight / 2.0f, barWidth, barHeight);
            using (GraphicsPath leftPath = RoundedRectangle(leftBar, Math.Min(leftBar.Width, leftBar.Height) * 0.35f))
            using (GraphicsPath rightPath = RoundedRectangle(rightBar, Math.Min(rightBar.Width, rightBar.Height) * 0.35f))
            {
                g.FillPath(brush, leftPath);
                g.FillPath(brush, rightPath);
            }

            return;
        }

        float w = rect.Width * 0.21f;
        float h = rect.Height * 0.31f;
        PointF[] triangle = new PointF[]
        {
            new PointF(cx - w * 0.55f, cy - h),
            new PointF(cx - w * 0.55f, cy + h),
            new PointF(cx + w * 0.85f, cy)
        };
        g.FillPolygon(brush, triangle);
    }

    private void DrawNextGlyph(Graphics g, RectangleF rect, Brush brush, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float w = rect.Width * 0.20f;
        float h = rect.Height * 0.30f;
        PointF[] triangle = new PointF[]
        {
            new PointF(cx - w * 0.75f, cy - h),
            new PointF(cx + w * 0.55f, cy),
            new PointF(cx - w * 0.75f, cy + h)
        };
        g.FillPolygon(brush, triangle);
        g.DrawLine(pen, cx + w * 1.15f, cy - h, cx + w * 1.15f, cy + h);
    }

    private bool HandleMediaClick(PointF point)
    {
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(point, true, out hoverIndex);
        if (layout.PreviousRect.Contains(point.X, point.Y))
        {
            StartMediaButtonPressAnimation(0);
            NativeMethods.SendMediaCommand(this.Handle, NativeMethods.APPCOMMAND_MEDIA_PREVIOUSTRACK);
            RefreshMediaInfoSoon(250);
            RefreshMediaInfoSoon(900);
            return true;
        }

        if (layout.PlayPauseRect.Contains(point.X, point.Y))
        {
            StartMediaButtonPressAnimation(1);
            ToggleMediaPlaybackStateOptimistically();
            NativeMethods.SendMediaCommand(this.Handle, NativeMethods.APPCOMMAND_MEDIA_PLAY_PAUSE);
            RefreshMediaInfoSoon(250);
            RefreshMediaInfoSoon(900);
            return true;
        }

        if (layout.NextRect.Contains(point.X, point.Y))
        {
            StartMediaButtonPressAnimation(2);
            NativeMethods.SendMediaCommand(this.Handle, NativeMethods.APPCOMMAND_MEDIA_NEXTTRACK);
            RefreshMediaInfoSoon(250);
            RefreshMediaInfoSoon(900);
            return true;
        }

        return false;
    }

    private void StartMediaButtonPressAnimation(int kind)
    {
        this.mediaButtonPressKind = kind;
        this.mediaButtonPressAnimationUtc = DateTime.UtcNow;
        this.lastMediaAnimationRenderUtc = DateTime.MinValue;
        SetDockTimerInterval(DockActiveTimerIntervalMs);
        RenderLayeredWindow();
    }

    private void RenderLayeredWindow()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawDock(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap, 255))
                {
                    if (!this.layeredUpdateFailureLogged)
                    {
                        this.layeredUpdateFailureLogged = true;
                        Program.LogInfo("Dock UpdateLayeredWindow failed; falling back to normal paint.");
                    }

                    this.Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.BackgroundTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private void ActivateOrLaunchItem(DockRuntimeItem item)
    {
        if (item == null)
        {
            return;
        }

        if (item.IsRunning && item.WindowHandle != IntPtr.Zero)
        {
            if (NativeMethods.ActivateWindow(item.WindowHandle))
            {
                return;
            }
        }

        LaunchItem(item.Item);
    }

    private void LaunchItem(DockItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Command))
        {
            return;
        }

        string fileName;
        string arguments;
        SplitCommandLine(item.Command, out fileName, out arguments);
        if (fileName.Length == 0)
        {
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
            Program.LogInfo("Dock item launched: " + item.Label + " -> " + item.Command);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            try
            {
                MessageBox.Show(
                    "无法启动 Dock 项目。\r\n\r\n" + item.Command + "\r\n\r\n" + ex.Message,
                    "DesktopPerfWidget",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch
            {
            }
        }
    }

    private Bitmap LoadItemBitmap(DockItem item)
    {
        string iconPath = ResolveExecutablePath(item == null ? string.Empty : item.Command);
        string label = item == null ? "App" : item.Label;
        return LoadItemBitmap(iconPath, label);
    }

    private Bitmap LoadItemBitmap(string iconPath, string label)
    {
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(iconPath))
                {
                    if (icon != null)
                    {
                        return IconToBitmap(icon);
                    }
                }
            }
            catch
            {
            }
        }

        return CreateFallbackIcon(label);
    }

    private Bitmap IconToBitmap(Icon icon)
    {
        using (Bitmap source = icon.ToBitmap())
        {
            Bitmap bitmap = new Bitmap(128, 128, PixelFormat.Format32bppPArgb);
            Rectangle contentBounds = GetVisibleBitmapBounds(source);
            if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
            {
                contentBounds = new Rectangle(0, 0, source.Width, source.Height);
            }

            Rectangle target = FitInsideSquare(contentBounds.Size, new Rectangle(1, 1, 126, 126));
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(source, target, contentBounds, GraphicsUnit.Pixel);
            }

            return bitmap;
        }
    }

    private static Rectangle GetVisibleBitmapBounds(Bitmap bitmap)
    {
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.A <= 8)
                {
                    continue;
                }

                if (x < left)
                {
                    left = x;
                }

                if (x > right)
                {
                    right = x;
                }

                if (y < top)
                {
                    top = y;
                }

                if (y > bottom)
                {
                    bottom = y;
                }
            }
        }

        if (right < left || bottom < top)
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Rectangle FitInsideSquare(Size sourceSize, Rectangle targetArea)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            return targetArea;
        }

        double scale = Math.Min(
            targetArea.Width / (double)sourceSize.Width,
            targetArea.Height / (double)sourceSize.Height);
        int width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
        int height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
        int left = targetArea.Left + (targetArea.Width - width) / 2;
        int top = targetArea.Top + (targetArea.Height - height) / 2;
        return new Rectangle(left, top, width, height);
    }

    private static Rectangle CropToFill(Rectangle sourceBounds, Rectangle targetArea)
    {
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0 || targetArea.Width <= 0 || targetArea.Height <= 0)
        {
            return sourceBounds;
        }

        double sourceAspect = sourceBounds.Width / (double)sourceBounds.Height;
        double targetAspect = targetArea.Width / (double)targetArea.Height;
        if (sourceAspect > targetAspect)
        {
            int width = Math.Max(1, (int)Math.Round(sourceBounds.Height * targetAspect));
            int left = sourceBounds.Left + (sourceBounds.Width - width) / 2;
            return new Rectangle(left, sourceBounds.Top, width, sourceBounds.Height);
        }

        int height = Math.Max(1, (int)Math.Round(sourceBounds.Width / targetAspect));
        int top = sourceBounds.Top + (sourceBounds.Height - height) / 2;
        return new Rectangle(sourceBounds.Left, top, sourceBounds.Width, height);
    }

    private Bitmap CreateFallbackIcon(string label)
    {
        Bitmap bitmap = new Bitmap(128, 128, PixelFormat.Format32bppPArgb);
        string glyph = string.IsNullOrEmpty(label) ? "A" : label.Substring(0, 1).ToUpperInvariant();
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            RectangleF rect = new RectangleF(10, 10, 108, 108);
            using (GraphicsPath path = RoundedRectangle(rect, 28))
            using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(76, 191, 255), Color.FromArgb(122, 236, 177), LinearGradientMode.ForwardDiagonal))
            using (Pen border = new Pen(Color.FromArgb(160, 255, 255, 255), 2.0f))
            {
                g.FillPath(brush, path);
                g.DrawPath(border, path);
            }

            using (Font font = new Font("Segoe UI", 46.0f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(245, 250, 252)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, font, textBrush, rect, format);
            }
        }

        return bitmap;
    }

    private static string ResolveExecutablePath(string command)
    {
        string fileName;
        string arguments;
        SplitCommandLine(command, out fileName, out arguments);
        string uriIconPath = ResolveUriIconPath(fileName);
        if (!string.IsNullOrEmpty(uriIconPath))
        {
            return uriIconPath;
        }

        if (fileName.Length == 0 || IsShellCommand(fileName))
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(fileName).Trim().Trim('"');
        if (File.Exists(expanded))
        {
            return expanded;
        }

        string[] candidates = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), expanded)
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
            {
                return candidates[i];
            }
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] directories = path.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < directories.Length; i++)
        {
            try
            {
                string candidate = Path.Combine(directories[i].Trim(), expanded);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string ResolveUriIconPath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        if (fileName.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ImmersiveControlPanel",
                "SystemSettings.exe");
            if (File.Exists(settingsPath))
            {
                return settingsPath;
            }

            string controlPanelPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "control.exe");
            if (File.Exists(controlPanelPath))
            {
                return controlPanelPath;
            }
        }

        return string.Empty;
    }

    private static void SplitCommandLine(string command, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (IsShellCommand(value) || File.Exists(value))
        {
            fileName = value;
            return;
        }

        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                fileName = value.Substring(1, end - 1);
                arguments = value.Substring(end + 1).Trim();
                return;
            }
        }

        int split = value.IndexOf(' ');
        if (split > 0)
        {
            string candidate = value.Substring(0, split);
            if (File.Exists(candidate) || candidate.IndexOf('\\') < 0)
            {
                fileName = candidate;
                arguments = value.Substring(split + 1).Trim();
                return;
            }
        }

        fileName = value;
    }

    private static bool IsShellCommand(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int colon = value.IndexOf(':');
        if (colon <= 1)
        {
            return false;
        }

        int slash = value.IndexOf('\\');
        return slash < 0 || slash > colon;
    }

    private int S(int value)
    {
        return (int)Math.Round(value * Math.Min(this.scale, 1.15f));
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DockPreviewForm : Form
{
    private IntPtr sourceWindow;
    private IntPtr thumbnailHandle;
    private string title;
    private float scale;
    private Rectangle thumbnailRect;
    private Rectangle closeButtonRect;
    private bool closeButtonHovered;

    public DockPreviewForm()
    {
        this.title = string.Empty;
        this.sourceWindow = IntPtr.Zero;
        this.thumbnailHandle = IntPtr.Zero;
        this.thumbnailRect = Rectangle.Empty;
        this.closeButtonRect = Rectangle.Empty;
        this.closeButtonHovered = false;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(33, 33, 36);
        this.Opacity = 0.94;
        this.Size = new Size(S(390), S(300));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    public bool ContainsScreenPoint(Point point)
    {
        return this.Visible && this.Bounds.Contains(point);
    }

    public void PrepareHidden()
    {
        if (!this.IsHandleCreated)
        {
            IntPtr handle = this.Handle;
        }
    }

    public void ShowPreview(Form owner, IntPtr sourceWindow, string title, Rectangle anchorRect, bool topMost)
    {
        if (sourceWindow == IntPtr.Zero || !NativeMethods.IsApplicationWindowVisible(sourceWindow))
        {
            HidePreview();
            return;
        }

        this.title = string.IsNullOrEmpty(title) ? "Window" : title;
        if (this.sourceWindow != sourceWindow)
        {
            UnregisterThumbnail();
            this.sourceWindow = sourceWindow;
            if (this.IsHandleCreated)
            {
                RegisterThumbnail();
            }
        }

        Size sourceSize = NativeMethods.QueryThumbnailSourceSize(this.thumbnailHandle);
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            sourceSize = new Size(16, 9);
        }

        Size desiredSize = CalculatePreviewSize(sourceSize);
        Rectangle screen = Screen.FromRectangle(anchorRect).WorkingArea;
        int left = anchorRect.Left + (anchorRect.Width - desiredSize.Width) / 2;
        left = Math.Max(screen.Left + S(8), Math.Min(left, screen.Right - desiredSize.Width - S(8)));
        int top = anchorRect.Top - desiredSize.Height - S(12);
        if (top < screen.Top + S(8))
        {
            top = anchorRect.Bottom + S(12);
        }

        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        this.Location = new Point(left, top);
        this.TopMost = topMost;
        if (!this.Visible)
        {
            this.Show(owner);
        }

        this.Refresh();
        NativeMethods.SetWindowPos(
            this.Handle,
            topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
            left,
            top,
            desiredSize.Width,
            desiredSize.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        UpdateThumbnailPlacement();
        this.Refresh();
    }

    public void HidePreview()
    {
        if (this.Visible)
        {
            this.Hide();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterThumbnail();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterThumbnail();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(10)))
        {
            this.Region = new Region(path);
        }

        UpdateThumbnailPlacement();
        UpdateCloseButtonRect();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF shellRect = new RectangleF(0, 0, this.Width - 1, this.Height - 1);
        using (GraphicsPath shell = RoundedRectangle(shellRect, S(10)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(246, 34, 34, 38)))
        using (Pen border = new Pen(Color.FromArgb(105, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, shell);
            g.DrawPath(border, shell);
        }

        Rectangle headerRect = new Rectangle(S(1), S(1), Math.Max(1, this.Width - S(2)), GetHeaderHeight());
        using (GraphicsPath headerPath = RoundedRectangle(headerRect, S(10)))
        using (SolidBrush headerBrush = new SolidBrush(Color.FromArgb(118, 10, 10, 12)))
        {
            g.FillPath(headerBrush, headerPath);
        }

        Rectangle titleRect = new Rectangle(S(15), S(8), Math.Max(1, this.Width - S(62)), Math.Max(S(30), GetHeaderHeight() - S(16)));
        using (Font font = new Font("Segoe UI", Math.Max(20.0f, 24.5f * Math.Min(this.scale, 1.15f)), FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(245, 245, 245, 245)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(this.title, font, brush, titleRect, format);
        }

        DrawCloseButton(g);

        if (this.thumbnailRect.Width > 0 && this.thumbnailRect.Height > 0)
        {
            using (Pen previewBorder = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1.0f, this.scale)))
            {
                Rectangle borderRect = this.thumbnailRect;
                borderRect.Width -= 1;
                borderRect.Height -= 1;
                g.DrawRectangle(previewBorder, borderRect);
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || this.sourceWindow == IntPtr.Zero)
        {
            return;
        }

        if (this.closeButtonRect.Contains(e.Location))
        {
            NativeMethods.RequestCloseWindow(this.sourceWindow);
            HidePreview();
            return;
        }

        if (this.thumbnailRect.Contains(e.Location))
        {
            NativeMethods.ActivateWindow(this.sourceWindow);
            HidePreview();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hovered = this.closeButtonRect.Contains(e.Location);
        if (hovered != this.closeButtonHovered)
        {
            this.closeButtonHovered = hovered;
            this.Invalidate(this.closeButtonRect);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (this.closeButtonHovered)
        {
            this.closeButtonHovered = false;
            this.Invalidate(this.closeButtonRect);
        }
    }

    private Size CalculatePreviewSize(Size sourceSize)
    {
        int maxThumbnailWidth = S(460);
        int maxThumbnailHeight = S(260);
        double scaleFactor = Math.Min(
            maxThumbnailWidth / (double)Math.Max(1, sourceSize.Width),
            maxThumbnailHeight / (double)Math.Max(1, sourceSize.Height));
        scaleFactor = Math.Min(1.0, scaleFactor);
        int thumbWidth = Math.Max(S(180), (int)Math.Round(sourceSize.Width * scaleFactor));
        int thumbHeight = Math.Max(S(100), (int)Math.Round(sourceSize.Height * scaleFactor));
        int width = thumbWidth + S(24);
        int height = thumbHeight + GetHeaderHeight() + S(18);
        return new Size(width, height);
    }

    private void RegisterThumbnail()
    {
        if (this.thumbnailHandle != IntPtr.Zero || this.sourceWindow == IntPtr.Zero || !this.IsHandleCreated)
        {
            return;
        }

        if (NativeMethods.RegisterDwmThumbnail(this.Handle, this.sourceWindow, out this.thumbnailHandle))
        {
            UpdateThumbnailPlacement();
        }
    }

    private void UnregisterThumbnail()
    {
        if (this.thumbnailHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterDwmThumbnail(this.thumbnailHandle);
            this.thumbnailHandle = IntPtr.Zero;
        }
    }

    private void UpdateThumbnailPlacement()
    {
        if (this.thumbnailHandle == IntPtr.Zero || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        int headerHeight = GetHeaderHeight();
        Rectangle bounds = new Rectangle(S(12), headerHeight + S(8), Math.Max(1, this.Width - S(24)), Math.Max(1, this.Height - headerHeight - S(18)));
        Size sourceSize = NativeMethods.QueryThumbnailSourceSize(this.thumbnailHandle);
        if (sourceSize.Width > 0 && sourceSize.Height > 0)
        {
            bounds = FitInside(bounds, sourceSize);
        }

        this.thumbnailRect = bounds;
        NativeMethods.UpdateDwmThumbnail(this.thumbnailHandle, bounds, 255);
    }

    private int GetHeaderHeight()
    {
        return S(58);
    }

    private void UpdateCloseButtonRect()
    {
        int size = S(28);
        this.closeButtonRect = new Rectangle(Math.Max(S(8), this.Width - size - S(12)), S(10), size, size);
    }

    private void DrawCloseButton(Graphics g)
    {
        UpdateCloseButtonRect();
        Color backgroundColor = this.closeButtonHovered
            ? Color.FromArgb(185, 232, 72, 74)
            : Color.FromArgb(72, 255, 255, 255);
        using (GraphicsPath path = RoundedRectangle(this.closeButtonRect, this.closeButtonRect.Height / 2.0f))
        using (SolidBrush background = new SolidBrush(backgroundColor))
        {
            g.FillPath(background, path);
        }

        float pad = this.closeButtonRect.Width * 0.32f;
        using (Pen pen = new Pen(Color.FromArgb(245, 255, 255, 255), Math.Max(1.4f, 1.8f * Math.Min(this.scale, 1.15f))))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(
                pen,
                this.closeButtonRect.Left + pad,
                this.closeButtonRect.Top + pad,
                this.closeButtonRect.Right - pad,
                this.closeButtonRect.Bottom - pad);
            g.DrawLine(
                pen,
                this.closeButtonRect.Right - pad,
                this.closeButtonRect.Top + pad,
                this.closeButtonRect.Left + pad,
                this.closeButtonRect.Bottom - pad);
        }
    }

    private static Rectangle FitInside(Rectangle bounds, Size sourceSize)
    {
        double scale = Math.Min(
            bounds.Width / (double)Math.Max(1, sourceSize.Width),
            bounds.Height / (double)Math.Max(1, sourceSize.Height));
        int width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
        int height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
        int left = bounds.Left + (bounds.Width - width) / 2;
        int top = bounds.Top + (bounds.Height - height) / 2;
        return new Rectangle(left, top, width, height);
    }

    private int S(int value)
    {
        return (int)Math.Round(value * Math.Min(this.scale, 1.15f));
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

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

internal sealed class SettingsForm : Form
{
    private readonly WidgetForm owner;
    private readonly WidgetSettings baseline;
    private NumericUpDown widthBox;
    private NumericUpDown heightBox;
    private NumericUpDown leftXBox;
    private NumericUpDown bottomYBox;
    private NumericUpDown backgroundTransparencyBox;
    private NumericUpDown applicationTransparencyBox;
    private NumericUpDown clockWidthBox;
    private NumericUpDown clockHeightBox;
    private NumericUpDown clockLeftXBox;
    private NumericUpDown clockBottomYBox;
    private NumericUpDown clockTransparencyBox;
    private NumericUpDown dockIconSizeBox;
    private NumericUpDown dockMagnificationBox;
    private NumericUpDown dockBottomMarginBox;
    private TrackBar widthSlider;
    private TrackBar heightSlider;
    private TrackBar leftXSlider;
    private TrackBar bottomYSlider;
    private TrackBar backgroundTransparencySlider;
    private TrackBar applicationTransparencySlider;
    private TrackBar clockWidthSlider;
    private TrackBar clockHeightSlider;
    private TrackBar clockLeftXSlider;
    private TrackBar clockBottomYSlider;
    private TrackBar clockTransparencySlider;
    private TrackBar dockIconSizeSlider;
    private TrackBar dockMagnificationSlider;
    private TrackBar dockBottomMarginSlider;
    private ComboBox visibilityCombo;
    private ComboBox thermalTestCombo;
    private Button alertTestButton;
    private CheckBox startupCheck;
    private CheckBox powerSavingCheck;
    private CheckBox hoverOpacityCheck;
    private CheckBox clockUse24HourCheck;
    private CheckBox clockCalendarCheck;
    private CheckBox clockPowerCheck;
    private CheckBox dockEnabledCheck;
    private TextBox dockItemsBox;
    private FlowLayoutPanel availableMetricsPanel;
    private TableLayoutPanel metricSlotsPanel;
    private Panel[] metricSlotPanels;
    private string draggedMetricId;
    private int draggedSourceSlotIndex;
    private bool initializing;
    private bool saved;

    public bool OwnerFormClosing { get; set; }

    public SettingsForm(WidgetForm owner, WidgetSettings baseline)
    {
        this.owner = owner;
        this.baseline = baseline.Clone();
        this.baseline.Normalize();

        this.Text = "性能小窗设置";
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = false;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.AutoScroll = true;
        this.AutoScrollMinSize = GetDesiredClientSize();
        this.ClientSize = FitClientSizeToScreen(GetDesiredClientSize());
        this.Font = new Font("Segoe UI", 10.0f);
        this.BackColor = DarkTheme.Window;
        this.ForeColor = DarkTheme.Text;

        BuildControls();
        LoadControls(this.baseline);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!this.saved && !this.OwnerFormClosing)
        {
            this.owner.RevertSettings(this.baseline);
        }

        base.OnFormClosing(e);
    }

    private void BuildControls()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Location = new Point(0, 0);
        root.Size = GetDesiredClientSize();
        root.Padding = new Padding(26, 20, 26, 20);
        root.BackColor = DarkTheme.Window;
        root.ColumnCount = 4;
        root.RowCount = 25;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        this.Controls.Add(root);

        this.widthBox = BuildNumberBox(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightBox = BuildNumberBox(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.backgroundTransparencyBox.Increment = 1;
        this.applicationTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.applicationTransparencyBox.Increment = 1;
        this.clockWidthBox = BuildNumberBox(WidgetSettings.MinClockWidth, WidgetSettings.MaxClockWidth);
        this.clockHeightBox = BuildNumberBox(WidgetSettings.MinClockHeight, WidgetSettings.MaxClockHeight);
        this.clockLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.clockBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.clockTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.clockTransparencyBox.Increment = 1;
        this.dockIconSizeBox = BuildNumberBox(WidgetSettings.MinDockIconSize, WidgetSettings.MaxDockIconSize);
        this.dockIconSizeBox.Increment = 2;
        this.dockMagnificationBox = BuildNumberBox(WidgetSettings.MinDockMagnificationPercent, WidgetSettings.MaxDockMagnificationPercent);
        this.dockMagnificationBox.Increment = 5;
        this.dockBottomMarginBox = BuildNumberBox(WidgetSettings.MinDockBottomMargin, WidgetSettings.MaxDockBottomMargin);
        this.dockBottomMarginBox.Increment = 2;
        this.widthSlider = BuildSlider(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightSlider = BuildSlider(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.applicationTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.clockWidthSlider = BuildSlider(WidgetSettings.MinClockWidth, WidgetSettings.MaxClockWidth);
        this.clockHeightSlider = BuildSlider(WidgetSettings.MinClockHeight, WidgetSettings.MaxClockHeight);
        this.clockLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.clockBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.clockTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.dockIconSizeSlider = BuildSlider(WidgetSettings.MinDockIconSize, WidgetSettings.MaxDockIconSize);
        this.dockMagnificationSlider = BuildSlider(WidgetSettings.MinDockMagnificationPercent, WidgetSettings.MaxDockMagnificationPercent);
        this.dockBottomMarginSlider = BuildSlider(WidgetSettings.MinDockBottomMargin, WidgetSettings.MaxDockBottomMargin);
        this.visibilityCombo = BuildCombo();
        this.thermalTestCombo = BuildCombo();
        this.alertTestButton = new Button();
        this.alertTestButton.Width = 96;
        this.alertTestButton.Height = 44;
        this.alertTestButton.MinimumSize = new Size(96, 44);
        this.alertTestButton.AutoSize = true;
        this.alertTestButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        this.alertTestButton.Padding = new Padding(14, 6, 14, 6);
        this.alertTestButton.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetAlertTestButtonState(!GetAlertTestButtonState());
            this.owner.PreviewSettings(ReadControls());
        };
        this.startupCheck = new CheckBox();
        this.startupCheck.Text = "开机自动启动";
        this.startupCheck.AutoSize = true;
        this.startupCheck.ForeColor = DarkTheme.Text;
        this.startupCheck.BackColor = DarkTheme.Window;
        this.startupCheck.CheckedChanged += OnSettingChanged;
        this.powerSavingCheck = new CheckBox();
        this.powerSavingCheck.Text = "节能模式";
        this.powerSavingCheck.AutoSize = true;
        this.powerSavingCheck.ForeColor = DarkTheme.Text;
        this.powerSavingCheck.BackColor = DarkTheme.Window;
        this.powerSavingCheck.CheckedChanged += OnSettingChanged;
        this.hoverOpacityCheck = new CheckBox();
        this.hoverOpacityCheck.Text = "悬停透明 95%";
        this.hoverOpacityCheck.AutoSize = true;
        this.hoverOpacityCheck.ForeColor = DarkTheme.Text;
        this.hoverOpacityCheck.BackColor = DarkTheme.Window;
        this.hoverOpacityCheck.CheckedChanged += OnSettingChanged;
        this.clockUse24HourCheck = new CheckBox();
        this.clockUse24HourCheck.Text = "24 小时制";
        this.clockUse24HourCheck.AutoSize = true;
        this.clockUse24HourCheck.ForeColor = DarkTheme.Text;
        this.clockUse24HourCheck.BackColor = DarkTheme.Window;
        this.clockUse24HourCheck.CheckedChanged += OnSettingChanged;
        this.clockCalendarCheck = new CheckBox();
        this.clockCalendarCheck.Text = "启用日历";
        this.clockCalendarCheck.AutoSize = true;
        this.clockCalendarCheck.ForeColor = DarkTheme.Text;
        this.clockCalendarCheck.BackColor = DarkTheme.Window;
        this.clockCalendarCheck.CheckedChanged += OnSettingChanged;
        this.clockPowerCheck = new CheckBox();
        this.clockPowerCheck.Text = "显示功耗";
        this.clockPowerCheck.AutoSize = true;
        this.clockPowerCheck.ForeColor = DarkTheme.Text;
        this.clockPowerCheck.BackColor = DarkTheme.Window;
        this.clockPowerCheck.CheckedChanged += OnSettingChanged;
        this.dockEnabledCheck = new CheckBox();
        this.dockEnabledCheck.Text = "启用 Dock";
        this.dockEnabledCheck.AutoSize = true;
        this.dockEnabledCheck.ForeColor = DarkTheme.Text;
        this.dockEnabledCheck.BackColor = DarkTheme.Window;
        this.dockEnabledCheck.CheckedChanged += OnSettingChanged;
        this.dockItemsBox = new TextBox();
        this.dockItemsBox.Multiline = true;
        this.dockItemsBox.ScrollBars = ScrollBars.Vertical;
        this.dockItemsBox.AcceptsReturn = true;
        this.dockItemsBox.AcceptsTab = false;
        this.dockItemsBox.Dock = DockStyle.Fill;
        this.dockItemsBox.Font = new Font("Consolas", 9.5f);
        this.dockItemsBox.BackColor = DarkTheme.Control;
        this.dockItemsBox.ForeColor = DarkTheme.Text;
        this.dockItemsBox.BorderStyle = BorderStyle.FixedSingle;
        this.dockItemsBox.TextChanged += OnSettingChanged;
        this.metricSlotPanels = new Panel[WidgetSettings.DefaultMetricOrder.Length];

        Label title = new Label();
        title.Text = "性能小窗设置";
        title.Font = new Font("Segoe UI", 16.0f, FontStyle.Bold);
        title.ForeColor = DarkTheme.Text;
        title.BackColor = DarkTheme.Window;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        root.SetColumnSpan(title, 4);
        root.Controls.Add(title, 0, 0);

        AddSliderRow(root, 1, "窗口宽度", this.widthBox, this.widthSlider);
        AddSliderRow(root, 2, "窗口高度", this.heightBox, this.heightSlider);
        AddSliderRow(root, 3, "位置 X", this.leftXBox, this.leftXSlider);
        AddSliderRow(root, 4, "位置 Y", this.bottomYBox, this.bottomYSlider);
        AddSliderRow(root, 5, "黑色背景透明度", this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        AddSliderRow(root, 6, "内容透明度", this.applicationTransparencyBox, this.applicationTransparencySlider);
        AddEditorRow(root, 7, "可见性", this.visibilityCombo);
        AddLabel(root, 8, "告警测试");
        Control alertEditor = BuildButtonEditor(this.alertTestButton);
        root.SetColumnSpan(alertEditor, 2);
        root.Controls.Add(alertEditor, 1, 8);
        AddEditorRow(root, 21, "温度测试", this.thermalTestCombo);

        FlowLayoutPanel runtimeOptions = new FlowLayoutPanel();
        runtimeOptions.Dock = DockStyle.Fill;
        runtimeOptions.FlowDirection = FlowDirection.LeftToRight;
        runtimeOptions.WrapContents = false;
        runtimeOptions.BackColor = DarkTheme.Window;
        runtimeOptions.Padding = new Padding(0, 10, 0, 0);
        this.startupCheck.Margin = new Padding(0, 0, 34, 0);
        this.powerSavingCheck.Margin = new Padding(0, 0, 34, 0);
        this.hoverOpacityCheck.Margin = new Padding(0, 0, 0, 0);
        runtimeOptions.Controls.Add(this.startupCheck);
        runtimeOptions.Controls.Add(this.powerSavingCheck);
        runtimeOptions.Controls.Add(this.hoverOpacityCheck);
        AddLabel(root, 9, "运行选项");
        root.SetColumnSpan(runtimeOptions, 2);
        root.Controls.Add(runtimeOptions, 1, 9);

        Control metricLayoutEditor = BuildMetricLayoutSidePanel();
        root.SetRowSpan(metricLayoutEditor, 23);
        root.Controls.Add(metricLayoutEditor, 3, 1);

        FlowLayoutPanel clockOptions = new FlowLayoutPanel();
        clockOptions.Dock = DockStyle.Fill;
        clockOptions.FlowDirection = FlowDirection.LeftToRight;
        clockOptions.WrapContents = false;
        clockOptions.BackColor = DarkTheme.Window;
        clockOptions.Padding = new Padding(0, 10, 0, 0);
        clockOptions.Controls.Add(this.clockUse24HourCheck);
        AddLabel(root, 10, "时间窗口");
        root.SetColumnSpan(clockOptions, 2);
        root.Controls.Add(clockOptions, 1, 10);
        AddSliderRow(root, 11, "时间宽度", this.clockWidthBox, this.clockWidthSlider);
        AddSliderRow(root, 12, "时间高度", this.clockHeightBox, this.clockHeightSlider);
        AddSliderRow(root, 13, "位置 X", this.clockLeftXBox, this.clockLeftXSlider);
        AddSliderRow(root, 14, "位置 Y", this.clockBottomYBox, this.clockBottomYSlider);
        FlowLayoutPanel calendarOptions = new FlowLayoutPanel();
        calendarOptions.Dock = DockStyle.Fill;
        calendarOptions.FlowDirection = FlowDirection.LeftToRight;
        calendarOptions.WrapContents = false;
        calendarOptions.BackColor = DarkTheme.Window;
        calendarOptions.Padding = new Padding(0, 10, 0, 0);
        this.clockCalendarCheck.Margin = new Padding(0, 0, 34, 0);
        this.clockPowerCheck.Margin = new Padding(0, 0, 0, 0);
        calendarOptions.Controls.Add(this.clockCalendarCheck);
        calendarOptions.Controls.Add(this.clockPowerCheck);
        AddLabel(root, 15, "时间信息");
        root.SetColumnSpan(calendarOptions, 2);
        root.Controls.Add(calendarOptions, 1, 15);

        FlowLayoutPanel dockOptions = new FlowLayoutPanel();
        dockOptions.Dock = DockStyle.Fill;
        dockOptions.FlowDirection = FlowDirection.LeftToRight;
        dockOptions.WrapContents = false;
        dockOptions.BackColor = DarkTheme.Window;
        dockOptions.Padding = new Padding(0, 10, 0, 0);
        dockOptions.Controls.Add(this.dockEnabledCheck);
        AddLabel(root, 16, "Dock");
        root.SetColumnSpan(dockOptions, 2);
        root.Controls.Add(dockOptions, 1, 16);
        AddSliderRow(root, 17, "Dock 图标", this.dockIconSizeBox, this.dockIconSizeSlider);
        AddSliderRow(root, 18, "Dock 放大", this.dockMagnificationBox, this.dockMagnificationSlider);
        AddSliderRow(root, 19, "Dock 底距", this.dockBottomMarginBox, this.dockBottomMarginSlider);
        AddEditorRow(root, 20, "Dock 项目", this.dockItemsBox);

        Button saveButton = new Button();
        saveButton.Text = "保存";
        saveButton.Width = 104;
        saveButton.Height = 36;
        saveButton.Click += delegate
        {
            this.owner.SaveSettings(ReadControls());
            this.saved = true;
            this.Close();
        };

        Button cancelButton = new Button();
        cancelButton.Text = "取消";
        cancelButton.Width = 96;
        cancelButton.Height = 36;
        cancelButton.Click += delegate { this.Close(); };

        Button resetButton = new Button();
        resetButton.Text = "重置";
        resetButton.Width = 96;
        resetButton.Height = 36;
        resetButton.Click += delegate
        {
            WidgetSettings defaults = WidgetSettings.CreateDefaults();
            defaults.StartupEnabled = Program.IsStartupEnabled();
            LoadControls(defaults);
            this.owner.PreviewSettings(ReadControls());
        };

        StyleButton(saveButton, true);
        StyleButton(cancelButton, false);
        StyleButton(resetButton, false);
        StyleButton(this.alertTestButton, false);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Dock = DockStyle.Fill;
        buttons.BackColor = DarkTheme.Window;
        buttons.Padding = new Padding(0, 10, 0, 0);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(resetButton);
        root.SetColumnSpan(buttons, 4);
        root.Controls.Add(buttons, 0, 24);

        this.visibilityCombo.Items.Add(new ComboOption("仅桌面可见", WidgetVisibilityMode.DesktopOnly));
        this.visibilityCombo.Items.Add(new ComboOption("一直可见", WidgetVisibilityMode.AlwaysVisible));
        this.visibilityCombo.Items.Add(new ComboOption("仅全屏不可见", WidgetVisibilityMode.HideWhenFullscreen));
        this.thermalTestCombo.Items.Add(new ComboOption("关闭", ThermalTestMode.Off));
        this.thermalTestCombo.Items.Add(new ComboOption("模拟 75 度", ThermalTestMode.Simulate75));
        this.thermalTestCombo.Items.Add(new ComboOption("模拟 100 度", ThermalTestMode.Simulate100));

        WirePair(this.widthBox, this.widthSlider);
        WirePair(this.heightBox, this.heightSlider);
        WirePair(this.leftXBox, this.leftXSlider);
        WirePair(this.bottomYBox, this.bottomYSlider);
        WirePair(this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        WirePair(this.applicationTransparencyBox, this.applicationTransparencySlider);
        WirePair(this.clockWidthBox, this.clockWidthSlider);
        WirePair(this.clockHeightBox, this.clockHeightSlider);
        WirePair(this.clockLeftXBox, this.clockLeftXSlider);
        WirePair(this.clockBottomYBox, this.clockBottomYSlider);
        WirePair(this.clockTransparencyBox, this.clockTransparencySlider);
        WirePair(this.dockIconSizeBox, this.dockIconSizeSlider);
        WirePair(this.dockMagnificationBox, this.dockMagnificationSlider);
        WirePair(this.dockBottomMarginBox, this.dockBottomMarginSlider);
    }

    private static Size GetDesiredClientSize()
    {
        return new Size(1280, 1440);
    }

    private static Size FitClientSizeToScreen(Size desiredSize)
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int margin = 64;
        int maxWidth = Math.Max(640, workArea.Width - margin);
        int maxHeight = Math.Max(520, workArea.Height - margin);
        int width = Math.Min(desiredSize.Width + SystemInformation.VerticalScrollBarWidth, maxWidth);
        int height = Math.Min(desiredSize.Height, maxHeight);
        return new Size(width, height);
    }

    private NumericUpDown BuildNumberBox(int min, int max)
    {
        NumericUpDown box = new NumericUpDown();
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = 10;
        box.Dock = DockStyle.Fill;
        box.Font = new Font("Segoe UI", 10.5f);
        box.BackColor = DarkTheme.Control;
        box.ForeColor = DarkTheme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.ValueChanged += OnSettingChanged;
        return box;
    }

    private TrackBar BuildSlider(int min, int max)
    {
        TrackBar slider = new TrackBar();
        slider.Minimum = min;
        slider.Maximum = max;
        slider.TickStyle = TickStyle.None;
        slider.AutoSize = false;
        slider.Height = 34;
        slider.Dock = DockStyle.Fill;
        slider.BackColor = DarkTheme.Window;
        slider.SmallChange = 1;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
        slider.ValueChanged += OnSettingChanged;
        return slider;
    }

    private ComboBox BuildCombo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.Font = new Font("Segoe UI", 10.5f);
        combo.BackColor = DarkTheme.Control;
        combo.ForeColor = DarkTheme.Text;
        combo.SelectedIndexChanged += OnSettingChanged;
        return combo;
    }

    private Control BuildMetricLayoutSidePanel()
    {
        TableLayoutPanel panel = new TableLayoutPanel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DarkTheme.Window;
        panel.ColumnCount = 1;
        panel.RowCount = 2;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Padding = new Padding(12, 0, 0, 0);

        Label label = new Label();
        label.Text = "栏目排序";
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DarkTheme.SubtleText;
        label.BackColor = DarkTheme.Window;

        Control editor = BuildMetricLayoutEditor();
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(editor, 0, 1);
        return panel;
    }

    private Control BuildMetricLayoutEditor()
    {
        TableLayoutPanel editor = new TableLayoutPanel();
        editor.Dock = DockStyle.Fill;
        editor.BackColor = DarkTheme.Window;
        editor.ColumnCount = 1;
        editor.RowCount = 2;
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        this.availableMetricsPanel = new FlowLayoutPanel();
        this.availableMetricsPanel.Dock = DockStyle.Fill;
        this.availableMetricsPanel.BackColor = DarkTheme.Control;
        this.availableMetricsPanel.Padding = new Padding(10, 10, 10, 8);
        this.availableMetricsPanel.AllowDrop = true;
        this.availableMetricsPanel.DragEnter += OnMetricDragEnter;
        this.availableMetricsPanel.DragDrop += delegate
        {
            if (this.draggedSourceSlotIndex >= 0)
            {
                SetSlotMetric(this.draggedSourceSlotIndex, string.Empty);
                RefreshMetricLayoutEditor();
                this.owner.PreviewSettings(ReadControls());
            }
        };

        this.metricSlotsPanel = new TableLayoutPanel();
        this.metricSlotsPanel.Dock = DockStyle.Fill;
        this.metricSlotsPanel.BackColor = DarkTheme.Window;
        int slotColumns = 2;
        int slotRows = (WidgetSettings.DefaultMetricOrder.Length + slotColumns - 1) / slotColumns;
        this.metricSlotsPanel.ColumnCount = slotColumns;
        this.metricSlotsPanel.RowCount = slotRows;
        for (int row = 0; row < slotRows; row++)
        {
            this.metricSlotsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f / slotRows));
        }

        for (int column = 0; column < slotColumns; column++)
        {
            this.metricSlotsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f / slotColumns));
        }

        for (int i = 0; i < WidgetSettings.DefaultMetricOrder.Length; i++)
        {
            Panel slot = BuildMetricSlot(i);
            this.metricSlotPanels[i] = slot;
            this.metricSlotsPanel.Controls.Add(slot, i % slotColumns, i / slotColumns);
        }

        editor.Controls.Add(this.availableMetricsPanel, 0, 0);
        editor.Controls.Add(this.metricSlotsPanel, 0, 1);
        return editor;
    }

    private Label BuildMetricChip(string metricId)
    {
        Label chip = new Label();
        chip.Text = WidgetSettings.MetricDisplayName(metricId);
        chip.Tag = metricId;
        chip.Width = 108;
        chip.Height = 34;
        chip.Margin = new Padding(0, 0, 8, 6);
        chip.TextAlign = ContentAlignment.MiddleCenter;
        chip.BackColor = Color.FromArgb(58, 58, 62);
        chip.ForeColor = DarkTheme.Text;
        chip.BorderStyle = BorderStyle.FixedSingle;
        chip.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        chip.Cursor = Cursors.Hand;
        chip.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                BeginMetricDrag(metricId, -1, chip);
            }
        };
        return chip;
    }

    private Panel BuildMetricSlot(int index)
    {
        Panel slot = new Panel();
        slot.Dock = DockStyle.Fill;
        slot.Margin = new Padding(0, 6, index % 2 == 0 ? 10 : 0, 0);
        slot.BackColor = DarkTheme.Control;
        slot.BorderStyle = BorderStyle.FixedSingle;
        slot.AllowDrop = true;
        slot.Tag = string.Empty;
        slot.DragEnter += OnMetricDragEnter;
        slot.DragDrop += delegate { DropMetricToSlot(index); };
        slot.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                string metricId = GetSlotMetric(index);
                if (metricId.Length > 0)
                {
                    BeginMetricDrag(metricId, index, slot);
                }
            }
        };

        Label number = new Label();
        number.Name = "slotNumber";
        number.Text = (index + 1).ToString();
        number.AutoSize = true;
        number.Location = new Point(14, 12);
        number.TextAlign = ContentAlignment.TopLeft;
        number.ForeColor = DarkTheme.SubtleText;
        number.BackColor = Color.Transparent;
        number.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        number.UseCompatibleTextRendering = true;

        Label content = new Label();
        content.Name = "slotContent";
        content.AutoSize = false;
        content.TextAlign = ContentAlignment.MiddleCenter;
        content.ForeColor = DarkTheme.Text;
        content.BackColor = Color.Transparent;
        content.Font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        content.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                string metricId = GetSlotMetric(index);
                if (metricId.Length > 0)
                {
                    BeginMetricDrag(metricId, index, content);
                }
            }
        };

        Button remove = new Button();
        remove.Name = "slotRemove";
        remove.Text = "x";
        remove.Width = 24;
        remove.Height = 22;
        remove.Visible = false;
        remove.FlatStyle = FlatStyle.Flat;
        remove.FlatAppearance.BorderColor = DarkTheme.Border;
        remove.FlatAppearance.BorderSize = 1;
        remove.BackColor = Color.FromArgb(54, 54, 58);
        remove.ForeColor = DarkTheme.Text;
        remove.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        remove.Click += delegate
        {
            SetSlotMetric(index, string.Empty);
            RefreshMetricLayoutEditor();
            this.owner.PreviewSettings(ReadControls());
        };

        slot.Controls.Add(number);
        slot.Controls.Add(content);
        slot.Controls.Add(remove);
        slot.Resize += delegate
        {
            content.Location = new Point(30, 8);
            content.Size = new Size(Math.Max(10, slot.ClientSize.Width - 60), Math.Max(20, slot.ClientSize.Height - 22));
            remove.Location = new Point(Math.Max(0, slot.ClientSize.Width - remove.Width - 7), Math.Max(0, slot.ClientSize.Height - remove.Height - 7));
        };

        return slot;
    }

    private void AddSliderRow(TableLayoutPanel root, int row, string labelText, NumericUpDown editor, TrackBar slider)
    {
        AddLabel(root, row, labelText);
        root.Controls.Add(editor, 1, row);
        root.Controls.Add(slider, 2, row);
    }

    private void AddEditorRow(TableLayoutPanel root, int row, string labelText, Control editor)
    {
        AddLabel(root, row, labelText);
        root.Controls.Add(editor, 1, row);
        root.SetColumnSpan(editor, 2);
    }

    private Control BuildButtonEditor(Button button)
    {
        Panel panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DarkTheme.Window;
        button.Anchor = AnchorStyles.Left;
        button.Location = new Point(0, 8);
        panel.Controls.Add(button);
        panel.Resize += delegate
        {
            button.Location = new Point(0, Math.Max(0, (panel.ClientSize.Height - button.Height) / 2));
        };
        return panel;
    }

    private void AddLabel(TableLayoutPanel root, int row, string labelText)
    {
        Label label = new Label();
        label.Text = labelText;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DarkTheme.SubtleText;
        label.BackColor = DarkTheme.Window;
        root.Controls.Add(label, 0, row);
    }

    private void WirePair(NumericUpDown box, TrackBar slider)
    {
        box.ValueChanged += delegate
        {
            if (this.initializing)
            {
                return;
            }

            int value = (int)box.Value;
            if (slider.Value != value)
            {
                slider.Value = value;
            }
        };

        slider.ValueChanged += delegate
        {
            if (this.initializing)
            {
                return;
            }

            if ((int)box.Value != slider.Value)
            {
                box.Value = slider.Value;
            }
        };
    }

    private void LoadControls(WidgetSettings settings)
    {
        this.initializing = true;
        try
        {
            this.widthBox.Value = settings.Width;
            this.heightBox.Value = settings.Height;
            UpdatePositionRanges(settings.Width, settings.Height);
            UpdateClockPositionRanges(settings.ClockWidth, settings.ClockHeight);
            this.leftXBox.Value = settings.LeftX;
            this.bottomYBox.Value = settings.BottomY;
            this.clockWidthBox.Value = settings.ClockWidth;
            this.clockHeightBox.Value = settings.ClockHeight;
            this.clockLeftXBox.Value = settings.ClockLeftX;
            this.clockBottomYBox.Value = settings.ClockBottomY;
            this.widthSlider.Value = settings.Width;
            this.heightSlider.Value = settings.Height;
            this.leftXSlider.Value = settings.LeftX;
            this.bottomYSlider.Value = settings.BottomY;
            this.clockWidthSlider.Value = settings.ClockWidth;
            this.clockHeightSlider.Value = settings.ClockHeight;
            this.clockLeftXSlider.Value = settings.ClockLeftX;
            this.clockBottomYSlider.Value = settings.ClockBottomY;
            this.backgroundTransparencyBox.Value = settings.BackgroundTransparencyPercent;
            this.backgroundTransparencySlider.Value = settings.BackgroundTransparencyPercent;
            this.applicationTransparencyBox.Value = settings.ApplicationTransparencyPercent;
            this.applicationTransparencySlider.Value = settings.ApplicationTransparencyPercent;
            this.clockTransparencyBox.Value = settings.BackgroundTransparencyPercent;
            this.clockTransparencySlider.Value = settings.BackgroundTransparencyPercent;
            this.dockEnabledCheck.Checked = settings.DockEnabled;
            this.dockIconSizeBox.Value = settings.DockIconSize;
            this.dockIconSizeSlider.Value = settings.DockIconSize;
            this.dockMagnificationBox.Value = settings.DockMagnificationPercent;
            this.dockMagnificationSlider.Value = settings.DockMagnificationPercent;
            this.dockBottomMarginBox.Value = settings.DockBottomMargin;
            this.dockBottomMarginSlider.Value = settings.DockBottomMargin;
            this.dockItemsBox.Text = settings.DockItemsText ?? string.Empty;
            SelectComboValue(this.visibilityCombo, settings.VisibilityMode);
            this.startupCheck.Checked = settings.StartupEnabled;
            this.powerSavingCheck.Checked = settings.PowerSavingEnabled;
            this.hoverOpacityCheck.Checked = settings.HoverOpacityEnabled;
            this.clockUse24HourCheck.Checked = settings.ClockUse24Hour;
            this.clockCalendarCheck.Checked = settings.ClockCalendarEnabled;
            this.clockPowerCheck.Checked = settings.ClockPowerEnabled;
            SelectComboValue(this.thermalTestCombo, settings.ThermalTestMode);
            SetAlertTestButtonState(settings.AlertTestEnabled);
            LoadMetricLayout(settings);
        }
        finally
        {
            this.initializing = false;
        }
    }

    private void OnSettingChanged(object sender, EventArgs e)
    {
        if (this.initializing)
        {
            return;
        }

        UpdatePositionRanges((int)this.widthBox.Value, (int)this.heightBox.Value);
        UpdateClockPositionRanges((int)this.clockWidthBox.Value, (int)this.clockHeightBox.Value);
        this.owner.PreviewSettings(ReadControls());
    }

    private void UpdatePositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.leftXBox, leftMin, leftMax);
            SetTrackRange(this.leftXSlider, leftMin, leftMax);
            SetNumericRange(this.bottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.bottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdateClockPositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.clockLeftXBox, leftMin, leftMax);
            SetTrackRange(this.clockLeftXSlider, leftMin, leftMax);
            SetNumericRange(this.clockBottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.clockBottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private static void SetNumericRange(NumericUpDown box, int min, int max)
    {
        if (box.Value < min)
        {
            box.Value = min;
        }
        else if (box.Value > max)
        {
            box.Value = max;
        }

        box.Minimum = min;
        box.Maximum = max;
    }

    private static void SetTrackRange(TrackBar slider, int min, int max)
    {
        if (slider.Value < min)
        {
            slider.Value = min;
        }
        else if (slider.Value > max)
        {
            slider.Value = max;
        }

        slider.Minimum = min;
        slider.Maximum = max;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
    }

    private WidgetSettings ReadControls()
    {
        WidgetSettings settings = this.baseline.Clone();
        settings.Width = (int)this.widthBox.Value;
        settings.Height = (int)this.heightBox.Value;
        settings.LeftX = (int)this.leftXBox.Value;
        settings.BottomY = (int)this.bottomYBox.Value;
        settings.BackgroundTransparencyPercent = (int)this.backgroundTransparencyBox.Value;
        settings.ApplicationTransparencyPercent = (int)this.applicationTransparencyBox.Value;
        settings.ClockWidth = (int)this.clockWidthBox.Value;
        settings.ClockHeight = (int)this.clockHeightBox.Value;
        settings.ClockLeftX = (int)this.clockLeftXBox.Value;
        settings.ClockBottomY = (int)this.clockBottomYBox.Value;
        settings.ClockTransparencyPercent = settings.BackgroundTransparencyPercent;
        settings.ClockUse24Hour = this.clockUse24HourCheck.Checked;
        settings.ClockCalendarEnabled = this.clockCalendarCheck.Checked;
        settings.ClockPowerEnabled = this.clockPowerCheck.Checked;
        settings.DockEnabled = this.dockEnabledCheck.Checked;
        settings.DockIconSize = (int)this.dockIconSizeBox.Value;
        settings.DockMagnificationPercent = (int)this.dockMagnificationBox.Value;
        settings.DockBottomMargin = (int)this.dockBottomMarginBox.Value;
        settings.DockItemsText = this.dockItemsBox.Text;
        settings.ThermalTestMode = (ThermalTestMode)GetComboValue(this.thermalTestCombo, ThermalTestMode.Off);
        settings.VisibilityMode = (WidgetVisibilityMode)GetComboValue(this.visibilityCombo, WidgetVisibilityMode.DesktopOnly);
        settings.StartupEnabled = this.startupCheck.Checked;
        settings.PowerSavingEnabled = this.powerSavingCheck.Checked;
        settings.HoverOpacityEnabled = this.hoverOpacityCheck.Checked;
        string[] selectedMetrics = ReadMetricSlots(false);
        settings.ShowCpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricCpu);
        settings.ShowMemory = ContainsMetricId(selectedMetrics, WidgetSettings.MetricMemory);
        settings.ShowDisk = ContainsMetricId(selectedMetrics, WidgetSettings.MetricDisk);
        settings.ShowNetwork = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNetwork);
        settings.ShowGpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricGpu);
        settings.ShowNpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNpu);
        settings.AlertTestEnabled = GetAlertTestButtonState();
        settings.MetricOrder = selectedMetrics;
        settings.Normalize();
        return settings;
    }

    private void LoadMetricLayout(WidgetSettings settings)
    {
        if (this.metricSlotPanels == null)
        {
            return;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            SetSlotMetric(i, string.Empty);
        }

        string[] order = settings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        int slotIndex = 0;
        for (int i = 0; i < order.Length && slotIndex < this.metricSlotPanels.Length; i++)
        {
            string metricId = order[i];
            if (!IsMetricShown(settings, metricId))
            {
                continue;
            }

            SetSlotMetric(slotIndex, metricId);
            slotIndex++;
        }

        RefreshMetricLayoutEditor();
    }

    private static bool IsMetricShown(WidgetSettings settings, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowCpu;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowMemory;
        }

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowDisk;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowNetwork;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowGpu;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowNpu;
        }

        return false;
    }

    private void RefreshMetricLayoutEditor()
    {
        if (this.metricSlotPanels != null)
        {
            for (int i = 0; i < this.metricSlotPanels.Length; i++)
            {
                RefreshMetricSlot(i);
            }
        }

        if (this.availableMetricsPanel == null)
        {
            return;
        }

        this.availableMetricsPanel.Controls.Clear();
        for (int i = 0; i < WidgetSettings.DefaultMetricOrder.Length; i++)
        {
            string metricId = WidgetSettings.DefaultMetricOrder[i];
            if (!IsMetricInAnySlot(metricId))
            {
                this.availableMetricsPanel.Controls.Add(BuildMetricChip(metricId));
            }
        }
    }

    private void RefreshMetricSlot(int index)
    {
        Panel slot = this.metricSlotPanels[index];
        string metricId = GetSlotMetric(index);
        Label content = FindChildLabel(slot, "slotContent");
        Button remove = FindChildButton(slot, "slotRemove");
        if (content != null)
        {
            content.Text = metricId.Length > 0 ? WidgetSettings.MetricDisplayName(metricId) : string.Empty;
        }

        if (remove != null)
        {
            remove.Visible = metricId.Length > 0;
        }

        slot.BackColor = metricId.Length > 0 ? Color.FromArgb(58, 58, 62) : DarkTheme.Control;
    }

    private static Label FindChildLabel(Control parent, string name)
    {
        Control[] found = parent.Controls.Find(name, false);
        return found.Length > 0 ? found[0] as Label : null;
    }

    private static Button FindChildButton(Control parent, string name)
    {
        Control[] found = parent.Controls.Find(name, false);
        return found.Length > 0 ? found[0] as Button : null;
    }

    private string GetSlotMetric(int index)
    {
        if (this.metricSlotPanels == null || index < 0 || index >= this.metricSlotPanels.Length)
        {
            return string.Empty;
        }

        return this.metricSlotPanels[index].Tag as string ?? string.Empty;
    }

    private void SetSlotMetric(int index, string metricId)
    {
        if (this.metricSlotPanels == null || index < 0 || index >= this.metricSlotPanels.Length)
        {
            return;
        }

        this.metricSlotPanels[index].Tag = metricId ?? string.Empty;
    }

    private bool IsMetricInAnySlot(string metricId)
    {
        if (this.metricSlotPanels == null)
        {
            return false;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            if (string.Equals(GetSlotMetric(i), metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int FindMetricSlot(string metricId)
    {
        if (this.metricSlotPanels == null)
        {
            return -1;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            if (string.Equals(GetSlotMetric(i), metricId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private string[] ReadMetricSlots(bool includeEmpty)
    {
        List<string> selected = new List<string>();
        if (this.metricSlotPanels != null)
        {
            for (int i = 0; i < this.metricSlotPanels.Length; i++)
            {
                string metricId = GetSlotMetric(i);
                if (metricId.Length > 0 || includeEmpty)
                {
                    selected.Add(metricId);
                }
            }
        }

        return selected.ToArray();
    }

    private static bool ContainsMetricId(string[] metrics, string metricId)
    {
        for (int i = 0; i < metrics.Length; i++)
        {
            if (string.Equals(metrics[i], metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void BeginMetricDrag(string metricId, int sourceSlotIndex, Control source)
    {
        if (string.IsNullOrEmpty(metricId))
        {
            return;
        }

        this.draggedMetricId = metricId;
        this.draggedSourceSlotIndex = sourceSlotIndex;
        source.DoDragDrop(metricId, DragDropEffects.Move);
        this.draggedMetricId = null;
        this.draggedSourceSlotIndex = -1;
    }

    private void OnMetricDragEnter(object sender, DragEventArgs e)
    {
        e.Effect = string.IsNullOrEmpty(this.draggedMetricId) ? DragDropEffects.None : DragDropEffects.Move;
    }

    private void DropMetricToSlot(int targetSlotIndex)
    {
        if (string.IsNullOrEmpty(this.draggedMetricId))
        {
            return;
        }

        int existingSlot = FindMetricSlot(this.draggedMetricId);
        if (existingSlot >= 0 && existingSlot != this.draggedSourceSlotIndex)
        {
            SetSlotMetric(existingSlot, string.Empty);
        }

        string targetMetric = GetSlotMetric(targetSlotIndex);
        if (this.draggedSourceSlotIndex >= 0 && this.draggedSourceSlotIndex != targetSlotIndex)
        {
            SetSlotMetric(this.draggedSourceSlotIndex, targetMetric);
        }

        SetSlotMetric(targetSlotIndex, this.draggedMetricId);
        RefreshMetricLayoutEditor();
        this.owner.PreviewSettings(ReadControls());
    }

    private bool GetAlertTestButtonState()
    {
        return this.alertTestButton != null &&
            this.alertTestButton.Tag is bool &&
            (bool)this.alertTestButton.Tag;
    }

    private void SetAlertTestButtonState(bool enabled)
    {
        if (this.alertTestButton == null)
        {
            return;
        }

        this.alertTestButton.Tag = enabled;
        this.alertTestButton.Text = enabled ? "开启" : "关闭";
        this.alertTestButton.BackColor = enabled ? Color.FromArgb(255, 92, 104) : DarkTheme.Control;
        this.alertTestButton.ForeColor = enabled ? Color.White : DarkTheme.Text;
        this.alertTestButton.FlatAppearance.BorderColor = enabled ? Color.FromArgb(255, 132, 142) : DarkTheme.Border;
    }

    private static void SelectComboValue(ComboBox combo, object value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            ComboOption option = combo.Items[i] as ComboOption;
            if (option != null && object.Equals(option.Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static object GetComboValue(ComboBox combo, object defaultValue)
    {
        ComboOption option = combo.SelectedItem as ComboOption;
        if (option == null)
        {
            return defaultValue;
        }

        return option.Value;
    }

    private sealed class ComboOption
    {
        public ComboOption(string text, object value)
        {
            this.Text = text;
            this.Value = value;
        }

        public string Text { get; private set; }
        public object Value { get; private set; }

        public override string ToString()
        {
            return this.Text;
        }
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? DarkTheme.Accent : DarkTheme.Border;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = primary ? DarkTheme.Accent : DarkTheme.Control;
        button.ForeColor = primary ? Color.Black : DarkTheme.Text;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        button.UseCompatibleTextRendering = true;
        button.Margin = new Padding(8, 0, 0, 0);
    }

    private static class DarkTheme
    {
        public static readonly Color Window = Color.FromArgb(32, 32, 32);
        public static readonly Color Control = Color.FromArgb(45, 45, 48);
        public static readonly Color Border = Color.FromArgb(72, 72, 78);
        public static readonly Color Text = Color.FromArgb(245, 245, 245);
        public static readonly Color SubtleText = Color.FromArgb(210, 210, 210);
        public static readonly Color Accent = Color.FromArgb(82, 211, 255);
    }
}

internal sealed class PdhSampler : IDisposable
{
    private readonly IntPtr query;
    private readonly PdhCounter cpuCounter;
    private readonly PdhCounter cpuFrequencyCounter;
    private readonly List<PdhCounter> cpuCoreCounters;
    private readonly PdhCounter diskCounter;
    private readonly List<PdhCounter> networkSentCounters;
    private readonly List<PdhCounter> networkReceivedCounters;
    private readonly List<PdhCounter> gpuEngineCounters;
    private readonly List<PdhCounter> gpuDedicatedMemoryCounters;
    private readonly List<PdhCounter> gpuSharedMemoryCounters;
    private readonly List<PdhCounter> npuEngineCounters;
    private readonly List<PdhCounter> npuDedicatedMemoryCounters;
    private readonly List<PdhCounter> npuSharedMemoryCounters;
    private readonly string networkName;
    private readonly DiskInfo diskInfo;
    private readonly string cpuName;
    private readonly int cpuCoreCount;
    private readonly double cpuBaseFrequencyGhz;
    private readonly double cpuCurrentFrequencyFallbackGhz;
    private readonly MemoryInfo memoryInfo;
    private readonly string gpuName;
    private readonly double gpuMemoryTotalGb;
    private readonly string npuName;
    private readonly double npuMemoryTotalGb;
    private readonly HashSet<string> npuLuidTokens;
    private bool disposed;

    public PdhSampler()
    {
        uint status = PdhNative.PdhOpenQuery(null, IntPtr.Zero, out this.query);
        if (status != PdhNative.ERROR_SUCCESS)
        {
            throw new InvalidOperationException("PdhOpenQuery failed: 0x" + status.ToString("X8"));
        }

        this.cpuCounter = AddFirstAvailable(new string[]
        {
            @"\Processor Information(_Total)\% Processor Utility",
            @"\Processor(_Total)\% Processor Time"
        });
        CpuInfo cpuInfo = DetectCpuInfo();
        this.cpuName = cpuInfo.Name;
        this.cpuCoreCount = cpuInfo.CoreCount;
        this.cpuBaseFrequencyGhz = cpuInfo.BaseFrequencyGhz;
        this.cpuCurrentFrequencyFallbackGhz = cpuInfo.CurrentFrequencyGhz;
        this.cpuFrequencyCounter = AddFirstAvailable(new string[]
        {
            @"\Processor Information(_Total)\Actual Frequency",
            @"\Processor Information(0,_Total)\Actual Frequency"
        });
        this.cpuCoreCounters = AddCpuCoreCounters();
        if (this.cpuCoreCounters.Count > 0)
        {
            this.cpuCoreCount = this.cpuCoreCounters.Count;
        }

        this.memoryInfo = DetectMemoryInfo();

        this.diskInfo = DetectDiskInfo();
        this.diskCounter = AddFirstAvailable(new string[]
        {
            this.diskInfo.CounterPath,
            @"\PhysicalDisk(_Total)\% Disk Time",
            @"\LogicalDisk(C:)\% Disk Time"
        });

        this.networkSentCounters = AddCountersFromWildcard(@"\Network Interface(*)\Bytes Sent/sec", ShouldUseNetworkPath);
        this.networkReceivedCounters = AddCountersFromWildcard(@"\Network Interface(*)\Bytes Received/sec", ShouldUseNetworkPath);
        string[] gpuEnginePaths = ExpandWildcard(@"\GPU Engine(*)\Utilization Percentage");
        string[] gpuDedicatedMemoryPaths = ExpandWildcard(@"\GPU Adapter Memory(*)\Dedicated Usage");
        string[] gpuSharedMemoryPaths = ExpandWildcard(@"\GPU Adapter Memory(*)\Shared Usage");
        GpuInfo npuInfo = DetectNpuInfo();
        this.npuLuidTokens = DetectNpuLuidTokens(gpuEnginePaths, npuInfo.IsDetected);
        this.gpuEngineCounters = AddCountersFromPaths(gpuEnginePaths, delegate(string path) { return !IsNpuPath(path, this.npuLuidTokens); });
        this.gpuDedicatedMemoryCounters = AddCountersFromPaths(gpuDedicatedMemoryPaths, delegate(string path) { return !IsNpuPath(path, this.npuLuidTokens); });
        this.gpuSharedMemoryCounters = AddCountersFromPaths(gpuSharedMemoryPaths, delegate(string path) { return !IsNpuPath(path, this.npuLuidTokens); });
        this.npuEngineCounters = AddCountersFromWildcard(@"\NPU Engine(*)\Utilization Percentage");
        if (this.npuEngineCounters.Count == 0)
        {
            this.npuEngineCounters = AddCountersFromPaths(gpuEnginePaths, delegate(string path) { return IsNpuPath(path, this.npuLuidTokens); });
        }

        this.npuDedicatedMemoryCounters = AddCountersFromWildcard(@"\NPU Adapter Memory(*)\Dedicated Usage");
        if (this.npuDedicatedMemoryCounters.Count == 0)
        {
            this.npuDedicatedMemoryCounters = AddCountersFromPaths(gpuDedicatedMemoryPaths, delegate(string path) { return IsNpuPath(path, this.npuLuidTokens); });
        }

        this.npuSharedMemoryCounters = AddCountersFromWildcard(@"\NPU Adapter Memory(*)\Shared Usage");
        if (this.npuSharedMemoryCounters.Count == 0)
        {
            this.npuSharedMemoryCounters = AddCountersFromPaths(gpuSharedMemoryPaths, delegate(string path) { return IsNpuPath(path, this.npuLuidTokens); });
        }

        NetworkState initialNetwork = DetectNetworkState();
        this.networkName = initialNetwork.Name;
        GpuInfo gpuInfo = DetectGpuInfo();
        this.gpuName = gpuInfo.Name;
        this.gpuMemoryTotalGb = gpuInfo.MemoryTotalGb;
        this.npuName = npuInfo.Name;
        this.npuMemoryTotalGb = npuInfo.MemoryTotalGb;

        PdhNative.PdhCollectQueryData(this.query);
        Program.LogInfo(string.Format(
            "PDH counters initialized. CPU={0}, CPUName={1}, CPUCores={2}, CPUFreq={3}, CPUBaseGHz={4:0.00}, Disk={5}, NetSent={6}, NetRecv={7}, GPU={8}, GPUMem={9}/{10}, NPU={11}, NPUMem={12}/{13}, NPULuids={14}",
            this.cpuCounter == null ? "none" : this.cpuCounter.Path,
            this.cpuName,
            this.cpuCoreCounters.Count,
            this.cpuFrequencyCounter == null ? "none" : this.cpuFrequencyCounter.Path,
            this.cpuBaseFrequencyGhz,
            this.diskCounter == null ? "none" : this.diskCounter.Path,
            this.networkSentCounters.Count,
            this.networkReceivedCounters.Count,
            this.gpuEngineCounters.Count,
            this.gpuDedicatedMemoryCounters.Count,
            this.gpuSharedMemoryCounters.Count,
            this.npuEngineCounters.Count,
            this.npuDedicatedMemoryCounters.Count,
            this.npuSharedMemoryCounters.Count,
            JoinSet(this.npuLuidTokens)));
        Program.LogInfo(string.Format(
            "Memory hardware initialized. Manufacturer={0}, Speed={1}MT/s",
            this.memoryInfo.Manufacturer,
            this.memoryInfo.SpeedMtps));
    }

    public PerfSnapshot Sample()
    {
        EnsureNotDisposed();
        PdhNative.PdhCollectQueryData(this.query);

        PerfSnapshot snapshot = new PerfSnapshot();
        snapshot.CpuName = this.cpuName;
        snapshot.CpuPercent = Clamp(ReadCounter(this.cpuCounter), 0.0, 100.0);
        snapshot.CpuCoreCount = this.cpuCoreCount;
        snapshot.CpuCorePercents = ReadCpuCorePercents();
        snapshot.CpuFrequencyGhz = ReadCpuFrequencyGhz();
        snapshot.CpuBaseFrequencyGhz = this.cpuBaseFrequencyGhz;
        snapshot.MemoryManufacturer = this.memoryInfo.Manufacturer;
        snapshot.MemorySpeedMtps = this.memoryInfo.SpeedMtps;
        snapshot.DiskPercent = Clamp(ReadCounter(this.diskCounter), 0.0, 100.0);
        NetworkState networkState = DetectNetworkState();
        snapshot.NetworkName = networkState.Name;
        snapshot.NetworkConnected = networkState.Connected;
        snapshot.DiskName = this.diskInfo.Name;
        snapshot.GpuName = this.gpuName;
        snapshot.NpuName = this.npuName;

        double sent = 0.0;
        for (int i = 0; i < this.networkSentCounters.Count; i++)
        {
            sent += Math.Max(0.0, ReadCounter(this.networkSentCounters[i]));
        }

        double received = 0.0;
        for (int i = 0; i < this.networkReceivedCounters.Count; i++)
        {
            received += Math.Max(0.0, ReadCounter(this.networkReceivedCounters[i]));
        }

        if (!snapshot.NetworkConnected)
        {
            sent = 0.0;
            received = 0.0;
        }

        snapshot.NetworkSentBytesPerSecond = sent;
        snapshot.NetworkReceivedBytesPerSecond = received;

        ApplyDiskUsage(snapshot, this.diskInfo);

        double gpuPercent = 0.0;
        for (int i = 0; i < this.gpuEngineCounters.Count; i++)
        {
            gpuPercent += Math.Max(0.0, ReadCounter(this.gpuEngineCounters[i]));
        }

        snapshot.GpuPercent = Clamp(gpuPercent, 0.0, 100.0);

        double gpuMemoryBytes = 0.0;
        for (int i = 0; i < this.gpuDedicatedMemoryCounters.Count; i++)
        {
            gpuMemoryBytes += Math.Max(0.0, ReadCounter(this.gpuDedicatedMemoryCounters[i]));
        }

        for (int i = 0; i < this.gpuSharedMemoryCounters.Count; i++)
        {
            gpuMemoryBytes += Math.Max(0.0, ReadCounter(this.gpuSharedMemoryCounters[i]));
        }

        snapshot.GpuMemoryUsedGb = gpuMemoryBytes / 1073741824.0;
        snapshot.GpuMemoryTotalGb = this.gpuMemoryTotalGb;
        if (snapshot.GpuMemoryTotalGb > 0.0)
        {
            snapshot.GpuMemoryPercent = Clamp(snapshot.GpuMemoryUsedGb * 100.0 / snapshot.GpuMemoryTotalGb, 0.0, 100.0);
        }

        double npuPercent = 0.0;
        for (int i = 0; i < this.npuEngineCounters.Count; i++)
        {
            npuPercent += Math.Max(0.0, ReadCounter(this.npuEngineCounters[i]));
        }

        snapshot.NpuPercent = Clamp(npuPercent, 0.0, 100.0);

        double npuMemoryBytes = 0.0;
        for (int i = 0; i < this.npuDedicatedMemoryCounters.Count; i++)
        {
            npuMemoryBytes += Math.Max(0.0, ReadCounter(this.npuDedicatedMemoryCounters[i]));
        }

        for (int i = 0; i < this.npuSharedMemoryCounters.Count; i++)
        {
            npuMemoryBytes += Math.Max(0.0, ReadCounter(this.npuSharedMemoryCounters[i]));
        }

        snapshot.NpuMemoryUsedGb = npuMemoryBytes / 1073741824.0;
        snapshot.NpuMemoryTotalGb = this.npuMemoryTotalGb;
        if (snapshot.NpuMemoryTotalGb > 0.0)
        {
            snapshot.NpuMemoryPercent = Clamp(snapshot.NpuMemoryUsedGb * 100.0 / snapshot.NpuMemoryTotalGb, 0.0, 100.0);
        }

        NativeMethods.MEMORYSTATUSEX memory = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(memory))
        {
            double totalBytes = memory.ullTotalPhys;
            double availableBytes = memory.ullAvailPhys;
            double usedBytes = Math.Max(0.0, totalBytes - availableBytes);
            snapshot.MemoryTotalGb = totalBytes / 1073741824.0;
            snapshot.MemoryUsedGb = usedBytes / 1073741824.0;
            snapshot.MemoryPercent = Clamp(memory.dwMemoryLoad, 0.0, 100.0);
        }

        return snapshot;
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            PdhNative.PdhCloseQuery(this.query);
            this.disposed = true;
        }
    }

    private PdhCounter AddFirstAvailable(string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (string.IsNullOrEmpty(paths[i]))
            {
                continue;
            }

            PdhCounter counter = AddCounter(paths[i]);
            if (counter != null)
            {
                return counter;
            }
        }

        return null;
    }

    private List<PdhCounter> AddCpuCoreCounters()
    {
        string[] paths = ExpandWildcard(@"\Processor Information(*)\% Processor Utility");
        Array.Sort(paths, CompareCpuCounterPaths);
        List<PdhCounter> counters = AddCountersFromPaths(paths, ShouldUseCpuCorePath);
        if (counters.Count > 0)
        {
            return counters;
        }

        paths = ExpandWildcard(@"\Processor(*)\% Processor Time");
        Array.Sort(paths, CompareCpuCounterPaths);
        return AddCountersFromPaths(paths, ShouldUseCpuCorePath);
    }

    private double[] ReadCpuCorePercents()
    {
        if (this.cpuCoreCounters == null || this.cpuCoreCounters.Count == 0)
        {
            return new double[0];
        }

        double[] values = new double[this.cpuCoreCounters.Count];
        for (int i = 0; i < this.cpuCoreCounters.Count; i++)
        {
            values[i] = Clamp(ReadCounter(this.cpuCoreCounters[i]), 0.0, 100.0);
        }

        return values;
    }

    private double ReadCpuFrequencyGhz()
    {
        double mhz = ReadCounter(this.cpuFrequencyCounter);
        if (mhz > 0.0)
        {
            return mhz / 1000.0;
        }

        return this.cpuCurrentFrequencyFallbackGhz;
    }

    private List<PdhCounter> AddCountersFromWildcard(string wildcardPath)
    {
        return AddCountersFromWildcard(wildcardPath, null);
    }

    private List<PdhCounter> AddCountersFromWildcard(string wildcardPath, Predicate<string> shouldUsePath)
    {
        string[] paths = ExpandWildcard(wildcardPath);
        return AddCountersFromPaths(paths, shouldUsePath);
    }

    private List<PdhCounter> AddCountersFromPaths(string[] paths, Predicate<string> shouldUsePath)
    {
        List<PdhCounter> counters = new List<PdhCounter>();
        for (int i = 0; i < paths.Length; i++)
        {
            if (shouldUsePath != null && !shouldUsePath(paths[i]))
            {
                continue;
            }

            PdhCounter counter = AddCounter(paths[i]);
            if (counter != null)
            {
                counters.Add(counter);
            }
        }

        return counters;
    }

    private PdhCounter AddCounter(string path)
    {
        IntPtr counterHandle;
        uint status = PdhNative.PdhAddEnglishCounter(this.query, path, IntPtr.Zero, out counterHandle);
        if (status == PdhNative.ERROR_SUCCESS)
        {
            return new PdhCounter(counterHandle, path);
        }

        return null;
    }

    private static string[] ExpandWildcard(string wildcardPath)
    {
        uint size = 0;
        uint status = PdhNative.PdhExpandWildCardPath(null, wildcardPath, null, ref size, 0);
        if (status != PdhNative.PDH_MORE_DATA || size == 0)
        {
            return ExpandWildcardWithCategory(wildcardPath);
        }

        StringBuilder buffer = new StringBuilder((int)size);
        status = PdhNative.PdhExpandWildCardPath(null, wildcardPath, buffer, ref size, 0);
        if (status != PdhNative.ERROR_SUCCESS)
        {
            return ExpandWildcardWithCategory(wildcardPath);
        }

        string[] paths = buffer.ToString().Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length <= 1 && wildcardPath.IndexOf("(*)", StringComparison.Ordinal) >= 0)
        {
            string[] categoryPaths = ExpandWildcardWithCategory(wildcardPath);
            if (categoryPaths.Length > paths.Length)
            {
                return categoryPaths;
            }
        }

        return paths;
    }

    private static string[] ExpandWildcardWithCategory(string wildcardPath)
    {
        try
        {
            int open = wildcardPath.IndexOf("(*)", StringComparison.Ordinal);
            if (open <= 1 || !wildcardPath.StartsWith("\\", StringComparison.Ordinal))
            {
                return new string[0];
            }

            int counterStart = open + 3;
            if (counterStart >= wildcardPath.Length || wildcardPath[counterStart] != '\\')
            {
                return new string[0];
            }

            string categoryName = wildcardPath.Substring(1, open - 1);
            string counterName = wildcardPath.Substring(counterStart + 1);
            PerformanceCounterCategory category = new PerformanceCounterCategory(categoryName);
            string[] instances = category.GetInstanceNames();
            List<string> paths = new List<string>();
            for (int i = 0; i < instances.Length; i++)
            {
                if (string.IsNullOrEmpty(instances[i]))
                {
                    continue;
                }

                paths.Add("\\" + categoryName + "(" + instances[i] + ")\\" + counterName);
            }

            return paths.ToArray();
        }
        catch
        {
            return new string[0];
        }
    }

    private static bool ShouldUseNetworkPath(string path)
    {
        string lower = path.ToLowerInvariant();
        if (lower.IndexOf("loopback", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        if (lower.IndexOf("isatap", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        if (lower.IndexOf("teredo", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldUseCpuCorePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static int CompareCpuCounterPaths(string left, string right)
    {
        int leftKey = ExtractCpuCounterSortKey(left);
        int rightKey = ExtractCpuCounterSortKey(right);
        if (leftKey != rightKey)
        {
            return leftKey.CompareTo(rightKey);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractCpuCounterSortKey(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return int.MaxValue;
        }

        int open = path.IndexOf('(');
        int close = path.IndexOf(')', open + 1);
        if (open < 0 || close <= open)
        {
            return int.MaxValue - 1;
        }

        string instance = path.Substring(open + 1, close - open - 1);
        if (instance.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return int.MaxValue;
        }

        string[] parts = instance.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        int group = 0;
        int core = 0;
        if (parts.Length == 1)
        {
            int.TryParse(parts[0], out core);
            return core;
        }

        int.TryParse(parts[0], out group);
        int.TryParse(parts[1], out core);
        return group * 10000 + core;
    }

    private static NetworkState DetectNetworkState()
    {
        NetworkState state = new NetworkState();
        state.Name = "Network";
        state.Connected = false;

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            NetworkInterface best = null;
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface item = interfaces[i];
                if (item.OperationalStatus != OperationalStatus.Up ||
                    item.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    item.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                if (best == null || item.Speed > best.Speed)
                {
                    best = item;
                }
            }

            if (best != null)
            {
                state.Connected = true;
                string ssid = GetWifiSsid(best);
                if (!string.IsNullOrEmpty(ssid))
                {
                    state.Name = ssid;
                    return state;
                }

                if (!string.IsNullOrEmpty(best.Name))
                {
                    state.Name = best.Name;
                    return state;
                }

                if (!string.IsNullOrEmpty(best.Description))
                {
                    state.Name = best.Description;
                }

                return state;
            }
        }
        catch
        {
        }

        return state;
    }

    private static string GetWifiSsid(NetworkInterface networkInterface)
    {
        if (networkInterface == null || networkInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
        {
            return string.Empty;
        }

        Guid interfaceGuid;
        try
        {
            interfaceGuid = new Guid(networkInterface.Id);
        }
        catch
        {
            return string.Empty;
        }

        return NativeMethods.TryGetConnectedWifiSsid(interfaceGuid);
    }

    private static CpuInfo DetectCpuInfo()
    {
        CpuInfo info = new CpuInfo();
        info.Name = "CPU";
        info.CoreCount = Math.Max(1, Environment.ProcessorCount);

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(item["Name"]);
                    if (!string.IsNullOrEmpty(name))
                    {
                        info.Name = name.Trim();
                    }

                    object logical = item["NumberOfLogicalProcessors"];
                    object cores = item["NumberOfCores"];
                    if (logical != null && Convert.ToInt32(logical) > 0)
                    {
                        info.CoreCount = Convert.ToInt32(logical);
                    }
                    else if (cores != null && Convert.ToInt32(cores) > 0)
                    {
                        info.CoreCount = Convert.ToInt32(cores);
                    }

                    object currentClock = item["CurrentClockSpeed"];
                    if (currentClock != null && Convert.ToDouble(currentClock) > 0.0)
                    {
                        info.CurrentFrequencyGhz = Convert.ToDouble(currentClock) / 1000.0;
                    }

                    object maxClock = item["MaxClockSpeed"];
                    if (maxClock != null && Convert.ToDouble(maxClock) > 0.0)
                    {
                        info.BaseFrequencyGhz = Convert.ToDouble(maxClock) / 1000.0;
                    }

                    if (info.BaseFrequencyGhz <= 0.0)
                    {
                        info.BaseFrequencyGhz = info.CurrentFrequencyGhz;
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return info;
    }

    private static MemoryInfo DetectMemoryInfo()
    {
        MemoryInfo info = new MemoryInfo();
        info.Manufacturer = "Memory";
        info.SpeedMtps = 0;

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Manufacturer, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                List<string> manufacturers = new List<string>();
                foreach (ManagementObject item in collection)
                {
                    string manufacturer = NormalizeMemoryManufacturer(Convert.ToString(item["Manufacturer"]));
                    if (manufacturer.Length > 0 && !ContainsText(manufacturers, manufacturer))
                    {
                        manufacturers.Add(manufacturer);
                    }

                    int configuredSpeed = ToPositiveInt(item["ConfiguredClockSpeed"]);
                    int speed = configuredSpeed > 0 ? configuredSpeed : ToPositiveInt(item["Speed"]);
                    if (speed > info.SpeedMtps)
                    {
                        info.SpeedMtps = speed;
                    }
                }

                if (manufacturers.Count == 1)
                {
                    info.Manufacturer = manufacturers[0];
                }
                else if (manufacturers.Count > 1)
                {
                    info.Manufacturer = string.Join("/", manufacturers.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return info;
    }

    private static string NormalizeMemoryManufacturer(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = CollapseWhitespace(value.Trim());
        if (text.Length == 0 ||
            string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Undefined", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Not Specified", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return text;
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        bool previousWhitespace = false;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                    previousWhitespace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        return builder.ToString();
    }

    private static bool ContainsText(List<string> values, string text)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ToPositiveInt(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            int number = Convert.ToInt32(value);
            return number > 0 ? number : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static DiskInfo DetectDiskInfo()
    {
        DiskInfo info = new DiskInfo();
        info.Name = "Physical Disk";
        info.CounterPath = string.Empty;
        info.VolumeRoots = new List<string>();

        string systemDrive = GetSystemDriveName();
        string[] physicalDiskPaths = ExpandWildcard(@"\PhysicalDisk(*)\% Disk Time");
        info.CounterPath = SelectPhysicalDiskCounterPath(physicalDiskPaths, systemDrive);
        info.VolumeRoots = ExtractVolumeRootsFromPhysicalDiskPath(info.CounterPath);
        int diskIndex = ExtractPhysicalDiskIndex(info.CounterPath);

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Index, Model, Size FROM Win32_DiskDrive"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                ManagementObject fallback = null;
                foreach (ManagementObject item in collection)
                {
                    if (fallback == null)
                    {
                        fallback = item;
                    }

                    object indexValue = item["Index"];
                    int index = -1;
                    if (indexValue != null)
                    {
                        index = Convert.ToInt32(indexValue);
                    }

                    if (diskIndex >= 0 && index != diskIndex)
                    {
                        continue;
                    }

                    ApplyDiskDriveObject(info, item, diskIndex);
                    break;
                }

                if (info.TotalBytes <= 0.0 && fallback != null)
                {
                    ApplyDiskDriveObject(info, fallback, diskIndex);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (info.VolumeRoots.Count == 0)
        {
            info.VolumeRoots = DetectFixedDriveRoots();
        }

        if (info.TotalBytes <= 0.0)
        {
            info.TotalBytes = SumDriveTotalBytes(info.VolumeRoots);
        }

        if (string.IsNullOrEmpty(info.Name))
        {
            info.Name = diskIndex >= 0 ? "Disk " + diskIndex : "Physical Disk";
        }

        return info;
    }

    private static void ApplyDiskDriveObject(DiskInfo info, ManagementObject item, int diskIndex)
    {
        string model = Convert.ToString(item["Model"]);
        if (!string.IsNullOrEmpty(model))
        {
            info.Name = model.Trim();
        }
        else if (diskIndex >= 0)
        {
            info.Name = "Disk " + diskIndex;
        }

        object size = item["Size"];
        if (size != null)
        {
            double bytes = Convert.ToDouble(size);
            if (bytes > 0.0)
            {
                info.TotalBytes = bytes;
            }
        }
    }

    private static void ApplyDiskUsage(PerfSnapshot snapshot, DiskInfo info)
    {
        double totalBytes = info.TotalBytes;
        double freeBytes = 0.0;
        double logicalTotalBytes = 0.0;
        List<string> roots = info.VolumeRoots;
        if (roots == null || roots.Count == 0)
        {
            roots = DetectFixedDriveRoots();
        }

        for (int i = 0; i < roots.Count; i++)
        {
            try
            {
                DriveInfo drive = new DriveInfo(roots[i]);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                logicalTotalBytes += drive.TotalSize;
                freeBytes += Math.Max(0.0, drive.AvailableFreeSpace);
            }
            catch
            {
            }
        }

        if (totalBytes <= 0.0)
        {
            totalBytes = logicalTotalBytes;
        }

        if (totalBytes <= 0.0)
        {
            return;
        }

        double usedBytes = Math.Max(0.0, totalBytes - freeBytes);
        usedBytes = Math.Min(usedBytes, totalBytes);
        snapshot.DiskTotalGb = totalBytes / 1073741824.0;
        snapshot.DiskUsedGb = usedBytes / 1073741824.0;
        snapshot.DiskCapacityPercent = Clamp(usedBytes * 100.0 / totalBytes, 0.0, 100.0);
    }

    private static string SelectPhysicalDiskCounterPath(string[] paths, string systemDrive)
    {
        string firstPhysicalDisk = string.Empty;
        string totalPath = string.Empty;
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (path.IndexOf("(_Total)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                totalPath = path;
                continue;
            }

            if (firstPhysicalDisk.Length == 0)
            {
                firstPhysicalDisk = path;
            }

            if (systemDrive.Length > 0 && path.IndexOf(systemDrive, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return path;
            }
        }

        if (firstPhysicalDisk.Length > 0)
        {
            return firstPhysicalDisk;
        }

        return totalPath;
    }

    private static int ExtractPhysicalDiskIndex(string counterPath)
    {
        if (string.IsNullOrEmpty(counterPath))
        {
            return -1;
        }

        int start = counterPath.IndexOf(@"\PhysicalDisk(", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return -1;
        }

        start += @"\PhysicalDisk(".Length;
        int end = start;
        while (end < counterPath.Length && char.IsDigit(counterPath[end]))
        {
            end++;
        }

        if (end <= start)
        {
            return -1;
        }

        int index;
        if (int.TryParse(counterPath.Substring(start, end - start), out index))
        {
            return index;
        }

        return -1;
    }

    private static List<string> ExtractVolumeRootsFromPhysicalDiskPath(string counterPath)
    {
        List<string> roots = new List<string>();
        if (string.IsNullOrEmpty(counterPath))
        {
            return roots;
        }

        int open = counterPath.IndexOf('(');
        int close = counterPath.IndexOf(')', open + 1);
        if (open < 0 || close <= open)
        {
            return roots;
        }

        string instance = counterPath.Substring(open + 1, close - open - 1);
        string[] parts = instance.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 2 && part[1] == ':')
            {
                roots.Add(part.ToUpperInvariant() + "\\");
            }
        }

        return roots;
    }

    private static List<string> DetectFixedDriveRoots()
    {
        List<string> roots = new List<string>();
        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            for (int i = 0; i < drives.Length; i++)
            {
                if (drives[i].DriveType == DriveType.Fixed)
                {
                    roots.Add(drives[i].Name);
                }
            }
        }
        catch
        {
        }

        return roots;
    }

    private static double SumDriveTotalBytes(List<string> roots)
    {
        double total = 0.0;
        for (int i = 0; i < roots.Count; i++)
        {
            try
            {
                DriveInfo drive = new DriveInfo(roots[i]);
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    total += drive.TotalSize;
                }
            }
            catch
            {
            }
        }

        return total;
    }

    private static string GetSystemDriveName()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.IsNullOrEmpty(root))
            {
                return root.TrimEnd('\\');
            }
        }
        catch
        {
        }

        return "C:";
    }

    private static GpuInfo DetectGpuInfo()
    {
        GpuInfo info = new GpuInfo();
        info.Name = "GPU";
        info.MemoryTotalGb = 0.0;

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(item["Name"]);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (info.Name == "GPU" || name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        info.Name = name;
                        info.IsDetected = true;
                    }

                    object adapterRam = item["AdapterRAM"];
                    if (adapterRam != null)
                    {
                        double bytes = Convert.ToDouble(adapterRam);
                        if (bytes > 0.0)
                        {
                            info.MemoryTotalGb = Math.Max(info.MemoryTotalGb, bytes / 1073741824.0);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (info.MemoryTotalGb < 0.25)
        {
            info.MemoryTotalGb = GetPhysicalMemoryTotalGb();
        }

        return info;
    }

    private static GpuInfo DetectNpuInfo()
    {
        GpuInfo info = new GpuInfo();
        info.Name = "NPU";
        info.MemoryTotalGb = 0.0;

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPClass, Manufacturer FROM Win32_PnPEntity"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(item["Name"]);
                    string pnpClass = Convert.ToString(item["PNPClass"]);
                    string manufacturer = Convert.ToString(item["Manufacturer"]);
                    if (!LooksLikeNpuDevice(name, pnpClass, manufacturer))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        info.Name = name;
                        info.IsDetected = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        if (info.IsDetected)
        {
            info.MemoryTotalGb = GetPhysicalMemoryTotalGb();
        }

        return info;
    }

    private static bool LooksLikeNpuDevice(string name, string pnpClass, string manufacturer)
    {
        if (string.Equals(pnpClass, "ComputeAccelerator", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string combined = ((name ?? string.Empty) + " " + (manufacturer ?? string.Empty)).ToLowerInvariant();
        return combined.IndexOf(" npu", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("(npu", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("neural", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("hexagon", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("ai boost", StringComparison.Ordinal) >= 0 ||
            combined.IndexOf("xdna", StringComparison.Ordinal) >= 0;
    }

    private static HashSet<string> DetectNpuLuidTokens(string[] gpuEnginePaths, bool hasNpuDevice)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < gpuEnginePaths.Length; i++)
        {
            if (ContainsNpuKeyword(gpuEnginePaths[i]))
            {
                string luid = ExtractLuidToken(gpuEnginePaths[i]);
                if (luid.Length > 0)
                {
                    result.Add(luid);
                }
            }
        }

        if (result.Count > 0 || !hasNpuDevice)
        {
            return result;
        }

        Dictionary<string, HashSet<string>> engineTypesByLuid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < gpuEnginePaths.Length; i++)
        {
            string luid = ExtractLuidToken(gpuEnginePaths[i]);
            string engineType = ExtractEngineType(gpuEnginePaths[i]);
            if (luid.Length == 0 || engineType.Length == 0)
            {
                continue;
            }

            HashSet<string> engineTypes;
            if (!engineTypesByLuid.TryGetValue(luid, out engineTypes))
            {
                engineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                engineTypesByLuid.Add(luid, engineTypes);
            }

            engineTypes.Add(engineType);
        }

        foreach (KeyValuePair<string, HashSet<string>> item in engineTypesByLuid)
        {
            if (item.Value.Count == 1 && item.Value.Contains("Compute"))
            {
                result.Add(item.Key);
            }
        }

        return result;
    }

    private static bool IsNpuPath(string path, HashSet<string> npuLuidTokens)
    {
        if (ContainsNpuKeyword(path))
        {
            return true;
        }

        if (npuLuidTokens == null || npuLuidTokens.Count == 0)
        {
            return false;
        }

        string luid = ExtractLuidToken(path);
        return luid.Length > 0 && npuLuidTokens.Contains(luid);
    }

    private static bool ContainsNpuKeyword(string path)
    {
        string lower = (path ?? string.Empty).ToLowerInvariant();
        return lower.IndexOf("npu", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("neural", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("hexagon", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("ai boost", StringComparison.Ordinal) >= 0 ||
            lower.IndexOf("xdna", StringComparison.Ordinal) >= 0;
    }

    private static string ExtractLuidToken(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        int start = path.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        int end = path.IndexOf("_phys_", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = path.IndexOf(")", start, StringComparison.Ordinal);
        }

        if (end < 0 || end <= start)
        {
            return string.Empty;
        }

        return path.Substring(start, end - start).ToLowerInvariant();
    }

    private static string ExtractEngineType(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        int start = path.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += "engtype_".Length;
        int end = path.IndexOf(")", start, StringComparison.Ordinal);
        if (end < 0 || end <= start)
        {
            return string.Empty;
        }

        return path.Substring(start, end - start);
    }

    private static double GetPhysicalMemoryTotalGb()
    {
        NativeMethods.MEMORYSTATUSEX memory = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(memory))
        {
            return memory.ullTotalPhys / 1073741824.0;
        }

        return 0.0;
    }

    private static string JoinSet(HashSet<string> values)
    {
        if (values == null || values.Count == 0)
        {
            return "none";
        }

        StringBuilder builder = new StringBuilder();
        foreach (string value in values)
        {
            if (builder.Length > 0)
            {
                builder.Append(",");
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static double ReadCounter(PdhCounter counter)
    {
        if (counter == null || counter.Handle == IntPtr.Zero)
        {
            return 0.0;
        }

        uint counterType;
        PdhNative.PDH_FMT_COUNTERVALUE_DOUBLE value;
        uint status = PdhNative.PdhGetFormattedCounterValue(
            counter.Handle,
            PdhNative.PDH_FMT_DOUBLE,
            out counterType,
            out value);

        if (status == PdhNative.ERROR_SUCCESS &&
            (value.CStatus == PdhNative.PDH_CSTATUS_VALID_DATA ||
             value.CStatus == PdhNative.PDH_CSTATUS_NEW_DATA))
        {
            return value.DoubleValue;
        }

        return 0.0;
    }

    private void EnsureNotDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException("PdhSampler");
        }
    }

    private static double Clamp(double value, double min, double max)
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
}

internal sealed class PdhCounter
{
    public PdhCounter(IntPtr handle, string path)
    {
        this.Handle = handle;
        this.Path = path;
    }

    public IntPtr Handle { get; private set; }
    public string Path { get; private set; }
}

internal sealed class GpuInfo
{
    public string Name { get; set; }
    public double MemoryTotalGb { get; set; }
    public bool IsDetected { get; set; }
}

internal sealed class NetworkState
{
    public string Name { get; set; }
    public bool Connected { get; set; }
}

internal sealed class CpuInfo
{
    public string Name { get; set; }
    public int CoreCount { get; set; }
    public double CurrentFrequencyGhz { get; set; }
    public double BaseFrequencyGhz { get; set; }
}

internal sealed class MemoryInfo
{
    public string Manufacturer { get; set; }
    public int SpeedMtps { get; set; }
}

internal sealed class DiskInfo
{
    public string Name { get; set; }
    public string CounterPath { get; set; }
    public List<string> VolumeRoots { get; set; }
    public double TotalBytes { get; set; }
}

internal sealed class PerfSnapshot
{
    public string CpuName { get; set; }
    public double CpuPercent { get; set; }
    public int CpuCoreCount { get; set; }
    public double[] CpuCorePercents { get; set; }
    public double CpuFrequencyGhz { get; set; }
    public double CpuBaseFrequencyGhz { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }
    public double MemoryPercent { get; set; }
    public string MemoryManufacturer { get; set; }
    public int MemorySpeedMtps { get; set; }
    public string DiskName { get; set; }
    public double DiskPercent { get; set; }
    public double DiskCapacityPercent { get; set; }
    public double DiskUsedGb { get; set; }
    public double DiskTotalGb { get; set; }
    public string NetworkName { get; set; }
    public bool NetworkConnected { get; set; }
    public double NetworkSentBytesPerSecond { get; set; }
    public double NetworkReceivedBytesPerSecond { get; set; }
    public string GpuName { get; set; }
    public double GpuPercent { get; set; }
    public double GpuMemoryUsedGb { get; set; }
    public double GpuMemoryTotalGb { get; set; }
    public double GpuMemoryPercent { get; set; }
    public string NpuName { get; set; }
    public double NpuPercent { get; set; }
    public double NpuMemoryUsedGb { get; set; }
    public double NpuMemoryTotalGb { get; set; }
    public double NpuMemoryPercent { get; set; }

    public PerfSnapshot()
    {
        this.CpuName = "CPU";
        this.CpuCorePercents = new double[0];
        this.MemoryManufacturer = "Memory";
        this.DiskName = "Physical Disk";
        this.NetworkName = "Network";
        this.NetworkConnected = true;
        this.GpuName = "GPU";
        this.NpuName = "NPU";
    }
}

internal static class Logger
{
    private const long MaxLogDirectoryBytes = 10L * 1024L * 1024L;
    private static readonly object SyncRoot = new object();

    public static string DirectoryPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopPerfWidget");
        }
    }

    public static string LogPath
    {
        get { return Path.Combine(DirectoryPath, "DesktopPerfWidget.log"); }
    }

    public static string ErrorLogPath
    {
        get { return Path.Combine(DirectoryPath, "error.log"); }
    }

    public static void Info(string message)
    {
        Append(LogPath, "INFO", message);
    }

    public static void Error(Exception ex)
    {
        string text = ex.ToString();
        Append(LogPath, "ERROR", text);
        Append(ErrorLogPath, "ERROR", text);
    }

    private static void Append(string path, string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(DirectoryPath);
                string line = DateTime.Now.ToString("u") + " [" + level + "] " + message + Environment.NewLine;
                EnforceLogDirectoryLimit(path, Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(path, line, Encoding.UTF8);
                EnforceLogDirectoryLimit(path, 0);
            }
        }
        catch
        {
        }
    }

    private static void EnforceLogDirectoryLimit(string activePath, long incomingBytes)
    {
        DirectoryInfo directory = new DirectoryInfo(DirectoryPath);
        if (!directory.Exists)
        {
            return;
        }

        FileInfo[] files = directory.GetFiles("*.log");
        long total = 0;
        for (int i = 0; i < files.Length; i++)
        {
            total += files[i].Length;
        }

        long budget = MaxLogDirectoryBytes - Math.Max(0, incomingBytes);
        if (budget < 4096)
        {
            budget = MaxLogDirectoryBytes;
        }

        if (total <= budget)
        {
            return;
        }

        Array.Sort(files, CompareLogFileAge);
        string activeFullPath = Path.GetFullPath(activePath);
        for (int i = 0; i < files.Length && total > budget; i++)
        {
            if (string.Equals(files[i].FullName, activeFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length = files[i].Length;
            try
            {
                files[i].Delete();
                total -= length;
            }
            catch
            {
            }
        }

        if (total > budget && File.Exists(activePath))
        {
            long otherTotal = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (!string.Equals(files[i].FullName, activeFullPath, StringComparison.OrdinalIgnoreCase) && files[i].Exists)
                {
                    otherTotal += files[i].Length;
                }
            }

            long keepBytes = Math.Max(4096, budget - otherTotal);
            TrimFileToLastBytes(activePath, keepBytes);
        }
    }

    private static int CompareLogFileAge(FileInfo left, FileInfo right)
    {
        int result = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
        if (result != 0)
        {
            return result;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void TrimFileToLastBytes(string path, long keepBytes)
    {
        try
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length <= keepBytes)
            {
                return;
            }

            byte[] buffer = new byte[(int)keepBytes];
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                input.Seek(-keepBytes, SeekOrigin.End);
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = input.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }

            File.WriteAllBytes(path, buffer);
        }
        catch
        {
        }
    }
}

internal static class PdhNative
{
    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_DOUBLE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint counterType,
        out PDH_FMT_COUNTERVALUE_DOUBLE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhExpandWildCardPath(
        string dataSource,
        string wildcardPath,
        StringBuilder expandedPathList,
        ref uint pathListLength,
        uint flags);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);
}

internal static class NativeMethods
{
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    public const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    public const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    private const uint WM_SPAWN_WORKER = 0x052C;
    private const uint SMTO_NORMAL = 0x0000;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x00000002;
    private const int ATTACH_PARENT_PROCESS = -1;
    private const uint GW_OWNER = 4;
    private const uint WM_APPCOMMAND = 0x0319;
    private const uint WM_CLOSE = 0x0010;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_D = 0x44;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
    private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;
    private const ushort IMAGE_FILE_MACHINE_ARMNT = 0x01C4;
    private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    private const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
    private const int DWM_TNP_RECTDESTINATION = 0x00000001;
    private const int DWM_TNP_OPACITY = 0x00000004;
    private const int DWM_TNP_VISIBLE = 0x00000008;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64Process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumWindowsProc enumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destinationWindow, IntPtr sourceWindow, out IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnailId, out SIZE size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnailId, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int processInformationSize);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            this.X = x;
            this.Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;

        public SIZE(int cx, int cy)
        {
            this.CX = cx;
            this.CY = cy;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public sealed class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    public sealed class ApplicationWindowInfo
    {
        public IntPtr Handle { get; set; }
        public int ProcessId { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;

        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bOneXEnabled;

        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;

        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public static bool TrySetProcessPowerThrottling(bool enabled)
    {
        const int processPowerThrottling = 4;
        const uint processPowerThrottlingCurrentVersion = 1;
        const uint processPowerThrottlingExecutionSpeed = 0x1;

        try
        {
            PROCESS_POWER_THROTTLING_STATE state = new PROCESS_POWER_THROTTLING_STATE();
            state.Version = processPowerThrottlingCurrentVersion;
            state.ControlMask = processPowerThrottlingExecutionSpeed;
            state.StateMask = enabled ? processPowerThrottlingExecutionSpeed : 0;
            return SetProcessInformation(
                GetCurrentProcess(),
                processPowerThrottling,
                ref state,
                Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
        }
        catch
        {
            return false;
        }
    }

    public static bool UpdateLayeredWindowFromBitmap(IntPtr handle, Point location, Bitmap bitmap)
    {
        return UpdateLayeredWindowFromBitmap(handle, location, bitmap, 255);
    }

    public static bool UpdateLayeredWindowFromBitmap(IntPtr handle, Point location, Bitmap bitmap, byte sourceAlpha)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return false;
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return false;
            }

            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memoryDc, bitmapHandle);

            POINT destination = new POINT(location.X, location.Y);
            SIZE size = new SIZE(bitmap.Width, bitmap.Height);
            POINT source = new POINT(0, 0);
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = sourceAlpha;
            blend.AlphaFormat = AC_SRC_ALPHA;

            return UpdateLayeredWindow(
                handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                ULW_ALPHA);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldBitmap);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    public static string TryGetConnectedWifiSsid(Guid interfaceGuid)
    {
        const uint wlanClientVersion = 2;
        const uint success = 0;
        const int wlanOpcodeCurrentConnection = 7;

        IntPtr clientHandle = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;
        try
        {
            uint negotiatedVersion;
            uint status = WlanOpenHandle(wlanClientVersion, IntPtr.Zero, out negotiatedVersion, out clientHandle);
            if (status != success || clientHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            int dataSize;
            int opcodeValueType;
            status = WlanQueryInterface(
                clientHandle,
                ref interfaceGuid,
                wlanOpcodeCurrentConnection,
                IntPtr.Zero,
                out dataSize,
                out data,
                out opcodeValueType);
            if (status != success || data == IntPtr.Zero || dataSize <= 0)
            {
                return string.Empty;
            }

            WLAN_CONNECTION_ATTRIBUTES attributes =
                (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(data, typeof(WLAN_CONNECTION_ATTRIBUTES));
            string ssid = DecodeSsid(attributes.wlanAssociationAttributes.dot11Ssid);
            if (!string.IsNullOrEmpty(ssid))
            {
                return ssid;
            }

            return string.IsNullOrEmpty(attributes.strProfileName) ? string.Empty : attributes.strProfileName.Trim();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }

            if (clientHandle != IntPtr.Zero)
            {
                WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
    }

    private static string DecodeSsid(DOT11_SSID ssid)
    {
        if (ssid.ucSSID == null || ssid.uSSIDLength == 0)
        {
            return string.Empty;
        }

        int length = (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length);
        string text = Encoding.UTF8.GetString(ssid.ucSSID, 0, length);
        return text.Trim(new char[] { '\0' });
    }

    public static void TrySetDpiAware()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch
        {
        }
    }

    public static void AttachToParentConsole()
    {
        try
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
        catch
        {
        }
    }

    public static List<ApplicationWindowInfo> EnumerateApplicationWindows(IntPtr ownHandle)
    {
        List<ApplicationWindowInfo> windows = new List<ApplicationWindowInfo>();
        int ownProcessId = 0;
        try
        {
            ownProcessId = Process.GetCurrentProcess().Id;
        }
        catch
        {
        }

        EnumWindows(delegate(IntPtr handle, IntPtr lParam)
        {
            if (handle == IntPtr.Zero || handle == ownHandle)
            {
                return true;
            }

            if (!IsWindowVisible(handle))
            {
                return true;
            }

            if (GetWindow(handle, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            string className = GetWindowClassName(handle);
            if (IsShellOrUtilityWindowClass(className))
            {
                return true;
            }

            uint processIdValue;
            GetWindowThreadProcessId(handle, out processIdValue);
            int processId = processIdValue > int.MaxValue ? 0 : (int)processIdValue;
            if (string.Equals(className, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
            {
                int hostedProcessId;
                if (TryGetHostedApplicationProcessId(handle, processId, out hostedProcessId))
                {
                    processId = hostedProcessId;
                }
                else
                {
                    return true;
                }
            }

            if (processId <= 0 || processId == ownProcessId)
            {
                return true;
            }

            if (IsUtilityWindowProcess(processId))
            {
                return true;
            }

            RECT rect;
            if (!GetWindowRect(handle, out rect) ||
                rect.Right - rect.Left < 32 ||
                rect.Bottom - rect.Top < 32)
            {
                return true;
            }

            string title = GetWindowTitle(handle);
            if (string.IsNullOrEmpty(title))
            {
                return true;
            }

            windows.Add(new ApplicationWindowInfo
            {
                Handle = handle,
                ProcessId = processId,
                Title = title,
                ClassName = className
            });
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool TryGetHostedApplicationProcessId(IntPtr frameHandle, int frameProcessId, out int hostedProcessId)
    {
        hostedProcessId = 0;
        int foundProcessId = 0;
        try
        {
            EnumChildWindows(frameHandle, delegate(IntPtr childHandle, IntPtr lParam)
            {
                uint childProcessIdValue;
                GetWindowThreadProcessId(childHandle, out childProcessIdValue);
                int childProcessId = childProcessIdValue > int.MaxValue ? 0 : (int)childProcessIdValue;
                if (childProcessId > 0 && childProcessId != frameProcessId)
                {
                    foundProcessId = childProcessId;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            foundProcessId = 0;
        }

        hostedProcessId = foundProcessId;
        return hostedProcessId > 0;
    }

    private static bool IsUtilityWindowProcess(int processId)
    {
        if (processId <= 0)
        {
            return true;
        }

        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                string name = process.ProcessName ?? string.Empty;
                return string.Equals(name, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "Widgets", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "ClickToDo", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (IsIconic(handle))
            {
                ShowWindow(handle, SW_RESTORE);
            }
            else
            {
                ShowWindow(handle, SW_SHOW);
            }

            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    public static void SendMediaCommand(IntPtr handle, int command)
    {
        try
        {
            SendMessage(handle, WM_APPCOMMAND, handle, new IntPtr(command << 16));
        }
        catch
        {
        }
    }

    public static bool IsApplicationWindowVisible(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!IsWindowVisible(handle))
            {
                return false;
            }

            RECT rect;
            return GetWindowRect(handle, out rect) &&
                rect.Right > rect.Left &&
                rect.Bottom > rect.Top;
        }
        catch
        {
            return false;
        }
    }

    public static bool RegisterDwmThumbnail(IntPtr destinationWindow, IntPtr sourceWindow, out IntPtr thumbnailId)
    {
        thumbnailId = IntPtr.Zero;
        if (destinationWindow == IntPtr.Zero || sourceWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return DwmRegisterThumbnail(destinationWindow, sourceWindow, out thumbnailId) == 0 &&
                thumbnailId != IntPtr.Zero;
        }
        catch
        {
            thumbnailId = IntPtr.Zero;
            return false;
        }
    }

    public static void UnregisterDwmThumbnail(IntPtr thumbnailId)
    {
        if (thumbnailId == IntPtr.Zero)
        {
            return;
        }

        try
        {
            DwmUnregisterThumbnail(thumbnailId);
        }
        catch
        {
        }
    }

    public static Size QueryThumbnailSourceSize(IntPtr thumbnailId)
    {
        if (thumbnailId == IntPtr.Zero)
        {
            return Size.Empty;
        }

        try
        {
            SIZE size;
            if (DwmQueryThumbnailSourceSize(thumbnailId, out size) == 0)
            {
                return new Size(Math.Max(0, size.CX), Math.Max(0, size.CY));
            }
        }
        catch
        {
        }

        return Size.Empty;
    }

    public static bool UpdateDwmThumbnail(IntPtr thumbnailId, Rectangle destination, byte opacity)
    {
        if (thumbnailId == IntPtr.Zero || destination.Width <= 0 || destination.Height <= 0)
        {
            return false;
        }

        try
        {
            DWM_THUMBNAIL_PROPERTIES properties = new DWM_THUMBNAIL_PROPERTIES();
            properties.dwFlags =
                DWM_TNP_RECTDESTINATION |
                DWM_TNP_OPACITY |
                DWM_TNP_VISIBLE |
                DWM_TNP_SOURCECLIENTAREAONLY;
            properties.rcDestination = ToRect(destination);
            properties.opacity = opacity;
            properties.fVisible = true;
            properties.fSourceClientAreaOnly = false;
            return DwmUpdateThumbnailProperties(thumbnailId, ref properties) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestCloseWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    public static void ToggleDesktop()
    {
        SendWinKeyChord(VK_D);
    }

    public static void OpenStartMenu()
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    private static void SendWinKeyChord(byte virtualKey)
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    public static IntPtr GetForegroundWindowHandle()
    {
        try
        {
            return GetForegroundWindow();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static bool TryGetWindowProcessId(IntPtr handle, out int processId)
    {
        processId = 0;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            uint processIdValue;
            GetWindowThreadProcessId(handle, out processIdValue);
            if (processIdValue == 0 || processIdValue > int.MaxValue)
            {
                return false;
            }

            processId = (int)processIdValue;
            return true;
        }
        catch
        {
            processId = 0;
            return false;
        }
    }

    private static RECT ToRect(Rectangle rectangle)
    {
        RECT rect = new RECT();
        rect.Left = rectangle.Left;
        rect.Top = rectangle.Top;
        rect.Right = rectangle.Right;
        rect.Bottom = rectangle.Bottom;
        return rect;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(length + 1);
        int copied = GetWindowText(handle, builder, builder.Capacity);
        if (copied <= 0)
        {
            return string.Empty;
        }

        return builder.ToString().Trim();
    }

    private static bool IsShellOrUtilityWindowClass(string className)
    {
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "DV2ControlHost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
    }

    public static IntPtr FindDesktopHostWindow()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr result;
            SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out result);
        }

        IntPtr worker = IntPtr.Zero;
        EnumWindows(delegate(IntPtr topHandle, IntPtr lParam)
        {
            IntPtr shellView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                IntPtr nextWorker = FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
                if (nextWorker != IntPtr.Zero)
                {
                    worker = nextWorker;
                }
            }

            return true;
        }, IntPtr.Zero);

        if (worker != IntPtr.Zero)
        {
            return worker;
        }

        return progman;
    }

    public static bool IsForegroundWindowFullscreen(IntPtr ownHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownHandle)
        {
            return false;
        }

        if (!IsWindowVisible(foreground))
        {
            return false;
        }

        string className = GetWindowClassName(foreground);
        if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RECT rect;
        if (!GetWindowRect(foreground, out rect))
        {
            return false;
        }

        Rectangle bounds = Screen.FromHandle(foreground).Bounds;
        const int Tolerance = 2;
        return rect.Left <= bounds.Left + Tolerance &&
               rect.Top <= bounds.Top + Tolerance &&
               rect.Right >= bounds.Right - Tolerance &&
               rect.Bottom >= bounds.Bottom - Tolerance;
    }

    public static bool IsForegroundDesktopOrShell(IntPtr ownHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownHandle)
        {
            return true;
        }

        string className = GetWindowClassName(foreground);
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        StringBuilder builder = new StringBuilder(256);
        int length = GetClassName(handle, builder, builder.Capacity);
        if (length <= 0)
        {
            return string.Empty;
        }

        return builder.ToString();
    }

    public static string DescribeProcessMachine()
    {
        ushort processMachine;
        ushort nativeMachine;
        try
        {
            if (IsWow64Process2(GetCurrentProcess(), out processMachine, out nativeMachine))
            {
                return string.Format(
                    "process={0}, native={1}, 64bit={2}",
                    MachineName(processMachine),
                    MachineName(nativeMachine),
                    Environment.Is64BitProcess);
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        bool wow64;
        if (IsWow64Process(GetCurrentProcess(), out wow64))
        {
            return string.Format("wow64={0}, 64bit={1}", wow64, Environment.Is64BitProcess);
        }

        return string.Format("64bit={0}", Environment.Is64BitProcess);
    }

    private static string MachineName(ushort machine)
    {
        if (machine == IMAGE_FILE_MACHINE_UNKNOWN)
        {
            return "native";
        }

        if (machine == IMAGE_FILE_MACHINE_ARM64)
        {
            return "ARM64";
        }

        if (machine == IMAGE_FILE_MACHINE_ARMNT)
        {
            return "ARM";
        }

        if (machine == IMAGE_FILE_MACHINE_AMD64)
        {
            return "x64";
        }

        if (machine == IMAGE_FILE_MACHINE_I386)
        {
            return "x86";
        }

        return "0x" + machine.ToString("X4");
    }
}
