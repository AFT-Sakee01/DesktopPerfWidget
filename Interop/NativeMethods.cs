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
    private const int VK_LBUTTON = 0x01;
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
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const int SHIL_LARGE = 0;
    private const int SHIL_EXTRALARGE = 2;
    private const int SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 0x00000001;
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_ICONONLY = 0x00000004;
    private const int MAX_PATH = 260;
    private const int INFOTIPSIZE = 1024;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder imageFileName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

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
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileString(
        string section,
        string key,
        string defaultValue,
        StringBuilder returnedString,
        uint size,
        string fileName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        IntPtr[] iconHandles,
        int[] iconIds,
        uint iconCount,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        IntPtr[] largeIcons,
        IntPtr[] smallIcons,
        uint icons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref SHFILEINFO fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(
        int imageList,
        ref Guid interfaceId,
        out IImageList imageListInterface);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string parsingName,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IShellItemImageFactory imageFactory);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int Add(IntPtr imageBitmap, IntPtr maskBitmap, ref int index);

        [PreserveSig]
        int ReplaceIcon(int index, IntPtr icon, ref int newIndex);

        [PreserveSig]
        int SetOverlayImage(int imageIndex, int overlayIndex);

        [PreserveSig]
        int Replace(int index, IntPtr imageBitmap, IntPtr maskBitmap);

        [PreserveSig]
        int AddMasked(IntPtr imageBitmap, int maskColor, ref int index);

        [PreserveSig]
        int Draw(IntPtr drawParameters);

        [PreserveSig]
        int Remove(int index);

        [PreserveSig]
        int GetIcon(int index, int flags, out IntPtr icon);
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxFile,
            IntPtr findData,
            uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr idList);

        [PreserveSig]
        int SetIDList(IntPtr idList);

        [PreserveSig]
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxDirectory);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxArguments);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotkey(out short hotkey);

        [PreserveSig]
        int SetHotkey(short hotkey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        [PreserveSig]
        int Resolve(IntPtr ownerHandle, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public sealed class ShellLinkInfo
    {
        public string TargetPath { get; set; }
        public string Arguments { get; set; }
        public string IconPath { get; set; }
        public int IconIndex { get; set; }
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

    public static string TryReadIniValue(string fileName, string section, string key)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            StringBuilder builder = new StringBuilder(4096);
            uint length = GetPrivateProfileString(section, key, string.Empty, builder, (uint)builder.Capacity, fileName);
            if (length == 0)
            {
                return string.Empty;
            }

            return builder.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryResolveShortcut(string fileName, out ShellLinkInfo linkInfo)
    {
        linkInfo = null;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        object linkObject = null;
        try
        {
            linkObject = new ShellLink();
            System.Runtime.InteropServices.ComTypes.IPersistFile persistFile =
                (System.Runtime.InteropServices.ComTypes.IPersistFile)linkObject;
            persistFile.Load(fileName, 0);

            IShellLinkW shellLink = (IShellLinkW)linkObject;
            StringBuilder target = new StringBuilder(MAX_PATH);
            StringBuilder arguments = new StringBuilder(INFOTIPSIZE);
            StringBuilder iconPath = new StringBuilder(MAX_PATH);
            int iconIndex;

            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            shellLink.GetArguments(arguments, arguments.Capacity);
            shellLink.GetIconLocation(iconPath, iconPath.Capacity, out iconIndex);

            linkInfo = new ShellLinkInfo
            {
                TargetPath = target.ToString().Trim(),
                Arguments = arguments.ToString().Trim(),
                IconPath = iconPath.ToString().Trim(),
                IconIndex = iconIndex
            };
            return !string.IsNullOrEmpty(linkInfo.TargetPath) || !string.IsNullOrEmpty(linkInfo.IconPath);
        }
        catch
        {
            linkInfo = null;
            return false;
        }
        finally
        {
            if (linkObject != null && Marshal.IsComObject(linkObject))
            {
                try
                {
                    Marshal.FinalReleaseComObject(linkObject);
                }
                catch
                {
                }
            }
        }
    }

    public static Bitmap TryExtractIconBitmap(string fileName, int iconIndex)
    {
        if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
        {
            return null;
        }

        Bitmap bitmap = TryExtractPrivateIconBitmap(fileName, iconIndex);
        if (bitmap != null)
        {
            return bitmap;
        }

        return TryExtractAssociatedIconBitmap(fileName, iconIndex);
    }

    public static Bitmap TryLoadShellItemBitmap(string parsingName)
    {
        if (string.IsNullOrEmpty(parsingName))
        {
            return null;
        }

        IShellItemImageFactory imageFactory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            Guid factoryGuid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
            int hresult = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref factoryGuid, out imageFactory);
            if (hresult != 0 || imageFactory == null)
            {
                return null;
            }

            SIZE size = new SIZE(256, 256);
            hresult = imageFactory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out bitmapHandle);
            if (hresult != 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            using (Bitmap bitmap = Image.FromHbitmap(bitmapHandle))
            {
                return new Bitmap(bitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (imageFactory != null && Marshal.IsComObject(imageFactory))
            {
                try
                {
                    Marshal.ReleaseComObject(imageFactory);
                }
                catch
                {
                }
            }
        }
    }

    public static Bitmap TryLoadShellIconBitmap(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
        {
            return null;
        }

        try
        {
            SHFILEINFO fileInfo = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(
                fileName,
                0,
                ref fileInfo,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_SYSICONINDEX);
            if (result == IntPtr.Zero || fileInfo.iIcon < 0)
            {
                return null;
            }

            int[] imageLists = new int[] { SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE };
            for (int i = 0; i < imageLists.Length; i++)
            {
                Bitmap bitmap = TryGetShellImageListBitmap(fileInfo.iIcon, imageLists[i]);
                if (bitmap != null)
                {
                    return bitmap;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static Bitmap TryExtractPrivateIconBitmap(string fileName, int iconIndex)
    {
        IntPtr[] icons = new IntPtr[1];
        int[] iconIds = new int[1];
        try
        {
            uint extracted = PrivateExtractIcons(fileName, iconIndex, 256, 256, icons, iconIds, 1, 0);
            if (extracted == 0 || extracted == uint.MaxValue || icons[0] == IntPtr.Zero)
            {
                return null;
            }

            return BitmapFromIconHandle(icons[0]);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero)
            {
                DestroyIcon(icons[0]);
            }
        }
    }

    private static Bitmap TryExtractAssociatedIconBitmap(string fileName, int iconIndex)
    {
        IntPtr[] icons = new IntPtr[1];
        try
        {
            uint extracted = ExtractIconEx(fileName, iconIndex, icons, null, 1);
            if (extracted == 0 || icons[0] == IntPtr.Zero)
            {
                return null;
            }

            return BitmapFromIconHandle(icons[0]);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero)
            {
                DestroyIcon(icons[0]);
            }
        }
    }

    private static Bitmap TryGetShellImageListBitmap(int iconIndex, int imageListId)
    {
        IImageList imageList = null;
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            Guid imageListGuid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            int hresult = SHGetImageList(imageListId, ref imageListGuid, out imageList);
            if (hresult != 0 || imageList == null)
            {
                return null;
            }

            if (imageList.GetIcon(iconIndex, ILD_TRANSPARENT, out iconHandle) != 0 || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            return BitmapFromIconHandle(iconHandle);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }

            if (imageList != null && Marshal.IsComObject(imageList))
            {
                try
                {
                    Marshal.ReleaseComObject(imageList);
                }
                catch
                {
                }
            }
        }
    }

    private static Bitmap BitmapFromIconHandle(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero)
        {
            return null;
        }

        using (Icon icon = (Icon)Icon.FromHandle(iconHandle).Clone())
        {
            return icon.ToBitmap();
        }
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

    public static string TryGetProcessImagePath(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            StringBuilder path = new StringBuilder(32768);
            int size = path.Capacity;
            if (!QueryFullProcessImageName(processHandle, 0, path, ref size) || size <= 0)
            {
                return string.Empty;
            }

            return path.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(processHandle);
                }
                catch
                {
                }
            }
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

    public static bool IsForegroundWindowMaximizedOrFullscreen(IntPtr ownHandle)
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

        if (IsZoomed(foreground))
        {
            return true;
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

    public static bool IsLeftMouseButtonDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & unchecked((short)0x8000)) != 0;
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
