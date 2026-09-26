using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pipes.Diagnostics;

/// <summary>
/// What the hardware is doing, sampled once a second on a background thread for the stats overlay: CPU load, and
/// the GPU's load, temperature, clocks, power and memory where the system can tell us.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the numbers come from.</b> CPU load is plain Windows bookkeeping (<c>GetSystemTimes</c> for the whole
/// machine, the process's own CPU time for Pipes). For the GPU there's no single Windows API that covers every
/// vendor, so it tries two:
/// <list type="bullet">
/// <item><b>NVML</b>, the NVIDIA Management Library (<c>nvml.dll</c>), which every NVIDIA driver installs. It's what
/// <c>nvidia-smi</c> and most monitoring tools use: load, temperature, clocks, power, fan, memory, P-state.</item>
/// <item>Otherwise the <b>"GPU Engine" performance counters</b> (the ones Task Manager's GPU graphs read), through
/// PDH, the Performance Data Helper API. They work on any vendor's GPU but only give load.</item>
/// </list>
/// Anything unavailable stays null and the overlay leaves that row out.
/// </para>
/// <para>
/// <b>Why a thread.</b> Some of these calls are slow: enumerating the GPU engine counters can take tens of
/// milliseconds, and NVML queries the driver. On the render thread that would show up as a hitch in the very frame
/// graph it's there to explain. Once a second on a background thread, it costs nothing visible.
/// </para>
/// </remarks>
internal sealed unsafe class SystemMonitor : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly string _glRenderer;

    public SystemMonitor(string glRenderer)
    {
        _glRenderer = glRenderer;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pipes stats sampler", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The latest sample, replaced as a whole once a second (so readers never see half an update).</summary>
    public SystemSnapshot Latest { get; private set; } = new();

    private void Run()
    {
        using var nvml = Nvml.TryOpen(_glRenderer);
        using var pdh = nvml == null ? GpuEngineCounters.TryOpen() : null;
        var cpu = new CpuMeter();
        while (!_stop.IsSet)
        {
            var snapshot = new SystemSnapshot();
            try
            {
                cpu.Sample(snapshot);
                nvml?.Sample(snapshot);
                pdh?.Sample(snapshot);
            }
            catch (Exception e) when (e is ExternalException or InvalidOperationException or EntryPointNotFoundException)
            {
                // A monitoring hiccup must never take the screensaver down: keep whatever was filled in.
            }
            Latest = snapshot;
            _stop.Wait(1000);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(2000);
        _stop.Dispose();
    }

    // ---- CPU -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// CPU load as Task Manager shows it: share of all cores busy since the last sample, for the whole machine and
    /// for this process.
    /// </summary>
    private sealed class CpuMeter
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private long _idle, _kernel, _user;
        private TimeSpan _processTime;
        private long _stamp;

        public void Sample(SystemSnapshot s)
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
            _process.Refresh();
            var processTime = _process.TotalProcessorTime;
            var stamp = Stopwatch.GetTimestamp();
            if (_stamp != 0)
            {
                // Kernel time includes idle time, so busy = (kernel + user) - idle.
                var total = (kernel - _kernel) + (user - _user);
                if (total > 0) s.SystemCpu = 100.0 * (total - (idle - _idle)) / total;
                var wall = Stopwatch.GetElapsedTime(_stamp, stamp).TotalSeconds * Environment.ProcessorCount;
                if (wall > 0) s.ProcessCpu = 100.0 * (processTime - _processTime).TotalSeconds / wall;
            }
            (_idle, _kernel, _user, _processTime, _stamp) = (idle, kernel, user, processTime, stamp);
            s.WorkingSetBytes = _process.WorkingSet64;
        }

        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    }

    // ---- NVIDIA: NVML --------------------------------------------------------------------------------------------

    /// <summary>
    /// The NVIDIA Management Library, loaded by hand (it's only there on machines with an NVIDIA driver) and called
    /// through function pointers. Every call returns 0 for success; anything else leaves that value out.
    /// </summary>
    private sealed class Nvml : IDisposable
    {
        private readonly IntPtr _library, _device;
        private readonly delegate* unmanaged<int> _shutdown;
        private readonly delegate* unmanaged<IntPtr, uint*, int> _utilization, _power, _powerLimit, _fan, _pstate;
        private readonly delegate* unmanaged<IntPtr, int, uint*, int> _temperature, _clock;
        private readonly delegate* unmanaged<IntPtr, ulong*, int> _memory;
        private readonly string _name;

        private Nvml(IntPtr library, IntPtr device, string name)
        {
            _library = library;
            _device = device;
            _name = name;
            _shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlShutdown");
            _utilization = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetUtilizationRates");
            _power = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetPowerUsage");
            _powerLimit = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetEnforcedPowerLimit");
            _fan = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetFanSpeed");
            _pstate = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetPerformanceState");
            _temperature = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetTemperature");
            _clock = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetClockInfo");
            _memory = (delegate* unmanaged<IntPtr, ulong*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetMemoryInfo");
        }

        /// <summary>
        /// Opens NVML and picks the GPU OpenGL is drawing on (the one whose name is in <paramref name="glRenderer"/>),
        /// or the first NVIDIA GPU if none matches: on a laptop drawing on its integrated GPU, the NVIDIA one's
        /// numbers still say it's sitting idle, which is worth knowing. Null without an NVIDIA driver.
        /// </summary>
        public static Nvml? TryOpen(string glRenderer)
        {
            IntPtr library = 0;
            foreach (var path in new[] { "nvml.dll", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll") })
                if (NativeLibrary.TryLoad(path, out library)) break;
            if (library == 0) return null;

            try
            {
                var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlInit_v2");
                if (init() != 0) return null;
                var getCount = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetCount_v2");
                var getHandle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetHandleByIndex_v2");
                var getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetName");

                uint count;
                if (getCount(&count) != 0 || count == 0) return null;
                (IntPtr Device, string Name)? chosen = null;
                var buffer = stackalloc byte[96];
                for (uint i = 0; i < count; i++)
                {
                    IntPtr device;
                    if (getHandle(i, &device) != 0) continue;
                    var name = getName(device, buffer, 96) == 0 ? Marshal.PtrToStringUTF8((IntPtr)buffer) ?? "" : "";
                    chosen ??= (device, name);
                    // GL_RENDERER reads like "NVIDIA GeForce RTX 2070/PCIe/SSE2"; NVML's name, "NVIDIA GeForce RTX 2070".
                    if (name.Length > 0 && glRenderer.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = (device, name);
                        break;
                    }
                }
                return chosen is { } c ? new Nvml(library, c.Device, c.Name) : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null; // a very old driver
            }
        }

        public void Sample(SystemSnapshot s)
        {
            s.NvidiaName = _name;
            uint value, value2;
            var util = stackalloc uint[2]; // nvmlUtilization_t: gpu %, memory-controller %
            if (_utilization(_device, util) == 0) s.GpuLoad = util[0];
            if (_temperature(_device, 0 /* NVML_TEMPERATURE_GPU */, &value) == 0) s.GpuTemperature = value;
            if (_clock(_device, 0 /* NVML_CLOCK_GRAPHICS */, &value) == 0) s.GpuClockMhz = value;
            if (_clock(_device, 2 /* NVML_CLOCK_MEM */, &value) == 0) s.MemoryClockMhz = value;
            if (_power(_device, &value) == 0) s.PowerWatts = value / 1000.0;
            if (_powerLimit(_device, &value2) == 0) s.PowerLimitWatts = value2 / 1000.0;
            if (_fan(_device, &value) == 0) s.FanPercent = value; // laptops usually say "not supported"
            if (_pstate(_device, &value) == 0 && value < 32) s.PState = (int)value;
            var memory = stackalloc ulong[3]; // nvmlMemory_t: total, free, used (bytes)
            if (_memory(_device, memory) == 0)
            {
                s.VramTotalBytes = (long)memory[0];
                s.VramUsedBytes = (long)memory[2];
            }
        }

        public void Dispose()
        {
            _shutdown();
            NativeLibrary.Free(_library);
        }
    }

    // ---- Any GPU: the "GPU Engine" performance counters -----------------------------------------------------------

    /// <summary>
    /// Task Manager's GPU load, from the "\GPU Engine(*)\Utilization Percentage" counters. There's one instance per
    /// process per engine per GPU, named like <c>pid_1234_luid_0x0_0xD1B3_phys_0_eng_0_engtype_3D</c>. The 3D engine
    /// is the one that draws; adding up every process's share of it, per GPU, gives that GPU's load, and the busiest
    /// GPU is the one we report.
    /// </summary>
    private sealed class GpuEngineCounters : IDisposable
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        private readonly IntPtr _query, _counter;
        private readonly string _ownPid = $"pid_{Environment.ProcessId}_";

        private GpuEngineCounters(IntPtr query, IntPtr counter)
        {
            _query = query;
            _counter = counter;
        }

        public static GpuEngineCounters? TryOpen()
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;
            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var counter) != 0)
            {
                PdhCloseQuery(query);
                return null;
            }
            PdhCollectQueryData(query); // a rate counter needs a first sample to measure from
            return new GpuEngineCounters(query, counter);
        }

        public void Sample(SystemSnapshot s)
        {
            if (PdhCollectQueryData(_query) != 0) return;
            uint size = 0;
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, 0) != PDH_MORE_DATA) return;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0) return;
                var perGpu = new Dictionary<string, double>();
                double own = 0;
                // PDH_FMT_COUNTERVALUE_ITEM_W on 64-bit: name pointer (8 bytes), status (4), padding (4), double (8).
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + i * 24;
                    if (Marshal.ReadInt32(item, 8) != 0) continue; // this instance's value isn't valid
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    if (!name.EndsWith("engtype_3D", StringComparison.Ordinal)) continue;
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    var luid = name.IndexOf("luid_", StringComparison.Ordinal) is var at and >= 0 && name.IndexOf("_phys", at, StringComparison.Ordinal) is var end and > 0
                        ? name[at..end] : "";
                    perGpu[luid] = perGpu.GetValueOrDefault(luid) + value;
                    if (name.StartsWith(_ownPid, StringComparison.Ordinal)) own += value;
                }
                if (perGpu.Count > 0) s.GpuLoad = Math.Min(100, perGpu.Values.Max());
                s.OwnGpuLoad = Math.Min(100, own);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose() => PdhCloseQuery(_query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
        private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
    }
}

/// <summary>One second's hardware readings. Null means "this system can't tell us".</summary>
internal sealed class SystemSnapshot
{
    public double? SystemCpu { get; set; }
    public double? ProcessCpu { get; set; }
    public long? WorkingSetBytes { get; set; }

    /// <summary>The NVIDIA GPU NVML is reporting on, if any.</summary>
    public string? NvidiaName { get; set; }
    public double? GpuLoad { get; set; }
    /// <summary>Pipes' own share of the GPU (performance counters only).</summary>
    public double? OwnGpuLoad { get; set; }
    public double? GpuTemperature { get; set; }
    public double? GpuClockMhz { get; set; }
    public double? MemoryClockMhz { get; set; }
    public double? PowerWatts { get; set; }
    public double? PowerLimitWatts { get; set; }
    public double? FanPercent { get; set; }
    /// <summary>NVIDIA performance state: 0 is flat out, up to 8 or 12 idling. Shows a GPU that's stayed clocked down.</summary>
    public int? PState { get; set; }
    public long? VramUsedBytes { get; set; }
    public long? VramTotalBytes { get; set; }
}
