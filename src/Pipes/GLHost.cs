using System.Diagnostics;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using System.Runtime.InteropServices;
using Pipes.Native;
using Pipes.Rendering;
using Silk.NET.OpenGL;

namespace Pipes;

internal enum HostMode
{
    /// <summary>/s: borderless, topmost, spanning every monitor; any input exits.</summary>
    Fullscreen,
    /// <summary>/p hwnd: child of the little monitor picture in Screen Saver Settings.</summary>
    Preview,
    /// <summary>/w: resizable window for development; Esc or close exits.</summary>
    Windowed,
}

/// <summary>
/// A bare Win32 window with a WGL context. Deliberately no windowing library, because the preview mode needs
/// to parent into a foreign HWND, which GLFW/SDL-style libraries can't do.
/// </summary>
internal sealed unsafe class GLHost : IDisposable
{
    private const string ClassName = "PipesScreensaverWindow";
    private const int MouseMoveTolerance = 6;

    // The window (and small/taskbar) icon, pulled back out of this exe rather than embedded a second time as a
    // resource -- the build already put it there via <ApplicationIcon> in the csproj. Kept alive for the whole
    // process: RegisterClassEx only copies the HICON value, it doesn't take ownership, so disposing these would
    // leave the window class pointing at a destroyed icon.
    private static readonly Icon? WindowIcon = Environment.ProcessPath is { } exePath ? Icon.ExtractAssociatedIcon(exePath) : null;
    private static readonly Icon? WindowIconSmall = WindowIcon is { } icon ? new Icon(icon, SystemInformation.SmallIconSize) : null;

    private readonly HostMode _mode;
    private readonly IntPtr _parent;
    private readonly Win32.WndProc _wndProc; // keep alive: native code holds a pointer to it
    private IntPtr _hwnd, _hdc, _hglrc;
    private GL _gl = null!;
    private bool _running = true;
    private bool _resized;
    private bool _moved;
    private bool _toggleStats;
    private Win32.POINT? _initialCursor;
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();

    public GLHost(HostMode mode, IntPtr parent = default)
    {
        _mode = mode;
        _parent = parent;
        _wndProc = WindowProc;
    }

    /// <summary>Create a hidden window and context for offscreen rendering (screenshots).</summary>
    public static GLHost CreateOffscreen()
    {
        var host = new GLHost(HostMode.Windowed);
        host.CreateWindow(visible: false);
        return host;
    }

    public GL GL => _gl;

