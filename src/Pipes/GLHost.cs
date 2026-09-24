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

    private readonly HostMode _mode;
    private readonly IntPtr _parent;
    private readonly Win32.WndProc _wndProc; // keep alive: native code holds a pointer to it
    private IntPtr _hwnd, _hdc, _hglrc;
    private GL _gl = null!;
    private bool _running = true;
    private bool _resized;
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
        var views = CreateViews(settings, w, h, perMonitor, _ => new Random());

        if (_mode == HostMode.Fullscreen)
        {
            Win32.ShowCursor(false);
            if (Win32.GetCursorPos(out var p)) _initialCursor = p;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;
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

                var now = clock.Elapsed.TotalSeconds;
                var dt = (float)Math.Min(now - last, 0.1); // clamp after hitches so pipes don't jump
                last = now;

                foreach (var view in views) view.Update(dt);
                RenderViews(views, 0, w, h);
                Win32.SwapBuffers(_hdc);
            }
        }
        finally
        {
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

        // Warm up (shader compilation, driver caches), then time. Finish() waits for the GPU, so the clock covers
        // the GPU's work, not just the CPU queueing commands.
        for (var i = 0; i < 30; i++) RenderViews(views, fbo, width, height);
        _gl.Finish();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            foreach (var view in views) view.Update(1f / 60f);
            RenderViews(views, fbo, width, height);
        }
        _gl.Finish();
        var ms = clock.Elapsed.TotalMilliseconds / frames;

        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(tex);
        var pieces = views.Sum(v => v.Scene.Pieces[Simulation.MeshKind.Cylinder].Count + v.Scene.Pieces[Simulation.MeshKind.Sphere].Count);
        foreach (var view in views) view.Dispose();
        File.WriteAllText(reportPath,
            $"{settings.Style} {width}x{height} in {views.Count} view(s), AA={settings.Antialiasing}: " +
            $"{ms:F2} ms/frame ({1000 / ms:F0} fps max), {pieces} pieces" + Environment.NewLine);
    }

    /// <summary>
    /// One view covering the whole target, or (with <paramref name="perMonitor"/> and more than one monitor) one per
    /// monitor, each placed where that monitor sits within the virtual desktop.
    /// </summary>
    private List<View> CreateViews(PipesSettings settings, int width, int height, bool perMonitor, Func<int, Random> rng)
    {
        if (perMonitor && MonitorLayout() is { Count: > 1 } monitors)
            return [.. monitors.Select((m, i) => new View(_gl, settings, rng(i), m.X, m.Y, m.Width, m.Height))];
        return [new View(_gl, settings, rng(0), 0, 0, width, height)];
    }

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

    private static (int Width, int Height) VirtualScreenSize() =>
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
