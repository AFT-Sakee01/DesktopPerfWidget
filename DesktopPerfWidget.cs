using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
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
                    "{0} {1:0}% {2} | Memory {3:0.0}/{4:0.0} GB ({5:0}%) | Disk {6:0}% | GPU {7:0}% {8:0.0}/{9:0.#} GB | NPU {10:0}% {11:0.0}/{12:0.#} GB | Network {13} UP {14:0.0} DL {15:0.0} Kbps",
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
                    snapshot.NetworkSentBytesPerSecond * 8.0 / 1000.0,
                    snapshot.NetworkReceivedBytesPerSecond * 8.0 / 1000.0);
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
        this.timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
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

        RenderLayeredWindow();
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int backgroundAlpha = GetBackgroundOpacityAlpha();

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(13)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(backgroundAlpha, 18, 19, 22)))
        using (Pen outline = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1, S(1))))
        {
            g.FillPath(background, shell);
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
                DrawWidget(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap))
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
                new string[] { "Memory", string.Format("MEM {0:0}%", this.snapshot.MemoryPercent), FormatGbPair(this.snapshot.MemoryUsedGb, this.snapshot.MemoryTotalGb) },
                new Color[] { Color.FromArgb(226, 126, 255) },
                new List<double>[] { this.memoryHistory },
                100.0,
                false);
            memoryPanel.AlertPercent = this.snapshot.MemoryPercent;
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

        return string.Format("{0:0.0} {1}", kbps / divisor, unit);
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
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private PowerReading cachedPowerReading;
    private DateTime cachedPowerReadingUtc;

    private struct PowerReading
    {
        public bool StatusKnown;
        public bool IsCharging;
        public bool WattsKnown;
        public double Watts;
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
        this.MaximumSize = new Size(WidgetSettings.MaxClockWidth, WidgetSettings.MaxClockHeight);
        this.Size = new Size(this.currentSettings.ClockWidth, this.currentSettings.ClockHeight);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = 250;
        this.timer.Tick += delegate { RenderLayeredWindow(); };
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
        this.timer.Tick -= delegate { RenderLayeredWindow(); };
        this.timer.Dispose();
        base.OnFormClosed(e);
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
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();

        Size desiredSize = new Size(this.currentSettings.ClockWidth, this.currentSettings.ClockHeight);
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
        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.ClockLeftX, workArea.Right - this.Width));
        int top = this.currentSettings.ClockBottomY - this.Height + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int alpha = GetClockOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(12)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(alpha, 18, 19, 22)))
        using (Pen outline = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1, S(1))))
        {
            g.FillPath(background, shell);
            g.DrawPath(outline, shell);
        }

        string formatText = this.currentSettings.ClockUse24Hour ? "HH:mm:ss" : "h:mm:ss tt";
        string timeText = DateTime.Now.ToString(formatText);
        RectangleF textRect = new RectangleF(S(12), S(5), Math.Max(10, this.Width - S(24)), Math.Max(10, this.Height - S(10)));
        if (this.currentSettings.ClockCalendarEnabled || this.currentSettings.ClockPowerEnabled)
        {
            DrawClockWithInfo(g, timeText, textRect);
            return;
        }

        float fontSize = Math.Max(14.0f, Math.Min(this.Height * 0.56f, this.Width * 0.16f));

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
                DrawClock(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap))
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

    private int GetClockOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ClockTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
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
    public int ClockWidth { get; set; }
    public int ClockHeight { get; set; }
    public int ClockLeftX { get; set; }
    public int ClockBottomY { get; set; }
    public int ClockTransparencyPercent { get; set; }
    public bool ClockUse24Hour { get; set; }
    public bool ClockCalendarEnabled { get; set; }
    public bool ClockPowerEnabled { get; set; }
    public WidgetVisibilityMode VisibilityMode { get; set; }
    public bool StartupEnabled { get; set; }
    public bool ShowCpu { get; set; }
    public bool ShowMemory { get; set; }
    public bool ShowDisk { get; set; }
    public bool ShowNetwork { get; set; }
    public bool ShowGpu { get; set; }
    public bool ShowNpu { get; set; }
    public bool AlertTestEnabled { get; set; }
    public bool PowerSavingEnabled { get; set; }
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
        this.ClockWidth = defaults.ClockWidth;
        this.ClockHeight = defaults.ClockHeight;
        this.ClockLeftX = defaults.ClockLeftX;
        this.ClockBottomY = defaults.ClockBottomY;
        this.ClockTransparencyPercent = DefaultBackgroundTransparency;
        this.ClockUse24Hour = true;
        this.ClockCalendarEnabled = false;
        this.ClockPowerEnabled = false;
        this.VisibilityMode = WidgetVisibilityMode.DesktopOnly;
        this.StartupEnabled = Program.IsStartupEnabled();
        this.ShowCpu = true;
        this.ShowMemory = true;
        this.ShowDisk = true;
        this.ShowNetwork = true;
        this.ShowGpu = true;
        this.ShowNpu = true;
        this.AlertTestEnabled = false;
        this.PowerSavingEnabled = true;
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
        settings.ClockWidth = Clamp((int)Math.Round(192.0f * scale), MinClockWidth, MaxClockWidth);
        settings.ClockHeight = Clamp((int)Math.Round(58.0f * scale), MinClockHeight, MaxClockHeight);
        settings.ClockLeftX = workArea.Right - settings.ClockWidth - margin;
        settings.ClockBottomY = settings.BottomY - settings.Height - margin;
        settings.ClockTransparencyPercent = DefaultBackgroundTransparency;
        settings.ClockUse24Hour = true;
        settings.ClockCalendarEnabled = false;
        settings.ClockPowerEnabled = false;
        settings.VisibilityMode = WidgetVisibilityMode.DesktopOnly;
        settings.StartupEnabled = Program.IsStartupEnabled();
        settings.ShowCpu = true;
        settings.ShowMemory = true;
        settings.ShowDisk = true;
        settings.ShowNetwork = true;
        settings.ShowGpu = true;
        settings.ShowNpu = true;
        settings.AlertTestEnabled = false;
        settings.PowerSavingEnabled = true;
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
            ClockWidth = this.ClockWidth,
            ClockHeight = this.ClockHeight,
            ClockLeftX = this.ClockLeftX,
            ClockBottomY = this.ClockBottomY,
            ClockTransparencyPercent = this.ClockTransparencyPercent,
            ClockUse24Hour = this.ClockUse24Hour,
            ClockCalendarEnabled = this.ClockCalendarEnabled,
            ClockPowerEnabled = this.ClockPowerEnabled,
            VisibilityMode = this.VisibilityMode,
            StartupEnabled = this.StartupEnabled,
            ShowCpu = this.ShowCpu,
            ShowMemory = this.ShowMemory,
            ShowDisk = this.ShowDisk,
            ShowNetwork = this.ShowNetwork,
            ShowGpu = this.ShowGpu,
            ShowNpu = this.ShowNpu,
            AlertTestEnabled = this.AlertTestEnabled,
            PowerSavingEnabled = this.PowerSavingEnabled,
            MetricOrder = CloneMetricOrder(this.MetricOrder)
        };
    }

    public void Normalize()
    {
        this.Width = Clamp(this.Width, MinWidth, MaxWidth);
        this.Height = Clamp(this.Height, MinHeight, MaxHeight);
        this.BackgroundTransparencyPercent = Clamp(this.BackgroundTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
        this.ClockWidth = Clamp(this.ClockWidth, MinClockWidth, MaxClockWidth);
        this.ClockHeight = Clamp(this.ClockHeight, MinClockHeight, MaxClockHeight);
        this.ClockTransparencyPercent = Clamp(this.ClockTransparencyPercent, MinBackgroundTransparency, MaxBackgroundTransparency);
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
            "Version=2",
            "Width=" + this.Width,
            "Height=" + this.Height,
            "LeftX=" + this.LeftX,
            "BottomY=" + this.BottomY,
            "BackgroundTransparencyPercent=" + this.BackgroundTransparencyPercent,
            "ClockWidth=" + this.ClockWidth,
            "ClockHeight=" + this.ClockHeight,
            "ClockLeftX=" + this.ClockLeftX,
            "ClockBottomY=" + this.ClockBottomY,
            "ClockTransparencyPercent=" + this.ClockTransparencyPercent,
            "ClockUse24Hour=" + this.ClockUse24Hour,
            "ClockCalendarEnabled=" + this.ClockCalendarEnabled,
            "ClockPowerEnabled=" + this.ClockPowerEnabled,
            "VisibilityMode=" + this.VisibilityMode,
            "StartupEnabled=" + this.StartupEnabled,
            "ShowCpu=" + this.ShowCpu,
            "ShowMemory=" + this.ShowMemory,
            "ShowDisk=" + this.ShowDisk,
            "ShowNetwork=" + this.ShowNetwork,
            "ShowGpu=" + this.ShowGpu,
            "ShowNpu=" + this.ShowNpu,
            "AlertTestEnabled=" + this.AlertTestEnabled,
            "PowerSavingEnabled=" + this.PowerSavingEnabled,
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

        if (string.Equals(key, "PowerSavingEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out boolValue))
        {
            settings.PowerSavingEnabled = boolValue;
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
    private NumericUpDown clockWidthBox;
    private NumericUpDown clockHeightBox;
    private NumericUpDown clockLeftXBox;
    private NumericUpDown clockBottomYBox;
    private NumericUpDown clockTransparencyBox;
    private TrackBar widthSlider;
    private TrackBar heightSlider;
    private TrackBar leftXSlider;
    private TrackBar bottomYSlider;
    private TrackBar backgroundTransparencySlider;
    private TrackBar clockWidthSlider;
    private TrackBar clockHeightSlider;
    private TrackBar clockLeftXSlider;
    private TrackBar clockBottomYSlider;
    private TrackBar clockTransparencySlider;
    private ComboBox visibilityCombo;
    private Button alertTestButton;
    private CheckBox startupCheck;
    private CheckBox powerSavingCheck;
    private CheckBox clockUse24HourCheck;
    private CheckBox clockCalendarCheck;
    private CheckBox clockPowerCheck;
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
        root.ColumnCount = 3;
        root.RowCount = 19;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 420));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        this.Controls.Add(root);

        this.widthBox = BuildNumberBox(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightBox = BuildNumberBox(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.backgroundTransparencyBox.Increment = 1;
        this.clockWidthBox = BuildNumberBox(WidgetSettings.MinClockWidth, WidgetSettings.MaxClockWidth);
        this.clockHeightBox = BuildNumberBox(WidgetSettings.MinClockHeight, WidgetSettings.MaxClockHeight);
        this.clockLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.clockBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.clockTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.clockTransparencyBox.Increment = 1;
        this.widthSlider = BuildSlider(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightSlider = BuildSlider(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.clockWidthSlider = BuildSlider(WidgetSettings.MinClockWidth, WidgetSettings.MaxClockWidth);
        this.clockHeightSlider = BuildSlider(WidgetSettings.MinClockHeight, WidgetSettings.MaxClockHeight);
        this.clockLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.clockBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.clockTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.visibilityCombo = BuildCombo();
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
        this.metricSlotPanels = new Panel[WidgetSettings.DefaultMetricOrder.Length];

        Label title = new Label();
        title.Text = "性能小窗设置";
        title.Font = new Font("Segoe UI", 16.0f, FontStyle.Bold);
        title.ForeColor = DarkTheme.Text;
        title.BackColor = DarkTheme.Window;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        root.SetColumnSpan(title, 3);
        root.Controls.Add(title, 0, 0);

        AddSliderRow(root, 1, "窗口宽度", this.widthBox, this.widthSlider);
        AddSliderRow(root, 2, "窗口高度", this.heightBox, this.heightSlider);
        AddSliderRow(root, 3, "位置 X", this.leftXBox, this.leftXSlider);
        AddSliderRow(root, 4, "位置 Y", this.bottomYBox, this.bottomYSlider);
        AddSliderRow(root, 5, "黑色背景透明度", this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        AddEditorRow(root, 6, "可见性", this.visibilityCombo);
        AddLabel(root, 7, "告警测试");
        Control alertEditor = BuildButtonEditor(this.alertTestButton);
        root.SetColumnSpan(alertEditor, 2);
        root.Controls.Add(alertEditor, 1, 7);

        FlowLayoutPanel runtimeOptions = new FlowLayoutPanel();
        runtimeOptions.Dock = DockStyle.Fill;
        runtimeOptions.FlowDirection = FlowDirection.LeftToRight;
        runtimeOptions.WrapContents = false;
        runtimeOptions.BackColor = DarkTheme.Window;
        runtimeOptions.Padding = new Padding(0, 10, 0, 0);
        this.startupCheck.Margin = new Padding(0, 0, 34, 0);
        this.powerSavingCheck.Margin = new Padding(0, 0, 0, 0);
        runtimeOptions.Controls.Add(this.startupCheck);
        runtimeOptions.Controls.Add(this.powerSavingCheck);
        AddLabel(root, 8, "运行选项");
        root.SetColumnSpan(runtimeOptions, 2);
        root.Controls.Add(runtimeOptions, 1, 8);

        Control metricLayoutEditor = BuildMetricLayoutEditor();
        AddLabel(root, 9, "栏目布局");
        root.SetColumnSpan(metricLayoutEditor, 2);
        root.Controls.Add(metricLayoutEditor, 1, 9);

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
        AddSliderRow(root, 15, "时间透明度", this.clockTransparencyBox, this.clockTransparencySlider);
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
        AddLabel(root, 16, "时间信息");
        root.SetColumnSpan(calendarOptions, 2);
        root.Controls.Add(calendarOptions, 1, 16);

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
        root.SetColumnSpan(buttons, 3);
        root.Controls.Add(buttons, 0, 18);

        this.visibilityCombo.Items.Add(new ComboOption("仅桌面可见", WidgetVisibilityMode.DesktopOnly));
        this.visibilityCombo.Items.Add(new ComboOption("一直可见", WidgetVisibilityMode.AlwaysVisible));
        this.visibilityCombo.Items.Add(new ComboOption("仅全屏不可见", WidgetVisibilityMode.HideWhenFullscreen));

        WirePair(this.widthBox, this.widthSlider);
        WirePair(this.heightBox, this.heightSlider);
        WirePair(this.leftXBox, this.leftXSlider);
        WirePair(this.bottomYBox, this.bottomYSlider);
        WirePair(this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        WirePair(this.clockWidthBox, this.clockWidthSlider);
        WirePair(this.clockHeightBox, this.clockHeightSlider);
        WirePair(this.clockLeftXBox, this.clockLeftXSlider);
        WirePair(this.clockBottomYBox, this.clockBottomYSlider);
        WirePair(this.clockTransparencyBox, this.clockTransparencySlider);
    }

    private static Size GetDesiredClientSize()
    {
        return new Size(1080, 1540);
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
            this.clockTransparencyBox.Value = settings.ClockTransparencyPercent;
            this.clockTransparencySlider.Value = settings.ClockTransparencyPercent;
            SelectComboValue(this.visibilityCombo, settings.VisibilityMode);
            this.startupCheck.Checked = settings.StartupEnabled;
            this.powerSavingCheck.Checked = settings.PowerSavingEnabled;
            this.clockUse24HourCheck.Checked = settings.ClockUse24Hour;
            this.clockCalendarCheck.Checked = settings.ClockCalendarEnabled;
            this.clockPowerCheck.Checked = settings.ClockPowerEnabled;
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
        settings.ClockWidth = (int)this.clockWidthBox.Value;
        settings.ClockHeight = (int)this.clockHeightBox.Value;
        settings.ClockLeftX = (int)this.clockLeftXBox.Value;
        settings.ClockBottomY = (int)this.clockBottomYBox.Value;
        settings.ClockTransparencyPercent = (int)this.clockTransparencyBox.Value;
        settings.ClockUse24Hour = this.clockUse24HourCheck.Checked;
        settings.ClockCalendarEnabled = this.clockCalendarCheck.Checked;
        settings.ClockPowerEnabled = this.clockPowerCheck.Checked;
        settings.VisibilityMode = (WidgetVisibilityMode)GetComboValue(this.visibilityCombo, WidgetVisibilityMode.DesktopOnly);
        settings.StartupEnabled = this.startupCheck.Checked;
        settings.PowerSavingEnabled = this.powerSavingCheck.Checked;
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
    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
    private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;
    private const ushort IMAGE_FILE_MACHINE_ARMNT = 0x01C4;
    private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    private const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

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
            blend.SourceConstantAlpha = 255;
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
