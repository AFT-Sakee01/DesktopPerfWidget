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
