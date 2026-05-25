using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WiredMonitorClient.Diagnostics;

namespace WiredMonitorClient.Display;

public readonly record struct ClientDisplayInfo(int Width, int Height, int RefreshRate, int Dpi);

public static class WindowsDisplayInfo
{
    private const int EnumCurrentSettings = -1;
    private const uint MonitorDefaultToNearest = 2;

    public static ClientDisplayInfo FromWindow(Window window)
    {
        var overrideInfo = TryReadOverride();
        if (overrideInfo != null)
            return overrideInfo.Value;

        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = hwnd != IntPtr.Zero
            ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
            : IntPtr.Zero;

        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfoEx
            {
                cbSize = Marshal.SizeOf<MonitorInfoEx>(),
            };

            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var mode = new DevMode
                {
                    dmSize = (ushort)Marshal.SizeOf<DevMode>(),
                };

                if (EnumDisplaySettings(monitorInfo.szDevice, EnumCurrentSettings, ref mode) &&
                    mode.dmPelsWidth >= 640 &&
                    mode.dmPelsHeight >= 360)
                {
                    var refreshRate = mode.dmDisplayFrequency is >= 24 and <= 240
                        ? Math.Min((int)mode.dmDisplayFrequency, 120)
                        : 60;
                    var dpi = GetWindowOrMonitorDpi(hwnd, monitor);
                    var info = new ClientDisplayInfo((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, refreshRate, dpi);
                    DiagLog.Write($"显示器探测: device={monitorInfo.szDevice}, mode={info.Width}x{info.Height}@{info.RefreshRate}, dpi={info.Dpi}");
                    return info;
                }

                var width = Math.Max(monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left, 640);
                var height = Math.Max(monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top, 360);
                var fallbackInfo = new ClientDisplayInfo(width, height, 60, GetWindowOrMonitorDpi(hwnd, monitor));
                DiagLog.Write($"显示器探测回退: device={monitorInfo.szDevice}, rect={fallbackInfo.Width}x{fallbackInfo.Height}, dpi={fallbackInfo.Dpi}");
                return fallbackInfo;
            }
        }

        var systemInfo = new ClientDisplayInfo(
            Math.Max(GetSystemMetrics(0), 640),
            Math.Max(GetSystemMetrics(1), 360),
            60,
            GetSystemDpi());
        DiagLog.Write($"显示器探测系统回退: {systemInfo.Width}x{systemInfo.Height}@{systemInfo.RefreshRate}, dpi={systemInfo.Dpi}");
        return systemInfo;
    }

    private static ClientDisplayInfo? TryReadOverride()
    {
        var width = ReadEnvInt("WIRED_MONITOR_CLIENT_WIDTH", 640, 16_384);
        var height = ReadEnvInt("WIRED_MONITOR_CLIENT_HEIGHT", 360, 16_384);
        if (width == null || height == null)
            return null;

        var refreshRate = ReadEnvInt("WIRED_MONITOR_CLIENT_REFRESH", 24, 240) ?? 60;
        var dpi = ReadEnvInt("WIRED_MONITOR_CLIENT_DPI", 72, 500) ?? GetSystemDpi();
        var info = new ClientDisplayInfo(width.Value, height.Value, Math.Min(refreshRate, 120), dpi);
        DiagLog.Write($"显示器探测使用环境变量: {info.Width}x{info.Height}@{info.RefreshRate}, dpi={info.Dpi}");
        return info;
    }

    private static int? ReadEnvInt(string name, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed >= min && parsed <= max
            ? parsed
            : null;
    }

    private static int GetWindowOrMonitorDpi(IntPtr hwnd, IntPtr monitor)
    {
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                var windowDpi = (int)GetDpiForWindow(hwnd);
                if (windowDpi is >= 72 and <= 500)
                    return windowDpi;
            }
        }
        catch
        {
        }

        try
        {
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out _) == 0 &&
                dpiX is >= 72 and <= 500)
            {
                return (int)dpiX;
            }
        }
        catch
        {
        }

        return GetSystemDpi();
    }

    private static int GetSystemDpi()
    {
        try
        {
            var dpi = (int)GetDpiForSystem();
            return dpi is >= 72 and <= 500 ? dpi : 96;
        }
        catch
        {
            return 96;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public RectStruct rcMonitor;
        public RectStruct rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
