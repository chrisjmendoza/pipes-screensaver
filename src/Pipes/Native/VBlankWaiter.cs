using System.Runtime.InteropServices;

namespace Pipes.Native;

/// <summary>
/// Sleeps until a monitor's next vertical blank (the moment it starts showing a new frame).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> With VSync on, a frame here takes about 1 ms of work, then has to wait roughly 15 ms for
/// the monitor. OpenGL does that waiting inside the driver, usually in the first draw call of the next frame, when
/// it needs a buffer the display is still showing. NVIDIA's driver sometimes waits by <em>spinning</em>, keeping a
/// whole CPU core busy doing nothing, which is a poor trait in a screensaver. It varied between runs of the same
/// settings, from 10% to 105% of a core.
/// </para>
/// <para>
/// <b>The fix.</b> Wait for the vertical blank ourselves, with a call that puts the thread to sleep. By the time it
/// returns, the display has just flipped to the previous frame, so a buffer is free. Drawing the next frame then
/// doesn't block, and <c>SwapBuffers</c> just queues it for the next flip. The driver never needs to wait, so it
/// never spins. VSync stays on in the driver as well, so there's still no tearing.
/// </para>
/// <para>
/// This uses <c>D3DKMTWaitForVerticalBlankEvent</c>, a documented but low-level Windows call (it's what graphics
/// drivers' user-mode parts use). If anything about it isn't available, <see cref="TryCreate"/> returns null and the
/// app simply behaves as before.
/// </para>
/// </remarks>
internal sealed class VBlankWaiter : IDisposable
{
    private uint _adapter;
    private readonly uint _sourceId;

    private VBlankWaiter(uint adapter, uint sourceId)
    {
        _adapter = adapter;
        _sourceId = sourceId;
    }

    /// <summary>A waiter for the monitor that most of <paramref name="hwnd"/> is on, or null if that isn't possible.</summary>
    public static VBlankWaiter? TryCreate(IntPtr hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;

            // A device context for that monitor ("\\.\DISPLAY2", say) lets the kernel graphics layer find which
            // adapter drives it and which output ("video present source") it is.
            var hdc = CreateDC(null, info.szDevice, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var open = new D3DKMT_OPENADAPTERFROMHDC { hDc = hdc };
                if (D3DKMTOpenAdapterFromHdc(ref open) != 0) return null;
                var waiter = new VBlankWaiter(open.hAdapter, open.VidPnSourceId);
                // One trial wait, so a system where it doesn't work falls back now rather than mid-run.
                return waiter.Wait() ? waiter : Dispose(waiter);
            }
            finally
            {
                DeleteDC(hdc);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        static VBlankWaiter? Dispose(VBlankWaiter w)
        {
            w.Dispose();
            return null;
        }
    }

    /// <summary>Sleep until the next vertical blank. False if the wait failed (e.g. the display was turned off).</summary>
    public bool Wait()
    {
        var wait = new D3DKMT_WAITFORVERTICALBLANKEVENT { hAdapter = _adapter, VidPnSourceId = _sourceId };
        return D3DKMTWaitForVerticalBlankEvent(ref wait) == 0;
    }

    public void Dispose()
    {
        if (_adapter == 0) return;
        var close = new D3DKMT_CLOSEADAPTER { hAdapter = _adapter };
        D3DKMTCloseAdapter(ref close);
        _adapter = 0;
    }

    // ---- Interop -----------------------------------------------------------------------------------------------

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public Win32.RECT rcMonitor;
        public Win32.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    // Field order and sizes match the C structs in d3dkmthk.h (handles are 32-bit, the LUID is two 32-bit halves).
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMHDC
    {
        public IntPtr hDc;
        public uint hAdapter;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_WAITFORVERTICALBLANKEVENT
    {
        public uint hAdapter;
        public uint hDevice;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr initData);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    // These return an NTSTATUS: 0 means success.
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromHdc(ref D3DKMT_OPENADAPTERFROMHDC data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTWaitForVerticalBlankEvent(ref D3DKMT_WAITFORVERTICALBLANKEVENT data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER data);
}