    public void Run(PipesSettings settings)
    {
        CreateWindow(visible: true);
        var (w, h) = ClientSize();
        var perMonitor = _mode == HostMode.Fullscreen && settings.SeparateMonitors;

        // On battery, the graphics settings are held down to PipesSettings.BatteryQuality. Only the renderers (and
        // the stats overlay's description of what's being measured) see the capped copy: the scenes are built from
        // the settings the user chose, so the power lead can't change the pipes, only what a frame costs. (The scene
        // does read the graphics settings, for its materials: with surface detail off it picks plain finishes. Given
        // the capped copy, a session started on battery would keep picking plain pipes after plugging in.)
        var onBattery = PowerSource.OnBattery();
        PipesSettings Quality(bool battery) => battery && settings.BatteryQuality is { } cap
            ? QualityPresets.LimitedTo(cap, settings)
            : settings;

        var views = CreateViews(settings, w, h, perMonitor, _ => new Random(), Quality(onBattery));

        // Pace frames by sleeping until the monitor's vertical blank, rather than letting the driver wait (which it
        // may do by spinning a CPU core). See VBlankWaiter. Null if unavailable: then the driver paces as before.
        var vblank = VBlankWaiter.TryCreate(_hwnd);

        // The stats overlay, if it's switched on. Never in the preview: the little monitor picture is too small.
        StatsOverlay? stats = null;
        void ShowStats(bool show)
        {
            if (show == (stats != null)) return;
            if (show) stats = new StatsOverlay(_gl, _hwnd, Quality(onBattery));
            else stats!.Dispose();
            if (!show) stats = null;
            foreach (var view in views) view.Renderer.Timer = stats?.Timer;
        }
        ShowStats(settings.ShowStats && _mode != HostMode.Preview);

        if (_mode == HostMode.Fullscreen)
        {
            Win32.ShowCursor(false);
            if (Win32.GetCursorPos(out var p)) _initialCursor = p;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;
            var lastPresent = last;
            var lastPowerCheck = last;
            while (_running)
            {
                while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
                {
                    if (msg.message == Win32.WM_QUIT) { _running = false; break; }
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
                if (!_running) break;

                // The Screen Saver Settings dialog destroys our parent when it closes or switches savers.
                if (_mode == HostMode.Preview && !Win32.IsWindow(_parent)) break;

                if (_resized)
                {
                    _resized = false;
                    (w, h) = ClientSize();
                    // A single view follows the window. Per-monitor views are fixed to the monitors.
                    if (views.Count == 1) views[0].Resize(w, h);
                }
                if (_moved)
                {
                    // The window may now be (mostly) on a different monitor, with a different refresh rate.
                    _moved = false;
                    vblank?.Dispose();
                    vblank = VBlankWaiter.TryCreate(_hwnd);
                    stats?.DisplayChanged();
                }
                if (_toggleStats)
                {
                    _toggleStats = false;
                    ShowStats(stats == null);
                }

                // The power lead coming out (or going back in) changes which quality the renderers are built for.
                // Swapping them compiles new shaders, so it costs a hitch of a frame or two; it only happens when the
                // machine actually changes power source, which is rare and deliberate.
                if (settings.BatteryQuality != null && clock.Elapsed.TotalSeconds - lastPowerCheck >= PowerSource.PollSeconds)
                {
                    lastPowerCheck = clock.Elapsed.TotalSeconds;
                    if (PowerSource.OnBattery() != onBattery)
                    {
                        onBattery = !onBattery;
                        var options = RenderOptions.From(Quality(onBattery));
                        foreach (var view in views)
                        {
                            view.ReplaceRenderer(new PipeRenderer(_gl, options));
                            view.Renderer.Timer = stats?.Timer; // the old renderer's timer went with it
                        }
                        if (stats != null) stats.Settings = Quality(onBattery); // its footer says what's being measured
                    }
                }

                if (vblank != null && !vblank.Wait())
                {
                    vblank.Dispose(); // e.g. the display went to sleep: fall back to the driver's pacing
                    vblank = null;
                }

                var now = clock.Elapsed.TotalSeconds;
                var dt = (float)Math.Min(now - last, 0.1); // clamp after hitches so pipes don't jump
                last = now;

                stats?.BeginFrame();
                foreach (var view in views) view.Update(dt);
                RenderViews(views, 0, w, h);
                // The CPU's share of the frame: simulating, and queueing the drawing (the GPU does it later).
                var cpuMs = (clock.Elapsed.TotalSeconds - now) * 1000;
                if (stats != null)
                {
                    var (left, top) = StatsCorner(views);
                    stats.Draw(w, h, left, top, views.Count);
                }
                Win32.SwapBuffers(_hdc);

                if (stats != null)
                {
                    // Frame interval, as seen on screen: from one SwapBuffers to the next.
                    var presented = clock.Elapsed.TotalSeconds;
                    stats.FrameDone((presented - lastPresent) * 1000, cpuMs);
                    lastPresent = presented;
                }
            }
        }
        finally
        {
            stats?.Dispose();
            vblank?.Dispose();
            foreach (var view in views) view.Dispose();
            if (_mode == HostMode.Fullscreen) Win32.ShowCursor(true);
        }
    }

    /// <summary>
    /// Simulates <paramref name="seconds"/> of animation at a fixed step, renders one frame offscreen, saves a PNG.
    /// With <paramref name="monitors"/>, renders the whole desktop exactly as fullscreen mode would lay it out
    /// (ignoring <paramref name="width"/>/<paramref name="height"/>).
    /// </summary>
    public void Screenshot(PipesSettings settings, int width, int height, float seconds, string path, int seed, bool monitors)
    {
        if (monitors) (width, height) = VirtualScreenSize();
        var views = CreateViews(settings, width, height, monitors, i => new Random(seed + i));
        for (var t = 0f; t < seconds; t += 1f / 60f)
            foreach (var view in views) view.Update(1f / 60f);

        var (fbo, tex) = CreateReadbackTarget(width, height);
        RenderViews(views, fbo, width, height);

        var pixels = new byte[width * height * 4];
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(tex);
        foreach (var view in views) view.Dispose();

        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (var y = 0; y < height; y++) // GL rows are bottom-up
            Marshal.Copy(pixels, (height - 1 - y) * width * 4, data.Scan0 + y * data.Stride, width * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Times rendering: simulates a busy scene, then renders <paramref name="frames"/> frames offscreen as fast as
    /// possible and writes the average GPU+CPU cost per frame to <paramref name="reportPath"/>. No VSync here, so
    /// the number is the real work per frame, not the monitor's refresh interval. With <paramref name="monitors"/>,
    /// measures the real fullscreen layout instead of one <paramref name="width"/> x <paramref name="height"/> view.
    /// </summary>
    public void Benchmark(PipesSettings settings, int width, int height, int frames, string reportPath, bool monitors)
    {
        if (monitors) (width, height) = VirtualScreenSize();
        var views = CreateViews(settings, width, height, monitors, i => new Random(1 + i));
        for (var t = 0f; t < 12f; t += 1f / 60f) // let the scenes fill up first
            foreach (var view in views) view.Update(1f / 60f);

        var (fbo, tex) = CreateReadbackTarget(width, height);

        // Warm up, then time. The warm-up covers shader compilation and driver caches, but mostly it gives the GPU
        // time to raise its clock: an idle card runs slow, and a short benchmark can finish before it speeds up,
        // which made the same settings measure anywhere from 2.5 to 13 ms a frame. A second and a half of steady
        // work settles it. Finish() waits for the GPU, so the clock covers the GPU's work, not just the CPU queueing
        // commands.
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 30 || clock.Elapsed.TotalSeconds < 1.5; i++)
        {
            RenderViews(views, fbo, width, height);
            _gl.Finish();
        }
        // The grid-build average should cover the timed frames only, not the warm-up's first (JIT-compiled,
        // array-growing) builds.
        foreach (var view in views) view.Renderer.ResetGridStats();
        clock.Restart();
        var pieces = 0; // the busiest frame's count: scenes fade out and restart, so the last frame's can be tiny
        for (var i = 0; i < frames; i++)
        {
            foreach (var view in views) view.Update(1f / 60f);
            RenderViews(views, fbo, width, height);
            pieces = Math.Max(pieces, views.Sum(v => Enumerable.Range(0, Simulation.PieceLists.KindCount).Sum(k => v.Scene.Pieces[(Simulation.MeshKind)k].Count)));
        }
        _gl.Finish();
        var ms = clock.Elapsed.TotalMilliseconds / frames;
        // Traced reflections rebuild their grid on the CPU every frame: report that part on its own too.
        var grid = views[0].Renderer.AverageGridBuildMilliseconds is { } gridMs
            ? $"; reflection grid build {gridMs:F3} ms/frame (CPU)"
            : "";

        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(tex);
        foreach (var view in views) view.Dispose();
        File.WriteAllText(reportPath,
            $"{settings.Style} {width}x{height} in {views.Count} view(s), AA={settings.Antialiasing}: " +
            $"{ms:F2} ms/frame ({1000 / ms:F0} fps max), {pieces} pieces at most{grid}" + Environment.NewLine);
    }

    /// <summary>
    /// The "Test this PC" check behind the settings dialog: which <see cref="QualityPreset"/> this machine can run
    /// at its real size and refresh rate. Writes an <see cref="AutotuneReport"/> as JSON to
    /// <paramref name="resultPath"/>. The rules it judges by (and why) are the constants in <see cref="Pipes.Autotune"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It measures what the screensaver will actually draw: the whole desktop, one view per monitor if
    /// <see cref="PipesSettings.SeparateMonitors"/> is on (as fullscreen does), with the user's own pipe and camera
    /// settings. Only the graphics settings change from preset to preset.
    /// </para>
    /// <para>
    /// The scenes are simulated once, then each preset draws <em>the same</em> scenes, so the presets are compared
    /// on equal work. A <see cref="View"/> bundles a scene with a renderer, and a renderer's options are fixed when
    /// it's created (they choose which shaders to compile), so each preset swaps a new renderer into the existing
    /// views (<see cref="View.ReplaceRenderer"/>) rather than building new views, which would mean new scenes.
    /// </para>
    /// <para>
    /// Each frame is timed on its own, including the scene update, with <c>Finish()</c> at the end so the time
    /// covers the GPU's work too, not just the CPU queueing it. That's a little pessimistic: in the real loop the
    /// CPU's part of the next frame overlaps the GPU's part of this one. Pessimistic is the right way to be wrong
    /// here (see <see cref="Pipes.Autotune.TargetShareOfBudget"/>).
    /// </para>
    /// </remarks>
    public void Autotune(PipesSettings settings, string resultPath)
    {
        var (width, height) = VirtualScreenSize();
        var refreshHz = PrimaryRefreshRate();
        var budgetMs = 1000.0 / refreshHz;
        var targetMs = budgetMs * Pipes.Autotune.TargetShareOfBudget;

        // Fixed seeds, so running the test twice measures the same scenes.
        var views = CreateViews(settings, width, height, settings.SeparateMonitors, i => new Random(1 + i));
        var simulated = SimulateForAutotune(views, settings.Camera == CameraMotion.FlyThrough);
        var (fbo, tex) = CreateReadbackTarget(width, height);

        void Frame()
        {
            foreach (var view in views) view.Update(1f / 60f); // keep the scenes moving, as they would live
            RenderViews(views, fbo, width, height);
            _gl.Finish();
        }

        var results = new List<AutotuneResult>();
        try
        {
            foreach (var preset in Enum.GetValues<QualityPreset>())
            {
                // Swap in this preset's renderers (the previous preset's are disposed, freeing their buffers).
                var options = RenderOptions.From(QualityPresets.With(preset, settings));
                foreach (var view in views) view.ReplaceRenderer(new PipeRenderer(_gl, options));

                var warmUp = results.Count == 0 ? Pipes.Autotune.FirstWarmUpSeconds : Pipes.Autotune.LaterWarmUpSeconds;
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < 10 || clock.Elapsed.TotalSeconds < warmUp; i++) Frame();

                var frameTimes = new List<double>();
                clock.Restart();
                while (frameTimes.Count < Pipes.Autotune.MinMeasuredFrames || clock.Elapsed.TotalSeconds < Pipes.Autotune.MeasureSeconds)
                {
                    var start = clock.Elapsed.TotalMilliseconds;
                    Frame();
                    frameTimes.Add(clock.Elapsed.TotalMilliseconds - start);
                    // On a very slow machine (software rendering, say), a clear failure needn't be timed precisely:
                    // once 15 frames have all taken more than twice the target, stop, rather than spend a minute
                    // measuring it.
                    if (frameTimes.Count >= 15 && frameTimes.Min() > 2 * targetMs) break;
                }

                var p90 = Pipes.Autotune.Percentile(frameTimes, Pipes.Autotune.JudgedPercentile);
                var verdict = Pipes.Autotune.Judge(p90, targetMs, budgetMs);
                results.Add(new AutotuneResult(preset, Math.Round(frameTimes.Average(), 3), Math.Round(p90, 3), verdict));
                // The presets only get heavier from here, so the first that can't hold the refresh ends the test.
                // Merely missing the headroom target doesn't: a preset that keeps up with little to spare is still
                // worth knowing about, and the next one up may yet be comfortable on a faster part of the scene.
                if (verdict == AutotuneVerdict.TooSlow) break;
            }
        }
        finally
        {
            _gl.DeleteFramebuffer(fbo);
            _gl.DeleteTexture(tex);
            foreach (var view in views) view.Dispose();
        }

        // The highest preset with room to spare (they're tried lightest first). Failing that, the highest that still
        // holds the refresh, even if barely. If even Lite can't, it's still the lightest there is, so recommend it.
        var recommended = (results.LastOrDefault(r => r.Verdict == AutotuneVerdict.Comfortable)
            ?? results.LastOrDefault(r => r.Verdict == AutotuneVerdict.Tight))?.Preset ?? QualityPreset.Lite;
        Pipes.Autotune.Write(new AutotuneReport(
            refreshHz, Math.Round(budgetMs, 3), Math.Round(targetMs, 3), width, height, views.Count,
            _gl.GetStringS(StringName.Renderer) ?? "unknown", settings.Camera, simulated, results, recommended), resultPath);
    }

    /// <summary>
    /// Run the scenes forward to a representative moment, at a fixed 1/60 s step: in fly-through, until every view
    /// is in flight plus a few seconds; otherwise, a fixed time into the scene. Returns the simulated seconds.
    /// </summary>
    private static double SimulateForAutotune(List<View> views, bool flyThrough)
    {
        const float step = 1f / 60f;
        float time = 0f, allFlyingAt = -1f;
        while (time < Pipes.Autotune.MaxSimulatedSeconds)
        {
            foreach (var view in views) view.Update(step);
            time += step;
            if (!flyThrough)
            {
                if (time >= Pipes.Autotune.SceneSeconds) break;
            }
            else if (allFlyingAt < 0f)
            {
                if (views.All(v => v.Scene.Flying)) allFlyingAt = time;
            }
            else if (time - allFlyingAt >= Pipes.Autotune.SecondsAfterTakeOff) break;
        }
        return Math.Round(time, 2);
    }

    /// <summary>
    /// The primary monitor's refresh rate in Hz, as Windows is running it now. 0 and 1 mean "the hardware default"
    /// (some drivers and virtual displays report them), which is 60 Hz in practice.
    /// </summary>
    public static int PrimaryRefreshRate()
    {
        var mode = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        return Win32.EnumDisplaySettings(null, Win32.ENUM_CURRENT_SETTINGS, ref mode) && mode.dmDisplayFrequency > 1
            ? (int)mode.dmDisplayFrequency
            : 60;
    }

    /// <summary>
    /// One view covering the whole target, or (with <paramref name="perMonitor"/> and more than one monitor) one per
    /// monitor, each placed where that monitor sits within the virtual desktop.
    /// </summary>
    private List<View> CreateViews(PipesSettings settings, int width, int height, bool perMonitor, Func<int, Random> rng, PipesSettings? renderSettings = null)
    {
        if (perMonitor && MonitorLayout() is { Count: > 1 } monitors)
            return [.. monitors.Select((m, i) => new View(_gl, settings, rng(i), m.X, m.Y, m.Width, m.Height, renderSettings))];
        return [new View(_gl, settings, rng(0), 0, 0, width, height, renderSettings)];
    }

    /// <summary>
    /// Where the stats overlay goes, in window pixels: the top-left corner of the main monitor, which is where the
    /// taskbar's start button and most people's eyes are. Only fullscreen spans several monitors; in a window it's
    /// the window's corner. The main monitor's top-left is (0, 0) on the desktop, so in the window that covers the
    /// whole desktop it's at minus the desktop's origin.
    /// </summary>
    private (int Left, int Top) StatsCorner(List<View> views) => _mode == HostMode.Fullscreen
        ? (-Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN), -Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN))
        : (0, 0);

    private void RenderViews(List<View> views, uint targetFbo, int width, int height)
    {
        if (views.Count > 1)
        {
            // Monitors of different sizes or offsets leave parts of the virtual desktop that no screen shows. Nobody
            // sees them live, but a screenshot would show leftover garbage there, so clear to black first.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        foreach (var view in views) view.Render(targetFbo, height);
    }

    /// <summary>
    /// Every monitor's rectangle in window pixels. The fullscreen window covers the "virtual desktop", the smallest
    /// rectangle around all monitors, whose top-left can be negative (a monitor left of or above the main one), so
    /// each monitor is shifted by that origin. The process is per-monitor DPI aware, so these are real pixels.
    /// </summary>
    private static List<(int X, int Y, int Width, int Height)> MonitorLayout()
    {
        var originX = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var originY = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        var monitors = new List<(int, int, int, int)>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref Win32.RECT r, IntPtr _) =>
        {
            monitors.Add((r.Left - originX, r.Top - originY, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    public static (int Width, int Height) VirtualScreenSize() =>
        (Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

    /// <summary>An 8-bit offscreen target we can read back (the default framebuffer of a hidden window is undefined).</summary>
    private (uint Fbo, uint Tex) CreateReadbackTarget(int width, int height)
    {
        var fbo = _gl.GenFramebuffer();
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        return (fbo, tex);
    }

    private void CreateWindow(bool visible)
    {
        var instance = Win32.GetModuleHandle(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_OWNDC | Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hIcon = WindowIcon?.Handle ?? IntPtr.Zero,
            hIconSm = WindowIconSmall?.Handle ?? IntPtr.Zero,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(Win32.IDC_ARROW)),
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);

        uint style, exStyle = 0;
        int x, y, w, h;
        switch (_mode)
        {
            case HostMode.Fullscreen:
                style = Win32.WS_POPUP | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
                exStyle = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW;
                x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
                y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
                w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
                h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
                break;
            case HostMode.Preview:
                style = Win32.WS_CHILD | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
                Win32.GetClientRect(_parent, out var r);
                (x, y, w, h) = (0, 0, r.Right - r.Left, r.Bottom - r.Top);
                break;
            default:
                style = visible ? Win32.WS_OVERLAPPEDWINDOW | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS : Win32.WS_POPUP;
                (x, y, w, h) = (100, 100, 1280, 760);
                break;
        }

        _hwnd = Win32.CreateWindowEx(exStyle, ClassName, "Pipes", style, x, y, w, h,
            _mode == HostMode.Preview ? _parent : IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        _hdc = Win32.GetDC(_hwnd);
        var pfd = new Win32.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Win32.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = Win32.PFD_DRAW_TO_WINDOW | Win32.PFD_SUPPORT_OPENGL | Win32.PFD_DOUBLEBUFFER,
            iPixelType = Win32.PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            iLayerType = Win32.PFD_MAIN_PLANE,
        };
        var format = Win32.ChoosePixelFormat(_hdc, ref pfd);
        if (format == 0 || !Win32.SetPixelFormat(_hdc, format, ref pfd))
            throw new InvalidOperationException("No usable OpenGL pixel format.");

        _hglrc = Win32.wglCreateContext(_hdc);
        if (_hglrc == IntPtr.Zero || !Win32.wglMakeCurrent(_hdc, _hglrc))
            throw new InvalidOperationException("Could not create an OpenGL context.");

        var opengl32 = Win32.LoadLibrary("opengl32.dll");
        _gl = GL.GetApi(name =>
        {
            var p = Win32.wglGetProcAddress(name);
            // wglGetProcAddress returns 0..3 or -1 for GL 1.1 entry points; those live in opengl32.dll itself.
            return p is 0 or 1 or 2 or 3 or -1 ? Win32.GetProcAddress(opengl32, name) : p;
        });

        // VSync: smooth motion and no busy-looping.
        var swapInterval = Win32.wglGetProcAddress("wglSwapIntervalEXT");
        if (swapInterval is not (0 or 1 or 2 or 3 or -1))
            ((delegate* unmanaged[Stdcall]<int, int>)swapInterval)(1);

        if (visible) Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
    }

    private (int Width, int Height) ClientSize()
    {
        Win32.GetClientRect(_hwnd, out var r);
        return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_SIZE:
                _resized = true;
                break;
            case Win32.WM_MOVE:
                _moved = true;
                break;
            case Win32.WM_ERASEBKGND:
                return 1;
            case Win32.WM_CLOSE:
                _running = false;
                break;
            case Win32.WM_DESTROY:
                _running = false;
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            case Win32.WM_SYSCOMMAND when _mode == HostMode.Fullscreen && ((int)wParam & 0xFFF0) == Win32.SC_SCREENSAVE:
                return IntPtr.Zero; // already the screensaver; don't let Windows start another
            case Win32.WM_SETCURSOR when _mode == HostMode.Fullscreen:
                Win32.SetCursor(IntPtr.Zero);
                return 1;
        }

        if (_mode == HostMode.Fullscreen && IsExitInput(msg)) _running = false;
        if (_mode == HostMode.Windowed && msg == Win32.WM_KEYDOWN && (int)wParam == Win32.VK_ESCAPE) _running = false;
        // F3 shows or hides the stats overlay in the windowed preview. (Fullscreen can't: any key ends a screensaver.)
        if (_mode == HostMode.Windowed && msg == Win32.WM_KEYDOWN && (int)wParam == Win32.VK_F3) _toggleStats = true;

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool IsExitInput(int msg)
    {
        // Ignore the burst of synthetic messages Windows sends while the window appears.
        if (_sinceStart.Elapsed < TimeSpan.FromMilliseconds(500)) return false;

        switch (msg)
        {
            case Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN or Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN
                or Win32.WM_MBUTTONDOWN or Win32.WM_MOUSEWHEEL:
                return true;
            case Win32.WM_MOUSEMOVE:
                if (!Win32.GetCursorPos(out var p)) return false;
                if (_initialCursor is not { } start) { _initialCursor = p; return false; }
                return Math.Abs(p.X - start.X) > MouseMoveTolerance || Math.Abs(p.Y - start.Y) > MouseMoveTolerance;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_hglrc != IntPtr.Zero)
        {
            Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            Win32.wglDeleteContext(_hglrc);
            _hglrc = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            Win32.ReleaseDC(_hwnd, _hdc);
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
