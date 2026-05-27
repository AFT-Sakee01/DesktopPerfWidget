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
            AppIconCache.DisposeAll();

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
