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
