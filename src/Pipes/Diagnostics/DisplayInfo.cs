using System.Runtime.InteropServices;
using Pipes.Native;

namespace Pipes.Diagnostics;

/// <summary>Facts about the monitor a window is on, for the stats overlay: refresh rate and display scaling.</summary>
internal static class DisplayInfo
{
    /// <summary>
    /// The refresh rate of the monitor most of <paramref name="hwnd"/> is on, in Hz: the same monitor VSync (and
    /// <c>VBlankWaiter</c>) paces frames to. 60 if Windows won't say (it reports 0 or 1 for "hardware default").
    /// </summary>
    public static int RefreshRate(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return 60;
        var mode = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        return Win32.EnumDisplaySettings(info.szDevice, Win32.ENUM_CURRENT_SETTINGS, ref mode) && mode.dmDisplayFrequency > 1
            ? (int)mode.dmDisplayFrequency
            : 60;
    }

    /// <summary>
    /// Display scaling for <paramref name="hwnd"/>: 1 at 100%, 1.25 at 125%... The process is per-monitor DPI aware,
    /// so this is the scaling of the monitor the window is on.
    /// </summary>
    public static float Scale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96f : 1f;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public int rcMonitorLeft, rcMonitorTop, rcMonitorRight, rcMonitorBottom;
        public int rcWorkLeft, rcWorkTop, rcWorkRight, rcWorkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
