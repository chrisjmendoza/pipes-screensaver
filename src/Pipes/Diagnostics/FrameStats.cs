namespace Pipes.Diagnostics;

/// <summary>
/// The last few thousand frames' timings, and the summary numbers the stats overlay shows (FPS, 1% lows...).
/// </summary>
/// <remarks>
/// Three numbers per frame, because they answer different questions:
/// <list type="bullet">
/// <item><b>Interval</b>: time from one frame reaching the screen to the next. This is what you see and feel; with
/// VSync it sits at the monitor's refresh interval (16.7 ms at 60 Hz) and jumps to a multiple of it when a frame
/// misses.</item>
/// <item><b>CPU</b>: time the CPU spent on the frame: moving the pipes and camera, and queueing the draw calls.</item>
/// <item><b>GPU</b>: time the GPU was busy drawing it (<see cref="GpuTimer"/>). Arrives a few frames late.</item>
/// </list>
/// A frame only misses the refresh when CPU or GPU time goes over the interval, so the two of them against the
/// refresh interval say how much headroom there is, which the interval alone can't (it's pinned at the refresh
/// rate until the moment it isn't).
/// </remarks>
internal sealed class FrameStats
{
    /// <summary>Frames kept: 2048 is 34 s at 60 Hz, 14 s at 144 Hz. Enough for steady 1% and 0.1% lows.</summary>
    public const int Capacity = 2048;

    private readonly float[] _interval = new float[Capacity];
    private readonly float[] _cpu = new float[Capacity];
    private readonly float[] _gpu = new float[Capacity];

    /// <summary>Frames recorded so far. Frame i lives at index i % Capacity.</summary>
    public long Count { get; private set; }

    /// <summary>Record a finished frame. Its GPU time is unknown (NaN) until <see cref="SetGpu"/>.</summary>
    public void Add(double intervalMs, double cpuMs)
    {
        var i = (int)(Count % Capacity);
        _interval[i] = (float)intervalMs;
        _cpu[i] = (float)cpuMs;
        _gpu[i] = float.NaN;
        Count++;
    }

    /// <summary>Fill in frame <paramref name="frame"/>'s GPU time, if it's still in the history.</summary>
    public void SetGpu(long frame, double ms)
    {
        if (frame < 0 || frame >= Count || Count - frame > Capacity) return;
        _gpu[(int)(frame % Capacity)] = (float)ms;
    }

    /// <summary>Frame <paramref name="age"/> frames ago (0 = the newest): its interval, CPU and GPU milliseconds.</summary>
    public (float Interval, float Cpu, float Gpu) Recent(int age)
    {
        var i = (int)((Count - 1 - age) % Capacity);
        return (_interval[i], _cpu[i], _gpu[i]);
    }

    /// <summary>
    /// Summarise the last <paramref name="windowMs"/> of frames (up to the whole history). "Now" numbers (FPS, CPU,
    /// GPU) use the last <paramref name="nowMs"/> only, so they react quickly; the lows and averages use the whole
    /// window, so they're steady.
    /// </summary>
    public Summary Summarise(double nowMs, double windowMs)
    {
        var available = (int)Math.Min(Count, Capacity);
        if (available == 0) return default;

        // Walk back from the newest frame, collecting the window.
        var window = new List<float>(Math.Min(available, 1024));
        double windowSum = 0, nowSum = 0, cpuNow = 0, gpuNow = 0;
        int nowFrames = 0, gpuFrames = 0;
        for (var age = 0; age < available && windowSum < windowMs; age++)
        {
            var (interval, cpu, gpu) = Recent(age);
            window.Add(interval);
            windowSum += interval;
            if (nowSum < nowMs)
            {
                nowSum += interval;
                nowFrames++;
                cpuNow += cpu;
                if (!float.IsNaN(gpu)) { gpuNow += gpu; gpuFrames++; }
            }
        }

        // The "1% low" is the average frame rate of the slowest 1% of frames: a steady 60 with a few hitches has a
        // high average but a low 1% low, which matches how it feels. Always at least one frame, so a short history
        // still gives a number.
        window.Sort(); // ascending: the slowest frames are at the end
        double Low(double fraction)
        {
            var n = Math.Max(1, (int)(window.Count * fraction));
            double sum = 0;
            for (var i = window.Count - n; i < window.Count; i++) sum += window[i];
            return 1000.0 * n / sum;
        }
        double Percentile(double p) => window[Math.Clamp((int)(window.Count * p), 0, window.Count - 1)];

        return new Summary(
            Fps: 1000.0 * nowFrames / nowSum,
            FrameMs: nowSum / nowFrames,
            CpuMs: cpuNow / nowFrames,
            GpuMs: gpuFrames > 0 ? gpuNow / gpuFrames : null,
            AverageFps: 1000.0 * window.Count / windowSum,
            Low1Fps: Low(0.01),
            Low01Fps: Low(0.001),
            WorstMs: window[^1],
            P99Ms: Percentile(0.99),
            Frames: window.Count);
    }

    /// <summary>What <see cref="Summarise"/> found. Zero everywhere (default) before the first frame.</summary>
    public readonly record struct Summary(
        double Fps, double FrameMs, double CpuMs, double? GpuMs,
        double AverageFps, double Low1Fps, double Low01Fps, double WorstMs, double P99Ms, int Frames);
}
