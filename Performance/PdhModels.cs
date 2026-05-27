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
